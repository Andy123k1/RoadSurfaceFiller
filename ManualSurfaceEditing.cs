using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using SubArea = Game.Areas.SubArea;

namespace SurfaceNetworkFilter
{
    // Separate from selection/exclusion: applying city rules or a new preset
    // must not silently resume automatic painting over a player's manual work.
    public struct ManualSurfaceRoad : IComponentData, IEmptySerializable { }

    internal sealed partial class SurfaceNetworkFilterSystem
    {
        internal void OpenManualSurfaceEditor(string surface)
        {
            if (!RuntimeState.TryGetSurface(surface, out Entity source) || !EntityManager.Exists(source))
                throw new ArgumentException("Choose an installed surface for the editor.");
            Entity prefab = GetNetworkOverlayPrefab(source);
            if (prefab == Entity.Null || !m_PrefabSystem.TryGetPrefab(prefab, out SurfacePrefab paint))
                throw new ArgumentException("The surface renderer is still preparing. Try again shortly.");
            if ((EntityManager.GetComponentData<AreaGeometryData>(prefab).m_Flags & GeometryFlags.HiddenIngame) != 0)
                throw new ArgumentException("This surface does not support the in-game area editor. Choose another surface.");
            AreaToolSystem editor = World.GetOrCreateSystemManaged<AreaToolSystem>();
            editor.mode = AreaToolSystem.Mode.Edit;
            editor.recreate = Entity.Null;
            if (!World.GetOrCreateSystemManaged<ToolSystem>().ActivatePrefabTool(paint))
                throw new ArgumentException("The surface editor is unavailable right now.");
        }

        private int MakeRoadSurfacesEditable(Entity edge)
        {
            if (EntityManager.HasComponent<ManualSurfaceRoad>(edge)) return 0;
            if (HasPendingFor(edge) || m_QueuedEdges.Contains(edge) || m_DeferredEdges.Contains(edge) ||
                EntityManager.HasComponent<Hidden>(edge))
                throw new ArgumentException("Wait for the selected roads to finish updating, then try again.");

            // Include native road subareas as well as the index. Never detach
            // another road's transferred area, a retired area, or native paint.
            HashSet<Entity> areas = new HashSet<Entity>();
            if (m_GeneratedByOwner.TryGetValue(edge, out List<Entity> indexed)) areas.UnionWith(indexed);
            if (EntityManager.HasBuffer<SubArea>(edge))
                foreach (SubArea sub in EntityManager.GetBuffer<SubArea>(edge, true)) areas.Add(sub.m_Area);
            areas.RemoveWhere(area => !EntityManager.Exists(area) || EntityManager.HasComponent<Deleted>(area) ||
                !EntityManager.HasComponent<GeneratedSurface>(area) || !EntityManager.HasComponent<Owner>(area) ||
                EntityManager.GetComponentData<Owner>(area).m_Owner != edge);
            if (areas.Count == 0) throw new ArgumentException("Some selected roads have no generated surfaces to convert.");

            // Validate the whole road before handing anything over. A pending
            // native preview must finish before its geometry can be preserved.
            foreach (Entity area in areas)
                if (EntityManager.HasComponent<Temp>(area) || EntityManager.HasComponent<Hidden>(area) ||
                    !EntityManager.HasComponent<Area>(area) || !EntityManager.HasBuffer<Node>(area) ||
                    !EntityManager.HasBuffer<Triangle>(area) || EntityManager.GetBuffer<Triangle>(area, true).Length == 0 ||
                    (EntityManager.GetComponentData<Area>(area).m_Flags & (AreaFlags.Complete | AreaFlags.NoTriangles)) != AreaFlags.Complete)
                    throw new ArgumentException("Some surfaces are still updating. Wait for generation to finish, then try again.");

            SetManualSurfaceRoad(edge, true);
            // Native road cleanup also follows SubArea references. Remove both
            // directions of ownership so later road demolition leaves paint alone.
            if (EntityManager.HasBuffer<SubArea>(edge))
            {
                DynamicBuffer<SubArea> subAreas = EntityManager.GetBuffer<SubArea>(edge);
                for (int i = subAreas.Length - 1; i >= 0; i--)
                    if (areas.Contains(subAreas[i].m_Area)) subAreas.RemoveAt(i);
            }
            foreach (Entity area in areas)
            {
                // Keep the native nodes, triangles, projection ranges and narrow
                // overlay renderer. Only the automatic mesh/ownership machinery
                // is removed; the area tool can now author its own geometry.
                EntityManager.RemoveComponent<Owner>(area);
                EntityManager.RemoveComponent<GeneratedSurface>(area);
                EntityManager.RemoveComponent<SurfaceMeshTriangle>(area);
                EntityManager.RemoveComponent<ExactSurfaceBoundary>(area);
                EntityManager.RemoveComponent<SmallRoundaboutSurface>(area);
                EntityManager.RemoveComponent<SurfaceProjectionLimits>(area);
                // Per-node renderer offsets belong to the old boundary. Let the
                // native renderer calculate offsets for subsequent manual edits.
                EntityManager.RemoveComponent<Expand>(area);
                Area data = EntityManager.GetComponentData<Area>(area);
                data.m_Flags &= ~AreaFlags.Slave;
                EntityManager.SetComponentData(area, data);
                if (!EntityManager.HasComponent<Updated>(area)) EntityManager.AddComponent<Updated>(area);
                if (!EntityManager.HasComponent<BatchesUpdated>(area)) EntityManager.AddComponent<BatchesUpdated>(area);
                m_BoundaryOffsets.Remove(area); m_CommittedSurfacePatches.Remove(area);
                m_PreparedSurfaceMeshes.Remove(area); m_KnownAreaEntities.Remove(area); m_RetiredSurfaceAreas.Remove(area);
            }
            // Forget every stale index reference before any repair/deletion pass
            // can mistake detached paint for a missing road overlay.
            foreach (Entity owner in new List<Entity>(m_GeneratedByOwner.Keys))
            {
                List<Entity> owned = m_GeneratedByOwner[owner];
                int removed = owned.RemoveAll(area => areas.Contains(area));
                m_GeneratedCount = math.max(0, m_GeneratedCount - removed);
                if (owned.Count == 0) m_GeneratedByOwner.Remove(owner);
            }
            m_ManagedEdges.Remove(edge); m_ManualEdges.Remove(edge); m_DeferredEdges.Remove(edge);
            InvalidateGenerationCaches();
            Mod.Log.Info("Converted road surfaces to independent editable areas: edge=" + edge.Index + "; areas=" + areas.Count + ".");
            return areas.Count;
        }

        private void SetManualSurfaceRoad(Entity edge, bool manual)
        {
            if (manual && !EntityManager.HasComponent<ManualSurfaceRoad>(edge)) EntityManager.AddComponent<ManualSurfaceRoad>(edge);
            else if (!manual && EntityManager.HasComponent<ManualSurfaceRoad>(edge)) EntityManager.RemoveComponent<ManualSurfaceRoad>(edge);
            // Accepted native edits may still be waiting for their final handoff.
            foreach (RoadStyleCommit commit in m_RoadStyleCommits.Values)
                if (commit.Target == edge) commit.Manual = manual;
        }

        private void ResumeAutomaticRoadSurfaces(Entity edge)
        {
            if (!EntityManager.HasComponent<ManualSurfaceRoad>(edge))
                throw new ArgumentException("Some selected roads are already using automatic surfaces.");
            SetManualSurfaceRoad(edge, false);
            QueueRoadStyleRebuild(edge);
        }
    }
}
