using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        // Boolean boundaries stay in local double coordinates until the final
        // native area is created. Internal ribbon/triangle edges never become
        // decal boundaries merely because they intersect a lane mask.
        private struct MaskPoint
        {
            internal double X, Z;
            internal MaskPoint(double x, double z) { X = x; Z = z; }
            public static MaskPoint operator +(MaskPoint a, MaskPoint b) => new MaskPoint(a.X + b.X, a.Z + b.Z);
            public static MaskPoint operator -(MaskPoint a, MaskPoint b) => new MaskPoint(a.X - b.X, a.Z - b.Z);
            public static MaskPoint operator *(MaskPoint a, double b) => new MaskPoint(a.X * b, a.Z * b);
        }

        private sealed class MaskSegment
        {
            internal MaskPoint A, B;
            internal readonly List<double> Cuts = new List<double> { 0d, 1d };
            internal MaskSegment(MaskPoint a, MaskPoint b) { A = a; B = b; }
        }

        private sealed class MaskRegion
        {
            internal readonly List<List<MaskPoint>> Loops = new List<List<MaskPoint>>();
        }

        private int m_LoggedContourMasks;
        private const double MaskProbe = 0.00001d;
        private const double MaskWeld = 0.000001d;

        private bool TrySubtractLaneContours(SurfacePatch source, List<LaneMaskCell> cells, out SurfacePatch result)
        {
            result = null;
            if (source.Parts.Count > 0 || source.Triangles.Count == 0) return false;
            List<float3> outline = new List<float3>();
            foreach (var vertex in source.Nodes) outline.Add(vertex.m_Position);
            List<LaneMaskCell> active = new List<LaneMaskCell>();
            foreach (LaneMaskCell cell in cells) if (LaneCellOverlaps(outline, cell)) active.Add(cell);
            float3 origin = source.Nodes[0].m_Position;
            List<MaskPoint[]> sourceTriangles = new List<MaskPoint[]>();
            foreach (int3 face in source.Triangles)
                AddMaskTriangle(sourceTriangles, ToMaskPoint(source.Nodes[face.x].m_Position, origin),
                    ToMaskPoint(source.Nodes[face.y].m_Position, origin), ToMaskPoint(source.Nodes[face.z].m_Position, origin));
            List<MaskPoint[]> triangles = new List<MaskPoint[]>();
            foreach (LaneMaskCell cell in active)
                AddMaskTriangle(triangles, ToMaskPoint(cell.Points[0], origin), ToMaskPoint(cell.Points[1], origin),
                    ToMaskPoint(cell.Points[2], origin));
            List<MaskSegment> sourceEdges = MeshUnionBoundary(sourceTriangles);
            List<MaskSegment> union = MeshUnionBoundary(triangles);
            foreach (MaskSegment a in sourceEdges) foreach (MaskSegment b in union) SplitMaskSegments(a, b);
            List<MaskSegment> difference = new List<MaskSegment>();
            foreach (MaskSegment edge in sourceEdges)
                foreach (MaskSegment part in MaskSegmentParts(edge))
                    AddMeshDifferenceEdge(part, sourceTriangles, triangles, difference);
            foreach (MaskSegment edge in union)
                foreach (MaskSegment part in MaskSegmentParts(edge))
                    AddMeshDifferenceEdge(part, sourceTriangles, triangles, difference);
            if (!TraceMaskLoops(difference, out List<List<MaskPoint>> loops)) return false;
            if (loops.Count == 0) { result = CopySurfacePatchProperties(source); MarkLaneMasksApplied(result); return true; }
            double expected = 0d; foreach (List<MaskPoint> loop in loops) expected += MaskLoopArea(loop);
            if (expected < -0.000005d || !PartitionMaskRegions(loops, out List<List<MaskPoint>> contours)) return false;
            SurfacePatch merged = CopySurfacePatchProperties(source);
            double actual = 0d;
            foreach (List<MaskPoint> contour in contours)
            {
                List<float3> boundary = new List<float3>();
                foreach (MaskPoint point in contour)
                    boundary.Add(new float3((float)(origin.x + point.X), SampleMaskDeckHeight(source, origin, point), (float)(origin.z + point.Z)));
                SurfacePatch part = TriangulateBoundary(boundary);
                if (part.Triangles.Count == 0)
                {
                    // Sub-centimetre remnants can collapse when doubles become
                    // native float nodes. Do not export unstable outline slivers.
                    if (MaskLoopArea(contour) <= 0.00005d) continue;
                    return false;
                }
                part.ExactBoundary = source.ExactBoundary; part.SmallRoundabout = source.SmallRoundabout;
                part.ProjectionMinimum = source.ProjectionMinimum; part.ProjectionMaximum = source.ProjectionMaximum;
                part.DedicatedLanesClipped = true;
                foreach (int3 t in part.Triangles)
                    actual += Math.Abs(LaneCellSide(part.Nodes[t.z].m_Position, part.Nodes[t.x].m_Position, part.Nodes[t.y].m_Position)) * 0.5d;
                merged.Parts.Add(part);
            }
            if (Math.Abs(actual - expected) > Math.Max(0.002d, expected * 0.001d)) return false;
            MarkLaneMasksApplied(merged);
            result = merged.Parts.Count == 1 ? merged.Parts[0] : merged;
            if (m_LoggedContourMasks++ < 16)
                Mod.Log.Info("Lane contour subtraction: source triangles=" + source.Triangles.Count + "; mask cells=" + active.Count +
                    "; output areas=" + merged.Parts.Count + "; footprint=" + actual.ToString("0.###") + " m2. Internal mesh edges omitted.");
            return true;
        }

        private static void AddMaskTriangle(List<MaskPoint[]> triangles, MaskPoint a, MaskPoint b, MaskPoint c)
        {
            double area = MaskCross(b - a, c - a);
            if (Math.Abs(area) > 0.000000001d) triangles.Add(area > 0d ? new[] { a, b, c } : new[] { a, c, b });
        }

        private static List<MaskSegment> MeshUnionBoundary(List<MaskPoint[]> triangles)
        {
            List<MaskPoint> vertices = new List<MaskPoint>();
            Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
            Dictionary<long, LaneMaskBorder> raw = new Dictionary<long, LaneMaskBorder>();
            foreach (MaskPoint[] triangle in triangles)
            {
                int a = WeldMaskPoint(triangle[0], vertices, grid), b = WeldMaskPoint(triangle[1], vertices, grid),
                    c = WeldMaskPoint(triangle[2], vertices, grid);
                AddLaneMaskBorder(raw, a, b); AddLaneMaskBorder(raw, b, c); AddLaneMaskBorder(raw, c, a);
            }
            List<MaskSegment> edges = new List<MaskSegment>();
            foreach (LaneMaskBorder edge in raw.Values) edges.Add(new MaskSegment(vertices[edge.From], vertices[edge.To]));
            for (int i = 0; i < edges.Count; i++)
                for (int j = i + 1; j < edges.Count; j++) SplitMaskSegments(edges[i], edges[j]);
            List<MaskSegment> boundary = new List<MaskSegment>();
            foreach (MaskSegment edge in edges)
                foreach (MaskSegment part in MaskSegmentParts(edge))
                {
                    MaskPoint mid = (part.A + part.B) * 0.5d, probe = MaskLeftProbe(part);
                    bool left = InsideMaskTriangles(mid + probe, triangles), right = InsideMaskTriangles(mid - probe, triangles);
                    if (left != right) boundary.Add(left ? part : new MaskSegment(part.B, part.A));
                }
            return boundary;
        }

        private static void AddMeshDifferenceEdge(MaskSegment edge, List<MaskPoint[]> source,
            List<MaskPoint[]> masks, List<MaskSegment> result)
        {
            MaskPoint mid = (edge.A + edge.B) * 0.5d, probe = MaskLeftProbe(edge);
            bool left = InsideMaskTriangles(mid + probe, source) && !InsideMaskTriangles(mid + probe, masks);
            bool right = InsideMaskTriangles(mid - probe, source) && !InsideMaskTriangles(mid - probe, masks);
            if (left != right) result.Add(left ? edge : new MaskSegment(edge.B, edge.A));
        }

        private static MaskPoint ToMaskPoint(float3 point, float3 origin) => new MaskPoint((double)point.x - origin.x, (double)point.z - origin.z);
        private static double MaskCross(MaskPoint a, MaskPoint b) => a.X * b.Z - a.Z * b.X;
        private static double MaskDot(MaskPoint a, MaskPoint b) => a.X * b.X + a.Z * b.Z;
        private static MaskPoint MaskLeftProbe(MaskSegment edge)
        {
            MaskPoint d = edge.B - edge.A;
            return new MaskPoint(-d.Z, d.X) * (MaskProbe / Math.Max(0.0000001d, Math.Sqrt(MaskDot(d, d))));
        }

        private static void SplitMaskSegments(MaskSegment a, MaskSegment b)
        {
            if (Math.Max(a.A.X, a.B.X) < Math.Min(b.A.X, b.B.X) - MaskProbe ||
                Math.Max(b.A.X, b.B.X) < Math.Min(a.A.X, a.B.X) - MaskProbe ||
                Math.Max(a.A.Z, a.B.Z) < Math.Min(b.A.Z, b.B.Z) - MaskProbe ||
                Math.Max(b.A.Z, b.B.Z) < Math.Min(a.A.Z, a.B.Z) - MaskProbe) return;
            MaskPoint r = a.B - a.A, s = b.B - b.A, offset = b.A - a.A;
            double denominator = MaskCross(r, s);
            double rr = MaskDot(r, r), ss = MaskDot(s, s);
            if (rr < 0.000000000001d || ss < 0.000000000001d) return;
            if (Math.Abs(denominator) > 0.000000000001d * Math.Max(1d, Math.Sqrt(rr * ss)))
            {
                double t = MaskCross(offset, s) / denominator, u = MaskCross(offset, r) / denominator;
                if (t >= -0.000000001d && t <= 1.000000001d && u >= -0.000000001d && u <= 1.000000001d)
                { a.Cuts.Add(Math.Max(0d, Math.Min(1d, t))); b.Cuts.Add(Math.Max(0d, Math.Min(1d, u))); }
            }
            else if (Math.Abs(MaskCross(offset, r)) <= 0.00000001d * Math.Max(1d, Math.Sqrt(rr)))
            {
                AddMaskCut(a, MaskDot(b.A - a.A, r) / rr); AddMaskCut(a, MaskDot(b.B - a.A, r) / rr);
                AddMaskCut(b, MaskDot(a.A - b.A, s) / ss); AddMaskCut(b, MaskDot(a.B - b.A, s) / ss);
            }
        }

        private static void AddMaskCut(MaskSegment edge, double t) { if (t > 0d && t < 1d) edge.Cuts.Add(t); }
        private static List<MaskSegment> MaskSegmentParts(MaskSegment edge)
        {
            edge.Cuts.Sort();
            List<MaskSegment> result = new List<MaskSegment>();
            double previous = edge.Cuts[0];
            for (int i = 1; i < edge.Cuts.Count; i++)
            {
                double t = edge.Cuts[i];
                if (t - previous <= 0.0000000001d) continue;
                MaskPoint a = edge.A + (edge.B - edge.A) * previous, b = edge.A + (edge.B - edge.A) * t;
                if (MaskDot(b - a, b - a) > 0.000000000001d) result.Add(new MaskSegment(a, b));
                previous = t;
            }
            return result;
        }

        private static bool InsideMaskTriangles(MaskPoint p, List<MaskPoint[]> triangles)
        {
            foreach (MaskPoint[] t in triangles)
                if (p.X >= Math.Min(t[0].X, Math.Min(t[1].X, t[2].X)) && p.X <= Math.Max(t[0].X, Math.Max(t[1].X, t[2].X)) &&
                    p.Z >= Math.Min(t[0].Z, Math.Min(t[1].Z, t[2].Z)) && p.Z <= Math.Max(t[0].Z, Math.Max(t[1].Z, t[2].Z)) &&
                    MaskCross(t[1] - t[0], p - t[0]) >= -0.0000000001d &&
                    MaskCross(t[2] - t[1], p - t[1]) >= -0.0000000001d &&
                    MaskCross(t[0] - t[2], p - t[2]) >= -0.0000000001d) return true;
            return false;
        }

        private static bool InsideMaskLoop(MaskPoint p, List<MaskPoint> loop)
        {
            bool inside = false;
            MaskPoint a = loop[loop.Count - 1];
            foreach (MaskPoint b in loop)
            {
                if ((a.Z > p.Z) != (b.Z > p.Z) && p.X < a.X + (b.X - a.X) * (p.Z - a.Z) / (b.Z - a.Z)) inside = !inside;
                a = b;
            }
            return inside;
        }

        private static List<MaskSegment> MaskLoopSegments(List<MaskPoint> loop)
        {
            List<MaskSegment> result = new List<MaskSegment>();
            for (int i = 0; i < loop.Count; i++) result.Add(new MaskSegment(loop[i], loop[(i + 1) % loop.Count]));
            return result;
        }

        private static double MaskLoopArea(List<MaskPoint> loop)
        {
            double result = 0d;
            for (int i = 1; i + 1 < loop.Count; i++) result += MaskCross(loop[i] - loop[0], loop[i + 1] - loop[0]) * 0.5d;
            return result;
        }

        private static int WeldMaskPoint(MaskPoint point, List<MaskPoint> vertices, Dictionary<long, List<int>> grid)
        {
            int x = (int)Math.Floor(point.X / MaskWeld), z = (int)Math.Floor(point.Z / MaskWeld);
            for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)
                if (grid.TryGetValue(((long)(x + dx) << 32) | (uint)(z + dz), out List<int> candidates))
                    foreach (int index in candidates)
                        if (MaskDot(vertices[index] - point, vertices[index] - point) <= MaskWeld * MaskWeld) return index;
            long key = ((long)x << 32) | (uint)z;
            if (!grid.TryGetValue(key, out List<int> bucket)) grid.Add(key, bucket = new List<int>());
            int result = vertices.Count; vertices.Add(point); bucket.Add(result); return result;
        }

        private static bool TraceMaskLoops(List<MaskSegment> segments, out List<List<MaskPoint>> loops)
        {
            loops = new List<List<MaskPoint>>();
            List<MaskPoint> vertices = new List<MaskPoint>();
            Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
            Dictionary<long, LaneMaskBorder> edges = new Dictionary<long, LaneMaskBorder>();
            foreach (MaskSegment segment in segments)
                AddLaneMaskBorder(edges, WeldMaskPoint(segment.A, vertices, grid), WeldMaskPoint(segment.B, vertices, grid));
            Dictionary<int, List<int>> outgoing = new Dictionary<int, List<int>>();
            foreach (LaneMaskBorder edge in edges.Values)
            {
                if (!outgoing.TryGetValue(edge.From, out List<int> ends)) outgoing.Add(edge.From, ends = new List<int>());
                ends.Add(edge.To);
            }
            HashSet<long> remaining = new HashSet<long>(edges.Keys);
            while (remaining.Count > 0)
            {
                long firstKey = 0; foreach (long key in remaining) { firstKey = key; break; }
                LaneMaskBorder first = edges[firstKey];
                List<MaskPoint> loop = new List<MaskPoint> { vertices[first.From] };
                int previous = first.From, current = first.To, limit = edges.Count + 1;
                remaining.Remove(firstKey);
                while (current != first.From && --limit > 0)
                {
                    loop.Add(vertices[current]);
                    if (!outgoing.TryGetValue(current, out List<int> candidates)) return false;
                    int next = -1; double best = double.NegativeInfinity;
                    MaskPoint incoming = vertices[current] - vertices[previous];
                    foreach (int candidate in candidates)
                    {
                        if (!remaining.Contains(LaneMaskBorderKey(current, candidate))) continue;
                        MaskPoint direction = vertices[candidate] - vertices[current];
                        double turn = Math.Atan2(MaskCross(incoming, direction), MaskDot(incoming, direction));
                        if (turn > best) { best = turn; next = candidate; }
                    }
                    if (next < 0) return false;
                    remaining.Remove(LaneMaskBorderKey(current, next)); previous = current; current = next;
                }
                if (limit <= 0) return false;
                if (Math.Abs(MaskLoopArea(loop)) > 0.000005d) loops.Add(loop);
            }
            return true;
        }

        private static bool GroupMaskRegions(List<List<MaskPoint>> loops, out List<MaskRegion> regions)
        {
            regions = new List<MaskRegion>();
            foreach (List<MaskPoint> loop in loops) if (MaskLoopArea(loop) > 0d)
            { MaskRegion region = new MaskRegion(); region.Loops.Add(loop); regions.Add(region); }
            foreach (List<MaskPoint> hole in loops)
            {
                if (MaskLoopArea(hole) >= 0d) continue;
                MaskSegment firstEdge = new MaskSegment(hole[0], hole[1]);
                MaskPoint probe = (firstEdge.A + firstEdge.B) * 0.5d - MaskLeftProbe(firstEdge);
                MaskRegion owner = null; double smallest = double.PositiveInfinity;
                foreach (MaskRegion region in regions)
                {
                    double area = MaskLoopArea(region.Loops[0]);
                    if (area < smallest && InsideMaskLoop(probe, region.Loops[0])) { smallest = area; owner = region; }
                }
                if (owner == null) return false;
                owner.Loops.Add(hole);
            }
            return true;
        }

        private static bool PartitionMaskRegions(List<List<MaskPoint>> loops, out List<List<MaskPoint>> contours)
        {
            contours = new List<List<MaskPoint>>();
            if (!GroupMaskRegions(loops, out List<MaskRegion> regions)) return false;
            int cuts = 0;
            for (int i = 0; i < regions.Count; i++)
            {
                MaskRegion region = regions[i];
                if (region.Loops.Count == 1) { contours.Add(region.Loops[0]); continue; }
                if (++cuts > 32 || regions.Count > 96) return false;
                double low = double.PositiveInfinity, high = double.NegativeInfinity;
                foreach (MaskPoint p in region.Loops[1]) { low = Math.Min(low, p.X); high = Math.Max(high, p.X); }
                if (high - low < MaskWeld * 4d) return false;
                double plane = (low + high) * 0.5d;
                for (int side = 0; side < 2; side++)
                {
                    if (!ClipMaskRegion(region, plane, side == 0, out List<List<MaskPoint>> clipped) ||
                        !GroupMaskRegions(clipped, out List<MaskRegion> pieces)) return false;
                    regions.AddRange(pieces);
                }
            }
            return true;
        }

        private static bool ClipMaskRegion(MaskRegion region, double plane, bool left, out List<List<MaskPoint>> loops)
        {
            List<MaskSegment> edges = new List<MaskSegment>();
            List<double> crossings = new List<double>();
            foreach (List<MaskPoint> loop in region.Loops)
                foreach (MaskSegment edge in MaskLoopSegments(loop))
                {
                    double sa = (edge.A.X - plane) * (left ? -1d : 1d), sb = (edge.B.X - plane) * (left ? -1d : 1d);
                    if (Math.Abs(sa) < 0.000000001d) crossings.Add(edge.A.Z);
                    if (Math.Abs(sb) < 0.000000001d) crossings.Add(edge.B.Z);
                    if (Math.Abs(sa) < 0.000000001d && Math.Abs(sb) < 0.000000001d) continue;
                    if ((sa >= 0d) != (sb >= 0d))
                    {
                        MaskPoint point = edge.A + (edge.B - edge.A) * (sa / (sa - sb));
                        point.X = plane; crossings.Add(point.Z);
                        if (sa >= 0d) edges.Add(new MaskSegment(edge.A, point));
                        else edges.Add(new MaskSegment(point, edge.B));
                    }
                    else if (sa >= 0d && sb >= 0d) edges.Add(edge);
                }
            crossings.Sort();
            for (int i = 1; i < crossings.Count; i++)
            {
                double from = crossings[i - 1], to = crossings[i];
                if (to - from <= MaskWeld) continue;
                MaskPoint mid = new MaskPoint(plane + (left ? -MaskProbe : MaskProbe), (from + to) * 0.5d);
                bool inside = false; foreach (List<MaskPoint> loop in region.Loops) if (InsideMaskLoop(mid, loop)) inside = !inside;
                if (inside) edges.Add(left ? new MaskSegment(new MaskPoint(plane, from), new MaskPoint(plane, to)) :
                    new MaskSegment(new MaskPoint(plane, to), new MaskPoint(plane, from)));
            }
            return TraceMaskLoops(edges, out loops);
        }

        private static float SampleMaskDeckHeight(SurfacePatch source, float3 origin, MaskPoint point)
        {
            foreach (int3 triangle in source.Triangles)
            {
                float3 a = source.Nodes[triangle.x].m_Position, b = source.Nodes[triangle.y].m_Position, c = source.Nodes[triangle.z].m_Position;
                MaskPoint pa = ToMaskPoint(a, origin), ab = ToMaskPoint(b, origin) - pa, ac = ToMaskPoint(c, origin) - pa;
                double denominator = MaskCross(ab, ac);
                if (Math.Abs(denominator) <= 0.000000001d) continue;
                double u = MaskCross(point - pa, ac) / denominator, v = MaskCross(ab, point - pa) / denominator;
                if (u >= -0.00001d && v >= -0.00001d && u + v <= 1.00001d) return (float)(a.y + u * (b.y - a.y) + v * (c.y - a.y));
            }
            double closest = double.PositiveInfinity; float height = origin.y;
            for (int i = 0; i < source.Count - 1; i++)
            {
                float3 a = source.Nodes[i].m_Position, b = source.Nodes[i + 1].m_Position;
                MaskPoint pa = ToMaskPoint(a, origin), d = ToMaskPoint(b, origin) - pa;
                double t = Math.Max(0d, Math.Min(1d, MaskDot(point - pa, d) / Math.Max(0.000000000001d, MaskDot(d, d))));
                MaskPoint delta = point - (pa + d * t); double distance = MaskDot(delta, delta);
                if (distance < closest) { closest = distance; height = (float)(a.y + t * (b.y - a.y)); }
            }
            return height;
        }
    }
}
