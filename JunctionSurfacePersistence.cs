using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    // A separate saved entity survives the road tool replacing either the
    // selected edge or the junction node. Keep the old node component's
    // serialized layout intact so cities from earlier versions still load.
    public struct SavedJunctionSurface : IComponentData, ISerializable
    {
        public Entity Node, RoadPrefab, SurfacePrefab;
        public float3 Position;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(Node); writer.Write(RoadPrefab);
            writer.Write(SurfacePrefab); writer.Write(Position);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out Node); reader.Read(out RoadPrefab);
            reader.Read(out SurfacePrefab); reader.Read(out Position);
        }
    }

    // A companion marker preserves the serialized layout of existing saves.
    // Its absence retains the original through-bus-lane behavior.
    public struct JunctionPavementCoversBusLanes : IComponentData, IEmptySerializable { }

    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private readonly Dictionary<Entity, Entity> m_JunctionSurfaceRecords = new Dictionary<Entity, Entity>();
        private readonly HashSet<Entity> m_SavedJunctionRecords = new HashSet<Entity>();
        private readonly Dictionary<Entity, Entity> m_JunctionReplacementCandidates = new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, int> m_MissingJunctionRecords = new Dictionary<Entity, int>();
        private bool m_JunctionRecordsInitialized;
        private int m_LoggedJunctionRestores;

        private void ResetJunctionSurfaceRecords()
        {
            m_JunctionSurfaceRecords.Clear(); m_SavedJunctionRecords.Clear();
            m_JunctionReplacementCandidates.Clear(); m_MissingJunctionRecords.Clear();
            m_JunctionRecordsInitialized = false; m_LoggedJunctionRestores = 0;
            m_JunctionReferenceChecks.Clear(); m_UnresolvedJunctionReferences.Clear();
            m_LoggedJunctionReferenceRepairs = m_LoggedJunctionReferenceFailures = 0;
        }

        private bool IsPermanentRoadNode(Entity node)
        {
            return EntityManager.Exists(node) && !EntityManager.HasComponent<Deleted>(node) &&
                !EntityManager.HasComponent<Temp>(node) && EntityManager.HasComponent<Game.Net.Node>(node) &&
                EntityManager.HasBuffer<ConnectedEdge>(node);
        }

        private bool TryGetSavedJunctionSurface(Entity node, out SavedJunctionSurface saved)
        {
            saved = default;
            if (!m_JunctionSurfaceRecords.TryGetValue(node, out Entity record) ||
                !EntityManager.Exists(record) || !EntityManager.HasComponent<SavedJunctionSurface>(record)) return false;
            saved = EntityManager.GetComponentData<SavedJunctionSurface>(record);
            return saved.Node == node;
        }

        private SurfaceChoice GetSavedJunctionChoice(Entity node)
        {
            if (!TryGetSavedJunctionSurface(node, out SavedJunctionSurface saved) ||
                !IsLoadedJunctionSurface(saved.SurfacePrefab) || m_UnresolvedJunctionReferences.Contains(m_JunctionSurfaceRecords[node])) return null;
            Entity overlay = GetNetworkOverlayPrefab(saved.SurfacePrefab);
            return overlay == Entity.Null ? null : new SurfaceChoice(overlay);
        }

        private bool JunctionCoversBusLanes(Entity node) =>
            m_JunctionSurfaceRecords.TryGetValue(node, out Entity record) && EntityManager.Exists(record) &&
            EntityManager.HasComponent<JunctionPavementCoversBusLanes>(record);

        private bool SavedJunctionRenderersReady()
        {
            foreach (Entity record in m_SavedJunctionRecords)
                if (EntityManager.Exists(record))
                {
                    Entity surface = EntityManager.GetComponentData<SavedJunctionSurface>(record).SurfacePrefab;
                    if (!m_UnresolvedJunctionReferences.Contains(record) && IsLoadedJunctionSurface(surface) && GetNetworkOverlayPrefab(surface) == Entity.Null)
                    {
                        RuntimeState.Status = "Preparing saved junction surface renderers.";
                        return false;
                    }
                }
            return true;
        }

        private bool SaveJunctionSurface(Entity node, Entity road, Setting setting, SurfaceChoice selected = null, bool coverBusLanes = false)
        {
            if (!IsPermanentRoadNode(node) || !IsRoadEdge(road)) return false;
            Entity roadPrefab = EntityManager.GetComponentData<PrefabRef>(road).m_Prefab;
            SurfaceChoice choice = selected ?? EffectiveRoadJunctionOption(road, setting)?.Surface;
            if (choice == null) return false;
            string name = m_PrefabSystem.GetPrefabName(choice.Prefab);
            if (IsNetworkOverlayPrefab(name)) name = name.Substring(NetworkOverlayPrefix.Length);
            if (!RuntimeState.TryGetSurface(name, out Entity originalSurface)) return false;

            if (!m_JunctionSurfaceRecords.TryGetValue(node, out Entity record) || !EntityManager.Exists(record))
            {
                record = EntityManager.CreateEntity(typeof(SavedJunctionSurface), typeof(PrefabRef));
                m_JunctionSurfaceRecords[node] = record;
                m_SavedJunctionRecords.Add(record);
            }
            EntityManager.SetComponentData(record, new SavedJunctionSurface {
                Node = node, RoadPrefab = roadPrefab, SurfacePrefab = originalSurface,
                Position = EntityManager.GetComponentData<Game.Net.Node>(node).m_Position
            });
            // PrefabRef also makes the selected asset visible to native prefab
            // serialization when its supplying road is later removed.
            EntityManager.SetComponentData(record, new PrefabRef { m_Prefab = originalSurface });
            StoreJunctionSurfaceIdentity(record, originalSurface, roadPrefab);
            m_JunctionReferenceChecks.Remove(record); m_UnresolvedJunctionReferences.Remove(record);
            if (coverBusLanes && !EntityManager.HasComponent<JunctionPavementCoversBusLanes>(record))
                EntityManager.AddComponent<JunctionPavementCoversBusLanes>(record);
            else if (!coverBusLanes && EntityManager.HasComponent<JunctionPavementCoversBusLanes>(record))
                EntityManager.RemoveComponent<JunctionPavementCoversBusLanes>(record);
            m_MissingJunctionRecords.Remove(record);
            return true;
        }

        private void ClearSavedJunctionSurface(Entity node)
        {
            if (m_JunctionSurfaceRecords.TryGetValue(node, out Entity record)) RemoveJunctionRecord(record);
        }

        private void RemoveJunctionRecord(Entity record)
        {
            foreach (Entity node in new List<Entity>(m_JunctionSurfaceRecords.Keys))
                if (m_JunctionSurfaceRecords[node] == record) m_JunctionSurfaceRecords.Remove(node);
            foreach (Entity candidate in new List<Entity>(m_JunctionReplacementCandidates.Keys))
                if (m_JunctionReplacementCandidates[candidate] == record) m_JunctionReplacementCandidates.Remove(candidate);
            m_SavedJunctionRecords.Remove(record); m_MissingJunctionRecords.Remove(record);
            m_JunctionReferenceChecks.Remove(record); m_UnresolvedJunctionReferences.Remove(record);
            if (EntityManager.Exists(record)) EntityManager.DestroyEntity(record);
        }

        private void UpdateSavedJunctionSurfaces(Setting setting)
        {
            if (!m_JunctionRecordsInitialized)
            {
                using (NativeArray<Entity> records = GetEntityQuery(ComponentType.ReadOnly<SavedJunctionSurface>()).ToEntityArray(Allocator.Temp))
                    foreach (Entity record in records)
                    {
                        m_SavedJunctionRecords.Add(record);
                        SavedJunctionSurface saved = EntityManager.GetComponentData<SavedJunctionSurface>(record);
                        if (saved.Node != Entity.Null) m_JunctionSurfaceRecords[saved.Node] = record;
                    }
                m_JunctionRecordsInitialized = true;
            }

            // Remember the native tool's exact original-node relationship while
            // it is still a preview. A committed replacement loses Temp, but
            // keeps its entity identity, allowing an exact transfer next frame.
            if (m_SavedJunctionRecords.Count > 0)
            {
                foreach (Entity candidate in new List<Entity>(m_JunctionReplacementCandidates.Keys))
                {
                    bool discard = !EntityManager.Exists(candidate) || EntityManager.HasComponent<Deleted>(candidate);
                    Entity record = m_JunctionReplacementCandidates[candidate];
                    if (!discard && !EntityManager.HasComponent<Temp>(candidate) && EntityManager.Exists(record))
                        discard = IsPermanentRoadNode(EntityManager.GetComponentData<SavedJunctionSurface>(record).Node);
                    if (discard)
                        m_JunctionReplacementCandidates.Remove(candidate);
                }
                using (NativeArray<Entity> previews = GetEntityQuery(ComponentType.ReadOnly<Game.Net.Node>(),
                    ComponentType.ReadOnly<Temp>(), ComponentType.Exclude<Deleted>()).ToEntityArray(Allocator.Temp))
                    foreach (Entity preview in previews)
                    {
                        Temp temp = EntityManager.GetComponentData<Temp>(preview);
                        if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel | TempFlags.Duplicate)) != 0) continue;
                        Entity original = temp.m_Original;
                        if (m_JunctionSurfaceRecords.TryGetValue(original, out Entity record))
                            m_JunctionReplacementCandidates[preview] = record;
                    }
            }

            foreach (Entity record in new List<Entity>(m_SavedJunctionRecords))
            {
                if (!EntityManager.Exists(record)) { RemoveJunctionRecord(record); continue; }
                SavedJunctionSurface saved = EntityManager.GetComponentData<SavedJunctionSurface>(record);
                Entity node = saved.Node;
                if (!IsPermanentRoadNode(node)) node = FindReplacementJunction(record, saved.Position);
                if (node == Entity.Null)
                {
                    // Keep the record across the native tool's deferred apply
                    // frames. A completely deleted junction is eventually
                    // forgotten rather than inherited by unrelated new roads.
                    if (!m_MissingJunctionRecords.TryGetValue(record, out int since))
                        m_MissingJunctionRecords[record] = m_Frame;
                    else if (m_Frame - since >= 120) RemoveJunctionRecord(record);
                    continue;
                }
                m_MissingJunctionRecords.Remove(record);
                float3 position = EntityManager.GetComponentData<Game.Net.Node>(node).m_Position;
                if (node != saved.Node)
                {
                    if (m_JunctionSurfaceRecords.TryGetValue(node, out Entity other) && other != record)
                        { RemoveJunctionRecord(record); continue; }
                    m_JunctionSurfaceRecords.Remove(saved.Node);
                    m_JunctionSurfaceRecords[node] = record;
                    Entity previous = saved.Node;
                    saved.Node = node;
                    QueueJunctionRebuild(node);
                    if (m_LoggedJunctionRestores++ < 8)
                        Mod.Log.Info("Retained saved junction pavement after road edit: original node=" + previous.Index +
                            "; replacement node=" + node.Index + "; surface='" + JunctionPrefabName(saved.SurfacePrefab) + "'.");
                }
                bool repaired = RecoverSavedJunctionReferences(record, ref saved);
                if (repaired || math.any(saved.Position != position) || EntityManager.GetComponentData<SavedJunctionSurface>(record).Node != node)
                {
                    saved.Position = position;
                    EntityManager.SetComponentData(record, saved);
                }
                if (repaired) QueueJunctionRebuild(node);
            }

            // Upgrade the earlier node/edge-only preferences without changing
            // their component schema or their currently selected material.
            using (NativeArray<Entity> nodes = GetEntityQuery(ComponentType.ReadOnly<JunctionSurfacePriority>(),
                ComponentType.ReadOnly<Game.Net.Node>(), ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>()).ToEntityArray(Allocator.Temp))
                foreach (Entity node in nodes)
                    if (!TryGetSavedJunctionSurface(node, out _) && IsPermanentRoadNode(node))
                    {
                        Entity road = GetPreferredJunctionRoad(node);
                        if (road != Entity.Null && SaveJunctionSurface(node, road, setting)) QueueJunctionRebuild(node);
                    }
        }

        private Entity FindReplacementJunction(Entity record, float3 position)
        {
            foreach (KeyValuePair<Entity, Entity> candidate in m_JunctionReplacementCandidates)
                if (candidate.Value == record && IsPermanentRoadNode(candidate.Key)) return candidate.Key;
            // Some native apply paths do not leave an original-node link. Limit
            // recovery to the same junction position, including its elevation.
            Entity nearest = Entity.Null;
            float distance = 0.25f * 0.25f;
            using (NativeArray<Entity> nodes = GetEntityQuery(ComponentType.ReadOnly<Game.Net.Node>(),
                ComponentType.ReadOnly<ConnectedEdge>(), ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>()).ToEntityArray(Allocator.Temp))
                foreach (Entity node in nodes)
                {
                    if (m_JunctionSurfaceRecords.TryGetValue(node, out Entity existing) && existing != record) continue;
                    float next = math.distancesq(EntityManager.GetComponentData<Game.Net.Node>(node).m_Position, position);
                    if (next <= distance && IsEditableSurfaceJunction(node)) { distance = next; nearest = node; }
                }
            return nearest;
        }
    }
}
