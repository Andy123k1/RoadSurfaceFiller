using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private sealed class NativeSplitSeedSource
        {
            internal Entity Prefab;
            internal Edge Endpoints;
            internal ushort First, Second;
            internal int Frame;
        }

        private readonly Dictionary<Entity, NativeSplitSeedSource> m_NativeSplitSeedSources = new Dictionary<Entity, NativeSplitSeedSource>();
        private int m_LoggedSeedSplitTransfers;

        private void CaptureNativeSplitSeedSource(Entity source)
        {
            if (!EntityManager.HasComponent<PseudoRandomSeed>(source) ||
                !TryReadRoadStyleSource(source, out Entity prefab, out Edge ends, out _)) return;
            // GenerateEdgesJob.SplitEdge derives these exact two child seeds
            // before calling CreateTempEdge. This survives reshaped handles and
            // an incomplete preview chain without guessing from road proximity.
            PseudoRandomSeed seed = EntityManager.GetComponentData<PseudoRandomSeed>(source);
            Unity.Mathematics.Random random = seed.GetRandom(PseudoRandomSeed.kSplitEdge);
            m_NativeSplitSeedSources[source] = new NativeSplitSeedSource
            {
                Prefab = prefab, Endpoints = ends, Frame = m_Frame,
                First = new PseudoRandomSeed(ref random).m_Seed,
                Second = new PseudoRandomSeed(ref random).m_Seed
            };
        }

        private bool HasNativeSplitSeedAnchor(Entity node, Entity source, Edge ends)
        {
            if (BelongsToNativeSource(node, source, ends)) return true;
            node = NativeSourceNode(node);
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return false;
            foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                if (connected.m_Edge == source) return true;
            return false;
        }

        private bool TryNativeSplitSeedSource(Entity preview, HashSet<Entity> originals, out Entity source)
        {
            source = Entity.Null;
            if (!EntityManager.HasComponent<PseudoRandomSeed>(preview)) return false;
            Temp temp = EntityManager.GetComponentData<Temp>(preview);
            if (temp.m_Original != Entity.Null || (temp.m_Flags & TempFlags.Essential) == 0 ||
                (temp.m_Flags & (TempFlags.Create | TempFlags.Delete | TempFlags.Cancel | TempFlags.Duplicate)) != 0) return false;
            ushort seed = EntityManager.GetComponentData<PseudoRandomSeed>(preview).m_Seed;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(preview).m_Prefab;
            Edge fragment = EntityManager.GetComponentData<Edge>(preview);
            foreach (Entity original in originals)
            {
                if (!IsRoadStyleSource(original)) continue;
                if (!m_NativeSplitSeedSources.TryGetValue(original, out NativeSplitSeedSource saved))
                {
                    CaptureNativeSplitSeedSource(original);
                    if (!m_NativeSplitSeedSources.TryGetValue(original, out saved)) continue;
                }
                if (m_Frame - saved.Frame > 240 || saved.Prefab != prefab ||
                    (seed != saved.First && seed != saved.Second) ||
                    (!HasNativeSplitSeedAnchor(fragment.m_Start, original, saved.Endpoints) &&
                     !HasNativeSplitSeedAnchor(fragment.m_End, original, saved.Endpoints))) continue;
                // Seeds are only 16 bits. Require native topology as well, and
                // reject a collision between eligible source roads outright.
                if (source != Entity.Null && source != original) { source = Entity.Null; return false; }
                source = original;
            }
            if (source == Entity.Null) return false;
            if (m_LoggedSeedSplitTransfers++ < 16)
                Mod.Log.Info("Resolved native split using child seed and topology: original=" + source.Index +
                    "; fragment=" + preview.Index + "; seed=" + seed + ".");
            return true;
        }
    }
}
