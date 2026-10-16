using System;
using System.Collections.Generic;
using Game.Areas;
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
        private sealed class BoundaryOffsets
        {
            internal float Expansion;
            internal float3[] Positions;
            internal float2[] Offsets;
        }

        private readonly Dictionary<Entity, BoundaryOffsets> m_BoundaryOffsets = new Dictionary<Entity, BoundaryOffsets>();
        private int m_LoggedBoundaryCompensation;
        private int m_LoggedSmallBoundaryCompensation;

        private SurfacePatch GuardExactSurfacePatch(SurfacePatch source, float expansion)
        {
            if (source.Count < 4 || source.Triangles.Count == 0) return source;
            SurfacePatch result = MergeShortRoundaboutEdges(RemoveUnusedSurfaceNodes(source));
            float limit = math.max(0.75f, expansion * 2f);
            float ratio = math.min(0.99f, expansion / limit);
            float sharpDot = 1f - 2f * ratio * ratio;
            for (int pass = 0; pass < 6 && GetRoundaboutCornerExpansion(result, expansion) > limit + 0.001f; pass++)
            {
                SurfacePatch next = BevelRoundaboutTips(result, sharpDot);
                if (ReferenceEquals(next, result)) break;
                result = next;
            }
            result.ExactBoundary = true;
            return result;
        }

        private void RestoreExactSurfaceBoundary(Entity area, Entity prefab)
        {
            DynamicBuffer<AreaNode> nodes = EntityManager.GetBuffer<AreaNode>(area, true);
            if (nodes.Length < 3) return;
            float expansion = EntityManager.HasComponent<RenderedAreaData>(prefab)
                ? EntityManager.GetComponentData<RenderedAreaData>(prefab).m_ExpandAmount : GetSurfaceExpansion(prefab);
            if (!math.isfinite(expansion)) return;
            bool cached = m_BoundaryOffsets.TryGetValue(area, out BoundaryOffsets data) &&
                data.Expansion == expansion && data.Positions.Length == nodes.Length;
            for (int i = 0; cached && i < nodes.Length; i++)
                cached = math.all(data.Positions[i] == nodes[i].m_Position);
            if (!cached)
            {
                data = CalculateBoundaryOffsets(nodes, expansion, EntityManager.HasComponent<SmallRoundaboutSurface>(area));
                m_BoundaryOffsets[area] = data;
            }
            // FillExpandedNodes applies this per-area offset BEFORE the native
            // GPU miter. Keep the saved/source nodes at the desired curb line;
            // compensate renderer rounding without changing any surface asset.
            if (!EntityManager.HasBuffer<Expand>(area)) EntityManager.AddBuffer<Expand>(area);
            DynamicBuffer<Expand> offsets = EntityManager.GetBuffer<Expand>(area);
            int count = data.Offsets.Length;
            bool resized = offsets.Length != count;
            bool changed = resized;
            if (resized) offsets.ResizeUninitialized(count);
            for (int i = 0; i < count; i++)
            {
                Expand value = resized ? default : offsets[i];
                changed |= math.distancesq(value.m_Offset, data.Offsets[i]) > 0.00000001f;
                value.m_Offset = data.Offsets[i];
                offsets[i] = value;
            }
            if (changed && !EntityManager.HasComponent<Updated>(area)) EntityManager.AddComponent<Updated>(area);
        }

        private BoundaryOffsets CalculateBoundaryOffsets(DynamicBuffer<AreaNode> source, float expansion, bool smallRoundabout)
        {
            int count = source.Length;
            BoundaryOffsets result = new BoundaryOffsets
            {
                Expansion = expansion,
                Positions = new float3[count],
                Offsets = new float2[count]
            };
            float limit = math.max(0.75f, math.abs(expansion) * 2f);
            float bestError = float.PositiveInfinity;
            float sourceReach = 0f;
            using (NativeList<AreaNode> desired = new NativeList<AreaNode>(count, Allocator.Temp))
            using (NativeList<AreaNode> contracted = new NativeList<AreaNode>(count, Allocator.Temp))
            {
                for (int i = 0; i < count; i++) { desired.Add(source[i]); result.Positions[i] = source[i].m_Position; }
                for (int i = 0; i < count; i++)
                {
                    float3 expanded = AreaUtils.GetExpandedNode(desired, i, (i + count - 1) % count,
                        (i + 1) % count, expansion, true);
                    float reach = math.distance(expanded.xz, desired[i].m_Position.xz);
                    sourceReach = math.max(sourceReach, math.isfinite(reach) ? reach : float.PositiveInfinity);
                }
                for (int i = 0; i < count; i++)
                {
                    float3 point = AreaUtils.GetExpandedNode(desired, i, (i + count - 1) % count,
                        (i + 1) % count, -expansion, true);
                    float2 delta = LimitOffset(point.xz - desired[i].m_Position.xz, limit);
                    AreaNode node = desired[i];
                    node.m_Position.xz += delta;
                    contracted.Add(node);
                }
                // Parallel polygon offsets invert exactly along straight edges.
                // Short curved samples need a bounded correction of the native
                // result. Each pass always refers to the unchanged desired nodes.
                float2[] corrections = new float2[count];
                NativeList<AreaNode> working = contracted;
                for (int pass = 0; pass < 16; pass++)
                {
                    float greatest = 0f;
                    for (int i = 0; i < count; i++)
                    {
                        float3 expanded = AreaUtils.GetExpandedNode(contracted, i, (i + count - 1) % count,
                            (i + 1) % count, expansion, true);
                        corrections[i] = desired[i].m_Position.xz - expanded.xz;
                        greatest = math.max(greatest, math.all(math.isfinite(corrections[i]))
                            ? math.length(corrections[i]) : float.PositiveInfinity);
                    }
                    if (greatest < bestError)
                    {
                        bestError = greatest;
                        for (int i = 0; i < count; i++) result.Offsets[i] = contracted[i].m_Position.xz - desired[i].m_Position.xz;
                    }
                    if (greatest <= 0.001f || !math.isfinite(greatest)) break;
                    for (int i = 0; i < count; i++)
                    {
                        AreaNode node = contracted[i];
                        node.m_Position.xz = desired[i].m_Position.xz + LimitOffset(
                            node.m_Position.xz - desired[i].m_Position.xz + corrections[i] * 0.5f, limit);
                        working[i] = node;
                    }
                }
            }
            bool fallback = (smallRoundabout || sourceReach <= limit + 0.001f) &&
                (!math.isfinite(bestError) || bestError > 0.025f);
            if (fallback) Array.Clear(result.Offsets, 0, result.Offsets.Length);
            // A poor inverse fit must not move a valid corner away from its
            // intended footprint. Use the source only when its native expansion
            // is already bounded; the small-roundabout path retains its guard.
            if (smallRoundabout ? m_LoggedSmallBoundaryCompensation++ < 8 : m_LoggedBoundaryCompensation++ < 8)
                Mod.Log.Info((smallRoundabout ? "Small roundabout" : "Curb") + " boundary renderer compensation: native expansion=" + expansion +
                    " m; inverse boundary error=" + bestError + " m; bounded source fallback=" + fallback +
                    "; source corner reach=" + sourceReach + " m; nodes=" + count + ".");
            return result;
        }

        private static float2 LimitOffset(float2 offset, float limit)
        {
            if (!math.all(math.isfinite(offset))) return default;
            float length = math.length(offset);
            return length > limit ? offset * (limit / length) : offset;
        }
    }
}
