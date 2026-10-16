using System.Collections.Generic;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private int m_LoggedJunctionShoulders;
        private void AddBusShoulderJunctionStrips(JunctionLaneProfile profile, Entity node,
            List<JunctionLaneRoad> roads, Setting setting)
        {
            if (!roads.Exists(road => road.Eligible && road.Layout.FillBusShoulders && road.Layout.SeparateBusLanes)) return;
            List<LaneMaskCell> protectedCells = ReadOrdinaryJunctionSidewalkMasks(node);
            protectedCells.AddRange(GetJunctionPlantMasks(node));
            foreach (JunctionLaneRoad road in roads)
            {
                if (!road.Eligible || !road.Layout.FillBusShoulders || !road.Layout.SeparateBusLanes || road.Layout.BusLanes.Count == 0) continue;
                Composition composition = EntityManager.GetComponentData<Composition>(road.Edge);
                if (road.Start ? !EntityManager.HasComponent<StartNodeGeometry>(road.Edge) : !EntityManager.HasComponent<EndNodeGeometry>(road.Edge)) continue;
                EdgeNodeGeometry geometry = road.Start ? EntityManager.GetComponentData<StartNodeGeometry>(road.Edge).m_Geometry :
                    EntityManager.GetComponentData<EndNodeGeometry>(road.Edge).m_Geometry;
                if (!TryReadSurfaceLayout(road.Start ? composition.m_StartNode : composition.m_EndNode, out SurfaceLayout layout,
                    road.Layout, road.Start, streetContext: true)) continue;
                SurfaceChoice surface = Choose(GetChoices(setting, SurfaceTarget.BusLanes, road.Prefab, segment: road.Edge),
                    m_PrefabSystem.GetPrefabName(road.Prefab), road.Curve, 4);
                if (surface == null) continue;
                bool wholeCarriageway = BusLanesCoverDriving(road.Layout);
                List<RoadBand> bands = wholeCarriageway ? SubtractLaneBands(layout.Roads, layout.BikeLanes) : GetDedicatedPaintBands(layout, false);
                int count = 0;
                foreach (RoadBand band in bands)
                    for (int side = 0; side < 2; side++)
                    {
                        bool left = side == 0;
                        CornerInsets mouth = wholeCarriageway ? GetJunctionMouth(road.Edge, road.Layout, layout, band, left, false, road.Start) :
                            BusShoulderMouth(road.Edge, road.Layout, layout, band, left, road.Start);
                        CornerInsets end = wholeCarriageway ? GetCornerInsets(road.Edge, node, geometry, layout, band, left, false, false) :
                            BandInsets(layout.Composition, band, left);
                        SurfacePatch patch = CreateJunctionSidePolygon(geometry, layout.Composition, band, left, mouth, end);
                        SnapJunctionLaneDeck(patch, profile.Deck);
                        patch = SubtractLaneMasks(patch, protectedCells);
                        MarkPlantedMasksApplied(patch); SetLanePatchFlags(patch, false);
                        // The footprint has a hard physical curb mask. Include
                        // raised asphalt ramps up to that curb as well as lanes.
                        patch.ProjectionMaximum = float.PositiveInfinity;
                        JunctionLaneStrip strip = new JunctionLaneStrip { Owner = road.Edge, Target = SurfaceTarget.BusLanes,
                            Surface = surface, Patch = patch };
                        AddLaneMaskCells(patch, strip.Cells);
                        if (strip.Cells.Count > 0) { profile.Strips.Add(strip); count += CountSurfacePatchAreas(patch); }
                    }
                if (count > 0 && m_LoggedJunctionShoulders++ < 16)
                    Mod.Log.Info("Bus shoulder junction coverage: node=" + node.Index + "; edge=" + road.Edge.Index +
                        "; entire carriageway=" + wholeCarriageway + "; curb-bounded areas=" + count + ".");
            }
        }

        private CornerInsets BusShoulderMouth(Entity owner, SurfaceLayout approach, SurfaceLayout layout,
            RoadBand band, bool left, bool start)
        {
            RoadBand selected = band; float best = float.PositiveInfinity;
            foreach (RoadBand candidate in GetDedicatedPaintBands(approach, false))
            {
                RoadBand oriented = start ? new RoadBand(-candidate.High, -candidate.Low, candidate.HighHeight, candidate.LowHeight) : candidate;
                float score = math.abs(oriented.Low / approach.Composition.m_Width - band.Low / layout.Composition.m_Width) +
                    math.abs(oriented.High / approach.Composition.m_Width - band.High / layout.Composition.m_Width);
                if (score < best) { selected = oriented; best = score; }
            }
            float middle = start ? -approach.Composition.m_MiddleOffset : approach.Composition.m_MiddleOffset;
            float low = left ? selected.Low : math.max(middle, selected.Low), high = left ? math.min(middle, selected.High) : selected.High;
            EdgeGeometry edge = EntityManager.GetComponentData<EdgeGeometry>(owner);
            float3 a = start ? edge.m_Start.m_Right.a : edge.m_End.m_Left.d, b = start ? edge.m_Start.m_Left.a : edge.m_End.m_Right.d;
            a.y -= start ? approach.Composition.m_EdgeHeights.y : approach.Composition.m_EdgeHeights.z;
            b.y -= start ? approach.Composition.m_EdgeHeights.x : approach.Composition.m_EdgeHeights.w;
            float near = left ? low : high, far = left ? high : low;
            return new CornerInsets { HasPositions = true,
                NearPosition = Lift(math.lerp(a, b, math.saturate(near / approach.Composition.m_Width + 0.5f)), PavementHeight(selected, near)),
                FarPosition = Lift(math.lerp(a, b, math.saturate(far / approach.Composition.m_Width + 0.5f)), PavementHeight(selected, far)) };
        }

        private List<LaneMaskCell> ReadOrdinaryJunctionSidewalkMasks(Entity node)
        {
            List<LaneMaskCell> result = new List<LaneMaskCell>();
            foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity edge = connected.m_Edge;
                if (!IsRoadEdge(edge)) continue;
                bool start = EntityManager.GetComponentData<Edge>(edge).m_Start == node;
                if (start ? !EntityManager.HasComponent<StartNodeGeometry>(edge) : !EntityManager.HasComponent<EndNodeGeometry>(edge)) continue;
                Composition composition = EntityManager.GetComponentData<Composition>(edge);
                if (!TryReadSurfaceLayout(composition.m_Edge, out SurfaceLayout approach, highway: IsHighway(edge)) ||
                    !TryReadSurfaceLayout(start ? composition.m_StartNode : composition.m_EndNode, out SurfaceLayout layout,
                        approach, start, streetContext: true)) continue;
                EdgeNodeGeometry geometry = start ? EntityManager.GetComponentData<StartNodeGeometry>(edge).m_Geometry :
                    EntityManager.GetComponentData<EndNodeGeometry>(edge).m_Geometry;
                foreach (LaneSpan walk in layout.Sidewalks)
                {
                    RoadBand band = new RoadBand(walk.Low, walk.High, walk.Height, walk.Height);
                    for (int side = 0; side < 2; side++)
                    {
                        bool left = side == 0;
                        AddLaneMaskCells(CreateJunctionSidePolygon(geometry, layout.Composition, band, left,
                            GetJunctionMouth(edge, approach, layout, band, left, true, start),
                            GetCornerInsets(edge, node, geometry, layout, band, left, true, false)), result);
                    }
                }
            }
            foreach (LaneMaskCell cell in result) { cell.Minimum.y = float.NegativeInfinity; cell.Maximum.y = float.PositiveInfinity; }
            return result;
        }
    }
}
