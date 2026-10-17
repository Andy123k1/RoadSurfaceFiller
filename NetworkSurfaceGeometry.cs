using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using UnityEngine;
using AreaNode = Game.Areas.Node;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private sealed class SurfaceLayout
        {
            internal NetCompositionData Composition;
            internal bool Highway;
            internal bool HighwaySource;
            internal int RecoveredRoundaboutSides;
            internal bool MixedRoundabout;
            internal bool SeparateBikeLanes, SeparateBusLanes;
            internal bool FillBusShoulders;
            internal readonly List<LaneSpan> Driving = new List<LaneSpan>();
            internal readonly List<RoadBand> Roads = new List<RoadBand>();
            internal readonly List<LaneSpan> Sidewalks = new List<LaneSpan>();
            internal readonly List<LaneSpan> BikeLanes = new List<LaneSpan>();
            internal readonly List<LaneSpan> BusLanes = new List<LaneSpan>();
            internal readonly List<LaneSpan> Planted = new List<LaneSpan>();
        }

        private readonly Dictionary<Entity, List<float3>> m_PieceTopVertices = new Dictionary<Entity, List<float3>>();
        private readonly HashSet<Entity> m_LoggedLayouts = new HashSet<Entity>();
        private bool m_LoggedMeshReadFailure;
        private bool m_LoggedMissingNodeLayout;
        private bool m_LoggedJunctionCounts;
        private bool m_LoggedRoundaboutCounts;
        private bool m_LoggedStreetJunctionRules;
        private bool m_LoggedRoundaboutStreetRules;
        private readonly HashSet<Entity> m_LoggedRoundaboutRuleContexts = new HashSet<Entity>();
        private bool m_LoggedJunctionMapping;

        private struct CornerInsets
        {
            internal float Near;
            internal float Far;
            internal bool HasPositions;
            internal float3 NearPosition;
            internal float3 FarPosition;
        }

        private sealed class SurfacePatch
        {
            internal readonly List<AreaNode> Nodes = new List<AreaNode>();
            internal readonly List<int3> Triangles = new List<int3>();
            internal readonly List<SurfacePatch> Parts = new List<SurfacePatch>();
            internal float ProjectionMinimum = float.NegativeInfinity;
            internal float ProjectionMaximum = float.PositiveInfinity;
            internal bool SmallRoundabout;
            internal bool ExactBoundary;
            internal bool DedicatedLanesClipped;
            internal bool PlantedAreasClipped;
            internal int BevelCount;
            internal int Count => Nodes.Count;
        }

        private bool TryReadSurfaceLayout(Entity prefab, out SurfaceLayout layout, SurfaceLayout fallback = null, bool invertFallback = false,
            bool highway = false, bool streetContext = false, Entity roadPrefab = default)
        {
            layout = null;
            if (!EntityManager.Exists(prefab) || !EntityManager.HasComponent<NetCompositionData>(prefab))
                return false;
            NetCompositionData composition = EntityManager.GetComponentData<NetCompositionData>(prefab);
            if (!math.isfinite(composition.m_Width) || composition.m_Width < 0.1f)
                return false;

            List<LaneSpan> driving = new List<LaneSpan>();
            List<LaneSpan> physicalDriving = new List<LaneSpan>();
            List<LaneSpan> walking = new List<LaneSpan>();
            List<LaneSpan> bicycles = new List<LaneSpan>();
            List<LaneSpan> buses = new List<LaneSpan>();
            bool hasLaneBuffer = EntityManager.HasBuffer<NetCompositionLane>(prefab);
            if (hasLaneBuffer)
            {
                DynamicBuffer<NetCompositionLane> lanes = EntityManager.GetBuffer<NetCompositionLane>(prefab, true);
                for (int i = 0; i < lanes.Length; i++)
                    ReadLane(lanes[i].m_Lane, lanes[i].m_Position, lanes[i].m_Flags, driving, walking, bicycles, buses, physicalDriving);
            }

            List<LaneSpan> barriers = new List<LaneSpan>();
            List<NetCompositionPiece> pieces = new List<NetCompositionPiece>();
            List<NetCompositionPiece> groundPieces = new List<NetCompositionPiece>();
            if (EntityManager.HasBuffer<NetCompositionPiece>(prefab))
            {
                DynamicBuffer<NetCompositionPiece> buffer = EntityManager.GetBuffer<NetCompositionPiece>(prefab, true);
                for (int i = 0; i < buffer.Length; i++)
                {
                    NetCompositionPiece piece = buffer[i];
                    if (piece.m_Size.x <= 0.01f ||
                        (piece.m_SectionFlags & (NetSectionFlags.Underground | NetSectionFlags.Overhead |
                            NetSectionFlags.Hidden | NetSectionFlags.HiddenSurface | NetSectionFlags.HiddenTop)) != 0)
                        continue;
                    groundPieces.Add(piece);
                    if ((piece.m_PieceFlags & NetPieceFlags.Surface) == 0) continue;
                    pieces.Add(piece);
                    // The game's node archetype deliberately omits NetCompositionLane.
                    // Its authored pieces still carry their NetPieceLane buffers.
                    if (!hasLaneBuffer && EntityManager.HasBuffer<NetPieceLane>(piece.m_Piece))
                    {
                        DynamicBuffer<NetPieceLane> pieceLanes = EntityManager.GetBuffer<NetPieceLane>(piece.m_Piece, true);
                        bool invert = (piece.m_SectionFlags & NetSectionFlags.Invert) != 0;
                        for (int j = 0; j < pieceLanes.Length; j++)
                        {
                            NetPieceLane lane = pieceLanes[j];
                            float3 position = lane.m_Position;
                            if (invert) position.x = -position.x;
                            ReadLane(lane.m_Lane, position + piece.m_Offset, lane.m_ExtraFlags, driving, walking, bicycles, buses, physicalDriving);
                        }
                    }
                }
            }

            // Master routing groups can span sidewalks and planted verges after
            // any upgrade. Physical lanes alone define the carriageway.
            if (physicalDriving.Count > 0)
            {
                driving.Clear();
                driving.AddRange(physicalDriving);
            }
            if (fallback != null)
            {
                if (driving.Count == 0)
                    CopyApproachSpans(fallback.Driving, driving, fallback.Composition, composition, invertFallback);
                if (walking.Count == 0)
                    CopyApproachSpans(fallback.Sidewalks, walking, fallback.Composition, composition, invertFallback);
            }

            bool highwaySource = highway || (fallback != null && fallback.HighwaySource);
            bool requireRaisedSidewalk = streetContext && highwaySource;
            highway = !streetContext && highwaySource;
            List<LaneSpan> shoulderLanes = new List<LaneSpan>();
            if (highway && (Mod.ActiveSetting == null || !Mod.ActiveSetting.IncludeHighwaySidewalks))
            {
                // Highway pedestrian/service lanes describe shoulders rather than
                // street sidewalks. They remain in the paved carriageway by default.
                shoulderLanes.AddRange(walking);
                walking.Clear();
            }
            layout = new SurfaceLayout { Composition = composition, Highway = highway, HighwaySource = highwaySource };
            if (fallback != null)
            {
                layout.SeparateBikeLanes = fallback.SeparateBikeLanes;
                layout.SeparateBusLanes = fallback.SeparateBusLanes;
                layout.FillBusShoulders = fallback.FillBusShoulders;
            }
            if (roadPrefab != Entity.Null && Mod.ActiveSetting != null)
                GetRuleLaneSettings(Mod.ActiveSetting, roadPrefab, streetContext, out layout.SeparateBikeLanes,
                    out layout.SeparateBusLanes, out _, out layout.FillBusShoulders);
            layout.Driving.AddRange(driving);
            // Never infer dedicated lanes from a different approach's fallback.
            // Intersections can deliberately replace an exclusive lane with
            // shared traffic; only this composition's authored lanes count.
            layout.BikeLanes.AddRange(bicycles);
            layout.BusLanes.AddRange(buses);
            MergeSidewalkSpans(layout.BikeLanes);
            MergeSidewalkSpans(layout.BusLanes);
            for (int i = 0; i < walking.Count; i++)
            {
                LaneSpan lane = walking[i];
                float center = (lane.Low + lane.High) * 0.5f;
                LaneSpan sidewalk = lane;
                bool sidewalkFound = false;
                float bestWidth = 0f;
                bool bestRaised = false;
                for (int j = 0; j < pieces.Count; j++)
                {
                    NetCompositionPiece piece = pieces[j];
                    LaneSpan physical = PieceSpan(piece, lane.Height);
                    if (center < physical.Low - 0.01f || center > physical.High + 0.01f ||
                        math.abs(piece.m_Offset.y - lane.Height) > 1f)
                        continue;
                    // A side piece can include both the raised sidewalk AND an
                    // asphalt shoulder. Its complete width is not a sidewalk mask.
                    physical = GetSidewalkPlateau(piece, physical, lane, driving, composition.m_MiddleOffset, out bool raised);
                    CompositionFlags.Side sideFlags = center < composition.m_MiddleOffset
                        ? composition.m_Flags.m_Left : composition.m_Flags.m_Right;
                    if (requireRaisedSidewalk && !raised &&
                        (sideFlags & (CompositionFlags.Side.Sidewalk | CompositionFlags.Side.WideSidewalk)) == 0)
                        continue;
                    bool crossesRoad = false;
                    for (int k = 0; k < driving.Count; k++)
                        crossesRoad |= physical.Low < driving[k].High - 0.01f && physical.High > driving[k].Low + 0.01f;
                    if (crossesRoad)
                        continue;
                    if (center < physical.Low - 0.01f || center > physical.High + 0.01f ||
                        (sidewalkFound && (bestRaised && !raised || bestRaised == raised && physical.High - physical.Low <= bestWidth)))
                        continue;
                    sidewalk = physical;
                    sidewalkFound = true;
                    bestWidth = physical.High - physical.Low;
                    bestRaised = raised;
                }
                if (requireRaisedSidewalk && !sidewalkFound) continue;
                sidewalk = RefineBicycleSidewalkCurb(sidewalk, lane, bicycles, composition.m_MiddleOffset);
                sidewalk = new LaneSpan(math.max(-composition.m_Width * 0.5f, sidewalk.Low),
                    math.min(composition.m_Width * 0.5f, sidewalk.High), sidewalk.Height);
                if (sidewalk.High > sidewalk.Low + 0.01f)
                {
                    layout.Sidewalks.Add(sidewalk);
                    barriers.Add(sidewalk);
                }
            }
            MergeSidewalkSpans(layout.Sidewalks);
            ReadPlantedSpans(prefab, layout, groundPieces, walking, hasLaneBuffer);
            ExcludePlantedSidewalks(layout);
            LogSidewalkMaterialLayout(prefab, walking, layout, hasLaneBuffer);
            barriers.Clear();
            barriers.AddRange(layout.Sidewalks);
            barriers.AddRange(layout.Planted);
            foreach (NetCompositionPiece piece in pieces)
            {
                if ((piece.m_PieceFlags & NetPieceFlags.BlockTraffic) == 0 ||
                    (piece.m_PieceFlags & NetPieceFlags.HasRoadLanes) != 0)
                    continue;
                LaneSpan physical = PieceSpan(piece, piece.m_Offset.y);
                bool containsSidewalk = false;
                foreach (LaneSpan lane in walking)
                {
                    float center = (lane.Low + lane.High) * 0.5f;
                    containsSidewalk |= center >= physical.Low - 0.01f && center <= physical.High + 0.01f;
                }
                foreach (LaneSpan lane in shoulderLanes)
                {
                    float center = (lane.Low + lane.High) * 0.5f;
                    containsSidewalk |= center >= physical.Low - 0.01f && center <= physical.High + 0.01f;
                }
                // Sidewalk pieces are already blocked at their actual raised curb.
                // Blocking the complete piece here would remove the shoulder again.
                if (!containsSidewalk)
                    barriers.Add(physical);
            }
            layout.Roads.AddRange(BuildPavementBands(composition.m_Width, driving, barriers));
            LogLayout(prefab, layout, hasLaneBuffer);
            return true;
        }

        private void ReadLane(Entity prefab, float3 position, LaneFlags flags, List<LaneSpan> driving, List<LaneSpan> walking,
            List<LaneSpan> bicycles, List<LaneSpan> buses, List<LaneSpan> physicalDriving)
        {
            if (!EntityManager.Exists(prefab) || !EntityManager.HasComponent<NetLaneData>(prefab))
                return;
            NetLaneData data = EntityManager.GetComponentData<NetLaneData>(prefab);
            flags |= data.m_Flags;
            if ((flags & (LaneFlags.Utility | LaneFlags.CrossRoad)) != 0)
                return;
            float halfWidth = math.max(0.25f, data.m_Width * 0.5f);
            LaneSpan span = new LaneSpan(position.x - halfWidth, position.x + halfWidth, position.y, (flags & LaneFlags.Parking) != 0);
            if ((flags & (LaneFlags.Road | LaneFlags.Parking)) != 0)
            {
                driving.Add(span);
                if ((flags & (LaneFlags.Master | LaneFlags.Virtual)) == 0) physicalDriving.Add(span);
            }
            else if ((flags & LaneFlags.Pedestrian) != 0)
                walking.Add(span);
            ReadDedicatedLane(prefab, position, flags, data, bicycles, buses);
        }

        private static void CopyApproachSpans(List<LaneSpan> source, List<LaneSpan> target,
            NetCompositionData from, NetCompositionData to, bool invert)
        {
            float scale = to.m_Width / math.max(0.01f, from.m_Width);
            foreach (LaneSpan span in source)
            {
                float low = invert ? -span.High : span.Low;
                float high = invert ? -span.Low : span.High;
                target.Add(new LaneSpan(low * scale, high * scale, span.Height, span.Parking));
            }
        }

        private LaneSpan GetSidewalkPlateau(NetCompositionPiece piece, LaneSpan physical, LaneSpan lane,
            List<LaneSpan> driving, float middle, out bool raised)
        {
            raised = false;
            float center = (lane.Low + lane.High) * 0.5f;
            bool left = center < middle;
            float roadHeight = lane.Height;
            float closestRoad = float.MaxValue;
            LaneSpan? adjacentRoad = null;
            foreach (LaneSpan road in driving)
            {
                float distance = math.abs(center - (road.Low + road.High) * 0.5f);
                if (distance < closestRoad)
                {
                    roadHeight = road.Height;
                    closestRoad = distance;
                    adjacentRoad = road;
                }
            }
            if (TryGetMaterialSidewalkPlateau(piece, physical, lane, roadHeight, adjacentRoad, middle, out LaneSpan painted))
            {
                raised = true;
                LogPieceMaterialProfile(piece);
                return CompleteSidewalkOuterEdge(painted, physical, lane, left);
            }
            List<float3> vertices = GetPieceTopVertices(piece.m_Piece);
            float sourceWidth = EntityManager.HasComponent<NetPieceData>(piece.m_Piece)
                ? EntityManager.GetComponentData<NetPieceData>(piece.m_Piece).m_Width : piece.m_Size.x;
            float scale = piece.m_Size.x / math.max(0.01f, sourceWidth);
            float meshXDirection = PieceMeshXDirection(piece);
            float raisedLow = float.MaxValue;
            float raisedHigh = float.MinValue;
            float threshold = roadHeight + math.max(0.035f, (lane.Height - roadHeight) * 0.5f);
            foreach (float3 vertex in vertices)
            {
                if (piece.m_Offset.y + vertex.y < threshold)
                    continue;
                float x = piece.m_Offset.x + vertex.x * scale * meshXDirection;
                if (x < physical.Low - 0.05f || x > physical.High + 0.05f)
                    continue;
                // A combined mesh can contain two sidewalks and a median.
                // Only this pedestrian lane's side of its adjacent physical
                // traffic lane contributes to the raised curb plateau.
                if (adjacentRoad.HasValue && (left ? x > adjacentRoad.Value.Low + 0.01f :
                    x < adjacentRoad.Value.High - 0.01f)) continue;
                raisedLow = math.min(raisedLow, x);
                raisedHigh = math.max(raisedHigh, x);
            }
            if (raisedHigh > raisedLow + 0.1f && center >= raisedLow - 0.1f && center <= raisedHigh + 0.1f)
            {
                raised = true;
                return CompleteSidewalkOuterEdge(new LaneSpan(math.max(physical.Low, raisedLow),
                    math.min(physical.High, raisedHigh), lane.Height), physical, lane, left);
            }

            // Without a usable raised mesh, extend only toward the OUTSIDE edge.
            // The pedestrian lane's inner edge remains the curb; shoulder space
            // toward the driving lanes therefore receives the road surface.
            return left
                ? new LaneSpan(physical.Low, math.min(physical.High, lane.High), lane.Height)
                : new LaneSpan(math.max(physical.Low, lane.Low), physical.High, lane.Height);
        }

        private List<float3> GetPieceTopVertices(Entity entity)
        {
            if (m_PieceTopVertices.TryGetValue(entity, out List<float3> cached))
                return cached;
            List<float3> result = new List<float3>();
            if (EntityManager.HasBuffer<MeshVertex>(entity))
            {
                DynamicBuffer<MeshVertex> vertices = EntityManager.GetBuffer<MeshVertex>(entity, true);
                bool normalsAvailable = EntityManager.HasBuffer<MeshNormal>(entity);
                DynamicBuffer<MeshNormal> normals = normalsAvailable ? EntityManager.GetBuffer<MeshNormal>(entity, true) : default;
                for (int i = 0; i < vertices.Length; i++)
                {
                    if (!normalsAvailable || i >= normals.Length || normals[i].m_Normal.y > 0.5f)
                        result.Add(vertices[i].m_Vertex);
                }
            }
            else if (m_PrefabSystem.TryGetPrefab(entity, out NetPiecePrefab piece) && piece.hasGeometryAsset)
            {
                try
                {
                    var asset = piece.geometryAsset;
                    Mesh[] meshes = asset.ObtainMeshes(true);
                    try
                    {
                        if (meshes != null && meshes.Length > 0 && meshes[0] != null)
                        {
                            using (Mesh.MeshDataArray meshData = Mesh.AcquireReadOnlyMeshData(meshes[0]))
                            {
                                Mesh.MeshData data = meshData[0];
                                using (NativeArray<Vector3> vertices = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp))
                                using (NativeArray<Vector3> normals = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp))
                                {
                                    data.GetVertices(vertices);
                                    bool hasNormals = data.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal);
                                    if (hasNormals) data.GetNormals(normals);
                                    for (int i = 0; i < vertices.Length; i++)
                                    {
                                        if (!hasNormals || normals[i].y > 0.5f)
                                            result.Add(new float3(vertices[i].x, vertices[i].y, vertices[i].z));
                                    }
                                }
                            }
                        }
                    }
                    finally { asset.ReleaseMeshes(); }
                }
                catch (Exception exception)
                {
                    if (!m_LoggedMeshReadFailure)
                    {
                        Mod.Log.Warn("Could not read a sidewalk mesh profile; using its pedestrian curb edge. " + exception.Message);
                        m_LoggedMeshReadFailure = true;
                    }
                }
            }
            m_PieceTopVertices[entity] = result;
            return result;
        }

        private void LogLayout(Entity prefab, SurfaceLayout layout, bool lanes)
        {
            if (m_LoggedLayouts.Count >= 16 || !m_LoggedLayouts.Add(prefab))
                return;
            List<string> roads = new List<string>();
            List<string> walks = new List<string>();
            List<string> bikes = new List<string>();
            List<string> buses = new List<string>();
            List<string> planted = new List<string>();
            foreach (RoadBand band in layout.Roads) roads.Add(band.Low.ToString("0.##") + ".." + band.High.ToString("0.##"));
            foreach (LaneSpan band in layout.Sidewalks) walks.Add(band.Low.ToString("0.##") + ".." + band.High.ToString("0.##"));
            foreach (LaneSpan band in layout.BikeLanes) bikes.Add(band.Low.ToString("0.##") + ".." + band.High.ToString("0.##"));
            foreach (LaneSpan band in layout.BusLanes) buses.Add(band.Low.ToString("0.##") + ".." + band.High.ToString("0.##"));
            foreach (LaneSpan band in layout.Planted) planted.Add(band.Low.ToString("0.##") + ".." + band.High.ToString("0.##"));
            Mod.Log.Info("Surface layout " + prefab.Index + ": node=" + !lanes + "; width=" + layout.Composition.m_Width +
                "; pavement=[" + string.Join(", ", roads) + "]; sidewalks=[" + string.Join(", ", walks) +
                "]; bikes=[" + string.Join(", ", bikes) + "]; buses=[" + string.Join(", ", buses) +
                "]; planted=[" + string.Join(", ", planted) + "].");
        }

        private static LaneSpan PieceSpan(NetCompositionPiece piece, float height)
        {
            return new LaneSpan(piece.m_Offset.x - piece.m_Size.x * 0.5f,
                piece.m_Offset.x + piece.m_Size.x * 0.5f, height);
        }

        private static void MergeSidewalkSpans(List<LaneSpan> spans)
        {
            spans.Sort((a, b) => a.Low.CompareTo(b.Low));
            for (int i = spans.Count - 1; i > 0; i--)
            {
                LaneSpan a = spans[i - 1];
                LaneSpan b = spans[i];
                if (b.Low <= a.High + 0.01f && math.abs(a.Height - b.Height) < 0.1f)
                {
                    spans[i - 1] = new LaneSpan(a.Low, math.max(a.High, b.High), a.Height);
                    spans.RemoveAt(i);
                }
            }
        }

        private static List<RoadBand> BuildPavementBands(float width, List<LaneSpan> driving, List<LaneSpan> barriers)
        {
            List<LaneSpan> clear = new List<LaneSpan> { new LaneSpan(-width * 0.5f, width * 0.5f, 0f) };
            for (int i = 0; i < barriers.Count; i++)
            {
                LaneSpan obstacle = barriers[i];
                for (int j = clear.Count - 1; j >= 0; j--)
                {
                    LaneSpan span = clear[j];
                    if (obstacle.High <= span.Low || obstacle.Low >= span.High)
                        continue;
                    clear.RemoveAt(j);
                    if (obstacle.Low > span.Low + 0.01f)
                        clear.Insert(j, new LaneSpan(span.Low, obstacle.Low, 0f));
                    if (obstacle.High < span.High - 0.01f)
                        clear.Insert(j, new LaneSpan(obstacle.High, span.High, 0f));
                }
            }
            clear.Sort((a, b) => a.Low.CompareTo(b.Low));
            List<RoadBand> result = new List<RoadBand>();
            for (int i = 0; i < clear.Count; i++)
            {
                LaneSpan span = clear[i];
                LaneSpan? left = null;
                LaneSpan? right = null;
                for (int j = 0; j < driving.Count; j++)
                {
                    LaneSpan lane = driving[j];
                    if (lane.High <= span.Low + 0.01f || lane.Low >= span.High - 0.01f)
                        continue;
                    if (!left.HasValue || lane.Low < left.Value.Low)
                        left = lane;
                    if (!right.HasValue || lane.High > right.Value.High)
                        right = lane;
                }
                if (left.HasValue && right.HasValue)
                    result.Add(new RoadBand(span.Low, span.High, left.Value.Height, right.Value.Height));
            }
            return result;
        }

        private void AddEdgeSurfaces(Entity edge, EdgeGeometry geometry, SurfaceLayout layout, Curve curve,
            string name, List<SurfaceChoice> roads, List<SurfaceChoice> sidewalks, Setting setting)
        {
            SurfaceChoice road = Choose(roads, name, curve, 0);
            if (road != null)
                foreach (RoadBand band in GetRoadPaintBands(layout, setting))
                {
                    SurfacePatch patch = ClipOrdinaryBranchBody(edge,
                        CreateEdgePolygon(geometry, layout.Composition, band), setting, layout, band, false);
                    LimitDedicatedLaneProjection(patch, layout, math.max(band.LowHeight, band.HighHeight));
                    patch = ClipApproachEdgeAtCrosswalks(edge, patch);
                    QueuePolygon(edge, road.Prefab, patch);
                }
            AddDedicatedEdgeSurfaces(edge, geometry, layout, curve, name, setting);
            foreach (LaneSpan band in SelectSidewalks(layout, setting, false, false))
            {
                SurfaceChoice choice = Choose(sidewalks, name, curve, band.Low + band.High < 0f ? 1 : 2);
                if (choice != null)
                    QueuePolygon(edge, choice.Prefab, ClipOrdinaryBranchBody(edge,
                        CreateEdgePolygon(geometry, layout.Composition,
                            new RoadBand(band.Low, band.High, band.Height, band.Height)), setting, layout,
                        new RoadBand(band.Low, band.High, band.Height, band.Height), true));
            }
        }

        private void AddJunctionSurfacePatches(Entity owner, Entity node, Entity prefab, EdgeNodeGeometry geometry,
            Curve curve, string name, List<SurfaceChoice> roads, List<SurfaceChoice> sidewalks, Setting setting, bool start,
            SurfaceLayout approach)
        {
            bool roundabout = geometry.m_MiddleRadius > 0.01f;
            Entity preferredEdge = GetPreferredJunctionRoad(node);
            Entity preferredRoad = GetPreferredJunctionPrefab(node);
            bool savedPavement = TryGetSavedJunctionSurface(node, out _);
            List<SurfaceChoice> preferredChoices = preferredRoad == Entity.Null ? null : GetChoices(setting, SurfaceTarget.Roads, preferredRoad, segment: preferredEdge);
            bool manualPavement = savedPavement || (preferredChoices != null && preferredChoices.Count > 0);
            Entity physicalMain = roundabout ? Entity.Null : GetOrdinaryJunctionReferencePrefab(node);
            bool highwayThroughJunction = physicalMain != Entity.Null && IsHighwayPrefab(physicalMain);
            bool hasStreet = TryGetConnectedStreetPrefab(node, out Entity streetPrefab);
            // A ring with street approaches uses their street rules. A purely
            // highway ring must keep highway categories and exact-prefab rules;
            // forcing street context silently excludes those finishes.
            bool streetContext = (roundabout && hasStreet) || (approach.Highway && hasStreet && !highwayThroughJunction);
            SurfaceLayout fallback = approach;
            if (streetContext)
            {
                if (!setting.IncludeStreets && !manualPavement) return;
                Entity rulePrefab = hasStreet ? streetPrefab : GetJunctionReferencePrefab(node, false);
                if (rulePrefab == Entity.Null) return;
                Entity ruleEdge = FindJunctionRuleRoad(node, rulePrefab, owner);
                roads = ruleEdge == Entity.Null ? new List<SurfaceChoice>() : GetChoices(setting, SurfaceTarget.Roads, rulePrefab, streetContext: true, segment: ruleEdge);
                sidewalks = setting.IncludeStreets && ruleEdge != Entity.Null ? GetChoices(setting, SurfaceTarget.Sidewalks, rulePrefab, streetContext: true, segment: ruleEdge) : new List<SurfaceChoice>();
                if (setting.IncludeStreets && sidewalks.Count == 0)
                {
                    Entity walkSource = FindActiveJunctionRuleRoad(node, SurfaceTarget.Sidewalks, setting, hasStreet);
                    if (walkSource != Entity.Null) sidewalks = GetChoices(setting, SurfaceTarget.Sidewalks,
                        EntityManager.GetComponentData<PrefabRef>(walkSource).m_Prefab, streetContext: true, segment: walkSource);
                }
                name = m_PrefabSystem.GetPrefabName(rulePrefab);
                curve = JunctionMaterialSeed(curve, node);
                if (approach.Highway)
                {
                    // Recover real raised sidewalks without reclassifying the
                    // highway's flat pedestrian/service shoulder as a sidewalk.
                    Composition ownerComposition = EntityManager.GetComponentData<Composition>(owner);
                    if (TryReadSurfaceLayout(ownerComposition.m_Edge, out SurfaceLayout streetApproach,
                        highway: true, streetContext: true)) fallback = streetApproach;
                }
                if ((roundabout && !m_LoggedRoundaboutStreetRules) || (!roundabout && !m_LoggedStreetJunctionRules))
                {
                    Mod.Log.Info((roundabout ? "Roundabout" : "Highway-to-street junction") +
                        " uses street surface rules from '" + name + "'. Highway material ends at the native approach mouth; node=" + node.Index + ".");
                    if (roundabout) m_LoggedRoundaboutStreetRules = true;
                    else m_LoggedStreetJunctionRules = true;
                }
            }
            else if (roundabout)
            {
                if (!setting.IncludeHighways && !manualPavement) return;
                Entity rulePrefab = GetJunctionReferencePrefab(node, false);
                if (rulePrefab == Entity.Null) return;
                Entity ruleEdge = FindJunctionRuleRoad(node, rulePrefab, owner);
                roads = ruleEdge == Entity.Null ? new List<SurfaceChoice>() : GetChoices(setting, SurfaceTarget.Roads, rulePrefab, segment: ruleEdge);
                sidewalks = setting.IncludeHighwaySidewalks && ruleEdge != Entity.Null ? GetChoices(setting, SurfaceTarget.Sidewalks,
                    rulePrefab, segment: ruleEdge) : new List<SurfaceChoice>();
                name = m_PrefabSystem.GetPrefabName(rulePrefab);
                curve = JunctionMaterialSeed(curve, node);
            }
            if (!TryReadSurfaceLayout(prefab, out SurfaceLayout layout, fallback, start,
                highway: !streetContext && approach.Highway, streetContext: streetContext))
            {
                if (!m_LoggedMissingNodeLayout)
                {
                    Mod.Log.Warn("A junction has no usable native composition; its patches were skipped. Node=" + node.Index +
                        "; composition=" + prefab.Index + ".");
                    m_LoggedMissingNodeLayout = true;
                }
                return;
            }
            if (roundabout && !EntityManager.HasComponent<Game.Net.Node>(node))
                return;
            if (roundabout)
            {
                layout.MixedRoundabout = HasRoundaboutHighwayConnection(node);
                RecoverRoundaboutSidewalks(node, geometry, layout);
                LogSmallRoundabout(node, geometry, layout);
            }
            if (roundabout && !m_LoggedRoundaboutGeometry)
            {
                Mod.Log.Info("Using curved roundabout perimeter patches with an island cutout. Native island radius=" +
                    geometry.m_MiddleRadius + "; composition width=" + layout.Composition.m_Width + ".");
                m_LoggedRoundaboutGeometry = true;
            }
            List<SurfaceChoice> junctionRoads = roads;
            string junctionRoadName = name;
            Curve junctionRoadSeed = curve;
            Entity mainRoad = Entity.Null;
            bool crosswalkOwned = !roundabout && GetJunctionPavementDomain(node) != null;
            if (!roundabout && (crosswalkOwned || setting.MainRoadJunctionSurfaces || manualPavement || highwayThroughJunction) &&
                (setting.IncludeStreets || (highwayThroughJunction && setting.IncludeHighways)))
            {
                mainRoad = physicalMain;
                if (mainRoad != Entity.Null)
                {
                    Entity referenceEdge = FindJunctionRuleRoad(node, mainRoad, owner);
                    junctionRoads = referenceEdge == Entity.Null ? new List<SurfaceChoice>() :
                        GetChoices(setting, SurfaceTarget.Roads, mainRoad, streetContext: !IsHighwayPrefab(mainRoad), segment: referenceEdge);
                    junctionRoadName = m_PrefabSystem.GetPrefabName(mainRoad);
                    junctionRoadSeed = JunctionMaterialSeed(curve, node);
                    if (m_LoggedMainRoadJunctions.Count < 8 && m_LoggedMainRoadJunctions.Add(node))
                        Mod.Log.Info("Intersection pavement uses the main road's surface rule. Node=" + node.Index +
                            "; main road='" + junctionRoadName + "'. Branch materials are clipped at the main road curb.");
                }
            }
            // A saved choice changes pavement rules, never the geometry source.
            // This allows highway pavement on a ring while retaining its curb
            // recovery, projection separation and center-island cutout.
            if (manualPavement)
            {
                junctionRoads = preferredChoices;
                junctionRoadName = m_PrefabSystem.GetPrefabName(preferredRoad);
                // Match the actual selected segment even when several
                // equally specific surface rules are enabled for its type.
                junctionRoadSeed = preferredEdge == Entity.Null ? JunctionMaterialSeed(curve, node) : EntityManager.GetComponentData<Curve>(preferredEdge);
            }
            SurfaceChoice road = savedPavement ? GetSavedJunctionChoice(node) : Choose(junctionRoads, junctionRoadName, junctionRoadSeed, 0);
            if (road == null && !savedPavement && !manualPavement &&
                (roundabout || crosswalkOwned || setting.MainRoadJunctionSurfaces || highwayThroughJunction))
            {
                // Preserve the physical reference, curb recovery and masks.
                // Fall back to an eligible connected segment's real finish;
                // mixed rings prefer styled streets, then styled highways.
                JunctionSurfaceOption automatic = FindAutomaticJunctionSurface(node, setting, hasStreet);
                if (automatic != null) road = automatic.Surface;
            }
            if (!savedPavement && !manualPavement && !roundabout && CountJunctionRoads(node) == 1)
            {
                // Only a terminal cap inherits the whole bus carriageway.
                // Junction shoulder coverage follows paired bus paths.
                Entity source = mainRoad == Entity.Null ? owner : FindJunctionRuleRoad(node, mainRoad, owner);
                SurfaceChoice bus = FilledBusJunctionChoice(source, setting);
                if (bus != null) road = bus;
            }
            if (roundabout && m_LoggedRoundaboutRuleContexts.Count < 16 && m_LoggedRoundaboutRuleContexts.Add(node))
                Mod.Log.Info("Roundabout rule context: node=" + node.Index + "; has street=" + hasStreet +
                    "; street rules=" + streetContext + "; highway approach=" + approach.Highway +
                    "; pavement=" + (road == null ? "<none>" : m_PrefabSystem.GetPrefabName(road.Prefab)) + ".");
            bool intersection = (layout.Composition.m_Flags.m_General & CompositionFlags.General.Intersection) != 0;
            bool separatedConnection = (m_ActiveJunctionLaneProfile == null || m_ActiveJunctionLaneProfile.Strips.Count == 0) &&
                !roundabout && !intersection && !JunctionCoversBusLanes(node) && HasSeparatedLanes(layout, setting) &&
                IsOrdinaryLaneConnection(node) && MatchesLaneConnectionLayout(layout, approach, setting, start);
            if (!roundabout) LogJunctionGeometry(owner, node, mainRoad, geometry, approach, layout, start);
            int beforeRoads = m_PendingAreas.Count;
            if (roundabout)
            {
                if (road != null && layout.Roads.Count > 0)
                {
                    float3 center = EntityManager.GetComponentData<Game.Net.Node>(node).m_Position;
                    if (EntityManager.HasComponent<NodeGeometry>(node))
                        center.y = EntityManager.GetComponentData<NodeGeometry>(node).m_Position;
                    RoadBand low = layout.Roads[0];
                    RoadBand high = layout.Roads[layout.Roads.Count - 1];
                    QueuePolygon(owner, road.Prefab, CreateRoundaboutPavement(owner, node, geometry, layout, approach,
                        center, low, true, start));
                    QueuePolygon(owner, road.Prefab, CreateRoundaboutPavement(owner, node, geometry, layout, approach,
                        center, high, false, start));
                }
            }
            else if (GetJunctionPavementDomain(node) is JunctionPavementDomain domain)
                QueueCrosswalkJunction(owner, node, road, domain, geometry, approach, layout, setting, start);
            else if (separatedConnection)
                AddSeparatedConnectionSurfaces(owner, geometry, layout, approach, curve, name, road, setting, start);
            else if (road != null)
            {
                if (!m_LoggedJunctionMapping)
                {
                    Mod.Log.Info("Junction surfaces use native side curves and vertex-match targets, shared corner endpoints, and sampled material boundaries.");
                    m_LoggedJunctionMapping = true;
                }
                bool shared = mainRoad != Entity.Null && TryQueueSharedJunction(owner, node, mainRoad, road.Prefab, setting);
                foreach (RoadBand band in layout.Roads)
                {
                    CornerInsets leftEnd = GetCornerInsets(owner, node, geometry, layout, band, true, false, false);
                    CornerInsets rightEnd = GetCornerInsets(owner, node, geometry, layout, band, false, false, false);
                    QueueOrdinaryJunctionPart(owner, node, mainRoad, road.Prefab, shared,
                        CreateJunctionSidePolygon(geometry, layout.Composition, band, true,
                            GetJunctionMouth(owner, approach, layout, band, true, false, start), leftEnd), false, 0, setting, layout, band);
                    QueueOrdinaryJunctionPart(owner, node, mainRoad, road.Prefab, shared,
                        CreateJunctionSidePolygon(geometry, layout.Composition, band, false,
                            GetJunctionMouth(owner, approach, layout, band, false, false, start), rightEnd), false, 0, setting, layout, band);
                }
            }
            int roadCount = m_PendingAreas.Count - beforeRoads;
            int beforeSidewalks = m_PendingAreas.Count;
            foreach (LaneSpan band in SelectSidewalks(layout, setting, start, roundabout))
            {
                bool left = band.Low + band.High < layout.Composition.m_MiddleOffset * 2f;
                SurfaceChoice choice = Choose(sidewalks, name, curve, (start ? !left : left) ? 1 : 2);
                if (choice == null)
                    continue;
                RoadBand span = new RoadBand(band.Low, band.High, band.Height, band.Height);
                if (roundabout)
                {
                    SurfacePatch patch = CreateRoundaboutWalkFootprint(owner, node, geometry, approach, layout, span, left, start);
                    ProtectRaisedSidewalkBand(patch, layout, span, setting);
                    LimitRoundaboutSidewalkProjection(patch, layout, span, left);
                    QueuePolygon(owner, choice.Prefab, patch);
                }
                else
                {
                    CornerInsets leftEnd = GetCornerInsets(owner, node, geometry, layout, span, true, true, false);
                    CornerInsets rightEnd = GetCornerInsets(owner, node, geometry, layout, span, false, true, false);
                    SurfacePatch leftPatch = CreateJunctionSidePolygon(geometry, layout.Composition, span, true,
                        GetJunctionMouth(owner, fallback, layout, span, true, true, start), leftEnd);
                    SurfacePatch rightPatch = CreateJunctionSidePolygon(geometry, layout.Composition, span, false,
                        GetJunctionMouth(owner, fallback, layout, span, false, true, start), rightEnd);
                    ProtectRaisedSidewalkBand(leftPatch, layout, span, setting);
                    ProtectRaisedSidewalkBand(rightPatch, layout, span, setting);
                    QueueOrdinaryJunctionPart(owner, node, mainRoad, choice.Prefab, false,
                        leftPatch, true, (start ? !left : left) ? 1 : 2, setting, layout, span);
                    QueueOrdinaryJunctionPart(owner, node, mainRoad, choice.Prefab, false,
                        rightPatch, true, (start ? !left : left) ? 1 : 2, setting, layout, span);
                }
            }
            if ((roundabout && !m_LoggedRoundaboutCounts) || (intersection && !m_LoggedJunctionCounts))
            {
                Mod.Log.Info((roundabout ? "Roundabout" : "Intersection") + " patches queued: road=" + roadCount +
                    "; sidewalk=" + (m_PendingAreas.Count - beforeSidewalks) + "; node=" + node.Index + ".");
                if (roundabout) m_LoggedRoundaboutCounts = true;
                else m_LoggedJunctionCounts = true;
            }
        }

        private static void ProtectRaisedSidewalkBand(SurfacePatch patch, SurfaceLayout layout, RoadBand sidewalk, Setting setting)
        {
            float laneHeight = float.NegativeInfinity;
            foreach (RoadBand road in layout.Roads) laneHeight = math.max(laneHeight, math.max(road.LowHeight, road.HighHeight));
            if (layout.SeparateBikeLanes) foreach (LaneSpan lane in layout.BikeLanes) laneHeight = math.max(laneHeight, lane.Height);
            if (layout.SeparateBusLanes) foreach (LaneSpan lane in layout.BusLanes) laneHeight = math.max(laneHeight, lane.Height);
            // The sidewalk already has a geometric curb boundary. Lane masks on
            // a lower physical deck must not slice this raised receiver into a
            // fan of separate decals. Coplanar/shared tracks still use masks.
            if (math.isfinite(laneHeight) && math.min(sidewalk.LowHeight, sidewalk.HighHeight) > laneHeight + 0.035f)
                MarkLaneMasksApplied(patch);
        }

        private static CornerInsets BandInsets(NetCompositionData composition, RoadBand band, bool left)
        {
            float half = composition.m_Width * 0.5f;
            return new CornerInsets
            {
                Near = left ? band.Low + half : half - band.High,
                Far = left ? band.High + half : half - band.Low
            };
        }

        private CornerInsets GetCornerInsets(Entity owner, Entity node, EdgeNodeGeometry geometry,
            SurfaceLayout layout, RoadBand band, bool left, bool sidewalk, bool roundabout, bool planted = false)
        {
            CornerInsets original = BandInsets(layout.Composition, band, left);
            if (!EntityManager.HasBuffer<ConnectedEdge>(node))
                return original;
            float3 joint = roundabout
                ? (left ? geometry.m_Right.m_Left.d : geometry.m_Right.m_Right.d)
                : (left ? geometry.m_Left.m_Left.d : geometry.m_Right.m_Right.d);
            float bestDistance = 0.04f;
            CornerInsets result = original;
            bool streetContext = TryGetConnectedStreetPrefab(node, out _);
            // Native corner curves from two adjoining roads share their end point.
            // Blend their physical curb widths to that same point, so small and
            // large road sidewalks meet without leaving a wedge or overlap.
            DynamicBuffer<ConnectedEdge> edges = EntityManager.GetBuffer<ConnectedEdge>(node, true);
            for (int i = 0; i < edges.Length; i++)
            {
                Entity other = edges[i].m_Edge;
                if (other == owner || !IsRoadEdge(other) || !EntityManager.HasComponent<Edge>(other) ||
                    !EntityManager.HasComponent<Composition>(other))
                    continue;
                Edge endpoints = EntityManager.GetComponentData<Edge>(other);
                bool start = endpoints.m_Start == node;
                if (!start && endpoints.m_End != node)
                    continue;
                if ((start && !EntityManager.HasComponent<StartNodeGeometry>(other)) ||
                    (!start && !EntityManager.HasComponent<EndNodeGeometry>(other)))
                    continue;
                EdgeNodeGeometry otherGeometry = start
                    ? EntityManager.GetComponentData<StartNodeGeometry>(other).m_Geometry
                    : EntityManager.GetComponentData<EndNodeGeometry>(other).m_Geometry;
                Composition otherComposition = EntityManager.GetComponentData<Composition>(other);
                TryReadSurfaceLayout(otherComposition.m_Edge, out SurfaceLayout otherApproach, highway: IsHighway(other), streetContext: streetContext);
                if (!TryReadSurfaceLayout(start ? otherComposition.m_StartNode : otherComposition.m_EndNode,
                    out SurfaceLayout otherLayout, otherApproach, start,
                    highway: roundabout && !streetContext && IsHighway(other), streetContext: streetContext))
                    continue;
                if (roundabout)
                    RecoverRoundaboutSidewalks(node, otherGeometry, otherLayout);
                for (int side = 0; side < 2; side++)
                {
                    bool otherLeft = side == 0;
                    float3 otherJoint = roundabout
                        ? (otherLeft ? otherGeometry.m_Right.m_Left.d : otherGeometry.m_Right.m_Right.d)
                        : (otherLeft ? otherGeometry.m_Left.m_Left.d : otherGeometry.m_Right.m_Right.d);
                    float distance = math.distancesq(joint.xz, otherJoint.xz);
                    if (distance >= bestDistance)
                        continue;
                    RoadBand? match = null;
                    float closestBand = float.MaxValue;
                    List<RoadBand> candidates = new List<RoadBand>();
                    if (sidewalk || planted)
                    {
                        foreach (LaneSpan span in planted ? otherLayout.Planted : otherLayout.Sidewalks)
                            candidates.Add(new RoadBand(span.Low, span.High, span.Height, span.Height));
                    }
                    else
                        candidates.AddRange(otherLayout.Roads);
                    foreach (RoadBand candidate in candidates)
                    {
                        bool onSide = otherLeft ? candidate.Low < otherLayout.Composition.m_MiddleOffset
                            : candidate.High > otherLayout.Composition.m_MiddleOffset;
                        if (!onSide)
                            continue;
                        CornerInsets inset = BandInsets(otherLayout.Composition, candidate, otherLeft);
                        float score = math.abs(inset.Near / otherLayout.Composition.m_Width -
                            original.Near / layout.Composition.m_Width);
                        if (score < closestBand)
                        {
                            match = candidate;
                            closestBand = score;
                        }
                    }
                    if (!match.HasValue)
                        continue;
                    CornerInsets target = BandInsets(otherLayout.Composition, match.Value, otherLeft);
                    // A highway's zero shoulder inset must not pull the street's
                    // curb inward. At a mixed junction, use the sidewalk boundary
                    // for both adjoining pavement patches.
                    result.Near = streetContext && !sidewalk && !planted && layout.HighwaySource != otherLayout.HighwaySource
                        ? math.max(original.Near, target.Near) : (original.Near + target.Near) * 0.5f;
                    result.Far = (original.Far + target.Far) * 0.5f;
                    if (!roundabout)
                    {
                        // Averaging inset distances is insufficient at an oblique
                        // join: the two offset normals can point in different
                        // directions. Resolve one world-space endpoint instead.
                        float3 near = JunctionBandPosition(geometry, layout.Composition, band, left, false, 1f);
                        float3 otherNear = JunctionBandPosition(otherGeometry, otherLayout.Composition, match.Value, otherLeft, false, 1f);
                        bool mixedRoad = streetContext && !sidewalk && !planted && layout.HighwaySource != otherLayout.HighwaySource;
                        result.NearPosition = mixedRoad ? (layout.HighwaySource ? otherNear : near) : (near + otherNear) * 0.5f;
                        result.FarPosition = (JunctionBandPosition(geometry, layout.Composition, band, left, true, 1f) +
                            JunctionBandPosition(otherGeometry, otherLayout.Composition, match.Value, otherLeft, true, 1f)) * 0.5f;
                        result.HasPositions = true;
                    }
                    bestDistance = distance;
                }
            }
            return result;
        }

        private CornerInsets GetJunctionMouth(Entity owner, SurfaceLayout approach, SurfaceLayout layout,
            RoadBand band, bool left, bool sidewalk, bool start, bool planted = false)
        {
            CornerInsets result = default;
            if (!EntityManager.HasComponent<EdgeGeometry>(owner)) return result;
            RoadBand? match = null;
            float best = float.MaxValue;
            List<RoadBand> candidates = new List<RoadBand>();
            if (sidewalk || planted)
            {
                foreach (LaneSpan span in planted ? approach.Planted : approach.Sidewalks)
                    candidates.Add(new RoadBand(span.Low, span.High, span.Height, span.Height));
            }
            else candidates.AddRange(approach.Roads);
            CornerInsets desired = BandInsets(layout.Composition, band, left);
            float approachMiddle = start ? -approach.Composition.m_MiddleOffset : approach.Composition.m_MiddleOffset;
            foreach (RoadBand candidate in candidates)
            {
                RoadBand oriented = start ? new RoadBand(-candidate.High, -candidate.Low, candidate.HighHeight, candidate.LowHeight) : candidate;
                if (left ? oriented.Low >= approachMiddle : oriented.High <= approachMiddle)
                    continue;
                CornerInsets insets = BandInsets(approach.Composition, oriented, left);
                float score = math.abs(insets.Near / approach.Composition.m_Width - desired.Near / layout.Composition.m_Width) +
                    math.abs(insets.Far / approach.Composition.m_Width - desired.Far / layout.Composition.m_Width);
                if (score < best) { best = score; match = oriented; }
            }
            if (!match.HasValue) return result;
            EdgeGeometry edge = EntityManager.GetComponentData<EdgeGeometry>(owner);
            float3 outerLeft = start ? edge.m_Start.m_Right.a : edge.m_End.m_Left.d;
            float3 outerRight = start ? edge.m_Start.m_Left.a : edge.m_End.m_Right.d;
            outerLeft.y -= start ? approach.Composition.m_EdgeHeights.y : approach.Composition.m_EdgeHeights.z;
            outerRight.y -= start ? approach.Composition.m_EdgeHeights.x : approach.Composition.m_EdgeHeights.w;
            RoadBand source = match.Value;
            float near = left ? source.Low : source.High;
            float far = left ? source.High : source.Low;
            result.NearPosition = Lift(math.lerp(outerLeft, outerRight, math.saturate(near / approach.Composition.m_Width + 0.5f)),
                left ? source.LowHeight : source.HighHeight);
            result.FarPosition = Lift(math.lerp(outerLeft, outerRight, math.saturate(far / approach.Composition.m_Width + 0.5f)),
                left ? source.HighHeight : source.LowHeight);
            if (sidewalk && !planted && HasPavedPlantingCap(approach, layout, source, band, left, start) &&
                (start ? EntityManager.HasComponent<StartNodeGeometry>(owner) : EntityManager.HasComponent<EndNodeGeometry>(owner)))
            {
                EdgeNodeGeometry geometry = start ? EntityManager.GetComponentData<StartNodeGeometry>(owner).m_Geometry :
                    EntityManager.GetComponentData<EndNodeGeometry>(owner).m_Geometry;
                // Grass stops at this mouth. The native node already has a
                // wider paved cap, so tapering its inner curb from the edge's
                // narrower walkway would leave an uncovered triangular wedge.
                // Keep the outer join, but start the inner boundary on the
                // node's actual sidewalk curb. Node planting masks still apply.
                result.FarPosition = JunctionBandPosition(geometry, layout.Composition, band, left, true, 0f);
                if (m_LoggedPlantingCapEdges.Count < 16 && m_LoggedPlantingCapEdges.Add(owner))
                    Mod.Log.Info("Paved planting cap: owner=" + owner.Index + "; start=" + start + "; left=" + left +
                        "; edge walk=" + source.Low + ".." + source.High + "; node walk=" + band.Low + ".." + band.High +
                        ". Native node curb retained where the approach planting ends.");
            }
            result.HasPositions = true;
            return result;
        }

        private static List<LaneSpan> SelectSidewalks(SurfaceLayout layout, Setting setting, bool start, bool roundabout)
        {
            List<LaneSpan> selected = new List<LaneSpan>();
            LaneSpan? outerLeft = null;
            LaneSpan? outerRight = null;
            foreach (LaneSpan band in layout.Sidewalks)
            {
                bool left = band.Low + band.High < layout.Composition.m_MiddleOffset * 2f;
                bool requestedLeft = start ? !left : left;
                if ((requestedLeft && setting.SidewalkSides == SideSelection.Right) ||
                    (!requestedLeft && setting.SidewalkSides == SideSelection.Left))
                    continue;
                // A roundabout's central pedestrian/median lanes do not describe its
                // outside sidewalk. Its perimeter follows the two outer corner chains.
                if (roundabout && layout.Roads.Count > 0 &&
                    band.High > layout.Roads[0].Low + 0.01f &&
                    band.Low < layout.Roads[layout.Roads.Count - 1].High - 0.01f)
                    continue;
                if (setting.IncludeInternalRoadPedestrianLanes)
                    selected.Add(band);
                else if (left && (!outerLeft.HasValue || band.Low < outerLeft.Value.Low))
                    outerLeft = band;
                else if (!left && (!outerRight.HasValue || band.High > outerRight.Value.High))
                    outerRight = band;
            }
            if (!setting.IncludeInternalRoadPedestrianLanes)
            {
                if (outerLeft.HasValue) selected.Add(outerLeft.Value);
                if (outerRight.HasValue) selected.Add(outerRight.Value);
            }
            return selected;
        }

        private void QueuePolygon(Entity owner, Entity prefab, SurfacePatch polygon)
        {
            List<LaneMaskCell> masks = new List<LaneMaskCell>();
            if (m_ActiveJunctionLaneProfile != null)
            {
                // Crosswalk ownership can bypass broad shoulder-fill masks.
                // Dedicated bike and bus paths still stay visible through it.
                masks.AddRange(!polygon.DedicatedLanesClipped ? m_ActiveJunctionLaneProfile.Cells :
                    m_ActiveJunctionLaneProfile.ContinuousCells);
            }
            bool clipPlants = !polygon.PlantedAreasClipped && m_ActiveJunctionPlantMasks != null && m_ActiveJunctionPlantMasks.Count > 0;
            if (clipPlants) masks.AddRange(m_ActiveJunctionPlantMasks);
            if (masks.Count > 0) polygon = SubtractLaneMasks(polygon, masks);
            if (clipPlants) MarkPlantedMasksApplied(polygon);
            QueueClippedPolygon(owner, prefab, polygon);
        }

        private void QueueClippedPolygon(Entity owner, Entity prefab, SurfacePatch polygon)
        {
            // Masks have already been applied to the complete footprint. Do not
            // clip each output part again: this used to multiply tiny fragments
            // and repeated the same expensive lane boolean at every recursion.
            // Keep neighbouring masks on their authored boundary. The private
            // renderer uses only the native minimum expansion, so inverse
            // compensation no longer needs to contract metre-scale rims.
            if (IsNetworkOverlayPrefab(m_PrefabSystem.GetPrefabName(prefab))) polygon.ExactBoundary = true;
            if (polygon.Parts.Count > 0)
            {
                foreach (SurfacePatch part in polygon.Parts)
                {
                    part.ProjectionMinimum = polygon.ProjectionMinimum;
                    part.ProjectionMaximum = polygon.ProjectionMaximum;
                    part.ExactBoundary = polygon.ExactBoundary;
                    QueueClippedPolygon(owner, prefab, part);
                }
                return;
            }
            if (polygon.Count < 4 || polygon.Triangles.Count == 0) return;
            if (m_PlanningSurfaceOwner == owner)
            {
                // Do not bevel every internal fragment. Those tiny bevel gaps
                // prevent otherwise adjoining pieces from joining later.
                // The complete plan is coalesced before its outside edges are
                // guarded, immediately before native areas are committed.
                QueueArea(owner, prefab, polygon);
                return;
            }
            polygon = GuardPlannedSurfacePatch(prefab, polygon);
            if (polygon.Count >= 4 && polygon.Triangles.Count > 0)
            {
                LogFinalPlannedSurface(owner, prefab, polygon);
                QueueArea(owner, prefab, polygon);
            }
        }

        private SurfacePatch GuardPlannedSurfacePatch(Entity prefab, SurfacePatch polygon)
        {
            if (polygon.SmallRoundabout)
                polygon = GuardSmallRoundaboutPatch(polygon, GetSurfaceExpansion(prefab));
            else if (polygon.ExactBoundary)
                polygon = GuardExactSurfacePatch(polygon, GetSurfaceExpansion(prefab));
            return polygon;
        }

        private void LogFinalPlannedSurface(Entity owner, Entity prefab, SurfacePatch polygon)
        {
            LogJunctionPatchCoordinates(owner, prefab, polygon);
            if (polygon.BevelCount > 0 && !m_LoggedRoundaboutBevels)
            {
                Mod.Log.Info("Beveled sharp surface tips to prevent native decal expansion spikes. Corners=" + polygon.BevelCount + ".");
                m_LoggedRoundaboutBevels = true;
            }
        }

        private static SurfacePatch CreateEdgePolygon(EdgeGeometry geometry, NetCompositionData composition, RoadBand band)
        {
            float width = composition.m_Width;
            float low = math.saturate(band.Low / width + 0.5f);
            float high = math.saturate(band.High / width + 0.5f);
            List<float3> a = new List<float3>();
            List<float3> b = new List<float3>();
            AppendSegmentBand(a, b, geometry.m_Start, composition.m_EdgeHeights, 0f, 0.5f, low, high, band.LowHeight, band.HighHeight);
            AppendSegmentBand(a, b, geometry.m_End, composition.m_EdgeHeights, 0.5f, 1f, low, high, band.LowHeight, band.HighHeight);
            return CloseStrip(a, b);
        }

        private static void AppendSegmentBand(List<float3> a, List<float3> b, Segment segment,
            float4 heights, float from, float to, float low, float high, float lowHeight, float highHeight)
        {
            segment.m_Left = RemoveEdgeHeight(segment.m_Left, heights.xz, from, to);
            segment.m_Right = RemoveEdgeHeight(segment.m_Right, heights.yw, from, to);
            List<float> samples = SampleParameters(segment.m_Left, segment.m_Right);
            foreach (float t in samples)
            {
                float3 left = MathUtils.Position(segment.m_Left, t);
                float3 right = MathUtils.Position(segment.m_Right, t);
                AppendPair(a, b, Lift(math.lerp(left, right, low), lowHeight), Lift(math.lerp(left, right, high), highHeight));
            }
        }

        private static SurfacePatch CreateJunctionSidePolygon(EdgeNodeGeometry geometry,
            NetCompositionData composition, RoadBand band, bool left, CornerInsets mouth, CornerInsets end)
        {
            float middle = composition.m_MiddleOffset;
            float low = left ? band.Low : math.max(band.Low, middle);
            float high = left ? math.min(band.High, middle) : band.High;
            if (high <= low + 0.01f)
                return new SurfacePatch();
            bool reachMiddle = left ? high >= middle - 0.01f : low <= middle + 0.01f;
            float farHeight = left ? band.HighHeight : band.LowHeight;
            RoadBand clipped = new RoadBand(low, high, band.LowHeight, band.HighHeight);
            float3 nearFirst = JunctionBandPosition(geometry, composition, clipped, left, false, 0f);
            float3 nearLast = JunctionBandPosition(geometry, composition, clipped, left, false, 1f);
            float3 farFirst = JunctionBandPosition(geometry, composition, clipped, left, true, 0f);
            float3 farLast = JunctionBandPosition(geometry, composition, clipped, left, true, 1f);
            Func<float, float3> nearBoundary = t => JoinBoundary(
                JunctionBandPosition(geometry, composition, clipped, left, false, t),
                nearFirst, nearLast, mouth, end, false, t);
            Func<float, float3> farBoundary = reachMiddle
                ? (Func<float, float3>)(t => Lift(MathUtils.Position(geometry.m_Middle, t), farHeight))
                : t => JoinBoundary(JunctionBandPosition(geometry, composition, clipped, left, true, t),
                    farFirst, farLast, mouth, end, true, t);
            List<float3> a = new List<float3>();
            List<float3> b = new List<float3>();
            foreach (float t in SampleMappedParameters(nearBoundary, farBoundary))
                AppendPair(a, b, nearBoundary(t), farBoundary(t));
            return CloseStrip(a, b);
        }

        private static float3 JunctionBandPosition(EdgeNodeGeometry geometry, NetCompositionData composition,
            RoadBand band, bool left, bool far, float t)
        {
            float lateral = left ? (far ? band.High : band.Low) : (far ? band.Low : band.High);
            float height = left ? (far ? band.HighHeight : band.LowHeight) : (far ? band.LowHeight : band.HighHeight);
            float half = composition.m_Width * 0.5f;
            float sideWidth = left ? half + composition.m_MiddleOffset : half - composition.m_MiddleOffset;
            float fraction = math.saturate(left ? (lateral + half) / math.max(0.01f, sideWidth) :
                (lateral - composition.m_MiddleOffset) / math.max(0.01f, sideWidth));
            float4 source = left ? composition.m_SyncVertexOffsetsLeft : composition.m_SyncVertexOffsetsRight;
            float4 target = left ? geometry.m_SyncVertexTargetsLeft : geometry.m_SyncVertexTargetsRight;
            float mapped = RemapSideFraction(fraction, source, target);
            fraction = math.lerp(fraction, mapped, t);
            // The side segment's inner curve follows the authored road mesh.
            // m_Middle is a separate fill curve and is not a curb cross-section.
            Segment segment = left ? geometry.m_Left : geometry.m_Right;
            Bezier4x3 outer = RemoveEdgeHeight(left ? segment.m_Left : segment.m_Right,
                left ? composition.m_EdgeHeights.xz : composition.m_EdgeHeights.yw, 0f, 1f);
            Bezier4x3 inner = left ? segment.m_Right : segment.m_Left;
            return Lift(math.lerp(MathUtils.Position(outer, t), MathUtils.Position(inner, t),
                left ? fraction : 1f - fraction), height);
        }

        private static float RemapSideFraction(float fraction, float4 source, float4 target)
        {
            if (!math.all(math.isfinite(source)) || !math.all(math.isfinite(target)) ||
                source.w <= source.x + 0.00001f || math.any(source.yzw < source.xyz) || math.any(target.yzw < target.xyz))
                return fraction;
            float previousSource = 0f, previousTarget = 0f;
            for (int i = 0; i < 4; i++)
            {
                float nextSource = math.saturate(source[i]), nextTarget = math.saturate(target[i]);
                if (fraction <= nextSource && nextSource > previousSource + 0.00001f)
                    return math.lerp(previousTarget, nextTarget, math.saturate((fraction - previousSource) / (nextSource - previousSource)));
                previousSource = nextSource; previousTarget = nextTarget;
            }
            return previousSource < 0.99999f
                ? math.lerp(previousTarget, 1f, math.saturate((fraction - previousSource) / (1f - previousSource))) : previousTarget;
        }

        private static float3 JoinBoundary(float3 point, float3 first, float3 last,
            CornerInsets mouth, CornerInsets end, bool far, float t)
        {
            // Corrections taper with zero slope at either end, preserving the
            // native curve between joins while connecting the exact edge mouth.
            float blend = t * t * (3f - 2f * t);
            if (mouth.HasPositions) point += ((far ? mouth.FarPosition : mouth.NearPosition) - first) * (1f - blend);
            if (end.HasPositions) point += ((far ? end.FarPosition : end.NearPosition) - last) * blend;
            return point;
        }

        private static List<float> SampleMappedParameters(Func<float, float3> a, Func<float, float3> b)
        {
            List<float> result = new List<float> { 0f };
            SampleMappedInterval(a, b, 0f, 1f, 0, result);
            return result;
        }

        private static void SampleMappedInterval(Func<float, float3> a, Func<float, float3> b,
            float start, float end, int depth, List<float> result)
        {
            bool divide = end - start > 0.0625f || NeedsMappedSubdivision(a, start, end) || NeedsMappedSubdivision(b, start, end);
            if (divide && depth < 10)
            {
                float middle = (start + end) * 0.5f;
                SampleMappedInterval(a, b, start, middle, depth + 1, result);
                SampleMappedInterval(a, b, middle, end, depth + 1, result);
            }
            else result.Add(end);
        }

        private static bool NeedsMappedSubdivision(Func<float, float3> boundary, float start, float end)
        {
            float3 first = boundary(start), last = boundary(end);
            if (math.distance(first, last) > SampleSpacing) return true;
            for (int i = 1; i < 4; i++)
            {
                float fraction = i * 0.25f;
                if (math.distance(boundary(math.lerp(start, end, fraction)), math.lerp(first, last, fraction)) > 0.01f)
                    return true;
            }
            return false;
        }

        private static SurfacePatch CreateRoundaboutSidewalkPolygon(EdgeNodeGeometry geometry,
            NetCompositionData composition, RoadBand band, bool left, CornerInsets end, CornerInsets? mouth)
        {
            float near = left ? band.Low + composition.m_Width * 0.5f : composition.m_Width * 0.5f - band.High;
            float far = left ? band.High + composition.m_Width * 0.5f : composition.m_Width * 0.5f - band.Low;
            List<float3> a = new List<float3>();
            List<float3> b = new List<float3>();
            AppendRoundaboutChain(a, b, geometry, composition, near, far, end, band.LowHeight, left, mouth);
            SurfacePatch patch = geometry.m_MiddleRadius <= 4.01f
                ? CreateSmallRoundaboutStrip(a, b) : CloseStrip(a, b);
            patch.SmallRoundabout = geometry.m_MiddleRadius <= 4.01f;
            return patch.SmallRoundabout ? patch : BevelRoundaboutTips(patch);
        }

        private static void AppendRoundaboutChain(List<float3> a, List<float3> b, EdgeNodeGeometry geometry, NetCompositionData composition,
            float near, float far, CornerInsets end, float height, bool left, CornerInsets? mouth = null)
        {
            float firstLength = MathUtils.Length(left ? geometry.m_Left.m_Left : geometry.m_Left.m_Right);
            float secondLength = MathUtils.Length(left ? geometry.m_Right.m_Left : geometry.m_Right.m_Right);
            float fraction = firstLength / math.max(0.01f, firstLength + secondLength);
            CornerInsets middle = mouth.HasValue ? new CornerInsets { Near = near, Far = far } : new CornerInsets
            {
                Near = math.lerp(near, end.Near, fraction),
                Far = math.lerp(far, end.Far, fraction)
            };
            // A recovered ring sidewalk is absent on the highway shoulder at
            // the mouth. Grow it through the first native curve, then retain
            // the ring profile through the second curve to the adjoining street.
            AppendRoundaboutInsets(a, b, geometry.m_Left, composition.m_EdgeHeights, 0f, 0.5f,
                mouth.HasValue ? mouth.Value.Near : near, mouth.HasValue ? mouth.Value.Far : far, middle, height, left, mouth);
            AppendRoundaboutInsets(a, b, geometry.m_Right, composition.m_EdgeHeights, 0.5f, 1f, middle.Near, middle.Far, end, height, left);
        }

        private static void AppendRoundaboutInsets(List<float3> a, List<float3> b, Segment segment,
            float4 heights, float from, float to, float near, float far, CornerInsets end, float height, bool left, CornerInsets? mouth = null)
        {
            Bezier4x3 outside = left ? segment.m_Left : segment.m_Right;
            outside = RemoveEdgeHeight(outside, left ? heights.xz : heights.yw, from, to);
            Bezier4x3 inside = left ? segment.m_Right : segment.m_Left;
            if (mouth.HasValue)
            {
                CornerInsets start = mouth.Value;
                float3 nearFirst = Lift(InsetBoundaryPosition(outside, 0f, near, left), height);
                float3 farFirst = Lift(InsetBoundaryPosition(outside, 0f, far, left), height);
                Func<float, float3> nearBoundary = t => RoundaboutEntryPosition(outside, t, near, end.Near,
                    height, left, start.HasPositions, nearFirst, start.NearPosition);
                Func<float, float3> farBoundary = t => RoundaboutEntryPosition(outside, t, far, end.Far,
                    height, left, start.HasPositions, farFirst, start.FarPosition);
                foreach (float t in SampleMappedParameters(nearBoundary, farBoundary))
                    AppendPair(a, b, nearBoundary(t), farBoundary(t));
                return;
            }
            foreach (float t in SampleParameters(outside, inside))
            {
                // The opposite curve travels around the OTHER side of the circle;
                // it is not an inner boundary. Inset the perimeter along its normal
                // so the sidewalk keeps its width where the two chains meet.
                AppendPair(a, b, Lift(InsetBoundaryPosition(outside, t, math.lerp(near, end.Near, t), left), height),
                    Lift(InsetBoundaryPosition(outside, t, math.lerp(far, end.Far, t), left), height));
            }
        }

        private static float3 RoundaboutEntryPosition(Bezier4x3 outside, float t, float mouthInset, float ringInset,
            float height, bool left, bool pinned, float3 first, float3 mouth)
        {
            float blend = t * t * (3f - 2f * t);
            float3 point = Lift(InsetBoundaryPosition(outside, t, math.lerp(mouthInset, ringInset, blend), left), height);
            if (pinned) point += (mouth - first) * (1f - blend);
            return point;
        }

        private static SurfacePatch CreateRoundaboutRoadPolygon(EdgeNodeGeometry geometry,
            NetCompositionData composition, float3 center, float curb, float height, bool left, float endInset,
            CornerInsets? mouthInset, RoundaboutLaneRing bikeRing = null, float? physicalIslandRadius = null)
        {
            float inset = left ? curb + composition.m_Width * 0.5f : composition.m_Width * 0.5f - curb;
            List<float3> perimeter = new List<float3>();
            List<float3> unused = new List<float3>();
            AppendRoundaboutChain(perimeter, unused, geometry, composition, inset, inset,
                new CornerInsets { Near = endInset, Far = endInset }, height, left, mouthInset);
            float fraction = math.saturate(composition.m_MiddleOffset / composition.m_Width + 0.5f);
            float3 mouthLeft = geometry.m_Left.m_Left.a, mouthRight = geometry.m_Left.m_Right.a;
            mouthLeft.y -= composition.m_EdgeHeights.x; mouthRight.y -= composition.m_EdgeHeights.y;
            float3 mouth = Lift(math.lerp(mouthLeft, mouthRight, fraction), height);
            if (bikeRing != null && geometry.m_MiddleRadius > 4.01f)
                return CreateSeparatedRoundaboutRibbon(perimeter, mouth, center, geometry.m_MiddleRadius, height, bikeRing);
            return CreateRoundaboutRibbon(perimeter, mouth, center, physicalIslandRadius ?? geometry.m_MiddleRadius, height);
        }

        private static Bezier4x3 RemoveEdgeHeight(Bezier4x3 curve, float2 heights, float from, float to)
        {
            // The native boundary's y follows the side mesh's edge, which can be
            // the bottom of a bridge structure. Recover the network datum before
            // applying the pavement/pedestrian lane's authored deck height.
            curve.a.y -= math.lerp(heights.x, heights.y, from);
            curve.b.y -= math.lerp(heights.x, heights.y, math.lerp(from, to, 1f / 3f));
            curve.c.y -= math.lerp(heights.x, heights.y, math.lerp(from, to, 2f / 3f));
            curve.d.y -= math.lerp(heights.x, heights.y, to);
            return curve;
        }

        private static SurfacePatch CreateRoundaboutRibbon(List<float3> perimeter, float3 mouth,
            float3 center, float radius, float height)
        {
            if (perimeter.Count < 2) return new SurfacePatch();
            if (radius <= 4.01f)
                return CreateSmallRoundaboutRibbon(perimeter, mouth, center, radius, height);
            List<float3> outer = new List<float3> { mouth };
            outer.AddRange(perimeter);
            List<float3> samples = new List<float3>();
            // Tie each island point to its perimeter sample. A single long polygon
            // can cross itself at the reentrant approach to a two-arm roundabout.
            // Local ribbon triangles fill that approach as well as the ring.
            for (int i = 0; i < outer.Count - 1; i++)
            {
                float3 from = outer[i], to = outer[i + 1];
                float2 p = math.normalizesafe(from.xz - center.xz);
                float2 q = math.normalizesafe(to.xz - center.xz);
                float angle = math.atan2(p.x * q.y - p.y * q.x, math.dot(p, q));
                int steps = math.max(1, (int)math.ceil(math.abs(angle) / (math.PI / 96f)));
                for (int j = 0; j <= steps; j++)
                {
                    float t = (float)j / steps;
                    float3 point = math.lerp(from, to, t);
                    if (math.distance(point.xz, center.xz) <= radius + 0.02f) continue;
                    AddDistinct(samples, point);
                }
            }
            List<float3> a = new List<float3>(), b = new List<float3>();
            for (int i = 0; i < samples.Count; i++)
            {
                float2 direction = math.normalizesafe(samples[i].xz - center.xz);
                float angularStep = 0f;
                for (int j = math.max(0, i - 1); j <= math.min(samples.Count - 1, i + 1); j++)
                {
                    float2 other = math.normalizesafe(samples[j].xz - center.xz);
                    angularStep = math.max(angularStep, math.acos(math.clamp(math.dot(direction, other), -1f, 1f)));
                }
                // Use the actual angle between successive radial samples; linear
                // approach interpolation does not space their angles uniformly.
                float safeRadius = radius / math.max(0.1f, math.cos(angularStep * 0.5f)) + 0.01f;
                float3 inner = Lift(center, height);
                inner.xz += direction * safeRadius;
                AppendPair(a, b, samples[i], inner);
            }
            SurfacePatch patch = CloseStrip(a, b);
            patch.SmallRoundabout = radius <= 4.01f;
            return patch.SmallRoundabout ? patch : BevelRoundaboutTips(patch);
        }


        private static float3 InsetBoundaryPosition(Bezier4x3 outside, float t, float distance, bool left)
        {
            float3 point = MathUtils.Position(outside, t);
            float2 direction = math.normalizesafe(MathUtils.Tangent(outside, t).xz);
            if (math.lengthsq(direction) < 0.001f)
                direction = math.normalizesafe((outside.d - outside.a).xz);
            float2 normal = new float2(direction.y, -direction.x);
            point.xz += normal * (left ? distance : -distance);
            return point;
        }

        private static List<float> SampleParameters(Bezier4x3 a, Bezier4x3 b)
        {
            List<float> result = new List<float> { 0f };
            SampleInterval(a, b, 0f, 1f, 0, result);
            return result;
        }

        private static void SampleInterval(Bezier4x3 a, Bezier4x3 b, float start, float end,
            int depth, List<float> result)
        {
            float middle = (start + end) * 0.5f;
            bool divide = NeedsSubdivision(a, start, middle, end) || NeedsSubdivision(b, start, middle, end);
            if (divide && depth < 9)
            {
                SampleInterval(a, b, start, middle, depth + 1, result);
                SampleInterval(a, b, middle, end, depth + 1, result);
            }
            else
                result.Add(end);
        }

        private static bool NeedsSubdivision(Bezier4x3 curve, float start, float middle, float end)
        {
            float3 a = MathUtils.Position(curve, start);
            float3 b = MathUtils.Position(curve, end);
            if (math.distance(a, b) > SampleSpacing)
                return true;
            // Quarter points catch S curves whose midpoint happens to lie on the chord.
            return math.distance(MathUtils.Position(curve, middle), math.lerp(a, b, 0.5f)) > 0.025f ||
                math.distance(MathUtils.Position(curve, math.lerp(start, end, 0.25f)), math.lerp(a, b, 0.25f)) > 0.025f ||
                math.distance(MathUtils.Position(curve, math.lerp(start, end, 0.75f)), math.lerp(a, b, 0.75f)) > 0.025f;
        }

        private static float3 Lift(float3 point, float height)
        {
            point.y += height + SurfaceLift;
            return point;
        }

        private static void AddDistinct(List<float3> points, float3 point)
        {
            if (points.Count == 0 || math.distancesq(points[points.Count - 1], point) > 0.000001f)
                points.Add(point);
        }

        private static void AppendPair(List<float3> a, List<float3> b, float3 first, float3 second)
        {
            if (a.Count > 0 && math.distancesq(a[a.Count - 1], first) < 0.000001f &&
                math.distancesq(b[b.Count - 1], second) < 0.000001f) return;
            a.Add(first); b.Add(second);
        }

        private static SurfacePatch CloseStrip(List<float3> a, List<float3> b)
        {
            SurfacePatch patch = new SurfacePatch();
            if (a.Count < 2 || a.Count != b.Count) return patch;
            TrimCoincidentStripEnds(a, b, false);
            TrimCoincidentStripEnds(a, b, true);
            int count = a.Count;
            int[] mapping = new int[count * 2];
            for (int i = 0; i < mapping.Length; i++)
            {
                float3 point = i < count ? a[i] : b[count * 2 - 1 - i];
                if (!math.all(math.isfinite(point))) return new SurfacePatch();
                if (patch.Nodes.Count == 0 || math.distancesq(patch.Nodes[patch.Nodes.Count - 1].m_Position, point) > 0.000001f)
                    patch.Nodes.Add(new AreaNode(point, 0f));
                mapping[i] = patch.Nodes.Count - 1;
            }
            if (patch.Nodes.Count > 1 && math.distancesq(patch.Nodes[0].m_Position, patch.Nodes[patch.Nodes.Count - 1].m_Position) <= 0.000001f)
            {
                int last = patch.Nodes.Count - 1;
                for (int i = 0; i < mapping.Length; i++) if (mapping[i] == last) mapping[i] = 0;
                patch.Nodes.RemoveAt(last);
            }
            if (patch.Nodes.Count < 3) return new SurfacePatch();
            for (int i = 0; i < count - 1; i++)
            {
                AddRibbonTriangle(patch, new int3(mapping[i], mapping[i + 1], mapping[count * 2 - i - 2]));
                AddRibbonTriangle(patch, new int3(mapping[i], mapping[count * 2 - i - 2], mapping[count * 2 - i - 1]));
            }
            // Keep the boundary counterclockwise for native edge effects. Remap
            // the explicit mesh when reversing the boundary's vertex order.
            double area = 0;
            for (int i = 0; i < patch.Nodes.Count; i++)
            {
                float3 p = patch.Nodes[i].m_Position, q = patch.Nodes[(i + 1) % patch.Nodes.Count].m_Position;
                area += (double)p.x * q.z - (double)q.x * p.z;
            }
            if (area < 0)
            {
                patch.Nodes.Reverse();
                for (int i = 0; i < patch.Triangles.Count; i++)
                    patch.Triangles[i] = new int3(patch.Nodes.Count - 1) - patch.Triangles[i];
            }
            patch.Nodes.Add(patch.Nodes[0]);
            return patch;
        }

        private static void AddRibbonTriangle(SurfacePatch patch, int3 indices)
        {
            float2 a = patch.Nodes[indices.x].m_Position.xz;
            float2 b = patch.Nodes[indices.y].m_Position.xz - a;
            float2 c = patch.Nodes[indices.z].m_Position.xz - a;
            float cross = b.x * c.y - b.y * c.x;
            if (math.abs(cross) <= 0.00001f) return;
            patch.Triangles.Add(cross > 0 ? indices : indices.xzy);
        }

        private static void TrimCoincidentStripEnds(List<float3> a, List<float3> b, bool start)
        {
            float3 tip = default;
            bool removed = false;
            while (a.Count > 1 && b.Count > 1)
            {
                int i = start ? 0 : a.Count - 1;
                int j = start ? 0 : b.Count - 1;
                if (math.distancesq(a[i].xz, b[j].xz) > 0.000001f)
                    break;
                tip = a[i];
                a.RemoveAt(i);
                b.RemoveAt(j);
                removed = true;
            }
            // A single shared tip closes a taper. A shared path followed twice
            // creates a zero-width tail that the game's triangulator can reject.
            if (removed)
            {
                if (start) { a.Insert(0, tip); b.Insert(0, tip); }
                else { a.Add(tip); b.Add(tip); }
            }
        }

    }
}
