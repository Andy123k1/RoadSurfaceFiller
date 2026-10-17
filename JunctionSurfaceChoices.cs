using System.Collections.Generic;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private Entity FindActiveJunctionRuleRoad(Entity node, SurfaceTarget target, Setting setting, bool streetsOnly)
        {
            Entity result = Entity.Null;
            float widest = -1f;
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return result;
            foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity edge = connected.m_Edge;
                if (!IsRoadEdge(edge) || (streetsOnly && IsHighway(edge))) continue;
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                if (!PassesRoadFilter(edge, prefab, setting) ||
                    GetChoices(setting, target, prefab, streetContext: streetsOnly, segment: edge).Count == 0) continue;
                float width = EntityManager.HasComponent<NetGeometryData>(prefab) ?
                    EntityManager.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth : 0f;
                if (width > widest + 0.01f || (math.abs(width - widest) <= 0.01f && (result == Entity.Null || edge.Index < result.Index)))
                { result = edge; widest = width; }
            }
            return result;
        }

        private sealed class JunctionSurfaceOption
        {
            internal Entity Road;
            internal SurfaceChoice Surface;
            internal SurfaceTarget Target;
            internal bool CoverBusLanes;
        }

        private JunctionSurfaceOption FindAutomaticJunctionSurface(Entity node, Setting setting, bool preferStreets)
        {
            JunctionSurfaceOption result = null;
            bool resultStreet = false;
            float widest = -1f;
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return null;
            foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity edge = connected.m_Edge;
                if (!IsRoadEdge(edge) || !HasPlacementRules(edge)) continue;
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                if (!PassesRoadFilter(edge, prefab, setting) || !PassesElevationFilter(edge, prefab, setting)) continue;
                // Resolve the actual segment's finish, as the cycler does. A
                // prefab reference alone can point at an unstyled approach or
                // force a highway preset through the street-category filter.
                JunctionSurfaceOption option = EffectiveRoadJunctionOption(edge, setting);
                if (option == null) continue;
                bool street = !IsHighway(edge);
                if (result != null && preferStreets && street != resultStreet)
                {
                    if (!street) continue;
                    result = null; widest = -1f;
                }
                float width = EntityManager.HasComponent<NetGeometryData>(prefab) ?
                    EntityManager.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth : 0f;
                if (result == null || width > widest + 0.01f ||
                    (math.abs(width - widest) <= 0.01f && edge.Index < result.Road.Index))
                { result = option; resultStreet = street; widest = width; }
            }
            return result;
        }

        private int MixAutomaticJunctionStyles(int hash, Entity node)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return hash;
            int connectedStyles = 0, count = 0;
            unchecked
            {
                foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                {
                    Entity edge = connection.m_Edge;
                    if (!IsRoadEdge(edge)) continue;
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                    int part = Mix(Mix(Mix(Mix(edge.Index, edge.Version), prefab.Index), prefab.Version), UsesCityRules(edge) ? 1 : 0);
                    part = MixSegmentRule(part, edge);
                    part = Mix(part, EntityManager.HasComponent<ManualSurfaceRoad>(edge) ? 1 : 0);
                    part = Mix(part, EntityManager.HasComponent<SurfaceSegmentExcluded>(edge) ? 1 : 0);
                    part = Mix(part, EntityManager.HasComponent<SurfaceSegmentSelected>(edge) ? 1 : 0);
                    connectedStyles += MixBezier(part, EntityManager.GetComponentData<Curve>(edge).m_Bezier);
                    count++;
                }
            }
            // Order-independent and free of material lookup/registration. The
            // settled-geometry checks can now notice a neighbor's preset change
            // even when none of the native road geometry has changed.
            return Mix(Mix(hash, count), connectedStyles);
        }

        private void QueueAutomaticJunctionApproaches(Entity source)
        {
            Setting setting = Mod.ActiveSetting;
            if (setting == null || !IsRoadEdge(source)) return;
            Edge ends = EntityManager.GetComponentData<Edge>(source);
            for (int end = 0; end < 2; end++)
            {
                Entity node = end == 0 ? ends.m_Start : ends.m_End;
                if ((end == 1 && node == ends.m_Start) || !IsEditableSurfaceJunction(node) ||
                    (!IsRoundaboutJunction(node) && CountJunctionRoads(node) < 3)) continue;
                foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                {
                    Entity edge = connection.m_Edge;
                    if (edge == source || !IsRoadEdge(edge)) continue;
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                    if (!PassesRoadFilter(edge, prefab, setting) || !PassesElevationFilter(edge, prefab, setting)) continue;
                    // An unstyled approach can own a ring sector. Rebuild its
                    // junction pieces without enrolling its body in any rule.
                    // Core deliberately does not recurse into its other node.
                    QueueEdgeCore(edge);
                }
            }
        }
        private List<JunctionSurfaceOption> GetJunctionSurfaceOptions(Entity node, Setting setting)
        {
            List<JunctionSurfaceOption> result = new List<JunctionSurfaceOption>();
            bool busPaths = HasJunctionBusPaths(node, setting);
            foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                foreach (JunctionSurfaceOption option in GetRoadJunctionSurfaceOptions(connected.m_Edge, setting))
                {
                    if (!result.Exists(item => item.Surface.Prefab == option.Surface.Prefab && !item.CoverBusLanes)) result.Add(option);
                    if (busPaths && option.Target == SurfaceTarget.Roads &&
                        !result.Exists(item => item.Surface.Prefab == option.Surface.Prefab && item.CoverBusLanes))
                        result.Add(new JunctionSurfaceOption { Road = option.Road, Surface = option.Surface,
                            Target = SurfaceTarget.Roads, CoverBusLanes = true });
                }
            result.Sort((a, b) => {
                int road = string.Compare(JunctionRoadLabel(a.Road), JunctionRoadLabel(b.Road), System.StringComparison.OrdinalIgnoreCase);
                if (road != 0) return road;
                int target = a.Target.CompareTo(b.Target);
                return target != 0 ? target : a.CoverBusLanes.CompareTo(b.CoverBusLanes);
            });
            return result;
        }

        private bool HasJunctionBusPaths(Entity node, Setting setting)
        {
            if (EntityManager.HasBuffer<Game.Net.SubLane>(node))
                foreach (Game.Net.SubLane sub in EntityManager.GetBuffer<Game.Net.SubLane>(node, true))
                    if (TryReadJunctionLane(sub.m_SubLane, setting, out SurfaceTarget target, out _, out _) &&
                        target == SurfaceTarget.BusLanes) return true;
            return false;
        }

        private int CountJunctionRoads(Entity node)
        {
            int count = 0;
            if (EntityManager.HasBuffer<ConnectedEdge>(node))
                foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                    if (IsRoadEdge(connected.m_Edge)) count++;
            return count;
        }

        private static string JunctionOptionLabel(JunctionSurfaceOption option) =>
            option.Target == SurfaceTarget.BusLanes ? "bus-lane junction fill" :
            option.CoverBusLanes ? "road pavement; bus lanes stop at crosswalks" : "road pavement; bus lanes continue";
        private List<JunctionSurfaceOption> GetRoadJunctionSurfaceOptions(Entity road, Setting setting)
        {
            // Bicycle finishes belong to their physical tracks. The cycler
            // chooses only road/bus pavement surrounding those tracks.
            List<JunctionSurfaceOption> options = new List<JunctionSurfaceOption>();
            if (!IsRoadEdge(road)) return options;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(road).m_Prefab;
            string name = m_PrefabSystem.GetPrefabName(prefab);
            Curve curve = EntityManager.GetComponentData<Curve>(road);
            SurfaceChoice pavement = Choose(GetChoices(setting, SurfaceTarget.Roads, prefab, segment: road), name, curve, 0);
            if (pavement != null) options.Add(new JunctionSurfaceOption { Road = road, Surface = pavement, Target = SurfaceTarget.Roads });
            Composition composition = EntityManager.GetComponentData<Composition>(road);
            if (TryReadSurfaceLayout(composition.m_Edge, out SurfaceLayout layout, highway: IsHighway(road), roadPrefab: prefab) && layout.BusLanes.Count > 0)
            {
                ApplySegmentLaneSettings(layout, road);
                SurfaceChoice bus = Choose(GetChoices(setting, SurfaceTarget.BusLanes, prefab, segment: road), name, curve, 4);
                if (bus != null && (pavement == null || pavement.Prefab != bus.Prefab))
                    options.Add(new JunctionSurfaceOption { Road = road, Surface = bus, Target = SurfaceTarget.BusLanes });
            }
            return options;
        }
        private JunctionSurfaceOption EffectiveRoadJunctionOption(Entity road, Setting setting)
        {
            List<JunctionSurfaceOption> options = GetRoadJunctionSurfaceOptions(road, setting);
            if (options.Count == 0) return null;
            Composition composition = EntityManager.GetComponentData<Composition>(road);
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(road).m_Prefab;
            if (TryReadSurfaceLayout(composition.m_Edge, out SurfaceLayout layout, highway: IsHighway(road), roadPrefab: prefab))
            {
                ApplySegmentLaneSettings(layout, road);
                if (layout.FillBusShoulders && BusLanesCoverDriving(layout))
                    foreach (JunctionSurfaceOption option in options) if (option.Target == SurfaceTarget.BusLanes) return option;
            }
            return options[0];
        }

        private SurfaceChoice FilledBusJunctionChoice(Entity road, Setting setting)
        {
            if (!IsRoadEdge(road)) return null;
            Composition composition = EntityManager.GetComponentData<Composition>(road);
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(road).m_Prefab;
            if (!TryReadSurfaceLayout(composition.m_Edge, out SurfaceLayout layout, highway: IsHighway(road), roadPrefab: prefab)) return null;
            ApplySegmentLaneSettings(layout, road);
            if (!layout.FillBusShoulders || !BusLanesCoverDriving(layout)) return null;
            return Choose(GetChoices(setting, SurfaceTarget.BusLanes, prefab, segment: road),
                m_PrefabSystem.GetPrefabName(prefab), EntityManager.GetComponentData<Curve>(road), 4);
        }
    }
}
