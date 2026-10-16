using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private sealed class SweepSpan
        {
            internal MaskSegment Low, High;
            internal double LowZ, HighZ;
            internal SweepSpan(MaskSegment low, MaskSegment high, double lowZ, double highZ)
            { Low = low; High = high; LowZ = lowZ; HighZ = highZ; }
        }
        private sealed class SweepStrip
        {
            internal readonly List<MaskPoint> Low = new List<MaskPoint>(), High = new List<MaskPoint>();
            internal readonly List<MaskPoint> SampleLow = new List<MaskPoint>(), SampleHigh = new List<MaskPoint>();
            internal double LastLow, LastHigh;
        }
        private int m_LoggedSweepMasks;

        // A plane sweep does not require stitching floating-point cut vertices
        // into a closed graph. Each output follows continuous lower/upper
        // boundaries; only real splits/joins create another native area.
        private bool TrySweepLaneMasks(SurfacePatch source, List<LaneMaskCell> masks, out SurfacePatch result, bool intersection = false)
        {
            result = null;
            if (source.Parts.Count > 0 || source.Triangles.Count == 0) return false;
            if (intersection && (source.Triangles.Count > 512 || masks.Count > 4096)) return false;
            float3 origin = source.Nodes[0].m_Position;
            List<MaskPoint[]> faces = new List<MaskPoint[]>(), cuts = new List<MaskPoint[]>();
            List<float3> bounds = new List<float3>();
            foreach (var node in source.Nodes) bounds.Add(node.m_Position);
            foreach (int3 face in source.Triangles)
                AddMaskTriangle(faces, ToMaskPoint(source.Nodes[face.x].m_Position, origin),
                    ToMaskPoint(source.Nodes[face.y].m_Position, origin), ToMaskPoint(source.Nodes[face.z].m_Position, origin));
            foreach (LaneMaskCell mask in masks)
                if (LaneCellOverlaps(bounds, mask))
                    AddMaskTriangle(cuts, ToMaskPoint(mask.Points[0], origin), ToMaskPoint(mask.Points[1], origin), ToMaskPoint(mask.Points[2], origin));
            List<MaskSegment> edges = intersection ? MeshUnionBoundary(faces) : RawSweepEdges(faces);
            edges.AddRange(intersection ? MeshUnionBoundary(cuts) : RawSweepEdges(cuts));
            if (intersection && edges.Count > 3072) return false;
            List<double> events = new List<double>();
            if (intersection)
                foreach (MaskSegment edge in edges) { events.Add(edge.A.X); events.Add(edge.B.X); }
            else
            {
                foreach (MaskPoint[] face in faces) foreach (MaskPoint point in face) events.Add(point.X);
                foreach (MaskPoint[] face in cuts) foreach (MaskPoint point in face) events.Add(point.X);
            }
            for (int i = 0; i < edges.Count; i++)
                for (int j = i + 1; j < edges.Count; j++) SplitMaskSegments(edges[i], edges[j]);
            foreach (MaskSegment edge in edges)
                foreach (double t in edge.Cuts) events.Add(edge.A.X + (edge.B.X - edge.A.X) * t);
            events.Sort();
            List<double> planes = new List<double>();
            foreach (double x in events) if (planes.Count == 0 || x - planes[planes.Count - 1] > 0.000001d) planes.Add(x);
            if (intersection && planes.Count > 4096) return false;
            List<SweepStrip> active = new List<SweepStrip>(), finished = new List<SweepStrip>();
            double expected = 0d;
            for (int slab = 1; slab < planes.Count; slab++)
            {
                double left = planes[slab - 1], right = planes[slab], middle = (left + right) * 0.5d;
                List<SweepSpan> spans = intersection ? IntersectBusSweepSpans(UnionSweepSpans(faces, middle), UnionSweepSpans(cuts, middle)) :
                    DifferenceSweepSpans(UnionSweepSpans(faces, middle), UnionSweepSpans(cuts, middle));
                List<SweepStrip> next = new List<SweepStrip>();
                int[] previousLinks = new int[active.Count], newLinks = new int[spans.Count];
                for (int i = 0; i < active.Count; i++)
                    for (int j = 0; j < spans.Count; j++)
                        if (Math.Min(active[i].LastHigh, SweepZ(spans[j].High, left)) -
                            Math.Max(active[i].LastLow, SweepZ(spans[j].Low, left)) > 0.000001d)
                        { previousLinks[i]++; newLinks[j]++; }
                HashSet<SweepStrip> continued = new HashSet<SweepStrip>();
                for (int i = 0; i < spans.Count; i++)
                {
                    SweepSpan span = spans[i];
                    double lowLeft = SweepZ(span.Low, left), highLeft = SweepZ(span.High, left),
                        lowRight = SweepZ(span.Low, right), highRight = SweepZ(span.High, right);
                    SweepStrip strip = null;
                    if (newLinks[i] == 1)
                        for (int j = 0; j < active.Count; j++)
                            if (previousLinks[j] == 1 && Math.Min(active[j].LastHigh, highLeft) - Math.Max(active[j].LastLow, lowLeft) > 0.000001d)
                            { strip = active[j]; continued.Add(strip); break; }
                    if (strip == null) strip = new SweepStrip();
                    AppendSweepPair(strip, new MaskPoint(left, lowLeft), new MaskPoint(left, highLeft));
                    AppendSweepPair(strip, new MaskPoint(right, lowRight), new MaskPoint(right, highRight));
                    AppendSweepPoint(strip.Low, new MaskPoint(left, lowLeft)); AppendSweepPoint(strip.High, new MaskPoint(left, highLeft));
                    AppendSweepPoint(strip.Low, new MaskPoint(right, lowRight)); AppendSweepPoint(strip.High, new MaskPoint(right, highRight));
                    strip.LastLow = lowRight; strip.LastHigh = highRight; next.Add(strip);
                    expected += (right - left) * (Math.Max(0d, highLeft - lowLeft) + Math.Max(0d, highRight - lowRight)) * 0.5d;
                }
                foreach (SweepStrip strip in active) if (!continued.Contains(strip)) finished.Add(strip);
                active = next;
                if (intersection && finished.Count + active.Count > 256) return false;
            }
            finished.AddRange(active);
            SurfacePatch merged = CopySurfacePatchProperties(source);
            double actual = 0d;
            foreach (SweepStrip strip in finished)
            {
                List<MaskPoint> contour = new List<MaskPoint>(strip.Low);
                for (int i = strip.High.Count - 1; i >= 0; i--) contour.Add(strip.High[i]);
                if (Math.Abs(MaskLoopArea(contour)) <= 0.00005d) continue;
                List<float3> boundary = new List<float3>();
                foreach (MaskPoint point in contour)
                    boundary.Add(new float3((float)(origin.x + point.X), SampleMaskDeckHeight(source, origin, point), (float)(origin.z + point.Z)));
                SurfacePatch part = TriangulateBoundary(boundary);
                if (part.Triangles.Count == 0)
                {
                    // Very narrow tapers can make native float ear clipping
                    // ambiguous. A sweep strip is monotone: its paired columns
                    // provide an exact mesh without splitting it into areas.
                    List<float3> low = new List<float3>(), high = new List<float3>();
                    for (int i = 0; i < strip.SampleLow.Count; i++)
                    {
                        MaskPoint a = strip.SampleLow[i], b = strip.SampleHigh[i];
                        low.Add(new float3((float)(origin.x + a.X), SampleMaskDeckHeight(source, origin, a), (float)(origin.z + a.Z)));
                        high.Add(new float3((float)(origin.x + b.X), SampleMaskDeckHeight(source, origin, b), (float)(origin.z + b.Z)));
                    }
                    part = RemoveUnusedSurfaceNodes(CloseStrip(low, high));
                    if (part.Triangles.Count == 0) return false;
                }
                part.ExactBoundary = source.ExactBoundary; part.SmallRoundabout = source.SmallRoundabout;
                part.ProjectionMinimum = source.ProjectionMinimum; part.ProjectionMaximum = source.ProjectionMaximum;
                foreach (int3 face in part.Triangles)
                    actual += Math.Abs(LaneCellSide(part.Nodes[face.z].m_Position, part.Nodes[face.x].m_Position, part.Nodes[face.y].m_Position)) * 0.5d;
                merged.Parts.Add(part);
            }
            if (Math.Abs(actual - expected) > Math.Max(0.005d, expected * 0.002d)) return false;
            MarkLaneMasksApplied(merged); result = merged.Parts.Count == 1 ? merged.Parts[0] : merged;
            if (m_LoggedSweepMasks++ < 16)
                Mod.Log.Info("Surface sweep partition: source triangles=" + faces.Count + "; mask triangles=" + cuts.Count +
                    "; slabs=" + (planes.Count - 1) + "; output areas=" + merged.Parts.Count + "; footprint=" + actual.ToString("0.###") + " m2.");
            return true;
        }

        private static List<MaskSegment> RawSweepEdges(List<MaskPoint[]> faces)
        {
            List<MaskPoint> vertices = new List<MaskPoint>();
            Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();
            Dictionary<long, LaneMaskBorder> borders = new Dictionary<long, LaneMaskBorder>();
            foreach (MaskPoint[] face in faces)
                for (int i = 0; i < 3; i++) AddLaneMaskBorder(borders, WeldMaskPoint(face[i], vertices, grid), WeldMaskPoint(face[(i + 1) % 3], vertices, grid));
            List<MaskSegment> edges = new List<MaskSegment>();
            foreach (LaneMaskBorder edge in borders.Values) edges.Add(new MaskSegment(vertices[edge.From], vertices[edge.To]));
            return edges;
        }
        private static double SweepZ(MaskSegment edge, double x) => edge.A.Z +
            (edge.B.Z - edge.A.Z) * ((x - edge.A.X) / (edge.B.X - edge.A.X));
        private static List<SweepSpan> UnionSweepSpans(List<MaskPoint[]> faces, double x)
        {
            List<SweepSpan> spans = new List<SweepSpan>();
            foreach (MaskPoint[] face in faces)
            {
                MaskSegment low = null, high = null; double lowZ = double.PositiveInfinity, highZ = double.NegativeInfinity;
                for (int i = 0; i < 3; i++)
                {
                    MaskPoint a = face[i], b = face[(i + 1) % 3];
                    if (x <= Math.Min(a.X, b.X) || x >= Math.Max(a.X, b.X)) continue;
                    MaskSegment edge = new MaskSegment(a, b); double z = SweepZ(edge, x);
                    if (z < lowZ) { low = edge; lowZ = z; } if (z > highZ) { high = edge; highZ = z; }
                }
                if (low != null && high != null && highZ - lowZ > 0.000000001d) spans.Add(new SweepSpan(low, high, lowZ, highZ));
            }
            spans.Sort((a, b) => a.LowZ.CompareTo(b.LowZ));
            List<SweepSpan> union = new List<SweepSpan>();
            foreach (SweepSpan span in spans)
            {
                if (union.Count == 0 || span.LowZ > union[union.Count - 1].HighZ + 0.000000001d) union.Add(span);
                else if (span.HighZ > union[union.Count - 1].HighZ)
                { SweepSpan last = union[union.Count - 1]; last.High = span.High; last.HighZ = span.HighZ; }
            }
            return union;
        }
        private static List<SweepSpan> DifferenceSweepSpans(List<SweepSpan> sources, List<SweepSpan> masks)
        {
            List<SweepSpan> result = new List<SweepSpan>();
            foreach (SweepSpan source in sources)
            {
                MaskSegment low = source.Low; double lowZ = source.LowZ;
                foreach (SweepSpan mask in masks)
                {
                    if (mask.HighZ <= lowZ || mask.LowZ >= source.HighZ) continue;
                    if (mask.LowZ > lowZ + 0.000000001d) result.Add(new SweepSpan(low, mask.Low, lowZ, mask.LowZ));
                    if (mask.HighZ > lowZ) { low = mask.High; lowZ = mask.HighZ; }
                    if (lowZ >= source.HighZ) break;
                }
                if (lowZ < source.HighZ - 0.000000001d) result.Add(new SweepSpan(low, source.High, lowZ, source.HighZ));
            }
            return result;
        }
        private static void AppendSweepPoint(List<MaskPoint> points, MaskPoint point)
        {
            if (points.Count > 0 && MaskDot(points[points.Count - 1] - point, points[points.Count - 1] - point) < 0.000000000001d) return;
            while (points.Count > 1)
            {
                MaskPoint a = points[points.Count - 2], b = points[points.Count - 1], d = point - a;
                if (Math.Abs(MaskCross(b - a, d)) > 0.000000001d * Math.Max(1d, Math.Sqrt(MaskDot(d, d))) || MaskDot(b - a, point - b) < 0d) break;
                points.RemoveAt(points.Count - 1);
            }
            points.Add(point);
        }
        private static void AppendSweepPair(SweepStrip strip, MaskPoint low, MaskPoint high)
        {
            int count = strip.SampleLow.Count;
            if (count > 0 && MaskDot(strip.SampleLow[count - 1] - low, strip.SampleLow[count - 1] - low) < 0.000000000001d &&
                MaskDot(strip.SampleHigh[count - 1] - high, strip.SampleHigh[count - 1] - high) < 0.000000000001d) return;
            strip.SampleLow.Add(low); strip.SampleHigh.Add(high);
        }
    }
}
