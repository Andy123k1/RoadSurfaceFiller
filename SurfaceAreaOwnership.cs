using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private readonly HashSet<Entity> m_InvalidatedSurfaceOwners = new HashSet<Entity>();
        private readonly HashSet<Entity> m_RetiredSurfaceAreas = new HashSet<Entity>();
        private int m_SurfaceInvalidationFrame = -1, m_LoggedSurfaceRepairs;

        private void InvalidateRoadSurfaceOwner(Entity owner)
        {
            if (m_SurfaceInvalidationFrame != m_Frame)
            { m_InvalidatedSurfaceOwners.Clear(); m_SurfaceInvalidationFrame = m_Frame; }
            if (!m_InvalidatedSurfaceOwners.Add(owner)) return;
            // Cancel obsolete requests immediately, but keep completed surfaces
            // visible until the replacement geometry has been calculated.
            CancelPendingFor(owner);
            QueueManualEdge(owner);
        }

        private bool PendingBoundaryMatches(SurfacePatch patch, Entity area)
        {
            if (!EntityManager.HasBuffer<AreaNode>(area)) return false;
            DynamicBuffer<AreaNode> nodes = EntityManager.GetBuffer<AreaNode>(area, true);
            int actual = nodes.Length, expected = patch.Nodes.Count;
            if (actual > 1 && math.distancesq(nodes[0].m_Position.xz, nodes[actual - 1].m_Position.xz) < 0.00000001f) actual--;
            if (expected > 1 && math.distancesq(patch.Nodes[0].m_Position.xz, patch.Nodes[expected - 1].m_Position.xz) < 0.00000001f) expected--;
            if (actual != expected || actual < 3) return false;
            // GenerateAreasSystem copies this order, dropping only the closing
            // duplicate. Its terrain adjustment changes y, never x/z. Keeping
            // the order also makes our saved triangle indices unambiguous.
            for (int i = 0; i < actual; i++)
                if (math.distancesq(nodes[i].m_Position.xz, patch.Nodes[i].m_Position.xz) > 0.0001f) return false;
            return true;
        }

        private bool IsPrivateRoadSurface(Entity area, Entity owner)
        {
            return EntityManager.Exists(area) && EntityManager.HasComponent<Owner>(area) &&
                EntityManager.GetComponentData<Owner>(area).m_Owner == owner && EntityManager.HasComponent<PrefabRef>(area) &&
                IsNetworkOverlayPrefab(m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(area).m_Prefab));
        }

        private bool HasDifferentSurfaceOwner(Entity area, Entity owner)
        {
            return EntityManager.Exists(area) && EntityManager.HasComponent<Owner>(area) &&
                EntityManager.GetComponentData<Owner>(area).m_Owner != owner;
        }

        private void RetireOwnedSurfaceArea(Entity owner, Entity area)
        {
            // Native splitting can transfer an area to a replacement edge before
            // our old owner's index is retired. That stale index must not delete
            // the live replacement's surface or mark it as intentionally retired.
            if (HasDifferentSurfaceOwner(area, owner)) return;
            MarkAreaDeleted(area); m_KnownAreaEntities.Remove(area);
        }

        private void DeleteUnindexedRoadSurfaces(Entity owner)
        {
            if (!EntityManager.HasBuffer<Game.Areas.SubArea>(owner)) return;
            List<Entity> areas = new List<Entity>();
            foreach (Game.Areas.SubArea sub in EntityManager.GetBuffer<Game.Areas.SubArea>(owner, true))
                if (IsPrivateRoadSurface(sub.m_Area, owner)) areas.Add(sub.m_Area);
            foreach (Entity area in areas)
            { MarkAreaDeleted(area); m_KnownAreaEntities.Remove(area); }
        }

        private void MaintainGeneratedSurfaceOwnership(Setting setting)
        {
            if (!m_AreaIndexInitialized || m_Frame % AreaScanInterval != 0) return;
            HashSet<Entity> indexed = new HashSet<Entity>();
            foreach (List<Entity> owned in m_GeneratedByOwner.Values)
                foreach (Entity area in owned) indexed.Add(area);
            m_RetiredSurfaceAreas.RemoveWhere(area => !EntityManager.Exists(area) && !indexed.Contains(area));
            HashSet<Entity> repair = new HashSet<Entity>();
            foreach (KeyValuePair<Entity, List<Entity>> entry in new List<KeyValuePair<Entity, List<Entity>>>(m_GeneratedByOwner))
            {
                for (int i = entry.Value.Count - 1; i >= 0; i--)
                    if (HasDifferentSurfaceOwner(entry.Value[i], entry.Key))
                    {
                        entry.Value.RemoveAt(i); m_GeneratedCount = math.max(0, m_GeneratedCount - 1);
                        if (IsRoadEdge(entry.Key) && HasPlacementRules(entry.Key)) repair.Add(entry.Key);
                    }
                if (entry.Value.Count == 0) m_GeneratedByOwner.Remove(entry.Key);
            }
            using (NativeArray<Entity> areas = m_AreaEntities.ToEntityArray(Allocator.Temp))
                foreach (Entity area in areas)
                {
                    Entity owner = EntityManager.GetComponentData<Owner>(area).m_Owner;
                    if (EntityManager.HasComponent<GeneratedSurface>(area))
                    {
                        // Native clones/transfers can retain our saved marker,
                        // while the runtime index still names the old road.
                        if (m_GeneratedByOwner.TryGetValue(owner, out List<Entity> owned) && owned.Contains(area)) continue;
                        if (IsRoadEdge(owner) && IsPrivateRoadSurface(area, owner))
                        {
                            AddGeneratedArea(owner, area); m_KnownAreaEntities.Add(area);
                            repair.Add(owner);
                        }
                        continue;
                    }
                    if (!IsPrivateRoadSurface(area, owner)) continue;
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                    if (TryGetAreaCenter(area, out float3 center) && FindPending(owner, prefab, center, area) >= 0) continue;
                    // Native jobs can materialize an old definition after it
                    // was canceled. A private area without a matching request
                    // is obsolete; user-painted surface prefabs are untouched.
                    MarkAreaDeleted(area); m_KnownAreaEntities.Remove(area);
                    if (setting.Enabled && IsRoadEdge(owner) && HasPlacementRules(owner) && !HasPendingFor(owner)) repair.Add(owner);
                }
            if (!setting.Enabled) return;
            foreach (KeyValuePair<Entity, List<Entity>> entry in m_GeneratedByOwner)
            {
                Entity owner = entry.Key;
                if (!IsRoadEdge(owner) || !HasPlacementRules(owner) || HasPendingFor(owner) || m_QueuedEdges.Contains(owner)) continue;
                foreach (Entity area in entry.Value)
                    if (!m_RetiredSurfaceAreas.Contains(area) && (!EntityManager.Exists(area) || EntityManager.HasComponent<Deleted>(area) ||
                        HasDifferentSurfaceOwner(area, owner) || GeneratedSurfaceNeedsRepair(area, owner)))
                    { repair.Add(owner); break; }
            }
            foreach (Entity owner in repair)
            {
                QueueRoadStyleRebuild(owner);
                if (m_LoggedSurfaceRepairs++ < 16)
                    Mod.Log.Info("Repairing road surface ownership/visibility while retaining saved assignments: edge=" + owner.Index + ".");
            }
        }

        private bool GeneratedSurfaceNeedsRepair(Entity area, Entity owner)
        {
            if (!EntityManager.HasComponent<Game.Areas.Area>(area) || !EntityManager.HasBuffer<SurfaceMeshTriangle>(area) || !EntityManager.HasBuffer<Game.Areas.Triangle>(area) ||
                EntityManager.GetBuffer<Game.Areas.Triangle>(area, true).Length == 0) return true;
            Game.Areas.Area data = EntityManager.GetComponentData<Game.Areas.Area>(area);
            if ((data.m_Flags & Game.Areas.AreaFlags.Complete) == 0 || (data.m_Flags & Game.Areas.AreaFlags.NoTriangles) != 0) return true;
            if (!EntityManager.HasComponent<Game.Tools.Hidden>(area) || EntityManager.HasComponent<Game.Tools.Hidden>(owner)) return false;
            // The tool may legitimately hide an original while its preview is
            // visible. Only repair a hidden area after that preview is gone.
            foreach (KeyValuePair<Entity, Entity> preview in m_PreviewStyleSources)
                if (preview.Value == owner && EntityManager.Exists(preview.Key) &&
                    EntityManager.HasComponent<Game.Tools.Temp>(preview.Key) && !EntityManager.HasComponent<Deleted>(preview.Key)) return false;
            return true;
        }
    }
}
