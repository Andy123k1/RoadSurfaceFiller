using System;
using System.Collections.Generic;
using Game.Net;
using Unity.Entities;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private sealed class LaneDeckFace
        {
            internal float3 A, B, C;
            internal float2 Minimum, Maximum;
        }

        private struct LaneDeckAlignment
        {
            internal int Matched, Nodes;
            internal float MaximumCorrection;
        }

        private List<LaneDeckFace> ReadJunctionLaneDeck(Entity node, List<JunctionLaneRoad> roads, float islandRadius)
        {
            List<LaneDeckFace> result = new List<LaneDeckFace>();
            float3 center = EntityManager.HasComponent<Game.Net.Node>(node) ? EntityManager.GetComponentData<Game.Net.Node>(node).m_Position : default;
            if (EntityManager.HasComponent<NodeGeometry>(node)) center.y = EntityManager.GetComponentData<NodeGeometry>(node).m_Position;
            foreach (JunctionLaneRoad road in roads)
            {
                if (road.Start ? !EntityManager.HasComponent<StartNodeGeometry>(road.Edge) : !EntityManager.HasComponent<EndNodeGeometry>(road.Edge)) continue;
                Composition composition = EntityManager.GetComponentData<Composition>(road.Edge);
                bool roundabout = islandRadius > 0.01f;
                if (!TryReadSurfaceLayout(road.Start ? composition.m_StartNode : composition.m_EndNode, out SurfaceLayout layout,
                    road.Layout, road.Start, highway: road.Layout.Highway, streetContext: roundabout)) continue;
                EdgeNodeGeometry geometry = road.Start ? EntityManager.GetComponentData<StartNodeGeometry>(road.Edge).m_Geometry :
                    EntityManager.GetComponentData<EndNodeGeometry>(road.Edge).m_Geometry;
                foreach (RoadBand band in layout.Roads)
                    for (int side = 0; side < 2; side++)
                    {
                        bool left = side == 0;
                        SurfacePatch patch;
                        if (roundabout)
                            patch = CreateRoundaboutRoadPolygon(geometry, layout.Composition, center,
                                (left ? -0.5f : 0.5f) * layout.Composition.m_Width,
                                left ? band.LowHeight : band.HighHeight, left, 0f, null);
                        else
                            patch = CreateJunctionSidePolygon(geometry, layout.Composition, band, left,
                                GetJunctionMouth(road.Edge, road.Layout, layout, band, left, false, road.Start),
                                GetCornerInsets(road.Edge, node, geometry, layout, band, left, false, false));
                        AddLaneDeckFaces(patch, result);
                    }
            }
            return result;
        }

        private static void AddLaneDeckFaces(SurfacePatch patch, List<LaneDeckFace> result)
        {
            foreach (SurfacePatch part in patch.Parts) AddLaneDeckFaces(part, result);
            foreach (int3 triangle in patch.Triangles)
            {
                float3 a = patch.Nodes[triangle.x].m_Position, b = patch.Nodes[triangle.y].m_Position, c = patch.Nodes[triangle.z].m_Position;
                result.Add(new LaneDeckFace { A = a, B = b, C = c, Minimum = math.min(a.xz, math.min(b.xz, c.xz)),
                    Maximum = math.max(a.xz, math.max(b.xz, c.xz)) });
            }
        }

        private static LaneDeckAlignment SnapJunctionLaneDeck(SurfacePatch patch, List<LaneDeckFace> faces)
        {
            LaneDeckAlignment alignment = default;
            foreach (SurfacePatch part in patch.Parts)
            {
                LaneDeckAlignment child = SnapJunctionLaneDeck(part, faces);
                alignment.Matched += child.Matched; alignment.Nodes += child.Nodes;
                alignment.MaximumCorrection = math.max(alignment.MaximumCorrection, child.MaximumCorrection);
            }
            for (int i = 0; i < patch.Nodes.Count; i++)
            {
                Game.Areas.Node node = patch.Nodes[i];
                float3 point = node.m_Position;
                bool matched = false;
                float height = point.y;
                foreach (LaneDeckFace face in faces)
                {
                    if (math.any(point.xz < face.Minimum - 0.0005f) || math.any(point.xz > face.Maximum + 0.0005f)) continue;
                    double bx = (double)face.B.x - face.A.x, bz = (double)face.B.z - face.A.z;
                    double cx = (double)face.C.x - face.A.x, cz = (double)face.C.z - face.A.z;
                    double px = (double)point.x - face.A.x, pz = (double)point.z - face.A.z;
                    double denominator = bx * cz - bz * cx;
                    if (Math.Abs(denominator) < 0.00000001d) continue;
                    double u = (px * cz - pz * cx) / denominator, v = (bx * pz - bz * px) / denominator;
                    if (u < -0.00001d || v < -0.00001d || u + v > 1.00001d) continue;
                    float y = (float)(face.A.y + u * (face.B.y - face.A.y) + v * (face.C.y - face.A.y));
                    // Adjoining approach meshes can overlap at a merge. The
                    // visible receiver is the upper deck, including a raised
                    // dedicated strip; do not choose a lower routing datum.
                    if (!matched || y > height) { matched = true; height = y; }
                }
                // The faces already include SurfaceLift and authored lane deck
                // heights. Navigation curves can sit above this receiver mesh,
                // especially where a highway joins a raised-curb roundabout.
                alignment.Nodes++;
                if (matched)
                {
                    alignment.Matched++;
                    alignment.MaximumCorrection = math.max(alignment.MaximumCorrection, math.abs(height - point.y));
                }
                node.m_Position.y = height;
                patch.Nodes[i] = node;
            }
            return alignment;
        }
    }
}
