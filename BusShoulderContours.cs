using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private bool TryJoinBusShoulderRibbon(SurfacePatch lane, SurfacePatch shoulder, out SurfacePatch result)
        {
            result = null;
            if (shoulder.Triangles.Count == 0 && shoulder.Parts.Count == 0) return false;
            SurfacePatch combined = CopySurfacePatchProperties(shoulder);
            combined.Parts.Add(lane); combined.Parts.Add(shoulder);
            SurfacePatch mesh = FlattenSurfacePatchMesh(combined);
            if (mesh.Triangles.Count > 1024) return false;
            // Union the two meshes directly. Subtracting the lane from the
            // shoulder and then adding it back creates narrow holes/slivers
            // and can invoke the expensive triangle-fragment fallback.
            List<LaneMaskCell> noMasks = new List<LaneMaskCell>();
            return (TrySubtractLaneContours(mesh, noMasks, out result) || TrySweepLaneMasks(mesh, noMasks, out result)) &&
                result.Parts.Count <= 64;
        }

        private bool TryIntersectBusContours(SurfacePatch source, List<LaneMaskCell> cells, out SurfacePatch result)
        {
            result = null;
            if (source.Triangles.Count == 0 || source.Nodes.Count == 0 || source.Triangles.Count > 512 || cells.Count > 4096) return false;
            float3 origin = source.Nodes[0].m_Position;
            List<MaskPoint[]> faces = new List<MaskPoint[]>(), masks = new List<MaskPoint[]>();
            List<float3> bounds = new List<float3>();
            foreach (var vertex in source.Nodes) bounds.Add(vertex.m_Position);
            foreach (int3 face in source.Triangles)
                AddMaskTriangle(faces, ToMaskPoint(source.Nodes[face.x].m_Position, origin),
                    ToMaskPoint(source.Nodes[face.y].m_Position, origin), ToMaskPoint(source.Nodes[face.z].m_Position, origin));
            foreach (LaneMaskCell cell in cells)
                if (LaneCellOverlaps(bounds, cell)) AddMaskTriangle(masks, ToMaskPoint(cell.Points[0], origin),
                    ToMaskPoint(cell.Points[1], origin), ToMaskPoint(cell.Points[2], origin));
            // Remove tessellation edges before finding intersections. The old
            // method intersected every pair of triangles, then attempted to
            // weld thousands of tiny polygons back into a few curved strips.
            List<MaskSegment> ribbon = MeshUnionBoundary(faces), curb = MeshUnionBoundary(masks);
            if (ribbon.Count > 1024 || curb.Count > 2048) return false;
            foreach (MaskSegment a in ribbon) foreach (MaskSegment b in curb) SplitMaskSegments(a, b);
            List<MaskSegment> boundary = new List<MaskSegment>();
            foreach (MaskSegment edge in ribbon)
                foreach (MaskSegment part in MaskSegmentParts(edge)) AddBusIntersectionEdge(part, faces, masks, boundary);
            foreach (MaskSegment edge in curb)
                foreach (MaskSegment part in MaskSegmentParts(edge)) AddBusIntersectionEdge(part, faces, masks, boundary);
            if (!TraceMaskLoops(boundary, out List<List<MaskPoint>> loops)) return false;
            if (loops.Count == 0) { result = CopySurfacePatchProperties(source); return true; }
            double expected = 0d;
            foreach (List<MaskPoint> loop in loops) expected += MaskLoopArea(loop);
            if (expected < -0.000005d || !PartitionMaskRegions(loops, out List<List<MaskPoint>> contours)) return false;
            SurfacePatch joined = CopySurfacePatchProperties(source);
            double actual = 0d;
            foreach (List<MaskPoint> contour in contours)
            {
                if (contour.Count > 2048) return false;
                List<float3> points = new List<float3>();
                foreach (MaskPoint point in contour)
                    points.Add(new float3((float)(origin.x + point.X), SampleMaskDeckHeight(source, origin, point), (float)(origin.z + point.Z)));
                SurfacePatch part = TriangulateBoundary(points);
                if (part.Triangles.Count == 0)
                {
                    if (Math.Abs(MaskLoopArea(contour)) < 0.00005d) continue;
                    return false;
                }
                part.ExactBoundary = source.ExactBoundary;
                part.ProjectionMinimum = source.ProjectionMinimum; part.ProjectionMaximum = source.ProjectionMaximum;
                foreach (int3 face in part.Triangles)
                    actual += Math.Abs(LaneCellSide(part.Nodes[face.z].m_Position, part.Nodes[face.x].m_Position, part.Nodes[face.y].m_Position)) * 0.5d;
                joined.Parts.Add(part);
            }
            if (Math.Abs(actual - expected) > Math.Max(0.002d, expected * 0.001d)) return false;
            MarkLaneMasksApplied(joined);
            result = joined.Parts.Count == 1 ? joined.Parts[0] : joined;
            return true;
        }

        private static void AddBusIntersectionEdge(MaskSegment edge, List<MaskPoint[]> faces,
            List<MaskPoint[]> masks, List<MaskSegment> boundary)
        {
            MaskPoint middle = (edge.A + edge.B) * 0.5d, probe = MaskLeftProbe(edge);
            bool left = InsideMaskTriangles(middle + probe, faces) && InsideMaskTriangles(middle + probe, masks);
            bool right = InsideMaskTriangles(middle - probe, faces) && InsideMaskTriangles(middle - probe, masks);
            if (left != right) boundary.Add(left ? edge : new MaskSegment(edge.B, edge.A));
        }

        private static List<SweepSpan> IntersectBusSweepSpans(List<SweepSpan> sources, List<SweepSpan> masks)
        {
            List<SweepSpan> result = new List<SweepSpan>();
            int i = 0, j = 0;
            while (i < sources.Count && j < masks.Count)
            {
                SweepSpan a = sources[i], b = masks[j];
                bool sourceLow = a.LowZ >= b.LowZ, sourceHigh = a.HighZ <= b.HighZ;
                double low = Math.Max(a.LowZ, b.LowZ), high = Math.Min(a.HighZ, b.HighZ);
                if (high - low > 0.000000001d)
                    result.Add(new SweepSpan(sourceLow ? a.Low : b.Low, sourceHigh ? a.High : b.High, low, high));
                if (a.HighZ <= b.HighZ) i++; else j++;
            }
            return result;
        }
    }
}
