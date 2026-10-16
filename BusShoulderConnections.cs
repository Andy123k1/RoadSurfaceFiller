using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private int m_LoggedBusShoulderFallbacks, m_LoggedBusShoulderConnections;
        private SurfacePatch CreateBusShoulderConnection(Entity lane, Bezier4x3 curve, float2 widths,
            List<JunctionLaneRoad> roads, JunctionPavementDomain domain, List<LaneMaskCell> protectedCells)
        {
            if (!EntityManager.HasComponent<Game.Net.Lane>(lane)) return null;
            Game.Net.Lane path = EntityManager.GetComponentData<Game.Net.Lane>(lane);
            JunctionLaneRoad from = roads.Find(road => road.Edge.Index == path.m_StartNode.GetOwnerIndex());
            JunctionLaneRoad to = roads.Find(road => road.Edge.Index == path.m_EndNode.GetOwnerIndex());
            // Use the actual native path owners, never the nearest upgraded
            // road. A one-sided shoulder upgrade stops at its crossing.
            if (from == null || to == null || from == to || !HasBusShoulderFinish(from) || !HasBusShoulderFinish(to)) return null;
            if (!BusShoulderOffsets(from, path.m_StartNode.GetLaneIndex(), curve.a, MathUtils.StartTangent(curve), widths.x,
                    out float2 start) ||
                !BusShoulderOffsets(to, path.m_EndNode.GetLaneIndex(), curve.d, MathUtils.EndTangent(curve), widths.y,
                    out float2 end)) return null;
            if (math.cmax(start - widths.x * 0.5f) < 0.01f && math.cmax(end - widths.y * 0.5f) < 0.01f) return null;

            System.Func<float, float3> left = t => ExpandedBusBoundary(curve, start, end, t, true);
            System.Func<float, float3> right = t => ExpandedBusBoundary(curve, start, end, t, false);
            List<float3> a = new List<float3>(), b = new List<float3>();
            // Offset curves can acquire a cusp on a tight inside turn. Bound
            // sampling and build the ribbon mesh once, rather than repeatedly
            // triangulating every growing prefix of a folded offset strip.
            List<float> samples = new List<float> { 0f };
            SampleBusShoulderInterval(left, right, 0f, 1f, 0, samples);
            foreach (float t in samples) AppendPair(a, b, left(t), right(t));
            SurfacePatch wide = CloseStrip(a, b);
            SetLanePatchFlags(wide, false);
            wide.ProjectionMaximum = float.PositiveInfinity;
            // Keep the widened part in the shared junction. Other approaches
            // own their shoulders outside their own crosswalk boundaries.
            if (domain != null) wide = IntersectLaneCells(wide, domain.Cells);
            else if (TrySubtractLaneContours(wide, protectedCells, out SurfacePatch trimmed) ||
                TrySweepLaneMasks(wide, protectedCells, out trimmed)) wide = trimmed;
            else return null;
            if (m_LoggedBusShoulderConnections++ < 16)
                Mod.Log.Info("Bounded bus shoulder connection: lane=" + lane.Index + "; from=" + from.Edge.Index +
                    "; to=" + to.Edge.Index + "; samples=" + samples.Count + "; curb cells=" + (domain?.Cells.Count ?? 0) +
                    "; output areas=" + CountSurfacePatchAreas(wide) + ".");
            return wide;
        }

        private static void SampleBusShoulderInterval(System.Func<float, float3> left, System.Func<float, float3> right,
            float start, float end, int depth, List<float> samples)
        {
            if (depth < 7 && (end - start > 0.0625f || NeedsMappedSubdivision(left, start, end) || NeedsMappedSubdivision(right, start, end)))
            {
                float middle = (start + end) * 0.5f;
                SampleBusShoulderInterval(left, right, start, middle, depth + 1, samples);
                SampleBusShoulderInterval(left, right, middle, end, depth + 1, samples);
            }
            else samples.Add(end);
        }

        private bool HasBusShoulderFinish(JunctionLaneRoad road) => road.Eligible && road.Layout.FillBusShoulders &&
            road.Layout.SeparateBusLanes && road.Layout.BusLanes.Count > 0 &&
            GetChoices(Mod.ActiveSetting, SurfaceTarget.BusLanes, road.Prefab, segment: road.Edge).Count > 0;

        private static void AllowShoulderProjection(SurfacePatch patch)
        {
            patch.ProjectionMaximum = float.PositiveInfinity;
            foreach (SurfacePatch part in patch.Parts) AllowShoulderProjection(part);
        }

        private bool BusShoulderOffsets(JunctionLaneRoad road, ushort laneIndex, float3 endpoint, float3 tangent,
            float width, out float2 offsets)
        {
            offsets = new float2(width * 0.5f);
            SurfaceLayout layout = road.Layout;
            EdgeGeometry geometry = EntityManager.GetComponentData<EdgeGeometry>(road.Edge);
            float3 lowPoint = road.Start ? geometry.m_Start.m_Left.a : geometry.m_End.m_Left.d;
            float3 highPoint = road.Start ? geometry.m_Start.m_Right.a : geometry.m_End.m_Right.d;
            float2 across = highPoint.xz - lowPoint.xz;
            float physicalWidth = math.length(across);
            if (physicalWidth < 0.01f || layout.Composition.m_Width < 0.01f) return false;
            float2 axis = across / physicalWidth;
            float position = (math.dot(endpoint.xz - lowPoint.xz, axis) / physicalWidth - 0.5f) * layout.Composition.m_Width;
            // Lane indices are native composition identities. Geometric lateral
            // position is used only when a generated path has a secondary index.
            Composition composition = EntityManager.GetComponentData<Composition>(road.Edge);
            if (EntityManager.HasBuffer<NetCompositionLane>(composition.m_Edge))
                foreach (NetCompositionLane native in EntityManager.GetBuffer<NetCompositionLane>(composition.m_Edge, true))
                    if (native.m_Index == laneIndex && (native.m_Flags & LaneFlags.PublicOnly) != 0)
                    { position = native.m_Position.x; break; }
            LaneSpan selected = default;
            float best = float.PositiveInfinity;
            foreach (LaneSpan span in layout.BusLanes)
            {
                float distance = math.abs(position - (span.Low + span.High) * 0.5f);
                if (distance < best) { best = distance; selected = span; }
            }
            if (best > (selected.High - selected.Low) * 0.5f + 0.25f) return false;
            foreach (RoadBand band in GetDedicatedPaintBands(layout, false))
            {
                float center = (selected.Low + selected.High) * 0.5f;
                if (center < band.Low - 0.01f || center > band.High + 0.01f) continue;
                float2 normal = math.normalizesafe(new float2(-tangent.z, tangent.x));
                float orientation = math.dot(normal, axis);
                if (math.abs(orientation) < 0.5f) return false;
                float scale = physicalWidth / layout.Composition.m_Width;
                float low = math.max(0f, selected.Low - band.Low) * scale;
                float high = math.max(0f, band.High - selected.High) * scale;
                offsets += orientation > 0f ? new float2(high, low) : new float2(low, high);
                return true;
            }
            return false;
        }

        private static float3 ExpandedBusBoundary(Bezier4x3 curve, float2 start, float2 end, float t, bool left)
        {
            float3 point = MathUtils.Position(curve, t);
            float2 tangent = MathUtils.Tangent(curve, t).xz;
            if (math.lengthsq(tangent) < 0.000001f)
                tangent = (MathUtils.Position(curve, math.min(1f, t + 0.001f)) - MathUtils.Position(curve, math.max(0f, t - 0.001f))).xz;
            float2 offset = math.lerp(start, end, t);
            point.xz += math.normalizesafe(new float2(-tangent.y, tangent.x)) * (left ? offset.x : -offset.y);
            point.y += SurfaceLift;
            return point;
        }

        private SurfacePatch IntersectLaneCells(SurfacePatch source, List<LaneMaskCell> cells)
        {
            SurfacePatch mesh = FlattenSurfacePatchMesh(source);
            if (mesh.Triangles.Count == 0 || cells.Count == 0) return CopySurfacePatchProperties(source);
            if (TryIntersectBusContours(mesh, cells, out SurfacePatch result) ||
                TrySweepLaneMasks(mesh, cells, out result, intersection: true)) return result;
            // Preserve the ordinary physical bus lane if a pathological offset
            // cannot produce a valid bounded area. Never enter the former
            // Cartesian triangle-fragment join as a fallback.
            if (m_LoggedBusShoulderFallbacks++ < 8)
                Mod.Log.Warn("Could not build a valid bus shoulder connection inside its junction curb; retained the physical bus lane.");
            return CopySurfacePatchProperties(source);
        }

        private SurfacePatch StopBusAtJunctionCrossings(SurfacePatch patch, Entity node,
            List<JunctionLaneRoad> roads, JunctionPavementDomain domain)
        {
            if (domain != null) return SubtractLaneMasks(patch, domain.Cells);
            SurfacePatch outside = CopySurfacePatchProperties(patch);
            // Two-road corners and roundabouts do not use the ordinary junction
            // domain. Their native mouths define the virtual crossing line.
            foreach (JunctionLaneRoad road in roads)
                outside.Parts.Add(ClipSurfacePatch(patch, JunctionCrosswalkCut(road.Edge, node, road.Start), true));
            return outside;
        }
    }
}
