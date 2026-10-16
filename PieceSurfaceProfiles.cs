using System;
using System.Collections.Generic;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private sealed class PieceSurfaceProfile
        {
            internal readonly List<RoadBand> Pavement = new List<RoadBand>();
            internal readonly List<RoadBand> Grass = new List<RoadBand>();
            internal readonly List<string> Materials = new List<string>();
            internal bool NativeCache;
        }

        private readonly Dictionary<Entity, PieceSurfaceProfile> m_PieceSurfaceProfiles = new Dictionary<Entity, PieceSurfaceProfile>();
        private readonly HashSet<Entity> m_LoggedPieceSurfaceProfiles = new HashSet<Entity>();
        private readonly HashSet<Entity> m_LoggedSidewalkMaterialLayouts = new HashSet<Entity>();

        private PieceSurfaceProfile GetPieceSurfaceProfile(Entity entity)
        {
            if (m_PieceSurfaceProfiles.TryGetValue(entity, out PieceSurfaceProfile cached)) return cached;
            PieceSurfaceProfile result = new PieceSurfaceProfile();
            if (!m_PrefabSystem.TryGetPrefab(entity, out NetPiecePrefab piece) || !piece.hasGeometryAsset) return result;
            try
            {
                List<bool> grassMaterials = new List<bool>();
                foreach (var surface in piece.surfaceAssets)
                {
                    // This identifies a rendered material, not a road/piece
                    // name: SidewalkGrass pieces contain paved faces as well.
                    string name = surface == null ? "" : surface.name;
                    result.Materials.Add(name);
                    grassMaterials.Add(name.IndexOf("Grass", StringComparison.OrdinalIgnoreCase) >= 0);
                }
                // Native net rendering flattens every geometry mesh into these
                // buffers, with one continuous material-slot sequence. Once
                // cached, the asset may have released its compressed attributes.
                if (TryReadCachedPieceSurfaceProfile(entity, grassMaterials, result))
                {
                    result.NativeCache = true;
                    MergePieceSurfaceBands(result.Pavement, false);
                    MergePieceSurfaceBands(result.Grass, true);
                    m_PieceSurfaceProfiles[entity] = result;
                    return result;
                }
                result.Pavement.Clear(); result.Grass.Clear();
                var asset = piece.geometryAsset;
                Mesh[] meshes = asset.ObtainMeshes(true);
                try
                {
                    int materialSlot = 0;
                    if (meshes != null)
                        foreach (Mesh mesh in meshes)
                        {
                            if (mesh == null) continue;
                            using (Mesh.MeshDataArray dataArray = Mesh.AcquireReadOnlyMeshData(mesh))
                            {
                                Mesh.MeshData data = dataArray[0];
                                using (NativeArray<Vector3> positions = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp))
                                {
                                    data.GetVertices(positions);
                                    float slice = mesh.bounds.center.z;
                                    for (int material = 0; material < data.subMeshCount; material++)
                                    {
                                        SubMeshDescriptor sub = data.GetSubMesh(material);
                                        bool grass = materialSlot < grassMaterials.Count && grassMaterials[materialSlot];
                                        materialSlot++;
                                        if (sub.topology != MeshTopology.Triangles) continue;
                                        for (int i = sub.indexStart; i + 2 < sub.indexStart + sub.indexCount; i += 3)
                                        {
                                            int a, b, c;
                                            if (data.indexFormat == IndexFormat.UInt16)
                                            {
                                                NativeArray<ushort> indices = data.GetIndexData<ushort>();
                                                a = indices[i] + sub.baseVertex; b = indices[i + 1] + sub.baseVertex;
                                                c = indices[i + 2] + sub.baseVertex;
                                            }
                                            else
                                            {
                                                NativeArray<uint> indices = data.GetIndexData<uint>();
                                                a = (int)indices[i] + sub.baseVertex; b = (int)indices[i + 1] + sub.baseVertex;
                                                c = (int)indices[i + 2] + sub.baseVertex;
                                            }
                                            if (a < 0 || b < 0 || c < 0 || a >= positions.Length || b >= positions.Length || c >= positions.Length) continue;
                                            float3 p = positions[a], q = positions[b], r = positions[c];
                                            AddPieceSurfaceFace(result, p, q, r, slice, grass);
                                        }
                                    }
                                }
                            }
                        }
                }
                finally { asset.ReleaseMeshes(); }
                MergePieceSurfaceBands(result.Pavement, false);
                MergePieceSurfaceBands(result.Grass, true);
                if (meshes != null && meshes.Length > 0) m_PieceSurfaceProfiles[entity] = result;
            }
            catch (Exception exception)
            {
                result.Pavement.Clear(); result.Grass.Clear();
                // Do not retain an incomplete profile: native mesh buffers can
                // become available after the asset has shed its source data.
                if (!m_LoggedMeshReadFailure)
                {
                    Mod.Log.Warn("Could not read native material footprints; using the lane curb fallback. " + exception.Message);
                    m_LoggedMeshReadFailure = true;
                }
            }
            return result;
        }

        private bool TryReadCachedPieceSurfaceProfile(Entity entity, List<bool> grassMaterials, PieceSurfaceProfile result)
        {
            if (!EntityManager.HasBuffer<MeshVertex>(entity) || !EntityManager.HasBuffer<MeshIndex>(entity) ||
                !EntityManager.HasBuffer<MeshMaterial>(entity)) return false;
            DynamicBuffer<MeshVertex> vertices = EntityManager.GetBuffer<MeshVertex>(entity, true);
            DynamicBuffer<MeshIndex> indices = EntityManager.GetBuffer<MeshIndex>(entity, true);
            DynamicBuffer<MeshMaterial> materials = EntityManager.GetBuffer<MeshMaterial>(entity, true);
            if (vertices.Length < 3 || indices.Length < 3 || materials.Length == 0 || materials.Length > grassMaterials.Count) return false;
            int count = 0;
            foreach (MeshMaterial material in materials)
            {
                if (material.m_StartIndex < 0 || material.m_IndexCount < 0 ||
                    material.m_StartIndex > indices.Length - material.m_IndexCount) return false;
                count += material.m_IndexCount;
            }
            if (count < 3) return false;
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
            foreach (MeshVertex vertex in vertices)
            {
                minZ = math.min(minZ, vertex.m_Vertex.z); maxZ = math.max(maxZ, vertex.m_Vertex.z);
            }
            float slice = (minZ + maxZ) * 0.5f;
            for (int slot = 0; slot < materials.Length; slot++)
            {
                MeshMaterial material = materials[slot];
                for (int i = material.m_StartIndex; i + 2 < material.m_StartIndex + material.m_IndexCount; i += 3)
                {
                    // CacheMeshDataJob already adds each source mesh's vertex
                    // base to these indices. Adding m_StartVertex would double it.
                    int a = indices[i].m_Index, b = indices[i + 1].m_Index, c = indices[i + 2].m_Index;
                    if (a < 0 || b < 0 || c < 0 || a >= vertices.Length || b >= vertices.Length || c >= vertices.Length) return false;
                    AddPieceSurfaceFace(result, vertices[a].m_Vertex, vertices[b].m_Vertex,
                        vertices[c].m_Vertex, slice, grassMaterials[slot]);
                }
            }
            return true;
        }

        private static void AddPieceSurfaceFace(PieceSurfaceProfile profile, float3 p, float3 q, float3 r, float slice, bool grass)
        {
            float3 normal = math.normalizesafe(math.cross(q - p, r - p));
            // Grass is a permanent footprint exclusion, including a ground
            // receiver with reverse winding. Pavement uses its upper face.
            if ((grass ? math.abs(normal.y) : normal.y) < 0.9f ||
                !TryPieceSurfaceSlice(p, q, r, slice, out RoadBand band)) return;
            (grass ? profile.Grass : profile.Pavement).Add(band);
        }

        private static LaneSpan CompleteSidewalkOuterEdge(LaneSpan sidewalk, LaneSpan physical, LaneSpan pedestrian, bool left)
        {
            // The outside rim can sit 0.2 m below the main walking plateau.
            // It is still in the native walking envelope. Retain that width
            // without moving the inner curb toward traffic or planting beds.
            return left ? new LaneSpan(math.max(physical.Low, math.min(sidewalk.Low, pedestrian.Low)), sidewalk.High, sidewalk.Height) :
                new LaneSpan(sidewalk.Low, math.min(physical.High, math.max(sidewalk.High, pedestrian.High)), sidewalk.Height);
        }

        private static bool TryPieceSurfaceSlice(float3 a, float3 b, float3 c, float z, out RoadBand band)
        {
            band = default;
            float3[] vertices = { a, b, c };
            List<float3> points = new List<float3>(4);
            for (int i = 0; i < 3; i++)
            {
                float3 p = vertices[i], q = vertices[(i + 1) % 3];
                float dp = p.z - z, dq = q.z - z;
                if (math.abs(dp) <= 0.00001f) points.Add(p);
                if ((dp < -0.00001f && dq > 0.00001f) || (dp > 0.00001f && dq < -0.00001f))
                    points.Add(math.lerp(p, q, dp / (dp - dq)));
            }
            if (points.Count < 2) return false;
            float3 low = points[0], high = points[0];
            foreach (float3 point in points)
            {
                if (point.x < low.x) low = point;
                if (point.x > high.x) high = point;
            }
            if (high.x <= low.x + 0.00001f) return false;
            band = new RoadBand(low.x, high.x, low.y, high.y);
            return true;
        }

        private static void MergePieceSurfaceBands(List<RoadBand> bands, bool grass)
        {
            bands.Sort((a, b) => a.Low != b.Low ? a.Low.CompareTo(b.Low) : a.LowHeight.CompareTo(b.LowHeight));
            // Merge only the same deck. Otherwise an underside or a narrow
            // curb lip could flatten the walking face's physical height.
            for (int i = 0; i < bands.Count; i++)
                for (int j = i + 1; j < bands.Count;)
                {
                    RoadBand a = bands[i], b = bands[j];
                    if (b.Low > a.High + 0.001f || (!grass &&
                        math.abs(PavementHeight(a, math.clamp(b.Low, a.Low, a.High)) - b.LowHeight) > 0.02f))
                    { j++; continue; }
                    bands[i] = new RoadBand(a.Low, math.max(a.High, b.High), a.LowHeight,
                        b.High > a.High ? b.HighHeight : a.HighHeight);
                    bands.RemoveAt(j);
                    j = i + 1; // Revisit neighbours after extending the interval.
                }
        }

        private static float PieceMeshXDirection(NetCompositionPiece piece)
        {
            // GenerateCompositionMesh mirrors mesh X by default. Section Invert
            // restores positive mesh X; lane positions use a separate convention.
            return (piece.m_SectionFlags & NetSectionFlags.Invert) != 0 ? 1f : -1f;
        }

        private RoadBand TransformPieceSurfaceBand(NetCompositionPiece piece, RoadBand band)
        {
            float sourceWidth = EntityManager.HasComponent<NetPieceData>(piece.m_Piece)
                ? EntityManager.GetComponentData<NetPieceData>(piece.m_Piece).m_Width : piece.m_Size.x;
            float scale = piece.m_Size.x / math.max(0.01f, sourceWidth);
            bool mirrored = PieceMeshXDirection(piece) < 0f;
            return mirrored ? new RoadBand(piece.m_Offset.x - band.High * scale, piece.m_Offset.x - band.Low * scale,
                piece.m_Offset.y + band.HighHeight, piece.m_Offset.y + band.LowHeight) :
                new RoadBand(piece.m_Offset.x + band.Low * scale, piece.m_Offset.x + band.High * scale,
                    piece.m_Offset.y + band.LowHeight, piece.m_Offset.y + band.HighHeight);
        }

        private bool TryGetMaterialSidewalkPlateau(NetCompositionPiece piece, LaneSpan physical,
            LaneSpan pedestrian, float roadHeight, LaneSpan? adjacentRoad, float middle, out LaneSpan sidewalk)
        {
            sidewalk = default;
            float center = (pedestrian.Low + pedestrian.High) * 0.5f;
            bool left = center < middle;
            float bestWidth = 0f;
            PieceSurfaceProfile profile = GetPieceSurfaceProfile(piece.m_Piece);
            foreach (RoadBand source in profile.Pavement)
            {
                RoadBand band = TransformPieceSurfaceBand(piece, source);
                float low = math.max(physical.Low, band.Low), high = math.min(physical.High, band.High);
                if (adjacentRoad.HasValue)
                {
                    if (left) high = math.min(high, adjacentRoad.Value.Low);
                    else low = math.max(low, adjacentRoad.Value.High);
                }
                if (center < low - 0.01f || center > high + 0.01f || high <= low + 0.1f) continue;
                float height = PavementHeight(band, math.clamp(center, low, high));
                if (height < roadHeight + 0.035f || high - low <= bestWidth) continue;
                sidewalk = new LaneSpan(low, high, height);
                bestWidth = high - low;
            }
            return bestWidth > 0f;
        }

        private void LogPieceMaterialProfile(NetCompositionPiece piece)
        {
            if (m_LoggedPieceSurfaceProfiles.Count >= 48 || !m_LoggedPieceSurfaceProfiles.Add(piece.m_Piece)) return;
            PieceSurfaceProfile profile = GetPieceSurfaceProfile(piece.m_Piece);
            List<string> grass = new List<string>(), pavement = new List<string>();
            foreach (RoadBand band in profile.Grass) grass.Add(band.Low.ToString("0.###") + ".." + band.High.ToString("0.###"));
            foreach (RoadBand band in profile.Pavement) pavement.Add(band.Low.ToString("0.###") + ".." + band.High.ToString("0.###") +
                " @ " + band.LowHeight.ToString("0.###") + ".." + band.HighHeight.ToString("0.###"));
            Mod.Log.Info("Native piece material profile: piece='" + m_PrefabSystem.GetPrefabName(piece.m_Piece) +
                "'; native cache=" + profile.NativeCache + "; mesh X direction=" + PieceMeshXDirection(piece) +
                "; materials=[" + string.Join(", ", profile.Materials) + "]; grass=[" + string.Join(", ", grass) +
                "]; paved decks=[" + string.Join(", ", pavement) + "].");
        }

        private void LogSidewalkMaterialLayout(Entity prefab, List<LaneSpan> walking, SurfaceLayout layout, bool edge)
        {
            if (walking.Count == 0 || m_LoggedSidewalkMaterialLayouts.Count >= 64 ||
                !m_LoggedSidewalkMaterialLayouts.Add(prefab)) return;
            List<string> routes = new List<string>(), painted = new List<string>(), grass = new List<string>();
            foreach (LaneSpan span in walking) routes.Add(span.Low.ToString("0.###") + ".." + span.High.ToString("0.###") + " @ " + span.Height.ToString("0.###"));
            foreach (LaneSpan span in layout.Sidewalks) painted.Add(span.Low.ToString("0.###") + ".." + span.High.ToString("0.###") + " @ " + span.Height.ToString("0.###"));
            foreach (LaneSpan span in layout.Planted) grass.Add(span.Low.ToString("0.###") + ".." + span.High.ToString("0.###"));
            Mod.Log.Info("Sidewalk material layout: composition=" + prefab.Index + "; node=" + !edge + "; width=" + layout.Composition.m_Width +
                "; walking routes=[" + string.Join(", ", routes) + "]; paved walks=[" + string.Join(", ", painted) +
                "]; protected planting=[" + string.Join(", ", grass) + "].");
        }
    }
}
