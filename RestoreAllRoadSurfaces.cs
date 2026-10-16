using System.Collections.Generic;
using Game.Common;
using Unity.Collections;
using Unity.Entities;

namespace SurfaceNetworkFilter
{
    internal static partial class RuntimeState
    {
        internal static bool RestoreAllRoadsRequested, RestoringAllRoads;
    }

    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private void RestoreAllRoadSurfaces(Setting setting)
        {
            if (!RuntimeState.RestoreAllRoadsRequested) return;
            RuntimeState.RestoreAllRoadsRequested = false;
            RuntimeState.RestoringAllRoads = true;
            HashSet<Entity> owners = new HashSet<Entity>(m_ManagedEdges);
            owners.UnionWith(m_GeneratedByOwner.Keys);
            owners.UnionWith(m_PendingAreaCounts.Keys);

            // Discard old definitions before another native generation pass.
            // Clear accepted/preview inheritance as well, so a pending native
            // commit cannot put the just-removed assignments back on a road.
            foreach (Entity owner in new List<Entity>(m_PendingAreaCounts.Keys)) CancelPendingFor(owner);
            m_CityMemberPreviews.Clear(); m_PlacementPreviews.Clear(); m_CitySurfaceSources.Clear();
            ResetRoadStyleTransfers();
            m_SegmentRuleReplacements.Clear(); m_MissingSegmentRules.Clear();
            RuntimeState.SegmentRuleRequests.Clear(); RuntimeState.UpgradeRequests.Clear();
            RuntimeState.JunctionRequests.Clear(); RuntimeState.ApplyCityRulesRequested = false;
            RuntimeState.RebuildRequested = false;

            EntityManager.DestroyEntity(GetEntityQuery(ComponentType.ReadOnly<SavedSegmentSurfaceRule>()));
            m_SegmentRuleRecords.Clear();
            EntityManager.RemoveComponent(GetEntityQuery(ComponentType.ReadOnly<CitySurfaceRuleMember>()), ComponentType.ReadWrite<CitySurfaceRuleMember>());
            EntityManager.DestroyEntity(GetEntityQuery(ComponentType.ReadOnly<SavedJunctionSurface>()));
            EntityManager.RemoveComponent(GetEntityQuery(ComponentType.ReadOnly<JunctionSurfacePriority>()), ComponentType.ReadWrite<JunctionSurfacePriority>());
            ResetJunctionSurfaceRecords();

            // Only the city's applied snapshot is cleared. The editable global
            // library and the user's segment presets remain exactly as saved.
            List<SurfaceRule> empty = new List<SurfaceRule>();
            setting.SetAppliedRules(empty); SaveCityRuleSnapshot(empty);
            m_CityRulesInitialized = true;
            RuntimeState.CityRuleMemberCount = RuntimeState.SegmentRuleCount = RuntimeState.ActiveSegmentRuleCount = 0;
            RuntimeState.SegmentRule = null;

            // Include private overlays that native road operations transferred
            // or cloned before they reached our runtime owner index. Authored
            // surfaces and other mods' areas never enter this cleanup queue.
            using (NativeArray<Entity> areas = m_AreaEntities.ToEntityArray(Allocator.Temp))
                foreach (Entity area in areas)
                {
                    Entity owner = EntityManager.GetComponentData<Owner>(area).m_Owner;
                    if (!IsPrivateRoadSurface(area, owner)) continue;
                    owners.Add(owner); AddGeneratedArea(owner, area);
                }

            m_EdgeQueue.Clear(); m_QueuedEdges.Clear(); m_EdgeReadyFrames.Clear(); m_QueueCursor = 0;
            m_DeferredEdges.Clear(); m_ManualEdges.Clear();
            // Roads without overlays already have their vanilla appearance;
            // removing their assignment above is sufficient. Avoid empty mesh
            // jobs and discard their old managed-road bookkeeping.
            m_ManagedEdges.Clear(); owners.IntersectWith(m_GeneratedByOwner.Keys);
            InvalidateGenerationCaches();
            foreach (Entity owner in owners)
                if (owner != Entity.Null) QueueEdge(owner);
            m_RefreshInProgress = true; m_RefreshCompletedFrame = -1;
            m_LastFingerprint = setting.ApplyFingerprint();
            RuntimeState.UpgradeMessage = RuntimeState.CityRulesMessage = "Restoring all existing roads to vanilla surfaces…";
            Mod.Log.Info("Queued city-wide vanilla surface restoration. Surface owners=" + owners.Count + ". Rule libraries retained.");
        }
    }
}
