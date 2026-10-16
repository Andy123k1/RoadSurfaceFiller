using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    // GenerateNodes has completed its barrier here. GenerateEdges has not yet
    // cleared split nodes' Temp.m_Original inside TryGetNodes.
    internal sealed partial class RoadNodeProvenanceSystem : GameSystemBase
    {
        protected override void OnUpdate() => World.GetOrCreateSystemManaged<SurfaceNetworkFilterSystem>().CaptureNativeRoadNodes();
    }

    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private sealed class NativeRoadNodeSource
        {
            internal Entity Source;
            internal float3 Position;
            internal float CurvePosition;
            internal TempFlags Flags;
        }
        private readonly Dictionary<Entity, NativeRoadNodeSource> m_NativeRoadNodeSources = new Dictionary<Entity, NativeRoadNodeSource>();
        private int m_LoggedNativeRoadNodes;

        internal void CaptureNativeRoadNodes()
        {
            if (!IsInLoadedGame()) return;
            foreach (Entity source in new List<Entity>(m_NativeSplitSeedSources.Keys))
                if (m_Frame - m_NativeSplitSeedSources[source].Frame > 240) m_NativeSplitSeedSources.Remove(source);
            foreach (Entity node in new List<Entity>(m_NativeRoadNodeSources.Keys))
                if (!TryNativeRoadNodeSource(node, out _)) m_NativeRoadNodeSources.Remove(node);
            using (NativeArray<Entity> nodes = GetEntityQuery(ComponentType.ReadOnly<Game.Net.Node>(),
                ComponentType.ReadOnly<Temp>(), ComponentType.Exclude<Deleted>()).ToEntityArray(Allocator.Temp))
                foreach (Entity node in nodes)
                {
                    Temp temp = EntityManager.GetComponentData<Temp>(node);
                    if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel | TempFlags.Duplicate)) != 0 ||
                        !IsRoadStyleSource(temp.m_Original)) continue;
                    Entity source = temp.m_Original;
                    CaptureNativeSplitSeedSource(source);
                    if (UsesCityRules(source) && IsRoadEdge(source)) RememberCitySurfaceSource(source);
                    bool changed = !m_NativeRoadNodeSources.TryGetValue(node, out NativeRoadNodeSource previous) || previous.Source != source;
                    m_NativeRoadNodeSources[node] = new NativeRoadNodeSource { Source = source,
                        Position = EntityManager.GetComponentData<Game.Net.Node>(node).m_Position,
                        CurvePosition = temp.m_CurvePosition, Flags = temp.m_Flags };
                    if (changed && m_LoggedNativeRoadNodes++ < 16)
                        Mod.Log.Info("Captured native split-node lineage before edge generation: node=" + node.Index +
                            "; original road=" + source.Index + "; position on original=" + temp.m_CurvePosition + ".");
                }
        }

        private bool TryNativeRoadNodeSource(Entity node, out Entity source)
        {
            source = Entity.Null;
            if (!m_NativeRoadNodeSources.TryGetValue(node, out NativeRoadNodeSource saved) ||
                !EntityManager.Exists(node) || EntityManager.HasComponent<Deleted>(node) ||
                !EntityManager.HasComponent<Temp>(node) || !EntityManager.HasComponent<Game.Net.Node>(node)) return false;
            Temp temp = EntityManager.GetComponentData<Temp>(node);
            // A reused preview is not the previous split point. Validate its
            // native parameter, planar position and creation role before using
            // the reference captured at the earlier generation barrier.
            // Native terrain fitting may adjust the node's elevation afterward.
            if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel | TempFlags.Duplicate)) != 0 ||
                (temp.m_Flags & TempFlags.Create) != (saved.Flags & TempFlags.Create) ||
                math.abs(temp.m_CurvePosition - saved.CurvePosition) > 0.000001f ||
                math.distancesq(EntityManager.GetComponentData<Game.Net.Node>(node).m_Position.xz, saved.Position.xz) > 0.0001f ||
                (temp.m_Original != Entity.Null && temp.m_Original != saved.Source)) return false;
            source = saved.Source;
            return IsRoadStyleSource(source);
        }
    }
}
