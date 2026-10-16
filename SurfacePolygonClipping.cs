using System;
using System.Collections.Generic;
using Clipper2Lib;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        // Clip the union of the mesh, rather than turning every cut triangle
        // into a native area. Integer coordinates also make adjacent cuts share
        // exactly the same endpoints. One unit is 0.1 mm, below native float
        // precision over most of the map. There is no polygon inflation here.
        private const double SurfacePolygonScale = 10000d;
        private const int SurfacePolygonNodeLimit = 256;
        private const double SurfacePolygonSeamTolerance = 0.001d;
        private int m_LoggedCompactFootprints, m_LoggedCompactFootprintFailures;
        private int m_CompactFootprintPasses, m_CompactFootprintRegions, m_CompactFootprintFallbacks;

        private sealed class SurfaceMaskContour
        {
            internal Path64 Boundary;
            internal int CellCount;
        }

        private bool TryClipSurfaceFootprint(SurfacePatch source, List<LaneMaskCell> cells,
            out SurfacePatch result, bool intersection = false)
        {
            result = null;
            SurfacePatch mesh = source.Parts.Count > 0 ? FlattenSurfacePatchMesh(source) : source;
            if (mesh.Triangles.Count == 0 || mesh.Count < 4) return false;
            Paths64 subjects = new Paths64(), masks = new Paths64();
            AddSurfacePolygonPaths(source, subjects);
            if (subjects.Count == 0) return false;
            subjects = RepairSurfacePolygonSeams(subjects);
            float3 low = new float3(float.PositiveInfinity), high = new float3(float.NegativeInfinity);
            foreach (var node in mesh.Nodes) { low = math.min(low, node.m_Position); high = math.max(high, node.m_Position); }
            AddSurfaceMaskPaths(cells, low, high, masks);

            PolyTree64 footprint = new PolyTree64();
            if (!ExecuteSurfacePolygons(subjects, masks,
                intersection ? ClipType.Intersection : ClipType.Difference, footprint))
                return ReportCompactFootprintFailure(mesh, cells.Count, "integer boolean");
            SurfacePatch compact = CopySurfacePatchProperties(source);
            int partitions = 0;
            if (!AppendSurfacePolygonTree(footprint, mesh, compact, 0, ref partitions))
                return ReportCompactFootprintFailure(mesh, cells.Count, "native contour partition");
            double expected = footprint.Area() / (SurfacePolygonScale * SurfacePolygonScale), actual = 0d;
            foreach (SurfacePatch part in compact.Parts)
                foreach (int3 face in part.Triangles)
                    actual += Math.Abs(LaneCellSide(part.Nodes[face.z].m_Position,
                        part.Nodes[face.x].m_Position, part.Nodes[face.y].m_Position)) * 0.5d;
            // Never silently replace a failed contour with a hull, fill a hole,
            // or drop a substantial region. Keep the existing recovery path.
            if (Math.Abs(actual - expected) > Math.Max(0.002d, expected * 0.001d))
                return ReportCompactFootprintFailure(mesh, cells.Count, "footprint area mismatch");
            MarkLaneMasksApplied(compact);
            m_CompactFootprintPasses++; m_CompactFootprintRegions += compact.Parts.Count;
            result = compact.Parts.Count == 1 ? compact.Parts[0] : compact;
            if (m_LoggedCompactFootprints++ < 16)
                Mod.Log.Info("Compact surface footprint: source triangles=" + mesh.Triangles.Count +
                    "; mask cells=" + cells.Count + "; areas=" + compact.Parts.Count +
                    "; hole/complexity cuts=" + partitions + "; footprint=" + actual.ToString("0.###") + " m2.");
            return true;
        }

        private static void AddSurfacePolygonPaths(SurfacePatch patch, Paths64 paths)
        {
            foreach (SurfacePatch part in patch.Parts) AddSurfacePolygonPaths(part, paths);
            if (TryGetSurfaceBoundaryPath(patch, out Path64 boundary)) { paths.Add(boundary); return; }
            foreach (int3 face in patch.Triangles)
                AddSurfacePolygonTriangle(paths, patch.Nodes[face.x].m_Position,
                    patch.Nodes[face.y].m_Position, patch.Nodes[face.z].m_Position);
        }

        private static Paths64 RepairSurfacePolygonSeams(Paths64 subjects)
        {
            Paths64 footprint = Clipper.Union(subjects, FillRule.NonZero);
            if (footprint.Count < 2) return footprint;
            // Independently clipped mesh cells can disagree at T junctions by
            // one native float unit. Close only millimetre-scale cracks before
            // subtracting the original lane/planting masks. Merging previously
            // clipped plans also preserves every non-numerical original hole.
            double tolerance = SurfacePolygonSeamTolerance * SurfacePolygonScale;
            Paths64 repaired = Clipper.InflatePaths(Clipper.InflatePaths(footprint, tolerance,
                JoinType.Miter, EndType.Polygon), -tolerance, JoinType.Miter, EndType.Polygon);
            if (repaired.Count == 0) return footprint;
            Paths64 holes = new Paths64();
            double perimeter = 0d;
            foreach (Path64 path in footprint)
            {
                for (int i = 0; i < path.Count; i++)
                {
                    Point64 a = path[i], b = path[(i + 1) % path.Count];
                    double x = (double)b.X - a.X, z = (double)b.Y - a.Y;
                    perimeter += Math.Sqrt(x * x + z * z);
                }
                if (Clipper.Area(path) >= 0d || IsNumericalSurfaceHole(path, tolerance)) continue;
                Path64 hole = new Path64(path); hole.Reverse(); holes.Add(hole);
            }
            if (holes.Count > 0) repaired = Clipper.Difference(repaired, holes, FillRule.NonZero);
            // A numerical repair must stay local. Reject any unexpected large
            // footprint change and retain the exact original union instead.
            double changedArea = Math.Abs(Clipper.Area(Clipper.Xor(footprint, repaired, FillRule.NonZero)));
            return changedArea <= perimeter * tolerance * 2d ? repaired : footprint;
        }

        private static bool IsNumericalSurfaceHole(Path64 path, double tolerance)
        {
            if (path.Count < 3) return true;
            Point64 minX = path[0], maxX = path[0], minZ = path[0], maxZ = path[0];
            foreach (Point64 point in path)
            {
                if (point.X < minX.X) minX = point; if (point.X > maxX.X) maxX = point;
                if (point.Y < minZ.Y) minZ = point; if (point.Y > maxZ.Y) maxZ = point;
            }
            Point64 a = minX, b = maxX;
            if ((double)maxZ.Y - minZ.Y > (double)maxX.X - minX.X) { a = minZ; b = maxZ; }
            double x = (double)b.X - a.X, z = (double)b.Y - a.Y, length = x * x + z * z;
            if (length <= tolerance * tolerance) return true;
            foreach (Point64 point in path)
            {
                double cross = x * ((double)point.Y - a.Y) - z * ((double)point.X - a.X);
                if (cross * cross > tolerance * tolerance * length) return false;
            }
            return true;
        }

        private static void AddSurfaceMaskPaths(List<LaneMaskCell> cells, float3 low, float3 high, Paths64 paths)
        {
            Dictionary<SurfaceMaskContour, int> selected = new Dictionary<SurfaceMaskContour, int>();
            foreach (LaneMaskCell cell in cells)
                if (cell.Contour != null && low.y < cell.Maximum.y + 0.12f && high.y > cell.Minimum.y - 0.12f)
                {
                    selected.TryGetValue(cell.Contour, out int count);
                    selected[cell.Contour] = count + 1;
                }
            HashSet<SurfaceMaskContour> emitted = new HashSet<SurfaceMaskContour>();
            foreach (LaneMaskCell cell in cells)
            {
                if (!LaneCellOverlaps(low, high, cell)) continue;
                // Off-footprint X/Z cells cannot affect the boolean, so they
                // need not prevent using the complete mask perimeter. Every
                // cell must still pass the deck-height filter. Partial-height
                // masks keep their individually filtered triangles.
                if (cell.Contour != null && selected[cell.Contour] == cell.Contour.CellCount)
                { if (emitted.Add(cell.Contour)) paths.Add(cell.Contour.Boundary); }
                else AddSurfacePolygonTriangle(paths, cell.Points[0], cell.Points[1], cell.Points[2]);
            }
        }

        private static bool TryGetSurfaceBoundaryPath(SurfacePatch patch, out Path64 boundary)
        {
            boundary = null;
            int count = patch.Count - 1;
            if (count < 3 || patch.Triangles.Count != count - 2 ||
                math.distancesq(patch.Nodes[0].m_Position, patch.Nodes[count].m_Position) > 0.00000001f) return false;
            // A closed node list alone is insufficient: flattened/folded meshes
            // can contain interior nodes. Prove that the indexed mesh has this
            // exact perimeter, and that its faces do not overlap or fold.
            Dictionary<long, int> edges = new Dictionary<long, int>();
            double trianglesArea = 0d;
            foreach (int3 face in patch.Triangles)
            {
                if (math.any(face < 0) || math.any(face >= count)) return false;
                for (int i = 0; i < 3; i++)
                {
                    long key = SurfaceMeshEdgeKey(face[i], face[(i + 1) % 3]);
                    edges.TryGetValue(key, out int uses); edges[key] = uses + 1;
                }
                trianglesArea += Math.Abs(LaneCellSide(patch.Nodes[face.z].m_Position,
                    patch.Nodes[face.x].m_Position, patch.Nodes[face.y].m_Position)) * 0.5d;
            }
            Path64 path = new Path64(count);
            for (int i = 0; i < count; i++)
            {
                long key = SurfaceMeshEdgeKey(i, (i + 1) % count);
                if (!edges.TryGetValue(key, out int uses) || uses != 1 || !math.all(math.isfinite(patch.Nodes[i].m_Position))) return false;
                edges.Remove(key); path.Add(SurfacePolygonPoint(patch.Nodes[i].m_Position));
            }
            foreach (int uses in edges.Values) if (uses != 2) return false;
            double area = Clipper.Area(path);
            if (Math.Abs(area) <= 1d) return false;
            if (Math.Abs(Math.Abs(area) / (SurfacePolygonScale * SurfacePolygonScale) - trianglesArea) >
                Math.Max(0.0001d, trianglesArea * 0.00001d)) return false;
            if (area < 0d) path.Reverse();
            boundary = path; return true;
        }

        private bool ReportCompactFootprintFailure(SurfacePatch mesh, int masks, string reason)
        {
            m_CompactFootprintFallbacks++;
            if (m_LoggedCompactFootprintFailures++ < 8)
                Mod.Log.Warn("Compact footprint retained geometry recovery: stage=" + reason +
                    "; source triangles=" + mesh.Triangles.Count + "; mask cells=" + masks + ".");
            return false;
        }

        private static void AddSurfacePolygonTriangle(Paths64 paths, float3 a, float3 b, float3 c)
        {
            if (!math.all(math.isfinite(a)) || !math.all(math.isfinite(b)) || !math.all(math.isfinite(c))) return;
            Path64 triangle = new Path64(3) { SurfacePolygonPoint(a), SurfacePolygonPoint(b), SurfacePolygonPoint(c) };
            double area = Clipper.Area(triangle);
            if (Math.Abs(area) <= 1d) return;
            if (area < 0d) triangle.Reverse();
            paths.Add(triangle);
        }

        private static Point64 SurfacePolygonPoint(float3 point) =>
            new Point64((double)point.x * SurfacePolygonScale, (double)point.z * SurfacePolygonScale);

        private static bool ExecuteSurfacePolygons(Paths64 subjects, Paths64 masks, ClipType operation, PolyTree64 result)
        {
            Clipper64 clipper = new Clipper64 { PreserveCollinear = false };
            clipper.AddSubject(subjects);
            if (masks.Count > 0) clipper.AddClip(masks);
            // Every input triangle is positive. NonZero is a union even where
            // native lane directions overlap; EvenOdd would punch false holes.
            return clipper.Execute(operation, FillRule.NonZero, result);
        }

        private static bool AppendSurfacePolygonTree(PolyPath64 root, SurfacePatch mesh,
            SurfacePatch result, int depth, ref int partitions)
        {
            for (int i = 0; i < root.Count; i++)
            {
                PolyPath64 outer = root[i];
                if (!AppendSurfacePolygonRegion(outer, mesh, result, depth, ref partitions)) return false;
                // Islands inside holes are independent positive regions.
                for (int h = 0; h < outer.Count; h++)
                    if (!AppendSurfacePolygonTree(outer[h], mesh, result, depth, ref partitions)) return false;
            }
            return true;
        }

        private static bool AppendSurfacePolygonRegion(PolyPath64 outer, SurfacePatch mesh,
            SurfacePatch result, int depth, ref int partitions)
        {
            Path64 outline = outer.Polygon;
            if (outline == null || outline.Count < 3) return true;
            double area = Math.Abs(Clipper.Area(outline)) / (SurfacePolygonScale * SurfacePolygonScale);
            if (area <= 0.00005d) return true;
            if (outer.Count == 0 && outline.Count <= SurfacePolygonNodeLimit)
            {
                float3 origin = mesh.Nodes[0].m_Position;
                List<float3> boundary = new List<float3>(outline.Count);
                foreach (Point64 point in outline)
                {
                    MaskPoint local = new MaskPoint(point.X / SurfacePolygonScale - origin.x,
                        point.Y / SurfacePolygonScale - origin.z);
                    boundary.Add(new float3((float)(point.X / SurfacePolygonScale),
                        SampleMaskDeckHeight(mesh, origin, local), (float)(point.Y / SurfacePolygonScale)));
                }
                SurfacePatch part = TriangulateBoundary(boundary);
                if (part.Triangles.Count > 0)
                {
                    part.ExactBoundary = result.ExactBoundary; part.SmallRoundabout = result.SmallRoundabout;
                    part.ProjectionMinimum = result.ProjectionMinimum; part.ProjectionMaximum = result.ProjectionMaximum;
                    part.PlantedAreasClipped = result.PlantedAreasClipped;
                    result.Parts.Add(part);
                    return true;
                }
            }

            // Native areas accept one simple perimeter, without holes. Open a
            // hole with one straight cut through its middle; ordinary connected
            // junctions stay one area. Very complex/float-degenerate perimeters
            // are subdivided locally, never into every source triangle.
            if (depth >= 20 || ++partitions > 256) return false;
            Rect64 bounds = Clipper.GetBounds(outline);
            Rect64 cutBounds = outer.Count > 0 ? Clipper.GetBounds(outer[0].Polygon) : bounds;
            bool vertical = cutBounds.Width >= cutBounds.Height;
            long low = vertical ? cutBounds.left : cutBounds.top, high = vertical ? cutBounds.right : cutBounds.bottom;
            if (high - low < 2) return false;
            long plane = low + (high - low) / 2;
            Paths64 region = new Paths64 { outline };
            for (int i = 0; i < outer.Count; i++) region.Add(outer[i].Polygon);
            for (int side = 0; side < 2; side++)
            {
                Rect64 half = new Rect64(bounds.left - 1, bounds.top - 1, bounds.right + 1, bounds.bottom + 1);
                if (vertical) { if (side == 0) half.right = plane; else half.left = plane; }
                else { if (side == 0) half.bottom = plane; else half.top = plane; }
                PolyTree64 pieces = new PolyTree64();
                if (!ExecuteSurfacePolygons(region, new Paths64 { half.AsPath() }, ClipType.Intersection, pieces) ||
                    !AppendSurfacePolygonTree(pieces, mesh, result, depth + 1, ref partitions)) return false;
            }
            return true;
        }
    }
}
