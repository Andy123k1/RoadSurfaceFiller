using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    internal enum SegmentAction { Add, OnlySelection, Remove, Exclude, Restore, UseRoadType, Rebuild, Clear }

    internal static partial class RuntimeState
    {
        internal static readonly List<string> RoadNames = new List<string>();
        internal static Entity SelectedEdge;
        internal static string SelectionStatus = "Select a road segment in the city first.";
        internal static string SelectionMessage = "";
        internal static readonly List<SegmentRequest> SegmentRequests = new List<SegmentRequest>();

        internal readonly struct SegmentRequest
        {
            internal readonly Entity Edge;
            internal readonly SegmentAction Action;
            internal SegmentRequest(Entity edge, SegmentAction action) { Edge = edge; Action = action; }
        }

        internal static void RequestSegmentAction(SegmentAction action)
        {
            SegmentRequests.Add(new SegmentRequest(SelectedEdge, action));
        }
    }

    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private readonly HashSet<Entity> m_ManualEdges = new HashSet<Entity>();

        private bool RefreshRoadCatalog()
        {
            List<string> names = new List<string>();
            using (NativeArray<Entity> prefabs = GetEntityQuery(ComponentType.ReadOnly<RoadData>(),
                ComponentType.ReadOnly<NetGeometryData>()).ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in prefabs)
                {
                    string name = m_PrefabSystem.GetPrefabName(entity);
                    if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
                }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            bool changed = names.Count != RuntimeState.RoadNames.Count;
            for (int i = 0; !changed && i < names.Count; i++) changed |= names[i] != RuntimeState.RoadNames[i];
            if (changed) { RuntimeState.RoadNames.Clear(); RuntimeState.RoadNames.AddRange(names); }
            return changed;
        }

        private bool IsHighway(Entity edge)
        {
            return EntityManager.HasComponent<PrefabRef>(edge) &&
                IsHighwayPrefab(EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab);
        }

        private bool IsHighwayPrefab(Entity prefab)
        {
            return EntityManager.HasComponent<RoadData>(prefab) &&
                (EntityManager.GetComponentData<RoadData>(prefab).m_Flags & Game.Prefabs.RoadFlags.UseHighwayRules) != 0;
        }

        private bool MatchesCategory(Entity prefab, RoadCategory category, bool streetContext = false)
        {
            bool highway = !streetContext && IsHighwayPrefab(prefab);
            float width = EntityManager.HasComponent<NetGeometryData>(prefab)
                ? EntityManager.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth : 0f;
            switch (category)
            {
                case RoadCategory.Streets: return !highway;
                case RoadCategory.Highways: return highway;
                case RoadCategory.SmallStreets: return !highway && width <= 16f;
                case RoadCategory.MediumStreets: return !highway && width > 16f && width <= 32f;
                case RoadCategory.LargeStreets: return !highway && width > 32f;
                default: return true;
            }
        }

        private bool PassesRoadFilter(Entity edge, Entity prefab, Setting setting)
        {
            if (!PassesSegmentFilter(edge, prefab, setting)) return false;
            return IsHighwayPrefab(prefab) ? setting.IncludeHighways : setting.IncludeStreets;
        }

        private bool PassesSegmentFilter(Entity edge, Entity prefab, Setting setting)
        {
            if (EntityManager.HasComponent<SurfaceSegmentExcluded>(edge) || EntityManager.HasComponent<ManualSurfaceRoad>(edge)) return false;
            if (setting.Scope == NetworkScope.SelectedSegments && !EntityManager.HasComponent<SurfaceSegmentSelected>(edge)) return false;
            return string.IsNullOrEmpty(setting.RoadPrefabFilter) || string.Equals(setting.RoadPrefabFilter,
                m_PrefabSystem.GetPrefabName(prefab), StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateRoadSelection(Setting setting)
        {
            SelectedInfoUISystem info = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            Entity selected = info.selectedEntity;
            if (selected == Entity.Null) selected = World.GetOrCreateSystemManaged<ToolSystem>().selected;
            Entity candidate = ResolveSelectedEdge(selected, info.selectedPosition);
            if (candidate != Entity.Null) RuntimeState.SelectedEdge = candidate;
            else if (selected != Entity.Null) RuntimeState.SelectedEdge = Entity.Null;
            if (!IsRoadEdge(RuntimeState.SelectedEdge)) RuntimeState.SelectedEdge = Entity.Null;
            if (candidate != Entity.Null) RuntimeState.LastRoadSelectionPosition = info.selectedPosition;

            while (RuntimeState.SegmentRequests.Count > 0)
            {
                RuntimeState.SegmentRequest request = RuntimeState.SegmentRequests[0];
                RuntimeState.SegmentRequests.RemoveAt(0);
                if (request.Action == SegmentAction.Clear)
                {
                    using (NativeArray<Entity> edges = GetEntityQuery(ComponentType.ReadOnly<SurfaceSegmentSelected>()).ToEntityArray(Allocator.Temp))
                    {
                        foreach (Entity edge in edges)
                        {
                            EntityManager.RemoveComponent<SurfaceSegmentSelected>(edge);
                            QueueManualEdge(edge);
                        }
                    }
                    RuntimeState.SelectionMessage = "Saved segment selection cleared.";
                    continue;
                }
                Entity current = request.Edge;
                if (!IsRoadEdge(current))
                {
                    RuntimeState.SelectionMessage = "Select a road segment with the Info tool first.";
                    continue;
                }
                switch (request.Action)
                {
                    case SegmentAction.Add:
                    case SegmentAction.OnlySelection:
                        if (!EntityManager.HasComponent<SurfaceSegmentSelected>(current)) EntityManager.AddComponent<SurfaceSegmentSelected>(current);
                        if (EntityManager.HasComponent<SurfaceSegmentExcluded>(current)) EntityManager.RemoveComponent<SurfaceSegmentExcluded>(current);
                        if (request.Action == SegmentAction.OnlySelection)
                        {
                            setting.Scope = NetworkScope.SelectedSegments;
                            setting.ApplyAndSave();
                            QueueRefresh(setting);
                        }
                        RuntimeState.SelectionMessage = "Segment added to the saved selection.";
                        break;
                    case SegmentAction.Remove:
                        EntityManager.RemoveComponent<SurfaceSegmentSelected>(current);
                        RuntimeState.SelectionMessage = "Segment removed from the saved selection.";
                        break;
                    case SegmentAction.Exclude:
                        if (!EntityManager.HasComponent<SurfaceSegmentExcluded>(current)) EntityManager.AddComponent<SurfaceSegmentExcluded>(current);
                        CancelPendingFor(current);
                        DestroyGeneratedFor(current);
                        RuntimeState.SelectionMessage = "Segment excluded from surface placement.";
                        break;
                    case SegmentAction.Restore:
                        EntityManager.RemoveComponent<SurfaceSegmentExcluded>(current);
                        RuntimeState.SelectionMessage = "Segment exclusion removed.";
                        break;
                    case SegmentAction.UseRoadType:
                        setting.RoadPrefabFilter = m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(current).m_Prefab);
                        setting.ApplyAndSave();
                        RuntimeState.SelectionMessage = "Road prefab filter set to " + setting.RoadPrefabFilter + ".";
                        break;
                    case SegmentAction.Rebuild:
                        RuntimeState.SelectionMessage = "Segment queued for rebuilding with the current filters.";
                        break;
                }
                QueueManualEdge(current);
            }
            UpdateJunctionSelection(setting);
            UpdateSegmentRuleSnapshot(setting);
            int count = GetEntityQuery(ComponentType.ReadOnly<SurfaceSegmentSelected>(), ComponentType.Exclude<Deleted>()).CalculateEntityCount();
            if (RuntimeState.SelectedEdge == Entity.Null)
                RuntimeState.SelectionStatus = "No current road segment. Saved selection: " + count + " segments.\n" + RuntimeState.SelectionMessage;
            else
            {
                Entity edge = RuntimeState.SelectedEdge;
                string name = m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab);
                RuntimeState.SelectionStatus = name + " — segment " + edge.Index + (IsHighway(edge) ? " (highway)" : " (street)") +
                    "; selected=" + EntityManager.HasComponent<SurfaceSegmentSelected>(edge) +
                    "; excluded=" + EntityManager.HasComponent<SurfaceSegmentExcluded>(edge) +
                    "; manual surfaces=" + EntityManager.HasComponent<ManualSurfaceRoad>(edge) +
                    ". Saved selection: " + count + " segments.\n" + RuntimeState.SelectionMessage;
            }
        }

        private void QueueManualEdge(Entity edge)
        {
            m_ManualEdges.Add(edge);
            QueueEdge(edge);
            if (IsRoadEdge(edge))
            {
                // Also refresh neighbors when a preset was removed, so this
                // road no longer passes HasPlacementRules in QueueEdge.
                if (!HasPlacementRules(edge)) QueueAutomaticJunctionApproaches(edge);
                Edge endpoints = EntityManager.GetComponentData<Edge>(edge);
                ScheduleJunctionGeometryChecks(endpoints.m_Start);
                ScheduleJunctionGeometryChecks(endpoints.m_End);
            }
        }

        private void CancelPendingFor(Entity owner)
        {
            for (int i = m_PendingAreas.Count - 1; i >= 0; i--)
            {
                PendingArea pending = m_PendingAreas[i];
                if (pending.Owner != owner) continue;
                if (EntityManager.Exists(pending.Definition)) EntityManager.DestroyEntity(pending.Definition);
                RetireOwnedSurfaceArea(pending.Owner, pending.MaterializedArea);
                RemovePendingArea(i);
            }
        }

        private Entity ResolveSelectedEdge(Entity selected, float3 position)
        {
            if (IsRoadEdge(selected)) return selected;
            if (!EntityManager.Exists(selected) || !EntityManager.HasBuffer<AggregateElement>(selected)) return Entity.Null;
            DynamicBuffer<AggregateElement> edges = EntityManager.GetBuffer<AggregateElement>(selected, true);
            Entity closest = Entity.Null;
            float distance = float.MaxValue;
            foreach (AggregateElement element in edges)
            {
                if (!IsRoadEdge(element.m_Edge)) continue;
                Curve curve = EntityManager.GetComponentData<Curve>(element.m_Edge);
                for (int i = 0; i <= 32; i++)
                {
                    float next = math.distancesq(MathUtils.Position(curve.m_Bezier, i / 32f), position);
                    if (next < distance) { distance = next; closest = element.m_Edge; }
                }
            }
            return closest;
        }
    }
}
