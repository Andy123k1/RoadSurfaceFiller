using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private sealed class CitySurfaceSource
        {
            internal Entity Prefab;
            internal Edge Endpoints;
            internal Bezier4x3 Curve;
            internal int MissingSince = -1;
        }
        // Local rules already have independent saved records. City membership
        // was only a tag on the native edge being destroyed during a split.
        // Keep its last native topology until accepted replacement fragments
        // finish materializing, without enrolling any newly drawn branch.
        private readonly Dictionary<Entity, CitySurfaceSource> m_CitySurfaceSources = new Dictionary<Entity, CitySurfaceSource>();

        private void MaintainCitySurfaceSources()
        {
            HashSet<Entity> present = new HashSet<Entity>();
            using (NativeArray<Entity> members = GetEntityQuery(ComponentType.ReadOnly<CitySurfaceRuleMember>(),
                ComponentType.ReadOnly<Edge>(), ComponentType.Exclude<Temp>(), ComponentType.Exclude<Deleted>()).ToEntityArray(Allocator.Temp))
                foreach (Entity edge in members)
                {
                    present.Add(edge);
                    if ((!m_CitySurfaceSources.ContainsKey(edge) || EntityManager.HasComponent<Updated>(edge)) && IsRoadEdge(edge))
                        RememberCitySurfaceSource(edge);
                }
            foreach (Entity edge in new List<Entity>(m_CitySurfaceSources.Keys))
            {
                CitySurfaceSource source = m_CitySurfaceSources[edge];
                if (present.Contains(edge)) { source.MissingSince = -1; continue; }
                if (IsRoadEdge(edge))
                {
                    // Native in-place updates can replace an edge's archetype.
                    // Restore membership on this exact entity, not its neighbors.
                    if (!EntityManager.HasComponent<CitySurfaceRuleMember>(edge))
                    { EntityManager.AddComponent<CitySurfaceRuleMember>(edge); QueueRoadStyleRebuild(edge); }
                    RememberCitySurfaceSource(edge);
                }
                else if (source.MissingSince < 0) source.MissingSince = m_Frame;
                else if (m_Frame - source.MissingSince > 240) m_CitySurfaceSources.Remove(edge);
            }
        }

        private void RememberCitySurfaceSource(Entity edge)
        {
            if (!m_CitySurfaceSources.TryGetValue(edge, out CitySurfaceSource source))
                m_CitySurfaceSources.Add(edge, source = new CitySurfaceSource());
            source.Prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
            source.Endpoints = EntityManager.GetComponentData<Edge>(edge);
            source.Curve = EntityManager.GetComponentData<Curve>(edge).m_Bezier;
            source.MissingSince = -1;
        }

        private bool TryReadRoadStyleSource(Entity edge, out Entity prefab, out Edge endpoints, out Bezier4x3 curve)
        {
            if (IsRoadEdge(edge))
            {
                prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                endpoints = EntityManager.GetComponentData<Edge>(edge);
                curve = EntityManager.GetComponentData<Curve>(edge).m_Bezier;
                return true;
            }
            if (m_CitySurfaceSources.TryGetValue(edge, out CitySurfaceSource source))
            { prefab = source.Prefab; endpoints = source.Endpoints; curve = source.Curve; return true; }
            // A deleted original may still retain its components before the
            // native destruction barrier. Local records can use that provenance.
            if (EntityManager.HasComponent<Edge>(edge) && EntityManager.HasComponent<Curve>(edge) &&
                EntityManager.HasComponent<PrefabRef>(edge) && !EntityManager.HasComponent<Temp>(edge))
            {
                prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                if (EntityManager.HasComponent<RoadData>(prefab))
                { endpoints = EntityManager.GetComponentData<Edge>(edge); curve = EntityManager.GetComponentData<Curve>(edge).m_Bezier; return true; }
            }
            prefab = Entity.Null; endpoints = default; curve = default; return false;
        }

        private bool IsRoadStyleSource(Entity edge) => TryReadRoadStyleSource(edge, out _, out _, out _);
    }
}
