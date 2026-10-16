using System.Collections.Generic;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private readonly HashSet<Entity> m_LoggedDedicatedLanes = new HashSet<Entity>();

        private static bool IsSurfaceTargetActive(SurfaceLayout layout, SurfaceTarget target)
        {
            return target == SurfaceTarget.BikeLanes ? layout.SeparateBikeLanes :
                target != SurfaceTarget.BusLanes || layout.SeparateBusLanes;
        }

        private void ReadDedicatedLane(Entity prefab, float3 position, LaneFlags flags, NetLaneData data,
            List<LaneSpan> bicycles, List<LaneSpan> buses)
        {
            // Masters and virtual lanes are routing groups, not physical strips.
            // PublicOnly is set by the game's CarLane.m_BusLane initializer;
            // BicyclesOnly is set for RoadTypes.Bicycle, not mixed car traffic.
            if ((flags & LaneFlags.Road) == 0 ||
                (flags & (LaneFlags.Master | LaneFlags.Virtual | LaneFlags.Utility | LaneFlags.CrossRoad |
                    LaneFlags.Underground | LaneFlags.OnWater)) != 0 ||
                !math.all(math.isfinite(position)) || !math.isfinite(data.m_Width) || data.m_Width <= 0.01f)
                return;
            bool bicycle = (flags & LaneFlags.BicyclesOnly) != 0;
            if (!bicycle && EntityManager.HasComponent<CarLaneData>(prefab))
                bicycle = EntityManager.GetComponentData<CarLaneData>(prefab).m_RoadTypes == RoadTypes.Bicycle;
            bool bus = !bicycle && (flags & LaneFlags.PublicOnly) != 0;
            if (!bicycle && !bus) return;
            LaneSpan span = new LaneSpan(position.x - data.m_Width * 0.5f,
                position.x + data.m_Width * 0.5f, position.y);
            (bicycle ? bicycles : buses).Add(span);
        }

        private int MixLaneComposition(int hash, Entity composition)
        {
            if (!EntityManager.HasBuffer<NetCompositionLane>(composition)) return hash;
            foreach (NetCompositionLane lane in EntityManager.GetBuffer<NetCompositionLane>(composition, true))
            {
                hash = Mix(hash, lane.m_Lane.Index);
                hash = Mix(hash, lane.m_Lane.Version);
                hash = Mix(hash, (int)lane.m_Flags);
                hash = MixVector(hash, lane.m_Position);
                if (!EntityManager.HasComponent<NetLaneData>(lane.m_Lane)) continue;
                NetLaneData data = EntityManager.GetComponentData<NetLaneData>(lane.m_Lane);
                hash = Mix(hash, (int)data.m_Flags);
                hash = Mix(hash, (int)(data.m_Width * 1000f));
                if (EntityManager.HasComponent<CarLaneData>(lane.m_Lane))
                    hash = Mix(hash, (int)EntityManager.GetComponentData<CarLaneData>(lane.m_Lane).m_RoadTypes);
            }
            return hash;
        }

        private static bool HasSeparatedLanes(SurfaceLayout layout, Setting setting)
        {
            return (layout.SeparateBikeLanes && layout.BikeLanes.Count > 0) ||
                (layout.SeparateBusLanes && layout.BusLanes.Count > 0);
        }

        private static LaneSpan RefineBicycleSidewalkCurb(LaneSpan sidewalk, LaneSpan pedestrian,
            List<LaneSpan> bicycles, float middle)
        {
            bool left = pedestrian.Low + pedestrian.High < middle * 2f;
            float center = (pedestrian.Low + pedestrian.High) * 0.5f;
            float best = 0.75f;
            LaneSpan result = sidewalk;
            foreach (LaneSpan bicycle in bicycles)
            {
                // Only the adjacent, lower bicycle strip defines this curb.
                // Never grow a sidewalk across a median or another traffic lane.
                float bicycleCenter = (bicycle.Low + bicycle.High) * 0.5f;
                if ((left ? bicycleCenter <= center || bicycleCenter >= middle :
                    bicycleCenter >= center || bicycleCenter <= middle) || bicycle.Height > pedestrian.Height - 0.035f)
                    continue;
                float curb = left ? bicycle.Low : bicycle.High;
                float distance = math.abs(curb - (left ? sidewalk.High : sidewalk.Low));
                if (distance >= best) continue;
                result = left ? new LaneSpan(sidewalk.Low, curb, sidewalk.Height) :
                    new LaneSpan(curb, sidewalk.High, sidewalk.Height);
                best = distance;
            }
            return result;
        }

        private static List<RoadBand> SubtractLaneBands(List<RoadBand> pavement, List<LaneSpan> lanes)
        {
            List<RoadBand> result = new List<RoadBand>(pavement);
            foreach (LaneSpan lane in lanes)
            {
                for (int i = result.Count - 1; i >= 0; i--)
                {
                    RoadBand band = result[i];
                    if (lane.High <= band.Low || lane.Low >= band.High) continue;
                    result.RemoveAt(i);
                    float low = math.max(band.Low, lane.Low), high = math.min(band.High, lane.High);
                    if (low > band.Low + 0.01f)
                        result.Insert(i, new RoadBand(band.Low, low, band.LowHeight, PavementHeight(band, low)));
                    if (high < band.High - 0.01f)
                        result.Insert(i, new RoadBand(high, band.High, PavementHeight(band, high), band.HighHeight));
                }
            }
            result.Sort((a, b) => a.Low.CompareTo(b.Low));
            return result;
        }

        private static List<RoadBand> GetRoadPaintBands(SurfaceLayout layout, Setting setting)
        {
            // Keep the physical layout intact: curb recovery, priority, shared
            // junction fills and roundabout islands depend on the full road.
            if (!HasSeparatedLanes(layout, setting)) return layout.Roads;
            List<RoadBand> result = layout.Roads;
            if (layout.SeparateBikeLanes) result = SubtractLaneBands(result, layout.BikeLanes);
            if (layout.SeparateBusLanes)
            {
                List<LaneSpan> buses = new List<LaneSpan>();
                foreach (RoadBand bus in GetDedicatedPaintBands(layout, false)) buses.Add(new LaneSpan(bus.Low, bus.High, bus.LowHeight));
                result = SubtractLaneBands(result, buses);
            }
            return result;
        }

        private static List<RoadBand> GetDedicatedPaintBands(SurfaceLayout layout, bool bicycle)
        {
            List<RoadBand> result = new List<RoadBand>();
            float halfWidth = layout.Composition.m_Width * 0.5f;
            foreach (LaneSpan lane in bicycle ? layout.BikeLanes : layout.BusLanes)
            {
                float low = math.max(-halfWidth, lane.Low), high = math.min(halfWidth, lane.High);
                if (!bicycle && layout.FillBusShoulders)
                    foreach (RoadBand road in layout.Roads)
                    {
                        if (lane.High <= road.Low || lane.Low >= road.High) continue;
                        float trafficLow = float.PositiveInfinity, trafficHigh = float.NegativeInfinity;
                        foreach (LaneSpan traffic in layout.Driving)
                        {
                            if (traffic.Parking || traffic.High <= road.Low || traffic.Low >= road.High) continue;
                            trafficLow = math.min(trafficLow, traffic.Low); trafficHigh = math.max(trafficHigh, traffic.High);
                        }
                        if (lane.Low <= trafficLow + 0.01f) low = math.min(low, road.Low);
                        if (lane.High >= trafficHigh - 0.01f) high = math.max(high, road.High);
                    }
                if (high > low + 0.01f) result.Add(new RoadBand(low, high, lane.Height, lane.Height));
            }
            // Raised bicycle tracks need not belong to the ordinary carriageway
            // bands. Use their native lane width, excluding actual sidewalks.
            result = SubtractLaneBands(result, layout.Sidewalks);
            result = SubtractLaneBands(result, layout.Planted);
            if (!bicycle) result = SubtractLaneBands(result, layout.BikeLanes);
            return result;
        }

        private void AddDedicatedEdgeSurfaces(Entity owner, EdgeGeometry geometry, SurfaceLayout layout,
            Curve curve, string name, Setting setting)
        {
            if (!HasSeparatedLanes(layout, setting)) return;
            Entity roadPrefab = EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab;
            for (int kind = 0; kind < 2; kind++)
            {
                bool bicycle = kind == 0;
                SurfaceTarget target = bicycle ? SurfaceTarget.BikeLanes : SurfaceTarget.BusLanes;
                if (!IsSurfaceTargetActive(layout, target)) continue;
                SurfaceChoice choice = Choose(GetChoices(setting, target, roadPrefab, segment: owner), name, curve, 3 + kind);
                if (choice == null) continue; // The reserved strip keeps the game's native finish.
                foreach (RoadBand band in GetDedicatedPaintBands(layout, bicycle))
                {
                    SurfacePatch patch = ClipOrdinaryBranchBody(owner,
                        CreateEdgePolygon(geometry, layout.Composition, band), setting, layout, band, false);
                    LimitDedicatedLaneProjection(patch, layout, band.LowHeight);
                    if (!bicycle && layout.FillBusShoulders) patch.ProjectionMaximum = float.PositiveInfinity;
                    if (!bicycle) patch = ClipApproachEdgeAtCrosswalks(owner, patch);
                    QueuePolygon(owner, choice.Prefab, patch);
                }
            }
            Entity composition = EntityManager.GetComponentData<Composition>(owner).m_Edge;
            if (m_LoggedDedicatedLanes.Count < 16 && m_LoggedDedicatedLanes.Add(composition))
            {
                Mod.Log.Info("Dedicated lane separation: road='" + name + "'; bike bands=" + layout.BikeLanes.Count +
                    "; bus bands=" + layout.BusLanes.Count + "; bike switch=" + layout.SeparateBikeLanes +
                    "; bus switch=" + layout.SeparateBusLanes +
                    ". Lane surfaces follow native edge boundaries and dedicated junction paths.");
                List<string> walks = new List<string>(), bikes = new List<string>(), pavement = new List<string>();
                foreach (LaneSpan span in layout.Sidewalks) walks.Add(span.Low.ToString("0.###") + ".." + span.High.ToString("0.###"));
                foreach (LaneSpan span in layout.BikeLanes) bikes.Add(span.Low.ToString("0.###") + ".." + span.High.ToString("0.###"));
                foreach (RoadBand span in GetRoadPaintBands(layout, setting)) pavement.Add(span.Low.ToString("0.###") + ".." + span.High.ToString("0.###"));
                Mod.Log.Info("Upgraded lane profile: composition=" + composition.Index + "; width=" + layout.Composition.m_Width +
                    "; sidewalks=[" + string.Join(", ", walks) + "]; bikes=[" + string.Join(", ", bikes) +
                    "]; road paint=[" + string.Join(", ", pavement) + "].");
            }
        }

        private bool IsOrdinaryLaneConnection(Entity node)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return false;
            int count = 0;
            foreach (ConnectedEdge edge in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                if (IsRoadEdge(edge.m_Edge) && ++count > 2) return false;
            return count == 2;
        }

        private static void LimitDedicatedLaneProjection(SurfacePatch patch, SurfaceLayout layout, float laneHeight)
        {
            foreach (LaneSpan sidewalk in layout.Sidewalks)
                if (sidewalk.Height > laneHeight + 0.035f)
                    patch.ProjectionMaximum = math.min(patch.ProjectionMaximum,
                        math.max(-SurfaceLift + 0.005f, (sidewalk.Height - laneHeight) * 0.5f - SurfaceLift));
            foreach (SurfacePatch part in patch.Parts)
            {
                LimitDedicatedLaneProjection(part, layout, laneHeight);
                part.ProjectionMaximum = math.min(part.ProjectionMaximum, patch.ProjectionMaximum);
            }
        }

        private static bool MatchesLaneConnectionLayout(SurfaceLayout layout, SurfaceLayout approach, Setting setting, bool start)
        {
            // A widening or lane drop is a material transition, not a uniform
            // lane strip. Keep the established node fill there instead of
            // guessing a lane's correspondence across different profiles.
            if (math.abs(layout.Composition.m_Width - approach.Composition.m_Width) > 0.01f ||
                math.abs(layout.Composition.m_MiddleOffset -
                    (start ? -approach.Composition.m_MiddleOffset : approach.Composition.m_MiddleOffset)) > 0.01f)
                return false;
            return (!layout.SeparateBikeLanes || MatchesLaneSpans(layout.BikeLanes, approach.BikeLanes, start)) &&
                (!layout.SeparateBusLanes || MatchesLaneSpans(layout.BusLanes, approach.BusLanes, start));
        }

        private static bool MatchesLaneSpans(List<LaneSpan> node, List<LaneSpan> edge, bool start)
        {
            if (node.Count != edge.Count) return false;
            for (int i = 0; i < node.Count; i++)
            {
                LaneSpan other = edge[start ? edge.Count - 1 - i : i];
                float low = start ? -other.High : other.Low, high = start ? -other.Low : other.High;
                if (math.abs(node[i].Low - low) > 0.01f || math.abs(node[i].High - high) > 0.01f ||
                    math.abs(node[i].Height - other.Height) > 0.01f) return false;
            }
            return true;
        }

        private void AddSeparatedConnectionSurfaces(Entity owner, EdgeNodeGeometry geometry, SurfaceLayout layout,
            SurfaceLayout approach, Curve curve, string name, SurfaceChoice road, Setting setting, bool start)
        {
            if (road != null)
                foreach (RoadBand band in GetRoadPaintBands(layout, setting))
                    QueueConnectionLaneBand(owner, road.Prefab, geometry, layout, approach, band, start);
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab;
            for (int kind = 0; kind < 2; kind++)
            {
                bool bicycle = kind == 0;
                SurfaceTarget target = bicycle ? SurfaceTarget.BikeLanes : SurfaceTarget.BusLanes;
                if (!IsSurfaceTargetActive(layout, target)) continue;
                SurfaceChoice choice = Choose(GetChoices(setting, target, prefab, segment: owner), name, curve, 3 + kind);
                if (choice == null) continue;
                foreach (RoadBand band in GetDedicatedPaintBands(layout, bicycle))
                    QueueConnectionLaneBand(owner, choice.Prefab, geometry, layout, approach, band, start, true);
            }
        }

        private void QueueConnectionLaneBand(Entity owner, Entity material, EdgeNodeGeometry geometry,
            SurfaceLayout layout, SurfaceLayout approach, RoadBand band, bool start, bool dedicated = false)
        {
            // Do not match a narrow lane against the full curb-to-curb RoadBand;
            // doing so widens it across the carriageway at the segment mouth.
            EdgeGeometry edge = EntityManager.GetComponentData<EdgeGeometry>(owner);
            float3 outerLeft = start ? edge.m_Start.m_Right.a : edge.m_End.m_Left.d;
            float3 outerRight = start ? edge.m_Start.m_Left.a : edge.m_End.m_Right.d;
            outerLeft.y -= start ? approach.Composition.m_EdgeHeights.y : approach.Composition.m_EdgeHeights.z;
            outerRight.y -= start ? approach.Composition.m_EdgeHeights.x : approach.Composition.m_EdgeHeights.w;
            for (int side = 0; side < 2; side++)
            {
                bool left = side == 0;
                float low = left ? band.Low : math.max(band.Low, layout.Composition.m_MiddleOffset);
                float high = left ? math.min(band.High, layout.Composition.m_MiddleOffset) : band.High;
                if (high <= low + 0.01f) continue;
                float near = left ? low : high, far = left ? high : low;
                CornerInsets mouth = new CornerInsets {
                    HasPositions = true,
                    NearPosition = Lift(math.lerp(outerLeft, outerRight,
                        math.saturate(near / layout.Composition.m_Width + 0.5f)), PavementHeight(band, near)),
                    FarPosition = Lift(math.lerp(outerLeft, outerRight,
                        math.saturate(far / layout.Composition.m_Width + 0.5f)), PavementHeight(band, far))
                };
                SurfacePatch patch = CreateJunctionSidePolygon(geometry, layout.Composition, band,
                    left, mouth, BandInsets(layout.Composition, band, left));
                LimitDedicatedLaneProjection(patch, layout, math.max(band.LowHeight, band.HighHeight));
                QueuePolygon(owner, material, patch);
            }
        }
    }
}
