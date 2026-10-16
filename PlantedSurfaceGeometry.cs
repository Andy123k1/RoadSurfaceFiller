using System.Collections.Generic;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private const CompositionFlags.Side PlantedSideFlags =
            CompositionFlags.Side.PrimaryBeautification | CompositionFlags.Side.SecondaryBeautification;
        private const CompositionFlags.General PlantedMiddleFlags =
            CompositionFlags.General.PrimaryMiddleBeautification | CompositionFlags.General.SecondaryMiddleBeautification;

        private readonly HashSet<Entity> m_LoggedPlantedLayouts = new HashSet<Entity>();
        private readonly HashSet<Entity> m_LoggedPlantingCapEdges = new HashSet<Entity>();
        private readonly Dictionary<Entity, List<LaneMaskCell>> m_JunctionPlantMasks = new Dictionary<Entity, List<LaneMaskCell>>();
        private int m_JunctionPlantFrame = -1;
        private List<LaneMaskCell> m_ActiveJunctionPlantMasks;

        private static bool HasPlantUpgrade(CompositionFlags flags)
        {
            // Native SideGrass/SideTrees and MiddleGrass/MiddleTrees map to
            // these flags in NetCompositionHelpers.GetRequirementFlags.
            return ((flags.m_Left | flags.m_Right) & PlantedSideFlags) != 0 ||
                (flags.m_General & PlantedMiddleFlags) != 0;
        }

        private bool PieceHasTravelLane(Entity piece)
        {
            if (!EntityManager.HasBuffer<NetPieceLane>(piece)) return false;
            foreach (NetPieceLane lane in EntityManager.GetBuffer<NetPieceLane>(piece, true))
            {
                LaneFlags flags = lane.m_ExtraFlags;
                if (EntityManager.HasComponent<NetLaneData>(lane.m_Lane))
                    flags |= EntityManager.GetComponentData<NetLaneData>(lane.m_Lane).m_Flags;
                if ((flags & (LaneFlags.Road | LaneFlags.Parking | LaneFlags.Pedestrian)) != 0 &&
                    (flags & (LaneFlags.Virtual | LaneFlags.Master | LaneFlags.CrossRoad)) == 0) return true;
            }
            return false;
        }

        private bool PieceHasTrees(NetCompositionPiece piece, CompositionFlags flags)
        {
            if (!EntityManager.HasBuffer<NetPieceObject>(piece.m_Piece)) return false;
            NetSectionFlags sectionFlags = piece.m_SectionFlags;
            if ((sectionFlags & NetSectionFlags.Invert) != 0)
            {
                flags = NetCompositionHelpers.InvertCompositionFlags(flags);
                sectionFlags = NetCompositionHelpers.InvertSectionFlags(sectionFlags);
            }
            foreach (NetPieceObject obj in EntityManager.GetBuffer<NetPieceObject>(piece.m_Piece, true))
                if (obj.m_Probability > 0 && NetCompositionHelpers.TestObjectFlags(obj, flags, sectionFlags) &&
                    (EntityManager.HasComponent<TreeData>(obj.m_Prefab) || EntityManager.HasComponent<PlantData>(obj.m_Prefab)))
                    return true;
            return false;
        }

        private void ReadPlantedSpans(Entity prefab, SurfaceLayout layout, List<NetCompositionPiece> pieces,
            List<LaneSpan> walking, bool edgeComposition)
        {
            CompositionFlags flags = layout.Composition.m_Flags;
            float half = layout.Composition.m_Width * 0.5f;
            foreach (NetCompositionPiece piece in pieces)
            {
                PieceSurfaceProfile profile = GetPieceSurfaceProfile(piece.m_Piece);
                foreach (RoadBand source in profile.Grass)
                {
                    RoadBand grass = TransformPieceSurfaceBand(piece, source);
                    grass = IncludePlantingCurbs(piece, profile, grass, layout.Driving, walking);
                    float low = math.max(-half, grass.Low), high = math.min(half, grass.High);
                    if (high > low + 0.01f)
                        layout.Planted.Add(new LaneSpan(low, high, (grass.LowHeight + grass.HighHeight) * 0.5f));
                }
                if (profile.Grass.Count > 0)
                {
                    LogPieceMaterialProfile(piece);
                    continue;
                }
                // A material footprint wins over navigation bounds. Grass in
                // a combined sidewalk piece can overlap its walking envelope;
                // subtracting that envelope used to discard the whole bed.
                if ((piece.m_PieceFlags & (NetPieceFlags.HasRoadLanes | NetPieceFlags.HasBicycleLanes)) != 0 ||
                    PieceHasTravelLane(piece.m_Piece)) continue;
                if (!PieceHasTrees(piece, flags)) continue;
                LaneSpan span = PieceSpan(piece, piece.m_Offset.y);
                List<RoadBand> protectedBands = new List<RoadBand> {
                    new RoadBand(math.max(-half, span.Low), math.min(half, span.High), span.Height, span.Height)
                };
                // A shared decorative piece may span another physical strip.
                // Its native travel lanes are never treated as a planting bed.
                protectedBands = SubtractLaneBands(protectedBands, layout.Driving);
                protectedBands = SubtractLaneBands(protectedBands, walking);
                foreach (RoadBand band in protectedBands)
                    if (band.High > band.Low + 0.01f)
                        layout.Planted.Add(new LaneSpan(band.Low, band.High, span.Height));
            }

            // Grass/tree upgrades can expose terrain between separate curb and
            // pavement pieces rather than authoring a grass surface mesh. Its
            // bounds come from the physical traffic and raised walking profile.
            // At intersections, use only actual planted pieces: approach grass
            // commonly ends before the crossing and must not invent a node gap.
            if (edgeComposition || (flags.m_General & CompositionFlags.General.DeadEnd) != 0)
                for (int side = 0; side < 2; side++)
                {
                    bool left = side == 0;
                    CompositionFlags.Side sideFlags = left ? flags.m_Left : flags.m_Right;
                    if ((sideFlags & PlantedSideFlags) == 0) continue;
                    // A grass-bearing mesh provides exact bounds, including
                    // its curb lip. Only missing meshes/tree-only verges use
                    // the gap beside the native walking route as a fallback.
                    bool materialGrass = layout.Planted.Exists(span => left ? span.High <= layout.Composition.m_MiddleOffset :
                        span.Low >= layout.Composition.m_MiddleOffset);
                    if ((sideFlags & CompositionFlags.Side.PrimaryBeautification) != 0 && materialGrass) continue;
                    float curb = left ? float.NegativeInfinity : float.PositiveInfinity;
                    foreach (LaneSpan sidewalk in walking)
                    {
                        if (left ? sidewalk.High > layout.Composition.m_MiddleOffset : sidewalk.Low < layout.Composition.m_MiddleOffset) continue;
                        curb = left ? math.max(curb, sidewalk.High) : math.min(curb, sidewalk.Low);
                    }
                    float traffic = left ? float.PositiveInfinity : float.NegativeInfinity;
                    foreach (LaneSpan lane in layout.Driving)
                        traffic = left ? math.min(traffic, lane.Low) : math.max(traffic, lane.High);
                    if (!math.isfinite(curb) || !math.isfinite(traffic)) continue;
                    float low = left ? curb : traffic, high = left ? traffic : curb;
                    if (high > low + 0.01f)
                        layout.Planted.Add(new LaneSpan(math.max(-half, low), math.min(half, high), 0f));
                }
            // Plant masks are footprint exclusions, so differing mesh heights
            // must not duplicate the same bed in the junction mask union.
            layout.Planted.Sort((a, b) => a.Low.CompareTo(b.Low));
            for (int i = layout.Planted.Count - 1; i > 0; i--)
            {
                LaneSpan a = layout.Planted[i - 1], b = layout.Planted[i];
                if (b.Low > a.High + 0.01f) continue;
                layout.Planted[i - 1] = new LaneSpan(a.Low, math.max(a.High, b.High), math.min(a.Height, b.Height));
                layout.Planted.RemoveAt(i);
            }
            if ((HasPlantUpgrade(flags) || layout.Planted.Count > 0) &&
                m_LoggedPlantedLayouts.Count < 24 && m_LoggedPlantedLayouts.Add(prefab))
            {
                List<string> bounds = new List<string>(), walks = new List<string>();
                foreach (LaneSpan span in layout.Planted) bounds.Add(span.Low.ToString("0.###") + ".." + span.High.ToString("0.###"));
                foreach (LaneSpan span in walking) walks.Add(span.Low.ToString("0.###") + ".." + span.High.ToString("0.###") + " @ " + span.Height.ToString("0.###"));
                Mod.Log.Info("Protected planting profile: composition=" + prefab.Index + "; node=" + !edgeComposition +
                    "; left=" + flags.m_Left + "; right=" + flags.m_Right + "; middle=" + flags.m_General +
                    "; bands=[" + string.Join(", ", bounds) + "]; walking routes=[" + string.Join(", ", walks) +
                    "]. Grass material footprints are excluded even inside a shared walking piece.");
            }
        }

        private RoadBand IncludePlantingCurbs(NetCompositionPiece piece, PieceSurfaceProfile profile,
            RoadBand grass, List<LaneSpan> driving, List<LaneSpan> walking)
        {
            float low = grass.Low, high = grass.High;
            // The same mesh contains the planting's curb top and lower rim.
            // These isolated paved faces belong to the bed boundary, rather
            // than the carriageway. Follow their actual connectivity, stopping
            // at every physical traffic lane and pedestrian walking envelope.
            // Revisit after each growth so differently elevated curb faces join.
            for (int pass = 0; pass < profile.Pavement.Count; pass++)
            {
                bool changed = false;
                foreach (RoadBand source in profile.Pavement)
                {
                    RoadBand curb = TransformPieceSurfaceBand(piece, source);
                    float roadHeight = float.NegativeInfinity;
                    foreach (LaneSpan lane in driving) roadHeight = math.max(roadHeight, lane.Height);
                    // Only the raised planting curb belongs to the bed. The
                    // adjoining lower asphalt rim is still road pavement.
                    if (!math.isfinite(roadHeight) || math.min(curb.LowHeight, curb.HighHeight) <= roadHeight + 0.035f) continue;
                    bool extendLow = math.abs(curb.High - low) <= 0.001f && curb.Low < low - 0.001f;
                    bool extendHigh = math.abs(curb.Low - high) <= 0.001f && curb.High > high + 0.001f;
                    if (!extendLow && !extendHigh) continue;
                    bool travel = false;
                    foreach (LaneSpan lane in driving)
                        travel |= curb.Low < lane.High - 0.01f && curb.High > lane.Low + 0.01f;
                    foreach (LaneSpan lane in walking)
                        travel |= curb.Low < lane.High - 0.01f && curb.High > lane.Low + 0.01f;
                    if (travel) continue;
                    if (extendLow) low = curb.Low;
                    if (extendHigh) high = curb.High;
                    changed = true;
                }
                if (!changed) break;
            }
            return new RoadBand(low, high, grass.LowHeight, grass.HighHeight);
        }

        private static void ExcludePlantedSidewalks(SurfaceLayout layout)
        {
            if (layout.Planted.Count == 0) return;
            List<RoadBand> walks = new List<RoadBand>();
            foreach (LaneSpan span in layout.Sidewalks) walks.Add(new RoadBand(span.Low, span.High, span.Height, span.Height));
            walks = SubtractLaneBands(walks, layout.Planted);
            layout.Sidewalks.Clear();
            foreach (RoadBand band in walks) layout.Sidewalks.Add(new LaneSpan(band.Low, band.High, band.LowHeight));
        }

        private static bool HasPavedPlantingCap(SurfaceLayout approach, SurfaceLayout junction,
            RoadBand source, RoadBand sidewalk, bool left, bool start)
        {
            if ((junction.Composition.m_Flags.m_General & CompositionFlags.General.Intersection) == 0 ||
                approach.Planted.Count == 0) return false;
            // Compare in the node's lateral coordinate system, including the
            // reversal at an edge's start. A cap is the part of the raised node
            // sidewalk replacing planting beside the narrower edge walkway.
            float scale = junction.Composition.m_Width / approach.Composition.m_Width;
            float edgeCurb = (left ? source.High : source.Low) * scale;
            float nodeCurb = left ? sidewalk.High : sidewalk.Low;
            float widening = left ? nodeCurb - edgeCurb : edgeCurb - nodeCurb;
            if (widening <= 0.01f) return false;
            float low = math.min(edgeCurb, nodeCurb), high = math.max(edgeCurb, nodeCurb);
            foreach (LaneSpan planted in junction.Planted)
                if (planted.Low < high - 0.01f && planted.High > low + 0.01f) return false;
            float plantedWidth = 0f;
            foreach (LaneSpan planted in approach.Planted)
            {
                float plantLow = (start ? -planted.High : planted.Low) * scale;
                float plantHigh = (start ? -planted.Low : planted.High) * scale;
                plantedWidth += math.max(0f, math.min(high, plantHigh) - math.max(low, plantLow));
            }
            // The node's paved cap also includes the planting curb lip. Its
            // complete width therefore exceeds the grass material footprint;
            // requiring full grass coverage rejected both reported profiles.
            // The native sidewalk supplies the curb, and real node planting
            // was ruled out above, so an overlap identifies the terminated bed.
            return plantedWidth > 0.01f;
        }

        private List<LaneMaskCell> GetJunctionPlantMasks(Entity node)
        {
            if (m_JunctionPlantFrame != m_GenerationCacheEpoch)
            {
                m_JunctionPlantMasks.Clear(); m_JunctionPlantFrame = m_GenerationCacheEpoch;
            }
            if (m_JunctionPlantMasks.TryGetValue(node, out List<LaneMaskCell> cached)) return cached;
            List<LaneMaskCell> cells = new List<LaneMaskCell>();
            m_JunctionPlantMasks[node] = cells;
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return cells;
            foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity edge = connected.m_Edge;
                if (!IsRoadEdge(edge) || !EntityManager.HasComponent<EdgeGeometry>(edge)) continue;
                bool start = EntityManager.GetComponentData<Edge>(edge).m_Start == node;
                if (start ? !EntityManager.HasComponent<StartNodeGeometry>(edge) : !EntityManager.HasComponent<EndNodeGeometry>(edge)) continue;
                Composition composition = EntityManager.GetComponentData<Composition>(edge);
                Entity nodeComposition = start ? composition.m_StartNode : composition.m_EndNode;
                if (!CompositionHasPlantGeometry(nodeComposition)) continue;
                if (!TryReadSurfaceLayout(composition.m_Edge, out SurfaceLayout approach, highway: IsHighway(edge)) ||
                    !TryReadSurfaceLayout(nodeComposition, out SurfaceLayout layout,
                        approach, start, highway: approach.Highway)) continue;
                EdgeNodeGeometry geometry = start ? EntityManager.GetComponentData<StartNodeGeometry>(edge).m_Geometry :
                    EntityManager.GetComponentData<EndNodeGeometry>(edge).m_Geometry;
                bool roundabout = geometry.m_MiddleRadius > 0.01f;
                foreach (LaneSpan span in layout.Planted)
                {
                    RoadBand band = new RoadBand(span.Low, span.High, span.Height, span.Height);
                    for (int side = 0; side < 2; side++)
                    {
                        bool left = side == 0;
                        if (left ? band.Low >= layout.Composition.m_MiddleOffset : band.High <= layout.Composition.m_MiddleOffset) continue;
                        CornerInsets end = GetCornerInsets(edge, node, geometry, layout, band, left, false, roundabout, planted: true);
                        SurfacePatch patch = roundabout ? CreateRoundaboutSidewalkPolygon(geometry, layout.Composition, band, left, end,
                            GetJunctionMouth(edge, approach, layout, band, left, false, start, planted: true)) :
                            CreateJunctionSidePolygon(geometry, layout.Composition, band, left,
                                GetJunctionMouth(edge, approach, layout, band, left, false, start, planted: true), end);
                        AddLaneMaskCells(patch, cells);
                    }
                }
            }
            // Permanent land exclusions do not depend on decal height or lane
            // separation switches. Other approaches cannot paint these beds.
            foreach (LaneMaskCell cell in cells)
            {
                cell.Minimum.y = float.NegativeInfinity; cell.Maximum.y = float.PositiveInfinity;
            }
            return cells;
        }

        private bool CompositionHasPlantGeometry(Entity prefab)
        {
            if (!EntityManager.HasComponent<NetCompositionData>(prefab)) return false;
            CompositionFlags flags = EntityManager.GetComponentData<NetCompositionData>(prefab).m_Flags;
            if (HasPlantUpgrade(flags)) return true;
            if (!EntityManager.HasBuffer<NetCompositionPiece>(prefab)) return false;
            foreach (NetCompositionPiece piece in EntityManager.GetBuffer<NetCompositionPiece>(prefab, true))
                if (GetPieceSurfaceProfile(piece.m_Piece).Grass.Count > 0 || PieceHasTrees(piece, flags)) return true;
            return false;
        }

        private static void MarkPlantedMasksApplied(SurfacePatch patch)
        {
            patch.PlantedAreasClipped = true;
            foreach (SurfacePatch part in patch.Parts) MarkPlantedMasksApplied(part);
        }

        private int MixPlantComposition(int hash, Entity prefab)
        {
            if (!EntityManager.HasComponent<NetCompositionData>(prefab)) return hash;
            NetCompositionData data = EntityManager.GetComponentData<NetCompositionData>(prefab);
            hash = Mix(hash, (int)data.m_Flags.m_General);
            hash = Mix(hash, (int)data.m_Flags.m_Left); hash = Mix(hash, (int)data.m_Flags.m_Right);
            if (!HasPlantUpgrade(data.m_Flags) || !EntityManager.HasBuffer<NetCompositionPiece>(prefab)) return hash;
            foreach (NetCompositionPiece piece in EntityManager.GetBuffer<NetCompositionPiece>(prefab, true))
            {
                hash = Mix(hash, piece.m_Piece.Index); hash = Mix(hash, piece.m_Piece.Version);
                hash = Mix(hash, (int)piece.m_PieceFlags); hash = Mix(hash, (int)piece.m_SectionFlags);
                hash = MixVector(hash, piece.m_Offset); hash = MixVector(hash, piece.m_Size);
            }
            return hash;
        }
    }
}
