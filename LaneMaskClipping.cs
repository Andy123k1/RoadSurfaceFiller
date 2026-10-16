using System;
using System.Collections.Generic;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private sealed class LaneMaskCell
        {
            internal float3[] Points;
            internal float3 Minimum, Maximum;
            internal SurfaceMaskContour Contour;
        }

        private sealed class LaneMaskBorder
        {
            internal int From, To;
        }

        private static void AddLaneMaskCells(SurfacePatch patch, List<LaneMaskCell> cells)
        {
            foreach (SurfacePatch part in patch.Parts) AddLaneMaskCells(part, cells);
            SurfaceMaskContour contour = TryGetSurfaceBoundaryPath(patch, out var boundary)
                ? new SurfaceMaskContour { Boundary = boundary } : null;
            foreach (int3 triangle in patch.Triangles)
            {
                float3 a = patch.Nodes[triangle.x].m_Position, b = patch.Nodes[triangle.y].m_Position,
                    c = patch.Nodes[triangle.z].m_Position;
                if (math.abs(Cross2(b.xz - a.xz, c.xz - a.xz)) <= 0.00001f) continue;
                cells.Add(new LaneMaskCell {
                    Points = Cross2(b.xz - a.xz, c.xz - a.xz) > 0f ? new[] { a, b, c } : new[] { a, c, b },
                    Minimum = math.min(a, math.min(b, c)), Maximum = math.max(a, math.max(b, c)), Contour = contour
                });
                if (contour != null) contour.CellCount++;
            }
        }

        private SurfacePatch SubtractLaneMasks(SurfacePatch source, List<LaneMaskCell> cells)
        {
            if (cells.Count == 0) return source;
            if (source.Parts.Count > 0)
            {
                SurfacePatch combined = FlattenSurfacePatchMesh(source);
                if (TryClipSurfaceFootprint(source, cells, out SurfacePatch joined) ||
                    TrySubtractLaneContoursLegacy(combined, cells, out joined) ||
                    TrySweepLaneMasks(combined, cells, out joined)) return joined;
                SurfacePatch result = CopySurfacePatchProperties(source);
                foreach (SurfacePatch part in source.Parts)
                {
                    SurfacePatch clipped = SubtractLaneMasks(part, cells);
                    if (clipped.Parts.Count > 0 || clipped.Triangles.Count > 0) result.Parts.Add(clipped);
                }
                result.DedicatedLanesClipped = true;
                return result;
            }
            if (source.Count < 4 || source.Triangles.Count == 0) return source;
            if (TrySubtractLaneContours(source, cells, out SurfacePatch contourResult)) return contourResult;
            if (TrySweepLaneMasks(source, cells, out SurfacePatch sweepResult)) return sweepResult;
            List<List<float3>> fragments = new List<List<float3>>();
            bool changed = false;
            foreach (int3 triangle in source.Triangles)
            {
                List<List<float3>> remaining = new List<List<float3>> {
                    new List<float3> { source.Nodes[triangle.x].m_Position, source.Nodes[triangle.y].m_Position,
                        source.Nodes[triangle.z].m_Position }
                };
                foreach (LaneMaskCell cell in cells)
                {
                    for (int i = remaining.Count - 1; i >= 0; i--)
                    {
                        List<float3> polygon = remaining[i];
                        if (!LaneCellOverlaps(polygon, cell)) continue;
                        List<List<float3>> outside = SubtractLaneCell(polygon, cell, out bool intersects);
                        if (!intersects) continue;
                        changed = true;
                        remaining.RemoveAt(i);
                        remaining.AddRange(outside);
                    }
                    if (remaining.Count == 0) break;
                }
                fragments.AddRange(remaining);
            }
            if (!changed) { source.DedicatedLanesClipped = true; return source; }
            if (fragments.Count == 0)
            {
                SurfacePatch empty = CopySurfacePatchProperties(source); empty.DedicatedLanesClipped = true; return empty;
            }
            SurfacePatch merged = MergeLaneMaskFragments(source, fragments);
            merged.DedicatedLanesClipped = true;
            return merged;
        }

        private static SurfacePatch FlattenSurfacePatchMesh(SurfacePatch source)
        {
            SurfacePatch result = CopySurfacePatchProperties(source);
            AppendSurfacePatchMesh(source, result);
            if (result.Nodes.Count > 0) result.Nodes.Add(result.Nodes[0]);
            return result;
        }
        private static void AppendSurfacePatchMesh(SurfacePatch source, SurfacePatch result)
        {
            foreach (SurfacePatch part in source.Parts) AppendSurfacePatchMesh(part, result);
            int offset = result.Nodes.Count;
            for (int i = 0; i < source.Count - 1; i++) result.Nodes.Add(source.Nodes[i]);
            foreach (int3 face in source.Triangles) result.Triangles.Add(face + offset);
        }

        private static bool LaneCellOverlaps(List<float3> polygon, LaneMaskCell cell)
        {
            float3 low = new float3(float.PositiveInfinity), high = new float3(float.NegativeInfinity);
            foreach (float3 point in polygon) { low = math.min(low, point); high = math.max(high, point); }
            return LaneCellOverlaps(low, high, cell);
        }

        private static bool LaneCellOverlaps(float3 low, float3 high, LaneMaskCell cell)
        {
            // Do not cut an elevated sidewalk/bridge with a lane on a lower deck.
            return low.x < cell.Maximum.x + 0.0001f && high.x > cell.Minimum.x - 0.0001f &&
                low.z < cell.Maximum.z + 0.0001f && high.z > cell.Minimum.z - 0.0001f &&
                low.y < cell.Maximum.y + 0.12f && high.y > cell.Minimum.y - 0.12f;
        }

        private static double LaneCellSide(float3 point, float3 a, float3 b)
        {
            // Relative double coordinates prevent cancellation at large map
            // positions and give adjoining source triangles the same cut plane.
            return ((double)b.x - a.x) * ((double)point.z - a.z) -
                ((double)b.z - a.z) * ((double)point.x - a.x);
        }

        private static List<List<float3>> SubtractLaneCell(List<float3> polygon, LaneMaskCell cell, out bool intersects)
        {
            intersects = false;
            List<float3> inside = polygon;
            for (int edge = 0; edge < 3; edge++)
            {
                inside = ClipLaneCell(inside, cell.Points[edge], cell.Points[(edge + 1) % 3], true);
                if (inside.Count < 3 || LanePolygonArea(inside) <= 0.000005) return null;
            }
            intersects = true;
            List<List<float3>> result = new List<List<float3>>();
            inside = polygon;
            for (int edge = 0; edge < 3 && inside.Count >= 3; edge++)
            {
                float3 a = cell.Points[edge], b = cell.Points[(edge + 1) % 3];
                List<float3> outside = ClipLaneCell(inside, a, b, false);
                if (outside.Count >= 3 && LanePolygonArea(outside) > 0.000005) result.Add(outside);
                inside = ClipLaneCell(inside, a, b, true);
            }
            return result;
        }

        private static List<float3> ClipLaneCell(List<float3> source, float3 a, float3 b, bool inside)
        {
            List<float3> result = new List<float3>();
            if (source.Count == 0) return result;
            float3 previous = source[source.Count - 1];
            double previousSide = LaneCellSide(previous, a, b) * (inside ? 1 : -1);
            foreach (float3 point in source)
            {
                double side = LaneCellSide(point, a, b) * (inside ? 1 : -1);
                if ((side >= 0) != (previousSide >= 0))
                    AddDistinct(result, math.lerp(previous, point, (float)(previousSide / (previousSide - side))));
                if (side >= 0) AddDistinct(result, point);
                previous = point; previousSide = side;
            }
            if (result.Count > 1 && math.distancesq(result[0], result[result.Count - 1]) < 0.00000001f)
                result.RemoveAt(result.Count - 1);
            return result;
        }

        private static double LanePolygonArea(List<float3> polygon)
        {
            double area = 0;
            if (polygon.Count < 3) return area;
            float3 origin = polygon[0];
            for (int i = 1; i + 1 < polygon.Count; i++)
                area += LaneCellSide(polygon[i + 1], origin, polygon[i]);
            return area * 0.5;
        }

        private SurfacePatch MergeLaneMaskFragments(SurfacePatch source, List<List<float3>> fragments)
        {
            List<float3> vertices = new List<float3>();
            Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
            Dictionary<long, LaneMaskBorder> borders = new Dictionary<long, LaneMaskBorder>();
            float2 origin = fragments[0][0].xz;
            foreach (List<float3> fragment in fragments)
            {
                int first = WeldLaneVertex(fragment[0], origin, vertices, grid), previous = first;
                for (int i = 1; i < fragment.Count; i++)
                {
                    int next = WeldLaneVertex(fragment[i], origin, vertices, grid);
                    AddLaneMaskBorder(borders, previous, next); previous = next;
                }
                AddLaneMaskBorder(borders, previous, first);
            }
            // Clipping neighbouring triangles can subdivide one shared edge
            // differently. Split remaining edges at collinear vertices before
            // cancelling them; internal mesh diagonals must not become seams.
            Dictionary<long, LaneMaskBorder> split = new Dictionary<long, LaneMaskBorder>();
            foreach (LaneMaskBorder border in borders.Values)
            {
                float3 a = vertices[border.From], b = vertices[border.To];
                float2 direction = b.xz - a.xz;
                float length = math.lengthsq(direction);
                List<KeyValuePair<float, int>> points = new List<KeyValuePair<float, int>> {
                    new KeyValuePair<float, int>(0f, border.From), new KeyValuePair<float, int>(1f, border.To)
                };
                if (length > 0.0000001f)
                    for (int i = 0; i < vertices.Count; i++)
                    {
                        if (i == border.From || i == border.To) continue;
                        float3 point = vertices[i];
                        float t = math.dot(point.xz - a.xz, direction) / length;
                        if (t <= 0.00001f || t >= 0.99999f ||
                            math.distancesq(point.xz, math.lerp(a.xz, b.xz, t)) > 0.00000225f ||
                            math.abs(point.y - math.lerp(a.y, b.y, t)) > 0.01f) continue;
                        points.Add(new KeyValuePair<float, int>(t, i));
                    }
                points.Sort((x, y) => x.Key.CompareTo(y.Key));
                for (int i = 1; i < points.Count; i++) AddLaneMaskBorder(split, points[i - 1].Value, points[i].Value);
            }
            SurfacePatch result = CopySurfacePatchProperties(source);
            Dictionary<int, List<int>> outgoing = new Dictionary<int, List<int>>();
            foreach (LaneMaskBorder border in split.Values)
            {
                if (!outgoing.TryGetValue(border.From, out List<int> ends)) outgoing.Add(border.From, ends = new List<int>());
                ends.Add(border.To);
            }
            HashSet<long> remaining = new HashSet<long>(split.Keys);
            double expectedArea = 0, actualArea = 0;
            foreach (List<float3> fragment in fragments) expectedArea += LanePolygonArea(fragment);
            while (remaining.Count > 0)
            {
                long firstKey = 0; foreach (long key in remaining) { firstKey = key; break; }
                LaneMaskBorder first = split[firstKey];
                List<float3> boundary = new List<float3> { vertices[first.From] };
                int previous = first.From, current = first.To;
                remaining.Remove(firstKey);
                int limit = split.Count + 1;
                while (current != first.From && --limit > 0)
                {
                    boundary.Add(vertices[current]);
                    if (!outgoing.TryGetValue(current, out List<int> candidates)) return LaneMaskFragmentFallback(source, fragments);
                    int next = -1; float bestTurn = float.NegativeInfinity;
                    float2 incoming = vertices[current].xz - vertices[previous].xz;
                    foreach (int candidate in candidates)
                    {
                        if (!remaining.Contains(LaneMaskBorderKey(current, candidate))) continue;
                        float2 direction = vertices[candidate].xz - vertices[current].xz;
                        float turn = math.atan2(Cross2(incoming, direction), math.dot(incoming, direction));
                        if (turn > bestTurn) { bestTurn = turn; next = candidate; }
                    }
                    if (next < 0) return LaneMaskFragmentFallback(source, fragments);
                    remaining.Remove(LaneMaskBorderKey(current, next)); previous = current; current = next;
                }
                double area = LanePolygonArea(boundary);
                // Native areas have one contour. A hole must be decomposed,
                // never reversed and filled as if it were an outer boundary.
                if (limit <= 0 || area <= 0.000005) return LaneMaskFragmentFallback(source, fragments);
                SurfacePatch part = TriangulateBoundary(boundary);
                if (part.Triangles.Count == 0) return LaneMaskFragmentFallback(source, fragments);
                part.ExactBoundary = source.ExactBoundary; part.SmallRoundabout = source.SmallRoundabout;
                part.ProjectionMinimum = source.ProjectionMinimum; part.ProjectionMaximum = source.ProjectionMaximum;
                part.DedicatedLanesClipped = true;
                result.Parts.Add(part); actualArea += area;
            }
            if (math.abs((float)(actualArea - expectedArea)) > math.max(0.002f, (float)expectedArea * 0.005f))
                return LaneMaskFragmentFallback(source, fragments);
            result.DedicatedLanesClipped = true;
            return result.Parts.Count == 1 ? result.Parts[0] : result;
        }

        private SurfacePatch LaneMaskFragmentFallback(SurfacePatch source, List<List<float3>> fragments)
        {
            // Recover the union in one operation before attempting the legacy
            // pairwise shared-edge join (which is sensitive to T junctions).
            SurfacePatch fragmentMesh = CopySurfacePatchProperties(source);
            bool valid = true;
            foreach (List<float3> fragment in fragments)
            {
                SurfacePatch part = TriangulateBoundary(fragment);
                if (part.Triangles.Count == 0 && LanePolygonArea(fragment) > 0.00005d) { valid = false; break; }
                fragmentMesh.Parts.Add(part);
            }
            if (valid && TryClipSurfaceFootprint(fragmentMesh, new List<LaneMaskCell>(), out SurfacePatch compact)) return compact;
            // A native area cannot contain a hole. Join adjacent fragments into
            // simple contours instead of exporting every clipped triangle as a
            // separate decal. A join that would close a hole stays split.
            List<List<float3>> joined = JoinLaneMaskFragments(fragments);
            SurfacePatch result = CopySurfacePatchProperties(source);
            foreach (List<float3> fragment in joined)
            {
                SurfacePatch part = TriangulateBoundary(fragment);
                if (part.Triangles.Count == 0) continue;
                part.ExactBoundary = source.ExactBoundary; part.SmallRoundabout = source.SmallRoundabout;
                part.ProjectionMinimum = source.ProjectionMinimum; part.ProjectionMaximum = source.ProjectionMaximum;
                part.DedicatedLanesClipped = true;
                result.Parts.Add(part);
            }
            result.DedicatedLanesClipped = true;
            if (m_LoggedLaneMaskFallbacks++ < 8)
                Mod.Log.Info("Lane mask contour partition: fragments=" + fragments.Count + "; joined areas=" + result.Parts.Count +
                    ". Reserved lane holes remain uncovered.");
            return result;
        }

        private static List<List<float3>> JoinLaneMaskFragments(List<List<float3>> fragments)
        {
            List<float3> vertices = new List<float3>();
            Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
            List<List<int>> contours = new List<List<int>>();
            float2 origin = fragments[0][0].xz;
            foreach (List<float3> fragment in fragments)
            {
                List<int> contour = new List<int>();
                foreach (float3 point in fragment)
                {
                    int index = WeldLaneVertex(point, origin, vertices, grid);
                    if (contour.Count == 0 || contour[contour.Count - 1] != index) contour.Add(index);
                }
                if (contour.Count > 1 && contour[0] == contour[contour.Count - 1]) contour.RemoveAt(contour.Count - 1);
                if (contour.Count >= 3) contours.Add(contour);
            }
            // Give both sides of each partial shared edge identical nodes.
            for (int c = 0; c < contours.Count; c++)
            {
                List<int> contour = contours[c], split = new List<int>();
                for (int j = 0; j < contour.Count; j++)
                {
                    int from = contour[j], to = contour[(j + 1) % contour.Count];
                    float3 a = vertices[from], b = vertices[to];
                    float2 direction = b.xz - a.xz;
                    float length = math.lengthsq(direction);
                    List<KeyValuePair<float, int>> points = new List<KeyValuePair<float, int>> {
                        new KeyValuePair<float, int>(0f, from)
                    };
                    if (length > 0.0000001f)
                        for (int i = 0; i < vertices.Count; i++)
                        {
                            if (i == from || i == to) continue;
                            float t = math.dot(vertices[i].xz - a.xz, direction) / length;
                            if (t <= 0.00001f || t >= 0.99999f ||
                                math.distancesq(vertices[i].xz, math.lerp(a.xz, b.xz, t)) > 0.00000225f ||
                                math.abs(vertices[i].y - math.lerp(a.y, b.y, t)) > 0.0015f) continue;
                            points.Add(new KeyValuePair<float, int>(t, i));
                        }
                    points.Sort((x, y) => x.Key.CompareTo(y.Key));
                    foreach (KeyValuePair<float, int> point in points)
                        if (split.Count == 0 || split[split.Count - 1] != point.Value) split.Add(point.Value);
                }
                contours[c] = split;
            }
            bool changed;
            do
            {
                changed = false;
                Dictionary<long, int> owners = new Dictionary<long, int>();
                HashSet<long> attempted = new HashSet<long>();
                for (int i = 0; i < contours.Count; i++)
                {
                    List<int> a = contours[i];
                    if (a == null) continue;
                    for (int j = 0; j < a.Count; j++)
                    {
                        int from = a[j], to = a[(j + 1) % a.Count];
                        long key = LaneMaskBorderKey(from, to);
                        if (owners.TryGetValue(LaneMaskBorderKey(to, from), out int other) && contours[other] != null)
                        {
                            long pair = SurfaceMeshEdgeKey(i, other);
                            if (attempted.Add(pair) && TryJoinLaneContours(contours[other], a, vertices, out List<int> merged))
                            {
                                contours[other] = merged; contours[i] = null; changed = true; break;
                            }
                        }
                        owners[key] = i;
                    }
                    if (changed) break;
                }
            } while (changed);
            List<List<float3>> result = new List<List<float3>>();
            double expected = 0, actual = 0;
            foreach (List<float3> fragment in fragments) expected += LanePolygonArea(fragment);
            foreach (List<int> contour in contours)
            {
                if (contour == null) continue;
                List<float3> boundary = new List<float3>(); foreach (int index in contour) boundary.Add(vertices[index]);
                if (TriangulateBoundary(boundary).Triangles.Count == 0) return fragments;
                actual += LanePolygonArea(boundary); result.Add(boundary);
            }
            return result.Count > 0 && math.abs((float)(expected - actual)) <= math.max(0.002f, (float)expected * 0.005f) ? result : fragments;
        }

        private static bool TryJoinLaneContours(List<int> a, List<int> b, List<float3> vertices, out List<int> result)
        {
            result = null;
            Dictionary<long, LaneMaskBorder> borders = new Dictionary<long, LaneMaskBorder>();
            foreach (List<int> contour in new[] { a, b })
                for (int i = 0; i < contour.Count; i++) AddLaneMaskBorder(borders, contour[i], contour[(i + 1) % contour.Count]);
            if (borders.Count < 3 || borders.Count > 384) return false;
            Dictionary<int, int> next = new Dictionary<int, int>();
            foreach (LaneMaskBorder border in borders.Values)
            {
                if (next.ContainsKey(border.From)) return false;
                next.Add(border.From, border.To);
            }
            int first = a[0];
            if (!next.ContainsKey(first)) { foreach (int index in next.Keys) { first = index; break; } }
            List<int> merged = new List<int>();
            int current = first;
            do
            {
                if (!next.TryGetValue(current, out int end) || merged.Count >= borders.Count) return false;
                merged.Add(current); current = end;
            } while (current != first);
            if (merged.Count != borders.Count) return false; // More than one contour: a hole or disconnected pieces.
            List<float3> boundary = new List<float3>(); foreach (int index in merged) boundary.Add(vertices[index]);
            if (LanePolygonArea(boundary) <= 0.000005 || TriangulateBoundary(boundary).Triangles.Count == 0) return false;
            result = merged; return true;
        }

        private static long LaneMaskBorderKey(int from, int to) => ((long)from << 32) | (uint)to;

        private static void AddLaneMaskBorder(Dictionary<long, LaneMaskBorder> borders, int from, int to)
        {
            if (from == to) return;
            long reverse = LaneMaskBorderKey(to, from);
            if (borders.Remove(reverse)) return;
            borders[LaneMaskBorderKey(from, to)] = new LaneMaskBorder { From = from, To = to };
        }

        private static int WeldLaneVertex(float3 point, float2 origin, List<float3> vertices, Dictionary<long, List<int>> grid)
        {
            int2 position = (int2)math.floor((point.xz - origin) * 100f);
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    if (grid.TryGetValue(((long)(position.x + x) << 32) | (uint)(position.y + y), out List<int> candidates))
                        foreach (int candidate in candidates)
                            if (math.distancesq(point.xz, vertices[candidate].xz) <= 0.00000225f &&
                                math.abs(point.y - vertices[candidate].y) <= 0.0015f) return candidate;
            long key = ((long)position.x << 32) | (uint)position.y;
            if (!grid.TryGetValue(key, out List<int> bucket)) grid.Add(key, bucket = new List<int>());
            int index = vertices.Count; vertices.Add(point); bucket.Add(index); return index;
        }
    }
}
