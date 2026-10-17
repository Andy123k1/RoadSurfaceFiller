using System;
using System.Collections.Generic;
using System.Text;
using Colossal.Serialization.Entities;
using Game;
using Game.Net;
using Game.Prefabs;
using Game.Serialization;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace SurfaceNetworkFilter
{
    // Additive component: the original SavedJunctionSurface save layout stays
    // readable. Names survive a prefab being recreated or temporarily absent.
    public struct SavedJunctionSurfaceIdentity : IComponentData, ISerializable
    {
        public FixedString4096Bytes SurfaceName;
        public FixedString512Bytes RoadPrefabName;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        { writer.Write(SurfaceName.ToString()); writer.Write(RoadPrefabName.ToString()); }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out string surface); reader.Read(out string road);
            SurfaceName = new FixedString4096Bytes(surface);
            RoadPrefabName = new FixedString512Bytes(road);
        }
    }

    // Native PrefabRef is patched by the game, but custom prefab fields need
    // to participate explicitly in its save/load reference-check phase.
    internal sealed partial class JunctionPrefabReferencesSystem : GameSystemBase
    {
        private EntityQuery m_Records;
        private CheckPrefabReferencesSystem m_CheckReferences;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Records = GetEntityQuery(ComponentType.ReadWrite<SavedJunctionSurface>());
            m_CheckReferences = World.GetOrCreateSystemManaged<CheckPrefabReferencesSystem>();
        }

        protected override void OnUpdate()
        {
            PrefabReferences references = m_CheckReferences.GetPrefabReferences(this, out JobHandle pending);
            JobHandle.CombineDependencies(Dependency, pending).Complete();
            using (NativeArray<Entity> records = m_Records.ToEntityArray(Allocator.Temp))
                foreach (Entity record in records)
                {
                    SavedJunctionSurface saved = EntityManager.GetComponentData<SavedJunctionSurface>(record);
                    // Very old broken references can already be gone. Their
                    // identity/native PrefabRef is recovered during gameplay.
                    if (EntityManager.HasComponent<PrefabData>(saved.RoadPrefab)) references.Check(ref saved.RoadPrefab);
                    if (EntityManager.HasComponent<PrefabData>(saved.SurfacePrefab)) references.Check(ref saved.SurfacePrefab);
                    EntityManager.SetComponentData(record, saved);
                }
            Dependency = default;
            m_CheckReferences.AddPrefabReferencesUser(Dependency);
        }
    }

    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private readonly Dictionary<Entity, int> m_JunctionReferenceChecks = new Dictionary<Entity, int>();
        private readonly HashSet<Entity> m_UnresolvedJunctionReferences = new HashSet<Entity>();
        private int m_LoggedJunctionReferenceRepairs, m_LoggedJunctionReferenceFailures;

        private bool IsLoadedJunctionSurface(Entity surface) => EntityManager.HasComponent<SurfaceData>(surface) &&
            EntityManager.HasComponent<AreaData>(surface) && m_PrefabSystem.TryGetPrefab(surface, out SurfacePrefab _);

        private string JunctionPrefabName(Entity prefab) => EntityManager.HasComponent<PrefabData>(prefab) ?
            m_PrefabSystem.GetPrefabName(prefab) : "";

        private bool TryResolveJunctionSurface(string name, out Entity surface)
        {
            if (IsNetworkOverlayPrefab(name)) name = name.Substring(NetworkOverlayPrefix.Length);
            return RuntimeState.TryGetSurface(name, out surface) && IsLoadedJunctionSurface(surface);
        }

        private bool TryResolveJunctionSurface(Entity candidate, out Entity surface) =>
            TryResolveJunctionSurface(JunctionPrefabName(candidate), out surface);

        private void StoreJunctionSurfaceIdentity(Entity record, Entity surface, Entity roadPrefab)
        {
            string surfaceName = JunctionPrefabName(surface), roadName = JunctionPrefabName(roadPrefab);
            // Never truncate an asset identifier into the name of a different
            // asset. Oversize names retain the native reference path instead.
            if (Encoding.UTF8.GetByteCount(surfaceName) > 4093) surfaceName = "";
            if (Encoding.UTF8.GetByteCount(roadName) > 509) roadName = "";
            if (roadName.Length == 0 && EntityManager.HasComponent<SavedJunctionSurfaceIdentity>(record))
                roadName = EntityManager.GetComponentData<SavedJunctionSurfaceIdentity>(record).RoadPrefabName.ToString();
            SavedJunctionSurfaceIdentity identity = new SavedJunctionSurfaceIdentity {
                SurfaceName = new FixedString4096Bytes(surfaceName), RoadPrefabName = new FixedString512Bytes(roadName)
            };
            if (EntityManager.HasComponent<SavedJunctionSurfaceIdentity>(record)) EntityManager.SetComponentData(record, identity);
            else EntityManager.AddComponentData(record, identity);
        }

        private Entity RecoverJunctionRoadPrefab(Entity node, Entity previous, string name)
        {
            // The selected city road is a normal entity reference. Its native
            // PrefabRef is authoritative when it is still connected here.
            if (EntityManager.HasComponent<JunctionSurfacePriority>(node))
            {
                Entity road = EntityManager.GetComponentData<JunctionSurfacePriority>(node).Road;
                if (IsRoadEdge(road))
                {
                    Edge ends = EntityManager.GetComponentData<Edge>(road);
                    if (ends.m_Start == node || ends.m_End == node)
                        return EntityManager.GetComponentData<PrefabRef>(road).m_Prefab;
                }
            }
            if (name.Length > 0)
                foreach (ConnectedEdge edge in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                    if (IsRoadEdge(edge.m_Edge))
                    {
                        Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge.m_Edge).m_Prefab;
                        if (string.Equals(JunctionPrefabName(prefab), name, StringComparison.OrdinalIgnoreCase)) return prefab;
                    }
            return EntityManager.HasComponent<NetGeometryData>(previous) ? previous : Entity.Null;
        }

        private bool RecoverSavedJunctionReferences(Entity record, ref SavedJunctionSurface saved)
        {
            // Missing packs are retried, without repeatedly rebuilding roads
            // or performing catalog/name lookups every frame.
            if (m_JunctionReferenceChecks.TryGetValue(record, out int checkedFrame) && m_Frame - checkedFrame < 120) return false;
            m_JunctionReferenceChecks[record] = m_Frame;
            bool legacy = !EntityManager.HasComponent<SavedJunctionSurfaceIdentity>(record);
            SavedJunctionSurfaceIdentity identity = legacy ? default : EntityManager.GetComponentData<SavedJunctionSurfaceIdentity>(record);
            string name = identity.SurfaceName.ToString();
            Entity native = EntityManager.HasComponent<PrefabRef>(record) ? EntityManager.GetComponentData<PrefabRef>(record).m_Prefab : Entity.Null;
            Entity surface;
            // An explicit name must never be replaced with another finish if
            // the supplying pack is absent. For old saves, native PrefabRef
            // has already passed through the game's prefab remapping.
            bool found = name.Length > 0 ? TryResolveJunctionSurface(name, out surface) :
                TryResolveJunctionSurface(native, out surface) || TryResolveJunctionSurface(saved.SurfacePrefab, out surface);
            Entity roadPrefab = RecoverJunctionRoadPrefab(saved.Node, saved.RoadPrefab, identity.RoadPrefabName.ToString());
            bool changed = roadPrefab != saved.RoadPrefab;
            saved.RoadPrefab = roadPrefab;
            if (!found)
            {
                bool becameUnavailable = m_UnresolvedJunctionReferences.Add(record);
                if (becameUnavailable && m_LoggedJunctionReferenceFailures++ < 16)
                    Mod.Log.Warn("Saved junction surface still unavailable: node=" + saved.Node.Index +
                        "; saved asset='" + name + "'; custom reference=" + saved.SurfacePrefab + "; native reference=" + native +
                        ". Choice retained; recovery will retry when surfaces refresh.");
                return changed || becameUnavailable;
            }

            bool recovered = m_UnresolvedJunctionReferences.Remove(record);
            changed |= saved.SurfacePrefab != surface || native != surface || legacy || recovered;
            saved.SurfacePrefab = surface;
            if (!EntityManager.HasComponent<PrefabRef>(record)) EntityManager.AddComponentData(record, new PrefabRef { m_Prefab = surface });
            else if (native != surface) EntityManager.SetComponentData(record, new PrefabRef { m_Prefab = surface });
            if (legacy || name.Length == 0 || changed) StoreJunctionSurfaceIdentity(record, surface, roadPrefab);
            if (changed && m_LoggedJunctionReferenceRepairs++ < 16)
                Mod.Log.Info("Recovered saved junction surface: node=" + saved.Node.Index + "; asset='" + JunctionPrefabName(surface) +
                    "'; legacy=" + legacy + "; connected approaches queued for rebuild.");
            return changed;
        }

        private string UnavailableJunctionSurfaceMessage(Entity node)
        {
            if (m_JunctionSurfaceRecords.TryGetValue(node, out Entity record) &&
                EntityManager.HasComponent<SavedJunctionSurfaceIdentity>(record))
            {
                string name = EntityManager.GetComponentData<SavedJunctionSurfaceIdentity>(record).SurfaceName.ToString();
                if (name.Length > 0) return "Waiting for saved surface: " + name + ". Restore its surface pack; recovery is automatic.";
            }
            return "Saved surface reference is unavailable. Retrying recovery; the choice has been retained.";
        }
    }
}
