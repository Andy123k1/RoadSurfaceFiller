using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private sealed class JunctionLaneStrip
        {
            internal Entity Lane, Owner;
            internal SurfaceTarget Target;
            internal SurfaceChoice Surface;
            internal SurfacePatch Patch;
            internal LaneDeckAlignment DeckAlignment;
            internal readonly List<LaneMaskCell> Cells = new List<LaneMaskCell>();
        }

        private sealed class JunctionLaneProfile
        {
            internal int Frame;
            internal RoundaboutLaneRing BikeRing;
            internal List<LaneDeckFace> Deck;
            internal readonly List<JunctionLaneStrip> Strips = new List<JunctionLaneStrip>();
            internal readonly List<LaneMaskCell> Cells = new List<LaneMaskCell>();
            internal readonly List<LaneMaskCell> ConnectorCells = new List<LaneMaskCell>();
            internal readonly List<LaneMaskCell> BikeCells = new List<LaneMaskCell>();
            internal readonly List<LaneMaskCell> ContinuousCells = new List<LaneMaskCell>();
        }

        private sealed class JunctionLaneRoad
        {
            internal Entity Edge, Prefab;
            internal Curve Curve;
            internal SurfaceLayout Layout;
            internal bool Start, Eligible;
        }

        private readonly Dictionary<Entity, JunctionLaneProfile> m_JunctionLaneProfiles = new Dictionary<Entity, JunctionLaneProfile>();
        private readonly HashSet<Entity> m_LoggedJunctionLanes = new HashSet<Entity>();
        private readonly Dictionary<Entity, int> m_LoggedJunctionLaneSignatures = new Dictionary<Entity, int>();
        private int m_JunctionLaneReportCount;
        private JunctionLaneProfile m_ActiveJunctionLaneProfile;
        private int m_LoggedLaneMaskFallbacks;
        private int m_JunctionLaneCacheFrame = -1;

        private void AddJunctionSurfaces(Entity owner, Entity node, Entity prefab, EdgeNodeGeometry geometry,
            Curve curve, string name, List<SurfaceChoice> roads, List<SurfaceChoice> sidewalks, Setting setting,
            bool start, SurfaceLayout approach)
        {
            JunctionLaneProfile profile = GetJunctionLaneProfile(node, setting, geometry.m_MiddleRadius);
            JunctionLaneProfile previous = m_ActiveJunctionLaneProfile;
            List<LaneMaskCell> previousPlants = m_ActiveJunctionPlantMasks;
            m_ActiveJunctionLaneProfile = profile;
            m_ActiveJunctionPlantMasks = GetJunctionPlantMasks(node);
            try
            {
                AddJunctionSurfacePatches(owner, node, prefab, geometry, curve, name, roads, sidewalks, setting, start, approach);
                // Lane strips have already been partitioned against each other.
                // Retain the permanent planting mask while queuing their paint.
                m_ActiveJunctionLaneProfile = previous;
                if (profile != null)
                    foreach (JunctionLaneStrip strip in profile.Strips)
                        if (strip.Owner == owner && strip.Surface != null)
                            QueuePolygon(owner, strip.Surface.Prefab, CopySurfacePatch(strip.Patch));
            }
            finally
            {
                m_ActiveJunctionLaneProfile = previous;
                m_ActiveJunctionPlantMasks = previousPlants;
            }
        }

        private JunctionLaneProfile GetJunctionLaneProfile(Entity node, Setting setting, float islandRadius)
        {
            if (!HasAnyLaneSeparation(setting) ||
                !EntityManager.HasBuffer<SubLane>(node) || !EntityManager.HasBuffer<ConnectedEdge>(node)) return null;
            if (m_JunctionLaneCacheFrame != m_GenerationCacheEpoch)
            {
                m_JunctionLaneProfiles.Clear();
                m_JunctionLaneCacheFrame = m_GenerationCacheEpoch;
            }
            if (m_JunctionLaneProfiles.TryGetValue(node, out JunctionLaneProfile cached) && cached.Frame == m_GenerationCacheEpoch)
                return cached;
            JunctionLaneProfile result = new JunctionLaneProfile { Frame = m_GenerationCacheEpoch };
            m_JunctionLaneProfiles[node] = result;
            List<JunctionLaneRoad> roads = new List<JunctionLaneRoad>();
            foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity edge = connected.m_Edge;
                if (!IsRoadEdge(edge) || !EntityManager.HasComponent<EdgeGeometry>(edge)) continue;
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                Composition composition = EntityManager.GetComponentData<Composition>(edge);
                if (!TryReadSurfaceLayout(composition.m_Edge, out SurfaceLayout layout, highway: IsHighway(edge), roadPrefab: prefab)) continue;
                ApplySegmentLaneSettings(layout, edge);
                roads.Add(new JunctionLaneRoad {
                    Edge = edge, Prefab = prefab, Curve = EntityManager.GetComponentData<Curve>(edge), Layout = layout,
                    Start = EntityManager.GetComponentData<Edge>(edge).m_Start == node,
                    Eligible = PassesSegmentFilter(edge, prefab, setting) && PassesRoadFilter(edge, prefab, setting) &&
                        PassesElevationFilter(edge, prefab, setting)
                });
            }
            bool laneApproach = roads.Exists(road => HasSeparatedLanes(road.Layout, setting));
            result.Deck = laneApproach ? ReadJunctionLaneDeck(node, roads, islandRadius) : new List<LaneDeckFace>();
            if (!laneApproach) return result;
            JunctionPavementDomain pavement = GetJunctionPavementDomain(node);
            bool coverBusLanes = JunctionCoversBusLanes(node);
            List<LaneMaskCell> protectedBusCells = ReadOrdinaryJunctionSidewalkMasks(node);
            protectedBusCells.AddRange(GetJunctionPlantMasks(node));
            bool separateBikes = roads.Exists(road => road.Layout.SeparateBikeLanes && road.Layout.BikeLanes.Count > 0);
            if (separateBikes && islandRadius > 0.01f)
                result.BikeRing = ReadRoundaboutBikeRing(node, roads, setting, islandRadius);
            if (result.BikeRing != null)
            {
                JunctionLaneStrip ring = CreateRoundaboutBikeRingStrip(result.BikeRing, islandRadius, result.Deck);
                result.Strips.Add(ring);
                result.Cells.AddRange(ring.Cells);
            }
            if (separateBikes && islandRadius <= 0.01f && roads.Count >= 3)
                AddCompositionJunctionBikeStrips(result, node, roads, setting);
            if (islandRadius <= 0.01f) AddBusShoulderJunctionStrips(result, node, roads, setting);
            foreach (SubLane subLane in EntityManager.GetBuffer<SubLane>(node, true))
            {
                Entity lane = subLane.m_SubLane;
                if (!TryReadJunctionLane(lane, setting, out SurfaceTarget target, out Bezier4x3 curve, out float2 widths)) continue;
                if (target == SurfaceTarget.BikeLanes && result.BikeRing != null &&
                    TryReadCirculatingBikeArc(lane, curve, widths, result.BikeRing.Center, islandRadius, out float ringRadius) &&
                    math.abs(ringRadius - result.BikeRing.Radius) <= math.max(0.4f, result.BikeRing.Width * 0.35f)) continue;
                JunctionLaneRoad source = FindJunctionLaneSource(lane, target, curve, roads);
                if (source == null || !IsSurfaceTargetActive(source.Layout, target)) continue;
                SurfacePatch patch = CreateNativeLaneRibbon(curve, widths, islandRadius > 0.01f && islandRadius <= 4.01f);
                bool expandedBus = false;
                if (target == SurfaceTarget.BusLanes && islandRadius <= 0.01f && !coverBusLanes)
                {
                    SurfacePatch wide = CreateBusShoulderConnection(lane, curve, widths, roads, pavement, protectedBusCells);
                    if (wide != null && TryJoinBusShoulderRibbon(patch, wide, out SurfacePatch joined))
                    {
                        expandedBus = true;
                        patch = joined;
                    }
                }
                if (target == SurfaceTarget.BusLanes && coverBusLanes)
                    patch = StopBusAtJunctionCrossings(patch, node, roads, pavement);
                if (patch.Triangles.Count == 0 && patch.Parts.Count == 0) continue;
                SnapJunctionLaneDeck(patch, result.Deck);
                JunctionLaneStrip strip = new JunctionLaneStrip { Lane = lane, Target = target, Patch = patch };
                if (result.BikeRing != null)
                    strip.Patch = patch = SubtractLaneMasks(patch, result.Cells);
                AddLaneMaskCells(patch, strip.Cells);
                if (strip.Cells.Count == 0) continue;
                if (source != null && source.Eligible)
                {
                    strip.Owner = source.Edge;
                    List<LaneSpan> spans = target == SurfaceTarget.BikeLanes ? source.Layout.BikeLanes : source.Layout.BusLanes;
                    if (spans.Count > 0) LimitDedicatedLaneProjection(patch, source.Layout, spans[0].Height);
                    if (expandedBus) AllowShoulderProjection(patch);
                    strip.Surface = Choose(GetChoices(setting, target, source.Prefab, segment: source.Edge),
                        m_PrefabSystem.GetPrefabName(source.Prefab), source.Curve, target == SurfaceTarget.BikeLanes ? 3 : 4);
                }
                result.Strips.Add(strip);
            }
            foreach (JunctionLaneStrip strip in result.Strips)
            {
                // Broad approach shoulders stop at crossings. Only native
                // connectors with two enabled endpoints widen through them.
                if (strip.Target == SurfaceTarget.BikeLanes || strip.Lane != Entity.Null) continue;
                JunctionLaneRoad approach = roads.Find(road => road.Edge == strip.Owner);
                if (roads.Count >= 2 && approach != null)
                    strip.Patch = ClipSurfacePatch(strip.Patch, JunctionCrosswalkCut(approach.Edge, node, approach.Start), true);
                if (pavement != null) strip.Patch = SubtractLaneMasks(strip.Patch, pavement.Cells);
                strip.Cells.Clear(); AddLaneMaskCells(strip.Patch, strip.Cells);
            }
            // Reserved footprints are a union. Remove earlier strips from
            // later paint so crossings/duplicate directions do not stack decals.
            result.Strips.Sort((a, b) => a.Target != b.Target ? a.Target.CompareTo(b.Target) : a.Lane.Index.CompareTo(b.Lane.Index));
            List<LaneMaskCell> reserved = new List<LaneMaskCell>();
            foreach (JunctionLaneStrip strip in result.Strips)
            {
                if (strip.Surface != null && reserved.Count > 0)
                    strip.Patch = SubtractLaneMasks(strip.Patch, reserved);
                strip.Cells.Clear(); AddLaneMaskCells(strip.Patch, strip.Cells);
                reserved.AddRange(strip.Cells);
                if (strip.Target == SurfaceTarget.BikeLanes) result.BikeCells.AddRange(strip.Cells);
                if (strip.Lane != Entity.Null) result.ConnectorCells.AddRange(strip.Cells);
                if (strip.Target == SurfaceTarget.BikeLanes || strip.Lane != Entity.Null) result.ContinuousCells.AddRange(strip.Cells);
            }
            result.Cells.Clear(); result.Cells.AddRange(reserved);
            int reportSignature = MixJunctionLaneSignature(result.Cells.Count, node);
            foreach (JunctionLaneRoad road in roads)
            {
                Composition composition = EntityManager.GetComponentData<Composition>(road.Edge);
                reportSignature = Mix(reportSignature, composition.m_Edge.Index);
                reportSignature = Mix(reportSignature, (road.Start ? composition.m_StartNode : composition.m_EndNode).Index);
            }
            if (laneApproach && m_JunctionLaneReportCount < 48 &&
                (m_LoggedJunctionLanes.Contains(node) || m_LoggedJunctionLanes.Count < 16) &&
                (!m_LoggedJunctionLaneSignatures.TryGetValue(node, out int previousReport) || previousReport != reportSignature))
            {
                m_LoggedJunctionLanes.Add(node); m_LoggedJunctionLaneSignatures[node] = reportSignature; m_JunctionLaneReportCount++;
                Mod.Log.Info("Native junction lane footprints: node=" + node.Index + "; strips=" + result.Strips.Count +
                    "; cells=" + result.Cells.Count + "; island radius=" + islandRadius +
                    "; composition bands=" + result.Strips.FindAll(strip => strip.Lane == Entity.Null && result.BikeRing == null).Count +
                    "; continuous bike ring=" + (result.BikeRing != null) +
                    (result.BikeRing == null ? "" : "; ring radius=" + result.BikeRing.Radius + "; ring width=" + result.BikeRing.Width) +
                    "; physical deck faces=" + result.Deck.Count +
                    (result.BikeRing == null ? "" : RingDeckDiagnostic(result)) +
                    ". Road/sidewalk overlays exclude dedicated lane footprints; circular pavement uses joined bands.");
                LogNativeJunctionLaneData(node, islandRadius);
            }
            return result;
        }

        private static string RingDeckDiagnostic(JunctionLaneProfile profile)
        {
            foreach (JunctionLaneStrip strip in profile.Strips)
                if (strip.Lane == Entity.Null && strip.Target == SurfaceTarget.BikeLanes)
                    return "; ring deck nodes=" + strip.DeckAlignment.Matched + "/" + strip.DeckAlignment.Nodes +
                        "; maximum deck correction=" + strip.DeckAlignment.MaximumCorrection.ToString("0.####") + " m";
            return "";
        }

        private void AddCompositionJunctionBikeStrips(JunctionLaneProfile profile, Entity node,
            List<JunctionLaneRoad> roads, Setting setting)
        {
            // Crossings and curb turns can be part of the node composition
            // mesh instead of separate navigation lanes. Use its authored bike
            // bands and native vertex matching, never an interior traffic turn.
            foreach (JunctionLaneRoad road in roads)
            {
                if (!road.Layout.SeparateBikeLanes || road.Layout.BikeLanes.Count == 0 || (road.Start ?
                    !EntityManager.HasComponent<StartNodeGeometry>(road.Edge) :
                    !EntityManager.HasComponent<EndNodeGeometry>(road.Edge))) continue;
                Composition composition = EntityManager.GetComponentData<Composition>(road.Edge);
                if (!TryReadSurfaceLayout(road.Start ? composition.m_StartNode : composition.m_EndNode,
                    out SurfaceLayout layout, road.Layout, road.Start, highway: road.Layout.Highway)) continue;
                EdgeNodeGeometry geometry = road.Start ? EntityManager.GetComponentData<StartNodeGeometry>(road.Edge).m_Geometry :
                    EntityManager.GetComponentData<EndNodeGeometry>(road.Edge).m_Geometry;
                SurfaceChoice surface = road.Eligible ? Choose(GetChoices(setting, SurfaceTarget.BikeLanes, road.Prefab, segment: road.Edge),
                    m_PrefabSystem.GetPrefabName(road.Prefab), road.Curve, 3) : null;
                foreach (RoadBand band in GetDedicatedPaintBands(layout, true))
                {
                    bool left = band.Low + band.High < layout.Composition.m_MiddleOffset * 2f;
                    SurfacePatch patch = CreateJunctionSidePolygon(geometry, layout.Composition, band, left,
                        GetDedicatedLaneMouth(road.Edge, road.Layout, layout, band, left, road.Start),
                        BandInsets(layout.Composition, band, left));
                    if (patch.Triangles.Count == 0) continue;
                    patch.ExactBoundary = true;
                    LimitDedicatedLaneProjection(patch, layout, band.LowHeight);
                    JunctionLaneStrip strip = new JunctionLaneStrip {
                        Owner = road.Eligible ? road.Edge : Entity.Null, Target = SurfaceTarget.BikeLanes,
                        Surface = surface, Patch = patch
                    };
                    AddLaneMaskCells(patch, strip.Cells);
                    if (strip.Cells.Count > 0) profile.Strips.Add(strip);
                }
            }
        }

        private CornerInsets GetDedicatedLaneMouth(Entity owner, SurfaceLayout approach,
            SurfaceLayout layout, RoadBand band, bool left, bool start)
        {
            EdgeGeometry edge = EntityManager.GetComponentData<EdgeGeometry>(owner);
            float3 outerLeft = start ? edge.m_Start.m_Right.a : edge.m_End.m_Left.d;
            float3 outerRight = start ? edge.m_Start.m_Left.a : edge.m_End.m_Right.d;
            outerLeft.y -= start ? approach.Composition.m_EdgeHeights.y : approach.Composition.m_EdgeHeights.z;
            outerRight.y -= start ? approach.Composition.m_EdgeHeights.x : approach.Composition.m_EdgeHeights.w;
            RoadBand mouthBand = band;
            float closest = float.PositiveInfinity;
            foreach (LaneSpan lane in approach.BikeLanes)
            {
                float low = start ? -lane.High : lane.Low, high = start ? -lane.Low : lane.High;
                float score = math.abs((low + high) / approach.Composition.m_Width -
                    (band.Low + band.High) / layout.Composition.m_Width);
                if (score < closest) { closest = score; mouthBand = new RoadBand(low, high, lane.Height, lane.Height); }
            }
            float near = left ? mouthBand.Low : mouthBand.High, far = left ? mouthBand.High : mouthBand.Low;
            return new CornerInsets {
                HasPositions = true,
                NearPosition = Lift(math.lerp(outerLeft, outerRight, math.saturate(near / approach.Composition.m_Width + 0.5f)),
                    PavementHeight(mouthBand, near)),
                FarPosition = Lift(math.lerp(outerLeft, outerRight, math.saturate(far / approach.Composition.m_Width + 0.5f)),
                    PavementHeight(mouthBand, far))
            };
        }

        private void LogNativeJunctionLaneData(Entity node, float islandRadius)
        {
            int count = 0;
            foreach (SubLane subLane in EntityManager.GetBuffer<SubLane>(node, true))
            {
                Entity lane = subLane.m_SubLane;
                if (!EntityManager.HasComponent<PrefabRef>(lane) ||
                    !EntityManager.HasComponent<NodeLane>(lane)) continue;
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;
                NetLaneData data = EntityManager.HasComponent<NetLaneData>(prefab) ? EntityManager.GetComponentData<NetLaneData>(prefab) : default;
                float meshWidth = EntityManager.HasComponent<NetLaneGeometryData>(prefab) ?
                    EntityManager.GetComponentData<NetLaneGeometryData>(prefab).m_Size.x : 0f;
                NodeLane nodeLane = EntityManager.GetComponentData<NodeLane>(lane);
                CarLaneFlags carFlags = EntityManager.HasComponent<Game.Net.CarLane>(lane) ?
                    EntityManager.GetComponentData<Game.Net.CarLane>(lane).m_Flags : 0;
                bool bicycle = (data.m_Flags & LaneFlags.BicyclesOnly) != 0 ||
                    (nodeLane.m_Flags & (NodeLaneFlags.StartBicycleOnly | NodeLaneFlags.EndBicycleOnly)) != 0 ||
                    (EntityManager.HasComponent<CarLaneData>(prefab) &&
                        EntityManager.GetComponentData<CarLaneData>(prefab).m_RoadTypes == RoadTypes.Bicycle);
                if ((!bicycle && (carFlags & CarLaneFlags.PublicOnly) == 0) || ++count > 24) continue;
                bool accepted = TryReadJunctionLane(lane, Mod.ActiveSetting, out SurfaceTarget target, out Bezier4x3 curve, out float2 widths);
                bool circulation = accepted && target == SurfaceTarget.BikeLanes && islandRadius > 0.01f &&
                    EntityManager.HasComponent<Game.Net.Node>(node);
                float radius = 0f;
                if (circulation) circulation = TryReadCirculatingBikeArc(lane, curve, widths,
                    EntityManager.GetComponentData<Game.Net.Node>(node).m_Position, islandRadius, out radius);
                Mod.Log.Info("Junction lane metadata: node=" + node.Index + "; lane=" + lane.Index +
                    "; prefab='" + m_PrefabSystem.GetPrefabName(prefab) + "'; flags=" + data.m_Flags +
                    "; car flags=" + carFlags +
                    "; node flags=" + nodeLane.m_Flags + "; width=" + data.m_Width + "; mesh width=" + meshWidth +
                    "; width offsets=" + nodeLane.m_WidthOffset + "; separate rendered mesh=" + HasVisibleJunctionLaneMesh(lane, prefab) +
                    "; accepted dedicated path=" + accepted + "; circulation arc=" + circulation +
                    (circulation ? "; ring anchor radius=" + radius : "") + ".");
            }
        }

        private bool TryReadJunctionLane(Entity lane, Setting setting, out SurfaceTarget target,
            out Bezier4x3 curve, out float2 widths)
        {
            target = SurfaceTarget.Roads; curve = default; widths = default;
            if (setting == null) return false;
            if (!EntityManager.Exists(lane) || EntityManager.HasComponent<Deleted>(lane) || EntityManager.HasComponent<Temp>(lane) ||
                EntityManager.HasComponent<MasterLane>(lane) || !EntityManager.HasComponent<NodeLane>(lane) ||
                !EntityManager.HasComponent<Curve>(lane) ||
                !EntityManager.HasComponent<PrefabRef>(lane)) return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;
            if (!EntityManager.HasComponent<NetLaneData>(prefab)) return false;
            NetLaneData data = EntityManager.GetComponentData<NetLaneData>(prefab);
            if (!math.isfinite(data.m_Width) || data.m_Width <= 0.01f ||
                (data.m_Flags & (LaneFlags.Master | LaneFlags.Virtual | LaneFlags.Utility)) != 0) return false;
            NodeLane nodeLane = EntityManager.GetComponentData<NodeLane>(lane);
            bool driveLane = EntityManager.HasComponent<Game.Net.CarLane>(lane);
            CarLaneFlags carFlags = driveLane ?
                EntityManager.GetComponentData<Game.Net.CarLane>(lane).m_Flags : 0;
            bool bicycle = (data.m_Flags & LaneFlags.BicyclesOnly) != 0 ||
                (EntityManager.HasComponent<CarLaneData>(prefab) &&
                    EntityManager.GetComponentData<CarLaneData>(prefab).m_RoadTypes == RoadTypes.Bicycle) ||
                (nodeLane.m_Flags & (NodeLaneFlags.StartBicycleOnly | NodeLaneFlags.EndBicycleOnly)) ==
                    (NodeLaneFlags.StartBicycleOnly | NodeLaneFlags.EndBicycleOnly);
            bool bus = !bicycle && ((carFlags & CarLaneFlags.PublicOnly) != 0 || (data.m_Flags & LaneFlags.PublicOnly) != 0);
            if (!bicycle && !bus) return false;
            // Native lane finishes can be drawn by the node composition mesh.
            // A drive prefab can therefore have no gameplay SubMesh at all.
            // Its physical lane restrictions, not debug/editor mesh visibility,
            // identify the dedicated crossings and roundabout circulation.
            float width = data.m_Width;
            if (driveLane)
            {
                if ((carFlags & CarLaneFlags.Unsafe) != 0) return false;
                if (bicycle && (nodeLane.m_Flags & (NodeLaneFlags.StartBicycleOnly | NodeLaneFlags.EndBicycleOnly)) !=
                    (NodeLaneFlags.StartBicycleOnly | NodeLaneFlags.EndBicycleOnly)) return false;
            }
            else
            {
                if (!EntityManager.HasComponent<Game.Net.LaneGeometry>(lane) ||
                    !EntityManager.HasComponent<NetLaneGeometryData>(prefab) || !HasVisibleJunctionLaneMesh(lane, prefab)) return false;
                width = EntityManager.GetComponentData<NetLaneGeometryData>(prefab).m_Size.x;
            }
            if (!math.isfinite(width) || width <= 0.01f) return false;
            target = bicycle ? SurfaceTarget.BikeLanes : SurfaceTarget.BusLanes;
            // Driving lane widths come from their physical definition. Rendered
            // secondary strips use their mesh width and native scaling.
            widths = width * (1f + nodeLane.m_WidthOffset / data.m_Width);
            if (!math.all(math.isfinite(widths)) || math.any(widths <= 0.01f)) return false;
            curve = EntityManager.GetComponentData<Curve>(lane).m_Bezier;
            return math.all(math.isfinite(curve.a)) && math.all(math.isfinite(curve.b)) &&
                math.all(math.isfinite(curve.c)) && math.all(math.isfinite(curve.d));
        }

        private bool HasVisibleJunctionLaneMesh(Entity lane, Entity prefab)
        {
            if (!EntityManager.HasBuffer<Game.Prefabs.SubMesh>(prefab)) return false;
            // Match the lane mesh exclusions in BatchInstanceJob. Geometry data
            // describes the prefab's bounds even when all its meshes are hidden.
            SubMeshFlags excluded = SubMeshFlags.RequireEditor;
            bool leftHandTraffic = World.GetOrCreateSystemManaged<Game.City.CityConfigurationSystem>().leftHandTraffic;
            excluded |= leftHandTraffic ? SubMeshFlags.RequireRightHandTraffic : SubMeshFlags.RequireLeftHandTraffic;
            if (EntityManager.HasComponent<Game.Net.CarLane>(lane))
            {
                CarLaneFlags flags = EntityManager.GetComponentData<Game.Net.CarLane>(lane).m_Flags;
                if ((flags & CarLaneFlags.Unsafe) != 0) excluded |= SubMeshFlags.RequireSafe;
                if ((flags & CarLaneFlags.LevelCrossing) == 0) excluded |= SubMeshFlags.RequireLevelCrossing;
            }
            if (EntityManager.HasComponent<Game.Net.PedestrianLane>(lane) &&
                (EntityManager.GetComponentData<Game.Net.PedestrianLane>(lane).m_Flags & PedestrianLaneFlags.Unsafe) != 0)
                excluded |= SubMeshFlags.RequireSafe;
            if (EntityManager.HasComponent<Game.Net.TrackLane>(lane))
            {
                TrackLaneFlags flags = EntityManager.GetComponentData<Game.Net.TrackLane>(lane).m_Flags;
                if ((flags & TrackLaneFlags.LevelCrossing) == 0) excluded |= SubMeshFlags.RequireLevelCrossing;
                excluded |= (flags & (TrackLaneFlags.TurnLeft | TrackLaneFlags.TurnRight)) != 0 ?
                    SubMeshFlags.RequireClear : SubMeshFlags.RequireTrack;
            }
            foreach (Game.Prefabs.SubMesh mesh in EntityManager.GetBuffer<Game.Prefabs.SubMesh>(prefab, true))
                if ((mesh.m_Flags & excluded) == 0 && EntityManager.HasComponent<MeshData>(mesh.m_SubMesh) &&
                    EntityManager.GetComponentData<MeshData>(mesh.m_SubMesh).m_DefaultLayers != 0) return true;
            return false;
        }

        private JunctionLaneRoad FindJunctionLaneSource(Entity lane, SurfaceTarget target, Bezier4x3 curve,
            List<JunctionLaneRoad> roads)
        {
            int startOwner = -1, endOwner = -1;
            if (EntityManager.HasComponent<Game.Net.Lane>(lane))
            {
                Game.Net.Lane path = EntityManager.GetComponentData<Game.Net.Lane>(lane);
                startOwner = path.m_StartNode.GetOwnerIndex(); endOwner = path.m_EndNode.GetOwnerIndex();
            }
            JunctionLaneRoad result = null;
            float best = float.PositiveInfinity;
            foreach (JunctionLaneRoad road in roads)
            {
                if ((target == SurfaceTarget.BikeLanes ? road.Layout.BikeLanes : road.Layout.BusLanes).Count == 0) continue;
                float3 mouth = road.Start ? road.Curve.m_Bezier.a : road.Curve.m_Bezier.d;
                float score = math.min(math.distancesq(mouth.xz, curve.a.xz), math.distancesq(mouth.xz, curve.d.xz));
                if (road.Edge.Index == startOwner) score -= 1000000f;
                else if (road.Edge.Index == endOwner) score -= 500000f;
                if (score < best || (score == best && (result == null || road.Edge.Index < result.Edge.Index)))
                    { result = road; best = score; }
            }
            return result;
        }

        private static SurfacePatch CreateNativeLaneRibbon(Bezier4x3 curve, float2 widths, bool smallRoundabout)
        {
            System.Func<float, float3> left = t => NativeLaneBoundary(curve, widths, t, true);
            System.Func<float, float3> right = t => NativeLaneBoundary(curve, widths, t, false);
            List<float3> a = new List<float3>(), b = new List<float3>();
            foreach (float t in SampleMappedParameters(left, right)) AppendPair(a, b, left(t), right(t));
            SurfacePatch patch = CreateSmallRoundaboutStrip(a, b);
            SetLanePatchFlags(patch, smallRoundabout);
            return patch;
        }

        private static float3 NativeLaneBoundary(Bezier4x3 curve, float2 widths, float t, bool left)
        {
            float3 position = MathUtils.Position(curve, t);
            float2 tangent = MathUtils.Tangent(curve, t).xz;
            if (math.lengthsq(tangent) < 0.000001f)
                tangent = (MathUtils.Position(curve, math.min(1f, t + 0.001f)) -
                    MathUtils.Position(curve, math.max(0f, t - 0.001f))).xz;
            float2 normal = math.normalizesafe(new float2(-tangent.y, tangent.x));
            position.xz += normal * math.lerp(widths.x, widths.y, t) * (left ? 0.5f : -0.5f);
            position.y += SurfaceLift;
            return position;
        }

        private static void SetLanePatchFlags(SurfacePatch patch, bool small)
        {
            patch.SmallRoundabout = small; patch.ExactBoundary = true;
            foreach (SurfacePatch part in patch.Parts) SetLanePatchFlags(part, small);
        }

        private int MixJunctionLaneSignature(int hash, Entity node)
        {
            if (!EntityManager.HasBuffer<SubLane>(node)) return hash;
            // Shared fills may be owned by a different connected road. A local
            // upgrade changes their exclusion footprint even if no native lane
            // or road geometry changed, so refresh those owners as well.
            int upgrades = 0;
            if (EntityManager.HasBuffer<ConnectedEdge>(node))
                foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                    if (TryGetSegmentRule(connected.m_Edge, out _, out _))
                        unchecked { upgrades += MixSegmentRule(Mix(17, connected.m_Edge.Index), connected.m_Edge); }
            hash = Mix(hash, upgrades);
            int lanes = 0;
            foreach (SubLane subLane in EntityManager.GetBuffer<SubLane>(node, true))
            {
                Entity lane = subLane.m_SubLane;
                if (!EntityManager.HasComponent<NodeLane>(lane) || !EntityManager.HasComponent<Curve>(lane) ||
                    (!EntityManager.HasComponent<Game.Net.CarLane>(lane) && !EntityManager.HasComponent<Game.Net.LaneGeometry>(lane)) ||
                    !EntityManager.HasComponent<PrefabRef>(lane)) continue;
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;
                if (!EntityManager.HasComponent<NetLaneData>(prefab)) continue;
                int part = Mix(Mix(lane.Index, lane.Version), prefab.Index);
                part = MixBezier(part, EntityManager.GetComponentData<Curve>(lane).m_Bezier);
                NodeLane nodeLane = EntityManager.GetComponentData<NodeLane>(lane);
                part = Mix(part, (int)nodeLane.m_Flags);
                part = Mix(part, (int)(nodeLane.m_WidthOffset.x * 1000f));
                part = Mix(part, (int)(nodeLane.m_WidthOffset.y * 1000f));
                if (EntityManager.HasComponent<Game.Net.CarLane>(lane))
                    part = Mix(part, (int)EntityManager.GetComponentData<Game.Net.CarLane>(lane).m_Flags);
                if (EntityManager.HasComponent<NetLaneGeometryData>(prefab))
                    part = Mix(part, (int)(EntityManager.GetComponentData<NetLaneGeometryData>(prefab).m_Size.x * 1000f));
                if (EntityManager.HasComponent<NetLaneData>(prefab))
                {
                    NetLaneData data = EntityManager.GetComponentData<NetLaneData>(prefab);
                    part = Mix(part, (int)data.m_Flags); part = Mix(part, (int)(data.m_Width * 1000f));
                }
                unchecked { lanes += part; }
            }
            return Mix(hash, lanes);
        }
    }
}
