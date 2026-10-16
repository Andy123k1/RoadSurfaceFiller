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
