using System.Collections.Generic;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private sealed class ClippedMeshEdge
        {
            internal int From, To, Triangle, Count = 1;
        }

        private static SurfacePatch CopySurfacePatchProperties(SurfacePatch source)
        {
            return new SurfacePatch
            {
                ExactBoundary = source.ExactBoundary,
                SmallRoundabout = source.SmallRoundabout,
                DedicatedLanesClipped = source.DedicatedLanesClipped,
                PlantedAreasClipped = source.PlantedAreasClipped,
                ProjectionMinimum = source.ProjectionMinimum,
                ProjectionMaximum = source.ProjectionMaximum
            };
        }

        private static long SurfaceMeshEdgeKey(int a, int b)
        {
            return ((long)math.min(a, b) << 32) | (uint)math.max(a, b);
        }

        private SurfacePatch ClipJunctionMesh(SurfacePatch source, CurbCut cut, bool outside, out bool complete)
        {
            complete = false;
            SurfacePatch mesh = CopySurfacePatchProperties(source);
            int count = source.Count - 1;
            for (int i = 0; i < count; i++) mesh.Nodes.Add(source.Nodes[i]);
            Dictionary<long, int> intersections = new Dictionary<long, int>();
            foreach (int3 triangle in source.Triangles)
            {
                if (math.any(triangle < 0) || math.any(triangle >= count)) return new SurfacePatch();
                int[] indices = { triangle.x, triangle.y, triangle.z };
                List<int> polygon = new List<int>(4);
                int previous = indices[2];
                float previousSide = cut.Side(mesh.Nodes[previous].m_Position) * (outside ? 1f : -1f);
                foreach (int index in indices)
                {
                    float side = cut.Side(mesh.Nodes[index].m_Position) * (outside ? 1f : -1f);
                    if ((side >= 0f) != (previousSide >= 0f))
                    {
                        int crossing;
                        // Reuse a vertex on the plane so adjacent triangles
                        // cannot acquire different copies of the same endpoint.
                        if (math.abs(previousSide) <= 0.0001f) crossing = previous;
                        else if (math.abs(side) <= 0.0001f) crossing = index;
                        else
                        {
                            long key = SurfaceMeshEdgeKey(previous, index);
                            if (!intersections.TryGetValue(key, out crossing))
                            {
                                crossing = mesh.Count;
                                mesh.Nodes.Add(new AreaNode(math.lerp(mesh.Nodes[previous].m_Position,
                                    mesh.Nodes[index].m_Position, math.saturate(previousSide / (previousSide - side))), 0f));
                                intersections.Add(key, crossing);
                            }
                        }
                        if (polygon.Count == 0 || polygon[polygon.Count - 1] != crossing) polygon.Add(crossing);
                    }
                    if (side >= 0f && (polygon.Count == 0 || polygon[polygon.Count - 1] != index)) polygon.Add(index);
                    previous = index;
                    previousSide = side;
                }
                if (polygon.Count > 1 && polygon[0] == polygon[polygon.Count - 1]) polygon.RemoveAt(polygon.Count - 1);
                for (int i = 1; i + 1 < polygon.Count; i++)
                    AddRibbonTriangle(mesh, new int3(polygon[0], polygon[i], polygon[i + 1]));
            }
            if (mesh.Triangles.Count == 0) { complete = true; return CopySurfacePatchProperties(source); }

            int[] parents = new int[mesh.Triangles.Count];
            for (int i = 0; i < parents.Length; i++) parents[i] = i;
            Dictionary<long, ClippedMeshEdge> edges = new Dictionary<long, ClippedMeshEdge>();
            for (int i = 0; i < mesh.Triangles.Count; i++)
            {
                int3 triangle = mesh.Triangles[i];
                for (int side = 0; side < 3; side++)
                {
                    int from = triangle[side], to = triangle[(side + 1) % 3];
                    long key = SurfaceMeshEdgeKey(from, to);
                    if (!edges.TryGetValue(key, out ClippedMeshEdge edge))
                        edges.Add(key, new ClippedMeshEdge { From = from, To = to, Triangle = i });
                    else
                    {
                        // Shared edges must have opposite directions. Joining
                        // only along edges keeps point-touching pieces separate.
                        if (++edge.Count != 2 || edge.From != to || edge.To != from) return RecoverClippedJunctionMesh(source, mesh, out complete);
                        parents[FindClippedMeshRoot(parents, i)] = FindClippedMeshRoot(parents, edge.Triangle);
                    }
                }
            }

            Dictionary<int, List<int>> trianglesByRoot = new Dictionary<int, List<int>>();
            Dictionary<int, Dictionary<int, int>> bordersByRoot = new Dictionary<int, Dictionary<int, int>>();
            for (int i = 0; i < parents.Length; i++)
            {
                int root = FindClippedMeshRoot(parents, i);
                if (!trianglesByRoot.TryGetValue(root, out List<int> triangles))
                    trianglesByRoot.Add(root, triangles = new List<int>());
                triangles.Add(i);
            }
            foreach (ClippedMeshEdge edge in edges.Values)
            {
                if (edge.Count != 1) continue;
                int root = FindClippedMeshRoot(parents, edge.Triangle);
                if (!bordersByRoot.TryGetValue(root, out Dictionary<int, int> borders))
                    bordersByRoot.Add(root, borders = new Dictionary<int, int>());
                if (borders.ContainsKey(edge.From)) return RecoverClippedJunctionMesh(source, mesh, out complete);
                borders.Add(edge.From, edge.To);
            }
            SurfacePatch result = CopySurfacePatchProperties(source);
            foreach (KeyValuePair<int, List<int>> component in trianglesByRoot)
            {
                if (!bordersByRoot.TryGetValue(component.Key, out Dictionary<int, int> borders) || borders.Count < 3)
                    return RecoverClippedJunctionMesh(source, mesh, out complete);
                SurfacePatch part = CopySurfacePatchProperties(source);
                Dictionary<int, int> mapping = new Dictionary<int, int>();
                int first = -1;
                foreach (int vertex in borders.Keys) { first = vertex; break; }
                int current = first;
                do
                {
                    if (mapping.ContainsKey(current) || !borders.TryGetValue(current, out int next)) return RecoverClippedJunctionMesh(source, mesh, out complete);
                    mapping.Add(current, part.Count);
                    part.Nodes.Add(mesh.Nodes[current]);
                    current = next;
                } while (current != first);
                if (mapping.Count != borders.Count) return RecoverClippedJunctionMesh(source, mesh, out complete);
                foreach (int index in component.Value)
                {
                    int3 triangle = mesh.Triangles[index];
                    // A clipped boundary-only ribbon still has all its vertices
                    // on its contour, including cut points on interior diagonals.
                    if (!mapping.TryGetValue(triangle.x, out int a) || !mapping.TryGetValue(triangle.y, out int b) ||
                        !mapping.TryGetValue(triangle.z, out int c)) return RecoverClippedJunctionMesh(source, mesh, out complete);
                    part.Triangles.Add(new int3(a, b, c));
                }
                part.Nodes.Add(part.Nodes[0]);
                result.Parts.Add(part);
            }
            complete = true;
            return result.Parts.Count == 1 ? result.Parts[0] : result;
        }

        private SurfacePatch RecoverClippedJunctionMesh(SurfacePatch source, SurfacePatch mesh, out bool complete)
        {
            // The clipped triangles are already exact. Some contain interior
            // vertices or point-touching contours that cannot be represented
            // by the boundary-only graph above. Union these triangles before
            // considering a perimeter fallback, which can lose whole wedges.
            mesh.Nodes.Add(mesh.Nodes[0]);
            List<LaneMaskCell> noMasks = new List<LaneMaskCell>();
            if (TrySubtractLaneContours(mesh, noMasks, out SurfacePatch result) || TrySweepLaneMasks(mesh, noMasks, out result))
            {
                RestoreLaneMaskState(result, source.DedicatedLanesClipped);
                complete = true; return result;
            }
            complete = false; return new SurfacePatch();
        }

        private static int FindClippedMeshRoot(int[] parents, int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }
            return index;
        }
    }
}
