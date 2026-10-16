using System.Collections.Generic;
using Game.Areas;
using Game.Common;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private sealed class PlannedSurfaceArea
        {
            internal readonly Entity Prefab;
            internal readonly SurfacePatch Patch;
            internal readonly SurfacePatchStamp Stamp;
            internal Entity ReusedArea;
            internal PlannedSurfaceArea(Entity prefab, SurfacePatch patch) { Prefab = prefab; Patch = patch; Stamp = GetSurfacePatchStamp(patch); }
        }

        private struct SurfacePatchStamp { internal ulong Geometry, Topology; }

        private Entity m_PlanningSurfaceOwner;
        private readonly List<PlannedSurfaceArea> m_PlannedSurfaceAreas = new List<PlannedSurfaceArea>();
        private readonly Dictionary<Entity, SurfacePatchStamp> m_CommittedSurfacePatches = new Dictionary<Entity, SurfacePatchStamp>();
        private readonly Dictionary<Entity, float> m_ProjectionExpansion = new Dictionary<Entity, float>();
        private bool m_InitialMeshPreparation = true;
        private int m_GenerationCacheEpoch;
        private bool m_GenerationCachesDirty;
        private int m_PerformanceRoads, m_ReusedSurfaceAreas, m_UpdatedSurfaceAreas, m_NewSurfaceAreas;
        private double m_GenerationMilliseconds, m_LongestRoadMilliseconds;

        private void ResetSurfaceGenerationPerformance()
        {
            EndSurfacePlan(); m_CommittedSurfacePatches.Clear(); m_ProjectionExpansion.Clear();
            m_InitialMeshPreparation = true; m_GenerationCacheEpoch = 0; m_GenerationCachesDirty = true;
            ResetSurfaceGenerationCosts();
        }

        private void InvalidateGenerationCaches()
        {
            // Consume once at the next generation pass, after all events for the
            // frame. Native apply callbacks can also enqueue work after our pass.
            m_GenerationCachesDirty = true;
        }

        private void ClearGenerationCaches()
        {
            // Share expensive junction clipping across adjacent queued roads, even
            // when the work is spread over several frames. Any new queue request
            // invalidates the cache before generation uses changed road/rule data.
            m_GenerationCacheEpoch++;
            m_JunctionPavementDomains.Clear(); m_JunctionLaneProfiles.Clear(); m_JunctionPlantMasks.Clear();
            m_RoundaboutWalkMasks.Clear();
        }

        private void BeginSurfacePlan(Entity owner)
        {
            if (m_JunctionPavementDomains.Count + m_JunctionLaneProfiles.Count + m_JunctionPlantMasks.Count >= 128)
                ClearGenerationCaches();
            m_PlannedSurfaceAreas.Clear(); m_PlanningSurfaceOwner = owner;
        }

        private void EndSurfacePlan()
        {
            m_PlanningSurfaceOwner = Entity.Null; m_PlannedSurfaceAreas.Clear();
        }

        private static SurfacePatch CopySurfacePatch(SurfacePatch source)
        {
            SurfacePatch copy = new SurfacePatch
            {
                ProjectionMinimum = source.ProjectionMinimum, ProjectionMaximum = source.ProjectionMaximum,
                SmallRoundabout = source.SmallRoundabout, ExactBoundary = source.ExactBoundary,
                DedicatedLanesClipped = source.DedicatedLanesClipped, PlantedAreasClipped = source.PlantedAreasClipped,
                BevelCount = source.BevelCount
            };
            copy.Nodes.AddRange(source.Nodes); copy.Triangles.AddRange(source.Triangles);
            foreach (SurfacePatch part in source.Parts) copy.Parts.Add(CopySurfacePatch(part));
            return copy;
        }

        private static ulong MixPatchStamp(ulong hash, uint value)
        {
            unchecked { return (hash ^ value) * 1099511628211UL; }
        }

        private static SurfacePatchStamp GetSurfacePatchStamp(SurfacePatch patch)
        {
            // Keep only a compact stamp after completion, rather than a managed
            // copy of every city's full node and triangle arrays. Native boundary
            // and saved triangle buffers are also checked before skipping work.
            ulong geometry = MixPatchStamp(14695981039346656037UL, (uint)patch.Count);
            geometry = MixPatchStamp(geometry, math.asuint(patch.ProjectionMinimum));
            geometry = MixPatchStamp(geometry, math.asuint(patch.ProjectionMaximum));
            geometry = MixPatchStamp(geometry, (patch.ExactBoundary ? 1u : 0u) | (patch.SmallRoundabout ? 2u : 0u));
            foreach (AreaNode node in patch.Nodes)
            {
                geometry = MixPatchStamp(geometry, math.asuint(node.m_Position.x));
                geometry = MixPatchStamp(geometry, math.asuint(node.m_Position.y));
                geometry = MixPatchStamp(geometry, math.asuint(node.m_Position.z));
                geometry = MixPatchStamp(geometry, math.asuint(node.m_Elevation));
            }
            ulong topology = MixPatchStamp(14695981039346656037UL, (uint)patch.Triangles.Count);
            foreach (int3 triangle in patch.Triangles)
            {
                topology = MixPatchStamp(topology, (uint)triangle.x);
                topology = MixPatchStamp(topology, (uint)triangle.y);
                topology = MixPatchStamp(topology, (uint)triangle.z);
            }
            return new SurfacePatchStamp { Geometry = geometry, Topology = topology };
        }

        private bool CanReuseSurfaceArea(Entity area, Entity owner, Entity prefab)
        {
            return EntityManager.Exists(area) && !EntityManager.HasComponent<Deleted>(area) &&
                !EntityManager.HasComponent<Game.Tools.Hidden>(area) && !EntityManager.HasComponent<Game.Tools.Temp>(area) &&
                EntityManager.HasComponent<Game.Areas.Area>(area) && EntityManager.HasBuffer<Triangle>(area) &&
                EntityManager.HasComponent<GeneratedSurface>(area) && EntityManager.HasBuffer<AreaNode>(area) &&
                IsPrivateRoadSurface(area, owner) && EntityManager.GetComponentData<PrefabRef>(area).m_Prefab == prefab;
        }

        private bool SurfaceAreaMatchesPlan(Entity area, PlannedSurfaceArea plan)
        {
            if (!m_CommittedSurfacePatches.TryGetValue(area, out SurfacePatchStamp previous) ||
                previous.Geometry != plan.Stamp.Geometry || previous.Topology != plan.Stamp.Topology ||
                !PendingBoundaryMatches(plan.Patch, area) || !m_PreparedSurfaceMeshes.Contains(area) ||
                !EntityManager.HasBuffer<SurfaceMeshTriangle>(area) || !EntityManager.HasBuffer<Triangle>(area)) return false;
            var native = EntityManager.GetBuffer<Triangle>(area, true);
            var data = EntityManager.GetComponentData<Game.Areas.Area>(area);
            if ((data.m_Flags & AreaFlags.Complete) == 0 || (data.m_Flags & AreaFlags.NoTriangles) != 0 || native.Length != plan.Patch.Triangles.Count) return false;
            var mesh = EntityManager.GetBuffer<SurfaceMeshTriangle>(area, true);
            if (mesh.Length != plan.Patch.Triangles.Count) return false;
            for (int i = 0; i < mesh.Length; i++)
                if (math.any(mesh[i].Indices != plan.Patch.Triangles[i]) || math.any(native[i].m_Indices != mesh[i].Indices)) return false;
            return true;
        }

        private void CommitSurfacePlan(Entity owner)
        {
            m_PlanningSurfaceOwner = Entity.Null;
            List<Entity> previous = m_GeneratedByOwner.TryGetValue(owner, out List<Entity> owned)
                ? new List<Entity>(owned) : new List<Entity>();
            HashSet<Entity> used = new HashSet<Entity>();
            // Reserve unchanged patches first so a changed corner cannot consume
            // the render entity of an unchanged road body or sidewalk.
            foreach (PlannedSurfaceArea plan in m_PlannedSurfaceAreas)
                foreach (Entity area in previous)
                    if (!used.Contains(area) && CanReuseSurfaceArea(area, owner, plan.Prefab) && SurfaceAreaMatchesPlan(area, plan))
                    { plan.ReusedArea = area; used.Add(area); m_ReusedSurfaceAreas++; break; }

            foreach (PlannedSurfaceArea plan in m_PlannedSurfaceAreas)
            {
                if (plan.ReusedArea != Entity.Null) continue;
                Entity best = Entity.Null; float distance = float.PositiveInfinity;
                float3 center = Average(plan.Patch.Nodes);
                foreach (Entity area in previous)
                {
                    if (used.Contains(area) || !CanReuseSurfaceArea(area, owner, plan.Prefab) || !TryGetAreaCenter(area, out float3 oldCenter)) continue;
                    float candidate = math.distancesq(center, oldCenter);
                    if (candidate < distance) { best = area; distance = candidate; }
                }
                if (best != Entity.Null)
                {
                    UpdateSurfaceAreaInPlace(best, plan.Patch);
                    plan.ReusedArea = best; used.Add(best); m_UpdatedSurfaceAreas++;
                }
                else QueueArea(owner, plan.Prefab, plan.Patch);
            }

            // Every desired patch is now either retained, updated, or requested.
            // Retire unused wide shoulders/corners in the same update as their
            // replacements, so downgrades cannot leave stale shoulder paint.
            List<Entity> retained = new List<Entity>();
            foreach (Entity area in previous)
            {
                if (used.Contains(area)) retained.Add(area);
                else { RetireOwnedSurfaceArea(owner, area); m_GeneratedCount = math.max(0, m_GeneratedCount - 1); }
            }
            if (retained.Count > 0) m_GeneratedByOwner[owner] = retained;
            else m_GeneratedByOwner.Remove(owner);
            foreach (PlannedSurfaceArea plan in m_PlannedSurfaceAreas)
                if (plan.ReusedArea != Entity.Null) m_CommittedSurfacePatches[plan.ReusedArea] = plan.Stamp;
        }

        private void UpdateSurfaceAreaInPlace(Entity area, SurfacePatch patch)
        {
            // Preserve the native area identity and render batch. Updated tells
            // native geometry/search/render systems to consume the new boundary;
            // our existing projection hooks restore its explicit ribbon mesh.
            int count = patch.Count;
            if (count > 1 && math.distancesq(patch.Nodes[0].m_Position.xz, patch.Nodes[count - 1].m_Position.xz) < 0.00000001f) count--;
            DynamicBuffer<AreaNode> nodes = EntityManager.GetBuffer<AreaNode>(area);
            nodes.ResizeUninitialized(count);
            for (int i = 0; i < count; i++) nodes[i] = patch.Nodes[i];
            if (!EntityManager.HasBuffer<SurfaceMeshTriangle>(area)) EntityManager.AddBuffer<SurfaceMeshTriangle>(area);
            DynamicBuffer<SurfaceMeshTriangle> mesh = EntityManager.GetBuffer<SurfaceMeshTriangle>(area);
            mesh.ResizeUninitialized(patch.Triangles.Count);
            for (int i = 0; i < patch.Triangles.Count; i++) mesh[i] = new SurfaceMeshTriangle { Indices = patch.Triangles[i] };
            SetSurfaceMarker<ExactSurfaceBoundary>(area, patch.ExactBoundary);
            SetSurfaceMarker<SmallRoundaboutSurface>(area, patch.SmallRoundabout);
            bool limited = math.isfinite(patch.ProjectionMinimum) || math.isfinite(patch.ProjectionMaximum);
            if (limited)
            {
                var limits = new SurfaceProjectionLimits { Minimum = patch.ProjectionMinimum, Maximum = patch.ProjectionMaximum };
                if (EntityManager.HasComponent<SurfaceProjectionLimits>(area)) EntityManager.SetComponentData(area, limits);
                else EntityManager.AddComponentData(area, limits);
            }
            else if (EntityManager.HasComponent<SurfaceProjectionLimits>(area)) EntityManager.RemoveComponent<SurfaceProjectionLimits>(area);
            m_BoundaryOffsets.Remove(area); m_PreparedSurfaceMeshes.Remove(area);
            if (!EntityManager.HasComponent<Updated>(area)) EntityManager.AddComponent<Updated>(area);
        }

        private void SetSurfaceMarker<T>(Entity area, bool present) where T : unmanaged, IComponentData
        {
            if (present && !EntityManager.HasComponent<T>(area)) EntityManager.AddComponent<T>(area);
            else if (!present && EntityManager.HasComponent<T>(area)) EntityManager.RemoveComponent<T>(area);
        }

        private void CompleteMaterializedSurfaceAreas()
        {
            // The render hook has already matched these entities by owner,
            // material and exact polygon. Complete them directly next frame;
            // only the fallback discovery pass scans every native city area.
            for (int i = m_PendingAreas.Count - 1; i >= 0; i--)
            {
                PendingArea pending = m_PendingAreas[i];
                Entity area = pending.MaterializedArea;
                if (area == Entity.Null || !EntityManager.Exists(area) || EntityManager.HasComponent<Deleted>(area) ||
                    !EntityManager.HasComponent<Game.Areas.Area>(area) || !EntityManager.HasBuffer<Triangle>(area) ||
                    !IsPrivateRoadSurface(area, pending.Owner) || EntityManager.GetComponentData<PrefabRef>(area).m_Prefab != pending.Prefab ||
                    EntityManager.GetBuffer<Triangle>(area, true).Length == 0 ||
                    (EntityManager.GetComponentData<Game.Areas.Area>(area).m_Flags & AreaFlags.Complete) == 0) continue;
                FinishPendingSurfaceArea(i, area);
            }
        }

        private void FinishPendingSurfaceArea(int index, Entity area)
        {
            PendingArea pending = m_PendingAreas[index];
            m_KnownAreaEntities.Add(area); AttachPendingSurfaceMesh(area, pending);
            if (!EntityManager.HasComponent<GeneratedSurface>(area)) EntityManager.AddComponent<GeneratedSurface>(area);
            AddGeneratedArea(pending.Owner, area); m_ManagedEdges.Add(pending.Owner);
            m_CommittedSurfacePatches[area] = GetSurfacePatchStamp(pending.Patch);
            if (!m_LoggedNativeAreaCompletion)
            {
                Mod.Log.Info("Native area generation produced a permanent surface entity. Triangles: " + EntityManager.GetBuffer<Triangle>(area, true).Length +
                    "; geometry=" + EntityManager.HasComponent<Game.Areas.Geometry>(area) +
                    "; render batch=" + EntityManager.HasComponent<Game.Areas.Batch>(area) + ".");
                m_LoggedNativeAreaCompletion = true;
            }
            if (EntityManager.Exists(pending.Definition)) EntityManager.DestroyEntity(pending.Definition);
            RemovePendingArea(index);
        }

        private void AttachPendingSurfaceMesh(Entity area, PendingArea pending)
        {
            if (EntityManager.HasBuffer<SurfaceMeshTriangle>(area)) return;
            DynamicBuffer<SurfaceMeshTriangle> mesh = EntityManager.AddBuffer<SurfaceMeshTriangle>(area);
            foreach (int3 triangle in pending.Patch.Triangles) mesh.Add(new SurfaceMeshTriangle { Indices = triangle });
            SetSurfaceMarker<SmallRoundaboutSurface>(area, pending.Patch.SmallRoundabout);
            SetSurfaceMarker<ExactSurfaceBoundary>(area, pending.Patch.ExactBoundary);
            if (math.isfinite(pending.Patch.ProjectionMinimum) || math.isfinite(pending.Patch.ProjectionMaximum))
            {
                var limits = new SurfaceProjectionLimits { Minimum = pending.Patch.ProjectionMinimum, Maximum = pending.Patch.ProjectionMaximum };
                if (EntityManager.HasComponent<SurfaceProjectionLimits>(area)) EntityManager.SetComponentData(area, limits);
                else EntityManager.AddComponentData(area, limits);
            }
            if (!EntityManager.HasComponent<Updated>(area)) EntityManager.AddComponent<Updated>(area);
        }

        private static double GenerationMillisecondsSince(long startedAt)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) * (1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

        private void RecordSurfaceGenerationCost(double milliseconds)
        {
            m_PerformanceRoads++; m_GenerationMilliseconds += milliseconds;
            m_LongestRoadMilliseconds = System.Math.Max(m_LongestRoadMilliseconds, milliseconds);
        }

        private void LogSurfaceGenerationCosts()
        {
            Mod.Log.Info("Surface refresh completed. Managed areas: " + m_GeneratedCount +
                "; roads=" + m_PerformanceRoads + "; generation elapsed=" + m_GenerationMilliseconds.ToString("F1") +
                " ms; longest road=" + m_LongestRoadMilliseconds.ToString("F1") + " ms; unchanged areas=" + m_ReusedSurfaceAreas +
                "; updated in place=" + m_UpdatedSurfaceAreas + "; new definitions=" + m_NewSurfaceAreas + ".");
            ResetSurfaceGenerationCosts();
        }

        private void ResetSurfaceGenerationCosts()
        {
            m_PerformanceRoads = m_ReusedSurfaceAreas = m_UpdatedSurfaceAreas = m_NewSurfaceAreas = 0;
            m_GenerationMilliseconds = m_LongestRoadMilliseconds = 0;
        }
    }
}
