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
        private readonly HashSet<Entity> m_LoggedPhysicalRoundaboutCurbs = new HashSet<Entity>();

        private float PhysicalRoundaboutIslandRadius(Entity node, float clearanceRadius, float defaultSurfaceHeight)
        {
            if (!EntityManager.HasBuffer<Game.Objects.SubObject>(node)) return clearanceRadius;
            float radius = 0f;
            foreach (Game.Objects.SubObject child in EntityManager.GetBuffer<Game.Objects.SubObject>(node, true))
            {
                Entity obj = child.m_SubObject;
                if (!EntityManager.Exists(obj) || !EntityManager.HasComponent<PrefabRef>(obj)) continue;
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(obj).m_Prefab;
                if (!EntityManager.HasComponent<PlaceableObjectData>(prefab) ||
                    !EntityManager.HasComponent<ObjectGeometryData>(prefab) ||
                    (EntityManager.GetComponentData<PlaceableObjectData>(prefab).m_Flags & Game.Objects.PlacementFlags.RoadNode) == 0) continue;
                ObjectGeometryData geometry = EntityManager.GetComponentData<ObjectGeometryData>(prefab);
                float physical = math.cmax(geometry.m_Size.xz) * 0.5f;
                if ((geometry.m_Flags & Game.Objects.GeometryFlags.Standing) != 0)
                {
                    float leg = math.cmax(geometry.m_LegSize.xz * 0.5f + geometry.m_LegOffset);
                    physical = geometry.m_LegSize.y >= defaultSurfaceHeight ? leg : math.max(physical, leg);
                }
                radius = math.max(radius, physical);
            }
            // CalculateMiddleRadius adds a one-metre traffic clearance to the
            // physical object/leg bounds. That margin is paved carriageway,
            // not part of the island's protected footprint.
            return radius > 0.01f && radius <= clearanceRadius + 0.01f ? radius : clearanceRadius;
        }

        private static SurfacePatch CreatePhysicalRoundaboutWalkMask(EdgeNodeGeometry geometry,
            NetCompositionData composition, RoadBand band, bool left, CornerInsets? mouth, float3 center)
        {
            CornerInsets start = BandInsets(composition, band, left);
            // Vertex targets already contain the neighbouring composition's
            // curb join. Averaging its width here would apply that join twice.
            List<float3> outer = new List<float3>(), inner = new List<float3>();
            AppendPhysicalRoundaboutWalk(outer, inner, geometry, composition, geometry.m_Left,
                start, start, band.LowHeight, left, center, false, mouth);
            AppendPhysicalRoundaboutWalk(outer, inner, geometry, composition, geometry.m_Right,
                start, start, band.LowHeight, left, center, true, null);
            return geometry.m_MiddleRadius <= 4.01f ? CreateSmallRoundaboutStrip(outer, inner) : CloseStrip(outer, inner);
        }

        private SurfacePatch CreateRoundaboutWalkFootprint(Entity owner, Entity node, EdgeNodeGeometry geometry,
            SurfaceLayout approach, SurfaceLayout layout, RoadBand band, bool left, bool start)
        {
            CornerInsets? mouth = GetRoundaboutMouth(owner, node, approach, layout, band, left, true, start);
            if ((layout.RecoveredRoundaboutSides & (left ? 1 : 2)) != 0)
            {
                // Recovery already measured the street's deformed world curb
                // at the shared join. Do not average that distance with the
                // neighbouring composition's undeformed nominal band again.
                return CreateRoundaboutSidewalkPolygon(geometry, layout.Composition, band, left,
                    BandInsets(layout.Composition, band, left), mouth);
            }
            float3 center = EntityManager.GetComponentData<Game.Net.Node>(node).m_Position;
            return CreatePhysicalRoundaboutWalkMask(geometry, layout.Composition, band, left, mouth, center);
        }

        private static void AppendPhysicalRoundaboutWalk(List<float3> outer, List<float3> inner,
            EdgeNodeGeometry geometry, NetCompositionData composition, Segment segment,
            CornerInsets start, CornerInsets end, float height, bool left, float3 center,
            bool circular, CornerInsets? mouth)
        {
            Bezier4x3 curve = RemoveEdgeHeight(left ? segment.m_Left : segment.m_Right,
                left ? composition.m_EdgeHeights.xz : composition.m_EdgeHeights.yw,
                circular ? 0.5f : 0f, circular ? 1f : 0.5f);
            float3 firstNear = Lift(InsetBoundaryPosition(curve, 0f, mouth.HasValue ? mouth.Value.Near : start.Near, left), height);
            float3 firstFar = Lift(InsetBoundaryPosition(curve, 0f, mouth.HasValue ? mouth.Value.Far : start.Far, left), height);
            System.Func<float, float, float3> boundary = (t, inset) => {
                float3 native = MathUtils.Position(curve, t);
                float physicalInset = RoundaboutMappedCurbInset(geometry, composition, left, inset,
                    math.distance(native.xz, center.xz));
                // The entry joins the unchanged edge footprint. Circulating
                // pieces use the radial vertex targets throughout their arc.
                float blend = circular ? 1f : t * t * (3f - 2f * t);
                return Lift(InsetBoundaryPosition(curve, t, math.lerp(inset, physicalInset, blend), left), height);
            };
            System.Func<float, float3> near = t => boundary(t, math.lerp(start.Near, end.Near, t));
            System.Func<float, float3> far = t => boundary(t, math.lerp(start.Far, end.Far, t));
            if (mouth.HasValue)
            {
                CornerInsets entry = mouth.Value;
                near = t => boundary(t, math.lerp(entry.Near, end.Near, t * t * (3f - 2f * t))) +
                    (entry.HasPositions ? (entry.NearPosition - firstNear) * (1f - t * t * (3f - 2f * t)) : default);
                far = t => boundary(t, math.lerp(entry.Far, end.Far, t * t * (3f - 2f * t))) +
                    (entry.HasPositions ? (entry.FarPosition - firstFar) * (1f - t * t * (3f - 2f * t)) : default);
            }
            foreach (float t in SampleMappedParameters(near, far)) AppendPair(outer, inner, near(t), far(t));
        }

        private static float RoundaboutMappedCurbInset(EdgeNodeGeometry geometry,
            NetCompositionData composition, bool left, float inset, float radialReach)
        {
            float4 source = left ? composition.m_SyncVertexOffsetsLeft : composition.m_SyncVertexOffsetsRight;
            float4 target = left ? geometry.m_SyncVertexTargetsLeft : geometry.m_SyncVertexTargetsRight;
            if (!math.all(math.isfinite(source)) || !math.all(math.isfinite(target)) ||
                source.w <= source.x + 0.00001f || math.any(source.yzw < source.xyz) ||
                target.w <= target.x + 0.00001f || math.any(target.yzw < target.xyz) || radialReach <= 0.01f) return inset;
            float width = composition.m_Width * 0.5f + (left ? composition.m_MiddleOffset : -composition.m_MiddleOffset);
            float fraction = math.saturate(inset / math.max(0.01f, width));
            float mapped = RemapSideFraction(left ? fraction : 1f - fraction, source, target);
            // Native roundabout sync targets are radial fractions, unlike the
            // ordinary node's fractions between opposing side curves.
            return math.clamp((left ? mapped : 1f - mapped) * radialReach, 0f, radialReach);
        }
    }
}
