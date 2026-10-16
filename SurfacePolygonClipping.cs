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
        private const int SurfacePolygonNodeLimit = 128;
        private int m_LoggedCompactFootprints, m_LoggedCompactFootprintFailures;
        private int m_CompactFootprintPasses, m_CompactFootprintRegions, m_CompactFootprintFallbacks;

        private bool TryClipSurfaceFootprint(SurfacePatch source, List<LaneMaskCell> cells,
            out SurfacePatch result, bool intersection = false)
        {
            result = null;
            SurfacePatch mesh = source.Parts.Count > 0 ? FlattenSurfacePatchMesh(source) : source;
            if (mesh.Triangles.Count == 0 || mesh.Count < 4) return false;
            Paths64 subjects = new Paths64(), masks = new Paths64();
            foreach (int3 face in mesh.Triangles)
                AddSurfacePolygonTriangle(subjects, mesh.Nodes[face.x].m_Position,
                    mesh.Nodes[face.y].m_Position, mesh.Nodes[face.z].m_Position);
            if (subjects.Count == 0) return false;
            float3 low = new float3(float.PositiveInfinity), high = new float3(float.NegativeInfinity);
            foreach (var node in mesh.Nodes) { low = math.min(low, node.m_Position); high = math.max(high, node.m_Position); }
            foreach (LaneMaskCell cell in cells)
                if (LaneCellOverlaps(low, high, cell))
                    AddSurfacePolygonTriangle(masks, cell.Points[0], cell.Points[1], cell.Points[2]);

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
