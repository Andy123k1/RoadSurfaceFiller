using System;
using System.Collections.Generic;
using Game.Areas;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private const float SmallRoundaboutCornerLimit = 0.75f;
        private int m_LoggedSmallRoundaboutGuards;

        private static SurfacePatch CreateSmallRoundaboutRibbon(List<float3> perimeter, float3 mouth,
            float3 center, float radius, float height)
        {
            const float angleStep = math.PI / 96f;
            float innerRadius = radius / math.cos(angleStep * 0.5f) + 0.01f;
            float clipRadius = innerRadius + 0.05f;
            SurfacePatch result = new SurfacePatch { SmallRoundabout = true };
            List<float3> run = new List<float3>();
            float3 previous = mouth;
            foreach (float3 point in perimeter)
            {
                float2 offset = previous.xz - center.xz, delta = point.xz - previous.xz;
                float a = math.dot(delta, delta);
                float b = math.dot(offset, delta);
                float c = math.dot(offset, offset) - clipRadius * clipRadius;
                float discriminant = b * b - a * c;
                bool clipped = false;
                if (a > 0.0000001f && discriminant > 0f)
                {
                    float root = math.sqrt(discriminant);
                    float enter = math.clamp((-b - root) / a, 0f, 1f);
                    float leave = math.clamp((-b + root) / a, 0f, 1f);
                    if (leave > enter + 0.000001f)
                    {
                        if (enter > 0.000001f)
                            AppendSmallRoundaboutSegment(run, previous, math.lerp(previous, point, enter), center, 0);
                        FlushSmallRoundaboutRun(result, run, center, innerRadius, height);
                        if (leave < 0.999999f)
                            AppendSmallRoundaboutSegment(run, math.lerp(previous, point, leave), point, center, 0);
                        clipped = true;
                    }
                }
                if (!clipped)
                {
                    if (math.distancesq(math.lerp(previous.xz, point.xz, 0.5f), center.xz) >= clipRadius * clipRadius)
                        AppendSmallRoundaboutSegment(run, previous, point, center, 0);
                    else
                        FlushSmallRoundaboutRun(result, run, center, innerRadius, height);
                }
                previous = point;
            }
            FlushSmallRoundaboutRun(result, run, center, innerRadius, height);
            return result;
        }

        private static void AppendSmallRoundaboutSegment(List<float3> points, float3 from, float3 to, float3 center, int depth)
        {
            float2 a = math.normalizesafe(from.xz - center.xz), b = math.normalizesafe(to.xz - center.xz);
            float angle = math.acos(math.clamp(math.dot(a, b), -1f, 1f));
            if (depth < 12 && (angle > math.PI / 96f || math.distance(from, to) > SampleSpacing))
            {
                float3 middle = math.lerp(from, to, 0.5f);
                AppendSmallRoundaboutSegment(points, from, middle, center, depth + 1);
                AppendSmallRoundaboutSegment(points, middle, to, center, depth + 1);
            }
            else
            {
                AddDistinct(points, from);
                AddDistinct(points, to);
            }
        }

        private static void FlushSmallRoundaboutRun(SurfacePatch result, List<float3> run,
            float3 center, float innerRadius, float height)
        {
            // A radial strip folds over itself when the outside chain reverses
            // its polar direction. Keep each run monotonic around the island.
            int first = 0;
            float direction = 0f;
            for (int i = 1; i < run.Count; i++)
            {
                float2 a = math.normalizesafe(run[i - 1].xz - center.xz);
                float2 b = math.normalizesafe(run[i].xz - center.xz);
                float angle = math.atan2(Cross2(a, b), math.dot(a, b));
                if (math.abs(angle) < 0.0001f) continue;
                float next = math.sign(angle);
                if (direction != 0f && next != direction)
                {
                    AddSmallRoundaboutRun(result, run, first, i - 1, center, innerRadius, height);
                    first = i - 1;
                }
                direction = next;
            }
            AddSmallRoundaboutRun(result, run, first, run.Count - 1, center, innerRadius, height);
            // A chord crossing the island separates two pavement patches. Do
            // not reconnect their radial vertices across that missing interval:
            // radius / cos(half the resulting jump) can create a very long tip.
            run.Clear();
        }

        private static void AddSmallRoundaboutRun(SurfacePatch result, List<float3> run, int first, int last,
            float3 center, float innerRadius, float height)
        {
            if (last <= first) return;
            List<float3> outer = new List<float3>(), island = new List<float3>();
            for (int i = first; i <= last; i++)
            {
                float3 inner = Lift(center, height);
                inner.xz += math.normalizesafe(run[i].xz - center.xz) * innerRadius;
                outer.Add(run[i]);
                island.Add(inner);
            }
            SurfacePatch part = CreateSmallRoundaboutStrip(outer, island);
            part.SmallRoundabout = true;
            if (part.Parts.Count > 0 || (part.Count >= 4 && part.Triangles.Count > 0)) result.Parts.Add(part);
        }

        private static SurfacePatch CreateSmallRoundaboutStrip(List<float3> outer, List<float3> inner)
        {
            SurfacePatch result = new SurfacePatch { SmallRoundabout = true };
            if (outer.Count < 2 || inner.Count != outer.Count) return result;
            List<float3> a = new List<float3>(), b = new List<float3>();
            a.Add(outer[0]); b.Add(inner[0]);
            for (int i = 1; i < outer.Count; i++)
            {
                a.Add(outer[i]); b.Add(inner[i]);
                SurfacePatch ribbon = RemoveUnusedSurfaceNodes(CloseStrip(new List<float3>(a), new List<float3>(b)));
                if (ribbon.Triangles.Count == 0 || IsSimpleSmallRoundaboutPatch(ribbon)) continue;
                a.RemoveAt(a.Count - 1); b.RemoveAt(b.Count - 1);
                AddSmallRoundaboutStripPart(result, a, b);
                a.Clear(); b.Clear();
                a.Add(outer[i - 1]); b.Add(inner[i - 1]);
                a.Add(outer[i]); b.Add(inner[i]);
                SurfacePatch cell = RemoveUnusedSurfaceNodes(CloseStrip(new List<float3>(a), new List<float3>(b)));
                if (cell.Triangles.Count > 0 && !IsSimpleSmallRoundaboutPatch(cell))
                {
                    // A folded offset cell cannot be one simple area. Keep its
                    // local mesh as separate triangles, each checked by the guard.
                    foreach (int3 triangle in cell.Triangles)
                    {
                        List<float3> points = new List<float3>
                        {
                            cell.Nodes[triangle.x].m_Position, cell.Nodes[triangle.y].m_Position, cell.Nodes[triangle.z].m_Position
                        };
                        SurfacePatch part = TriangulateBoundary(points);
                        part.SmallRoundabout = true;
                        if (part.Triangles.Count > 0) result.Parts.Add(part);
                    }
                    a.Clear(); b.Clear();
                    a.Add(outer[i]); b.Add(inner[i]);
                }
            }
            AddSmallRoundaboutStripPart(result, a, b);
            return result;
        }

        private static bool IsSimpleSmallRoundaboutPatch(SurfacePatch patch)
        {
            List<float3> boundary = new List<float3>();
            for (int i = 0; i < patch.Count - 1; i++) boundary.Add(patch.Nodes[i].m_Position);
            return TriangulateBoundary(boundary).Triangles.Count > 0;
        }

        private static void AddSmallRoundaboutStripPart(SurfacePatch result, List<float3> a, List<float3> b)
        {
            SurfacePatch part = RemoveUnusedSurfaceNodes(CloseStrip(new List<float3>(a), new List<float3>(b)));
            part.SmallRoundabout = true;
            if (part.Triangles.Count > 0) result.Parts.Add(part);
        }

        private float GetSurfaceExpansion(Entity prefab)
        {
            float expansion = EntityManager.HasComponent<RenderedAreaData>(prefab)
                ? math.abs(EntityManager.GetComponentData<RenderedAreaData>(prefab).m_ExpandAmount) : 0f;
            // A newly loaded material may not have its render batch yet. Match
            // AreaBatchSystem's calculation rather than assuming zero expansion.
            if (m_PrefabSystem.TryGetPrefab(prefab, out SurfacePrefab surface) && surface.TryGet(out RenderedArea renderer) &&
                EntityManager.HasComponent<AreaGeometryData>(prefab))
            {
                AreaType type = EntityManager.GetComponentData<AreaGeometryData>(prefab).m_Type;
                float authored = AreaUtils.GetMinNodeDistance(type) * math.clamp(renderer.m_Roundness, 0.01f, 0.99f) * 0.5f;
                expansion = math.max(expansion, authored);
            }
            return math.isfinite(expansion) ? expansion : 0.375f;
        }

        private SurfacePatch GuardSmallRoundaboutPatch(SurfacePatch source, float expansion)
        {
            if (source.Count < 4 || source.Triangles.Count == 0) return source;
            float limit = math.max(SmallRoundaboutCornerLimit, expansion * 2f);
            float before = GetRoundaboutCornerExpansion(source, expansion);
            SurfacePatch compact = RemoveUnusedSurfaceNodes(source);
            if (ReferenceEquals(compact, source) && before <= limit + 0.001f)
            {
                source.ExactBoundary = true;
                return source;
            }
            List<float3> boundary = new List<float3>();
            for (int i = 0; i < compact.Count - 1; i++) boundary.Add(compact.Nodes[i].m_Position);
            SurfacePatch result = TriangulateBoundary(boundary);
            int removed = source.Count - compact.Count;
            // A 12 cm bevel at a nearly parallel return can be sub-millimetre
            // wide in world coordinates. Rounding that edge creates new acute
            // tips. Remove the unstable tail vertex instead and rebuild its mesh.
            while (result.Count >= 4)
            {
                int worst = FindWorstSurfaceCorner(result, expansion, out float reach);
                if (worst < 0 || reach <= limit + 0.001f) break;
                boundary.Clear();
                for (int i = 0; i < result.Count - 1; i++)
                    if (i != worst) boundary.Add(result.Nodes[i].m_Position);
                result = TriangulateBoundary(boundary);
                removed++;
            }
            result.SmallRoundabout = true;
            result.ExactBoundary = true;
            result.ProjectionMinimum = source.ProjectionMinimum;
            result.ProjectionMaximum = source.ProjectionMaximum;
            float after = result.Count >= 4 ? GetRoundaboutCornerExpansion(result, expansion) : 0f;
            if (m_LoggedSmallRoundaboutGuards++ < 12)
                Mod.Log.Info("Small roundabout bounded corner repair: native expansion=" + expansion +
                    " m; largest reach before=" + before + " m; after=" + after + " m; limit=" + limit +
                    " m; removed tail nodes=" + removed + "; source nodes=" + (source.Count - 1) +
                    "; repaired nodes=" + math.max(0, result.Count - 1) + "; suppressed sliver=" + (result.Triangles.Count == 0) + ".");
            return result;
        }

        private static SurfacePatch RemoveUnusedSurfaceNodes(SurfacePatch source)
        {
            if (source.Count < 4 || source.Triangles.Count == 0) return source;
            int count = source.Count - 1;
            bool[] used = new bool[count];
            foreach (int3 triangle in source.Triangles)
            {
                if (math.any(triangle < 0) || math.any(triangle >= count)) return new SurfacePatch();
                used[triangle.x] = used[triangle.y] = used[triangle.z] = true;
            }
            int[] mapping = new int[count];
            SurfacePatch result = new SurfacePatch
            {
                SmallRoundabout = source.SmallRoundabout, ExactBoundary = source.ExactBoundary,
                ProjectionMinimum = source.ProjectionMinimum, ProjectionMaximum = source.ProjectionMaximum
            };
            for (int i = 0; i < count; i++)
            {
                mapping[i] = result.Count;
                if (used[i]) result.Nodes.Add(source.Nodes[i]);
            }
            if (result.Count == count) return source;
            if (result.Count < 3) return new SurfacePatch();
            foreach (int3 triangle in source.Triangles)
                result.Triangles.Add(new int3(mapping[triangle.x], mapping[triangle.y], mapping[triangle.z]));
            result.Nodes.Add(result.Nodes[0]);
            return result;
        }

        private static int FindWorstSurfaceCorner(SurfacePatch patch, float expansion, out float greatest)
        {
            int count = patch.Count - 1, worst = -1;
            greatest = 0f;
            using (NativeList<AreaNode> nodes = new NativeList<AreaNode>(count, Allocator.Temp))
            {
                for (int i = 0; i < count; i++) nodes.Add(patch.Nodes[i]);
                for (int i = 0; i < count; i++)
                {
                    float3 expanded = AreaUtils.GetExpandedNode(nodes, i, (i + count - 1) % count, (i + 1) % count, expansion, true);
                    float reach = math.distance(expanded.xz, nodes[i].m_Position.xz);
                    if (!math.isfinite(reach)) { greatest = float.PositiveInfinity; return i; }
                    if (reach > greatest) { greatest = reach; worst = i; }
                }
            }
            return worst;
        }

        private static float GetRoundaboutCornerExpansion(SurfacePatch patch, float expansion)
        {
            int count = patch.Count - 1;
            float greatest = 0f;
            using (NativeList<AreaNode> nodes = new NativeList<AreaNode>(count, Allocator.Temp))
            {
                for (int i = 0; i < count; i++) nodes.Add(patch.Nodes[i]);
                for (int i = 0; i < count; i++)
                {
                    // Call the same native expansion function used to build GPU
                    // area triangles, including its near-zero-angle behavior.
                    float3 expanded = AreaUtils.GetExpandedNode(nodes, i, (i + count - 1) % count, (i + 1) % count, expansion, true);
                    float reach = math.distance(expanded.xz, nodes[i].m_Position.xz);
                    if (!math.isfinite(reach)) return float.PositiveInfinity;
                    greatest = math.max(greatest, reach);
                }
            }
            return greatest;
        }

        private static SurfacePatch MergeShortRoundaboutEdges(SurfacePatch source)
        {
            int count = source.Count - 1;
            int[] mapping = new int[count];
            SurfacePatch result = new SurfacePatch
            {
                SmallRoundabout = source.SmallRoundabout,
                ExactBoundary = source.ExactBoundary,
                ProjectionMinimum = source.ProjectionMinimum,
                ProjectionMaximum = source.ProjectionMaximum
            };
            for (int i = 0; i < count; i++)
            {
                AreaNode node = source.Nodes[i];
                // The renderer normalizes XZ directions even if a tiny edge has
                // a different Y. Remove short planar edges before cutting tips.
                if (result.Nodes.Count == 0 ||
                    math.distancesq(result.Nodes[result.Nodes.Count - 1].m_Position.xz, node.m_Position.xz) >= 0.000625f)
                    result.Nodes.Add(node);
                mapping[i] = result.Nodes.Count - 1;
            }
            if (result.Nodes.Count > 1 && math.distancesq(result.Nodes[0].m_Position.xz,
                result.Nodes[result.Nodes.Count - 1].m_Position.xz) < 0.000625f)
            {
                int removed = result.Nodes.Count - 1;
                for (int i = 0; i < count; i++) if (mapping[i] == removed) mapping[i] = 0;
                result.Nodes.RemoveAt(removed);
            }
            if (result.Nodes.Count == count || result.Nodes.Count < 3) return source;
            foreach (int3 triangle in source.Triangles)
                AddBevelTriangle(result, new int3(mapping[triangle.x], mapping[triangle.y], mapping[triangle.z]));
            result.Nodes.Add(result.Nodes[0]);
            return result.Triangles.Count > 0 ? result : source;
        }

        private void GuardSmallRoundaboutMesh(Entity area, Entity prefab)
        {
            if (m_PreparedSurfaceMeshes.Contains(area) && EntityManager.HasComponent<ExactSurfaceBoundary>(area) &&
                !EntityManager.HasComponent<Game.Common.Updated>(area) && !EntityManager.HasComponent<Game.Common.Created>(area) &&
                m_BoundaryOffsets.TryGetValue(area, out BoundaryOffsets cached) && cached.Expansion == GetSurfaceExpansion(prefab)) return;
            // Native generation and later geometry updates can change a saved
            // area's nodes. Check the final buffer as well as the queued patch.
            DynamicBuffer<AreaNode> nodes = EntityManager.GetBuffer<AreaNode>(area, true);
            DynamicBuffer<SurfaceMeshTriangle> triangles = EntityManager.GetBuffer<SurfaceMeshTriangle>(area, true);
            if (nodes.Length < 3 || triangles.Length == 0) return;
            SurfacePatch source = new SurfacePatch
            {
                SmallRoundabout = true,
                ExactBoundary = EntityManager.HasComponent<ExactSurfaceBoundary>(area)
            };
            for (int i = 0; i < nodes.Length; i++) source.Nodes.Add(nodes[i]);
            if (!math.all(source.Nodes[0].m_Position == source.Nodes[source.Count - 1].m_Position))
                source.Nodes.Add(source.Nodes[0]);
            foreach (SurfaceMeshTriangle triangle in triangles)
            {
                if (math.any(triangle.Indices < 0) || math.any(triangle.Indices >= source.Count - 1)) return;
                source.Triangles.Add(triangle.Indices);
            }
            SurfacePatch guarded = GuardSmallRoundaboutPatch(source, GetSurfaceExpansion(prefab));
            if (guarded.Count < 4 || guarded.Triangles.Count == 0)
            {
                // Do not let native triangulation redraw a rejected old sliver.
                // A rebuild creates the newly split, bounded approach ribbons.
                MarkAreaDeleted(area);
                return;
            }
            if (!EntityManager.HasComponent<ExactSurfaceBoundary>(area)) EntityManager.AddComponent<ExactSurfaceBoundary>(area);
            if (ReferenceEquals(guarded, source)) return;
            DynamicBuffer<AreaNode> outputNodes = EntityManager.GetBuffer<AreaNode>(area);
            outputNodes.ResizeUninitialized(guarded.Count - 1);
            for (int i = 0; i < outputNodes.Length; i++) outputNodes[i] = guarded.Nodes[i];
            DynamicBuffer<SurfaceMeshTriangle> outputTriangles = EntityManager.GetBuffer<SurfaceMeshTriangle>(area);
            outputTriangles.ResizeUninitialized(guarded.Triangles.Count);
            for (int i = 0; i < outputTriangles.Length; i++)
                outputTriangles[i] = new SurfaceMeshTriangle { Indices = guarded.Triangles[i] };
            // SurfaceExpandSystem expects a matching entry for every final node.
            if (EntityManager.HasBuffer<Expand>(area))
            {
                DynamicBuffer<Expand> expand = EntityManager.GetBuffer<Expand>(area);
                expand.ResizeUninitialized(outputNodes.Length);
                for (int i = 0; i < expand.Length; i++) expand[i] = default;
            }
            if (!EntityManager.HasComponent<Game.Common.Updated>(area)) EntityManager.AddComponent<Game.Common.Updated>(area);
        }
    }
}
