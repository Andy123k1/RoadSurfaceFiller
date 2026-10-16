using System;
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
        private readonly HashSet<Entity> m_LoggedMainRoadJunctions = new HashSet<Entity>();
        private readonly Dictionary<Entity, bool> m_AlleyApproachLayouts = new Dictionary<Entity, bool>();

        private struct JunctionStreet
        {
            internal Entity Prefab;
            internal float Width;
            internal float2 Direction;
            internal string Name;
        }

        private bool IsAlleyApproach(Entity edge)
        {
            if (!IsRoadEdge(edge) || IsHighway(edge)) return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
            if (!EntityManager.HasComponent<NetGeometryData>(prefab) ||
                EntityManager.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth > 8.5f) return false;
            Entity composition = EntityManager.GetComponentData<Composition>(edge).m_Edge;
            if (m_AlleyApproachLayouts.TryGetValue(composition, out bool cached)) return cached;
            // A composition can still be initializing when selection is read.
            // Cache only a usable profile so that an early lookup cannot keep
            // misclassifying the alley after the native geometry is ready.
            if (!TryReadSurfaceLayout(composition, out SurfaceLayout layout) || layout.Roads.Count == 0) return false;
            bool alley = true;
            float roadHeight = float.NegativeInfinity;
            foreach (RoadBand road in layout.Roads) roadHeight = math.max(roadHeight, math.max(road.LowHeight, road.HighHeight));
            foreach (LaneSpan walk in layout.Sidewalks)
                if (walk.Height > roadHeight + 0.12f) alley = false;
            m_AlleyApproachLayouts[composition] = alley;
            return alley;
        }

        private Entity GetOrdinaryJunctionReferencePrefab(Entity node)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return Entity.Null;
            List<JunctionStreet> streets = new List<JunctionStreet>();
            List<JunctionStreet> highways = new List<JunctionStreet>();
            bool hasAlley = false, onlyAlleys = true;
            int roadCount = 0;
            foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity edge = connection.m_Edge;
                if (!IsRoadEdge(edge)) continue;
                roadCount++;
                bool highway = IsHighway(edge);
                if (!highway)
                {
                    bool alley = IsAlleyApproach(edge);
                    hasAlley |= alley;
                    onlyAlleys &= alley;
                }
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                Edge endpoints = EntityManager.GetComponentData<Edge>(edge);
                Curve curve = EntityManager.GetComponentData<Curve>(edge);
                float3 tangent = endpoints.m_Start == node ? MathUtils.StartTangent(curve.m_Bezier)
                    : -MathUtils.EndTangent(curve.m_Bezier);
                (highway ? highways : streets).Add(new JunctionStreet
                {
                    Prefab = prefab,
                    Width = EntityManager.HasComponent<NetGeometryData>(prefab)
                        ? EntityManager.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth : 0f,
                    Direction = math.normalizesafe(tangent.xz),
                    Name = m_PrefabSystem.GetPrefabName(prefab) ?? string.Empty
                });
            }
            // Keep ordinary two-segment material transitions and dead ends at
            // their existing boundary. A branching intersection shares one fill.
            // A narrow flat alley must yield to its connected highway, including
            // a curved merge. Other mixed street/highway junctions retain their
            // existing street reference. A direct alley-to-road transition also
            // inherits the larger road's junction material.
            if (hasAlley && onlyAlleys && highways.Count > 0) streets = highways;
            if (roadCount < 3 && !(hasAlley && (highways.Count > 0 || !onlyAlleys))) return Entity.Null;
            if (streets.Count == 0) return Entity.Null;
            int best = -1;
            float widest = -1f, bestContinuity = -1f;
            for (int i = 0; i < streets.Count; i++)
            {
                JunctionStreet candidate = streets[i];
                float continuity = -1f;
                for (int j = 0; j < streets.Count; j++)
                    if (j != i && math.lengthsq(candidate.Direction) > 0.5f && math.lengthsq(streets[j].Direction) > 0.5f)
                        continuity = math.max(continuity, -math.dot(candidate.Direction, streets[j].Direction));
                bool wider = candidate.Width > widest + 0.01f;
                bool sameWidth = math.abs(candidate.Width - widest) <= 0.01f;
                bool straighter = continuity > bestContinuity + 0.001f;
                bool sameContinuity = math.abs(continuity - bestContinuity) <= 0.001f;
                if (best < 0 || wider || (sameWidth && (straighter || (sameContinuity &&
                    string.Compare(candidate.Name, streets[best].Name, StringComparison.OrdinalIgnoreCase) < 0))))
                {
                    best = i;
                    widest = candidate.Width;
                    bestContinuity = continuity;
                }
            }
            return streets[best].Prefab;
        }

        private int MixOrdinaryJunctionReference(int hash, Entity node)
        {
            Entity reference = GetOrdinaryJunctionReferencePrefab(node);
            hash = Mix(Mix(hash, reference.Index), reference.Version);
            if (reference == Entity.Null || !EntityManager.HasBuffer<ConnectedEdge>(node)) return hash;
            // The shared fill and branch cutoff depend on all the adjoining
            // geometry and on segment eligibility, not just the chosen prefab.
            int junction = 0;
            unchecked
            {
                foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                {
                    Entity edge = connection.m_Edge;
                    if (!IsRoadEdge(edge)) continue;
                    int part = Mix(edge.Index, edge.Version);
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                    part = Mix(Mix(part, prefab.Index), prefab.Version);
                    part = Mix(part, EntityManager.HasComponent<SurfaceSegmentSelected>(edge) ? 1 : 0);
                    part = Mix(part, EntityManager.HasComponent<SurfaceSegmentExcluded>(edge) ? 1 : 0);
                    Composition composition = EntityManager.GetComponentData<Composition>(edge);
                    part = Mix(part, composition.m_Edge.Index);
                    Edge endpoints = EntityManager.GetComponentData<Edge>(edge);
                    if (endpoints.m_Start == node && EntityManager.HasComponent<StartNodeGeometry>(edge))
                        part = MixNodeGeometry(part, EntityManager.GetComponentData<StartNodeGeometry>(edge).m_Geometry);
                    if (endpoints.m_End == node && EntityManager.HasComponent<EndNodeGeometry>(edge))
                        part = MixNodeGeometry(part, EntityManager.GetComponentData<EndNodeGeometry>(edge).m_Geometry);
                    if (EntityManager.HasComponent<EdgeGeometry>(edge))
                    {
                        EdgeGeometry geometry = EntityManager.GetComponentData<EdgeGeometry>(edge);
                        part = MixSegment(part, endpoints.m_Start == node ? geometry.m_Start : geometry.m_End);
                    }
                    junction += part;
                }
            }
            return Mix(hash, junction);
        }

        private void ScheduleJunctionGeometryChecks(Entity node)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return;
            foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                if (IsRoadEdge(connection.m_Edge) && !m_GeometryChecks.ContainsKey(connection.m_Edge))
                    m_GeometryChecks.Add(connection.m_Edge, m_Frame + 1);
        }

        private bool TryGetConnectedStreetPrefab(Entity node, out Entity prefab)
        {
            prefab = GetJunctionReferencePrefab(node, true);
            return prefab != Entity.Null;
        }

        private bool HasStreetSurfaceJunction(Entity edge)
        {
            Edge endpoints = EntityManager.GetComponentData<Edge>(edge);
            if (EntityManager.HasComponent<StartNodeGeometry>(edge) &&
                EntityManager.GetComponentData<StartNodeGeometry>(edge).m_Geometry.m_MiddleRadius > 0.01f) return true;
            if (EntityManager.HasComponent<EndNodeGeometry>(edge) &&
                EntityManager.GetComponentData<EndNodeGeometry>(edge).m_Geometry.m_MiddleRadius > 0.01f) return true;
            return IsHighway(edge) && (TryGetConnectedStreetPrefab(endpoints.m_Start, out _) ||
                TryGetConnectedStreetPrefab(endpoints.m_End, out _));
        }

        private Entity GetJunctionReferencePrefab(Entity node, bool streetsOnly)
        {
            Entity result = Entity.Null;
            float widest = -1f;
            if (!EntityManager.Exists(node) || !EntityManager.HasBuffer<ConnectedEdge>(node)) return result;
            foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                if (!IsRoadEdge(connection.m_Edge)) continue;
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(connection.m_Edge).m_Prefab;
                if (streetsOnly && IsHighwayPrefab(prefab)) continue;
                float width = EntityManager.HasComponent<NetGeometryData>(prefab)
                    ? EntityManager.GetComponentData<NetGeometryData>(prefab).m_DefaultWidth : 0f;
                // Every approaching segment must resolve the same street reference,
                // including when the roundabout has several different road types.
                if (width > widest || (width == widest && (result == Entity.Null || prefab.Index < result.Index)))
                {
                    result = prefab;
                    widest = width;
                }
            }
            return result;
        }

        private bool RuleMatchesRoadContext(Setting setting, int slot, Entity roadPrefab, bool streetContext)
        {
            if (!MatchesCategory(roadPrefab, setting.GetCategory(slot), streetContext)) return false;
            string filter = setting.GetRoadPrefab(slot);
            // An exact highway-prefab rule is still a highway rule even if its
            // category selector says All roads. It cannot paint a street junction.
            if (streetContext && IsHighwayPrefab(roadPrefab) && !string.IsNullOrEmpty(filter)) return false;
            return string.IsNullOrEmpty(filter) || string.Equals(filter,
                m_PrefabSystem.GetPrefabName(roadPrefab), StringComparison.OrdinalIgnoreCase);
        }

        private Curve JunctionMaterialSeed(Curve curve, Entity node)
        {
            if (!EntityManager.HasComponent<Game.Net.Node>(node)) return curve;
            float3 position = EntityManager.GetComponentData<Game.Net.Node>(node).m_Position;
            curve.m_Bezier = new Bezier4x3(position, position, position, position);
            return curve;
        }
    }
}
