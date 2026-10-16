using System.Collections.Generic;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private sealed class NativeSplitFragment
        {
            internal Entity Edge, Start, End, Original;
        }

        private Dictionary<Entity, Entity> ReadNativeSplitSources(NativeArray<Entity> previews, HashSet<Entity> originals)
        {
            Dictionary<Entity, Dictionary<Entity, List<NativeSplitFragment>>> networks =
                new Dictionary<Entity, Dictionary<Entity, List<NativeSplitFragment>>>();
            foreach (Entity preview in previews)
            {
                Temp temp = EntityManager.GetComponentData<Temp>(preview);
                // GenerateEdgesJob.CreateTempEdge marks existing-road split
                // fragments Essential. Include directly linked fragments too:
                // a split can retain Original on only one half. Omitting that
                // half breaks the complete chain for its unlinked sibling.
                // An original-null Create branch never joins this graph.
                if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel | TempFlags.Duplicate)) != 0 ||
                    (temp.m_Original == Entity.Null && ((temp.m_Flags & TempFlags.Essential) == 0 ||
                    (temp.m_Flags & TempFlags.Create) != 0))) continue;
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(preview).m_Prefab;
                if (!networks.TryGetValue(prefab, out Dictionary<Entity, List<NativeSplitFragment>> network))
                    networks.Add(prefab, network = new Dictionary<Entity, List<NativeSplitFragment>>());
                Edge edge = EntityManager.GetComponentData<Edge>(preview);
                NativeSplitFragment fragment = new NativeSplitFragment { Edge = preview, Original = temp.m_Original,
                    Start = NativeSourceNode(edge.m_Start), End = NativeSourceNode(edge.m_End) };
                if (fragment.Start == fragment.End) continue;
                foreach (Entity node in new[] { fragment.Start, fragment.End })
                {
                    if (!network.TryGetValue(node, out List<NativeSplitFragment> attached))
                        network.Add(node, attached = new List<NativeSplitFragment>());
                    attached.Add(fragment);
                }
            }
            if (networks.Count == 0) return new Dictionary<Entity, Entity>();
            // Some split jobs clear every temporary node's original-edge link.
            // Recover candidate originals through the unchanged outer nodes'
            // native ConnectedEdge buffers, then require the complete chain.
            foreach (KeyValuePair<Entity, Dictionary<Entity, List<NativeSplitFragment>>> entry in networks)
                foreach (Entity node in entry.Value.Keys)
                {
                    if (!EntityManager.HasBuffer<ConnectedEdge>(node)) continue;
                    foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                    {
                        Entity original = connected.m_Edge;
                        if (!TryReadRoadStyleSource(original, out Entity prefab, out Edge source, out _) || prefab != entry.Key) continue;
                        if (entry.Value.ContainsKey(NativeSourceNode(source.m_Start)) && entry.Value.ContainsKey(NativeSourceNode(source.m_End)))
                            originals.Add(original);
                    }
                }
            foreach (KeyValuePair<Entity, CitySurfaceSource> entry in m_CitySurfaceSources)
                if (networks.TryGetValue(entry.Value.Prefab, out Dictionary<Entity, List<NativeSplitFragment>> network) &&
                    network.ContainsKey(NativeSourceNode(entry.Value.Endpoints.m_Start)) &&
                    network.ContainsKey(NativeSourceNode(entry.Value.Endpoints.m_End))) originals.Add(entry.Key);
            Dictionary<Entity, Entity> result = new Dictionary<Entity, Entity>();
            HashSet<Entity> ambiguous = new HashSet<Entity>();
            foreach (Entity original in originals)
            {
                if (!TryReadRoadStyleSource(original, out Entity prefab, out Edge source, out _)) continue;
                if (!networks.TryGetValue(prefab, out Dictionary<Entity, List<NativeSplitFragment>> network)) continue;
                Entity start = NativeSourceNode(source.m_Start), end = NativeSourceNode(source.m_End);
                if (start == end || !network.TryGetValue(start, out List<NativeSplitFragment> first) || !network.ContainsKey(end)) continue;
                List<Entity> path = null;
                foreach (NativeSplitFragment entry in first)
                {
                    List<Entity> candidate = TraceNativeSplitChain(network, entry, start, end, original);
                    if (candidate == null) continue;
                    if (path != null) { path = null; break; } // Multiple routes are not proven provenance.
                    path = candidate;
                }
                if (path == null) continue;
                // TryGetNodes can clear the new junction node's m_Original
                // when splitting the same edge at several positions. The
                // complete chain still connects that original edge's two
                // native endpoints. No curve tolerance or nearest-road guess
                // is needed, including when the native job reshapes a fragment.
                foreach (Entity fragment in path)
                {
                    if (ambiguous.Contains(fragment)) continue;
                    if (result.TryGetValue(fragment, out Entity previous) && previous != original)
                    { result.Remove(fragment); ambiguous.Add(fragment); }
                    else result[fragment] = original;
                }
            }
            return result;
        }

        private Entity NativeSourceNode(Entity node)
        {
            for (int i = 0; i < 8 && EntityManager.HasComponent<Temp>(node); i++)
            {
                Entity original = EntityManager.GetComponentData<Temp>(node).m_Original;
                if (original == node || !EntityManager.HasComponent<Game.Net.Node>(original)) break;
                node = original;
            }
            return node;
        }

        private static List<Entity> TraceNativeSplitChain(Dictionary<Entity, List<NativeSplitFragment>> network,
            NativeSplitFragment edge, Entity start, Entity end, Entity original)
        {
            List<Entity> path = new List<Entity>();
            HashSet<Entity> visited = new HashSet<Entity> { start };
            Entity previous = start;
            while (true)
            {
                if (edge.Original != Entity.Null && edge.Original != original) return null;
                path.Add(edge.Edge);
                Entity next = edge.Start == previous ? edge.End : edge.Start;
                if (next == end) return path;
                if (!visited.Add(next) || !network.TryGetValue(next, out List<NativeSplitFragment> attached)) return null;
                NativeSplitFragment continuation = null;
                foreach (NativeSplitFragment candidate in attached)
                {
                    if (candidate.Edge == edge.Edge || (candidate.Original != Entity.Null && candidate.Original != original)) continue;
                    if (continuation != null) return null;
                    continuation = candidate;
                }
                if (continuation == null) return null;
                edge = continuation;
                previous = next;
            }
        }
    }
}
