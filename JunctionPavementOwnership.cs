using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;
using PedestrianLane = Game.Net.PedestrianLane;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private sealed class JunctionPavementDomain
        {
            internal Entity Owner;
            internal SurfacePatch Interior;
            internal readonly Dictionary<Entity, CurbCut> Mouths = new Dictionary<Entity, CurbCut>();
            internal readonly List<LaneMaskCell> Cells = new List<LaneMaskCell>();
            internal readonly List<LaneMaskCell> ProtectedCells = new List<LaneMaskCell>();
        }
        private readonly Dictionary<Entity, JunctionPavementDomain> m_JunctionPavementDomains = new Dictionary<Entity, JunctionPavementDomain>();
        private int m_JunctionDomainFrame = -1, m_LoggedCrosswalkDomains;

        private JunctionPavementDomain GetJunctionPavementDomain(Entity node)
        {
            if (m_JunctionDomainFrame != m_GenerationCacheEpoch) { m_JunctionPavementDomains.Clear(); m_JunctionDomainFrame = m_GenerationCacheEpoch; }
            if (m_JunctionPavementDomains.TryGetValue(node, out JunctionPavementDomain cached)) return cached;
            m_JunctionPavementDomains[node] = null;
            if (!EntityManager.HasBuffer<ConnectedEdge>(node) || IsRoundaboutJunction(node)) return null;
            List<JunctionOutline> outlines = new List<JunctionOutline>();
            SurfacePatch mesh = new SurfacePatch { ExactBoundary = true };
            JunctionPavementDomain domain = new JunctionPavementDomain();
            List<float3> centers = new List<float3>();
            Dictionary<Entity, SurfacePatch> approachPatches = new Dictionary<Entity, SurfacePatch>();
            foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity edge = connected.m_Edge;
                if (!IsRoadEdge(edge) || !EntityManager.HasComponent<EdgeGeometry>(edge)) continue;
                bool start = EntityManager.GetComponentData<Edge>(edge).m_Start == node;
                if (start ? !EntityManager.HasComponent<StartNodeGeometry>(edge) : !EntityManager.HasComponent<EndNodeGeometry>(edge)) return null;
                Composition composition = EntityManager.GetComponentData<Composition>(edge);
                if (!TryReadSurfaceLayout(composition.m_Edge, out SurfaceLayout approach, highway: IsHighway(edge)) ||
                    !TryReadSurfaceLayout(start ? composition.m_StartNode : composition.m_EndNode, out SurfaceLayout layout,
                        approach, start, streetContext: true) || layout.Roads.Count == 0) return null;
                EdgeNodeGeometry geometry = start ? EntityManager.GetComponentData<StartNodeGeometry>(edge).m_Geometry :
                    EntityManager.GetComponentData<EndNodeGeometry>(edge).m_Geometry;
                RoadBand outer = new RoadBand(layout.Roads[0].Low, layout.Roads[layout.Roads.Count - 1].High,
                    layout.Roads[0].LowHeight, layout.Roads[layout.Roads.Count - 1].HighHeight);
                outlines.Add(new JunctionOutline {
                    Left = JunctionCurbBoundary(edge, node, geometry, approach, layout, outer, true, start, false, default),
                    Right = JunctionCurbBoundary(edge, node, geometry, approach, layout, outer, false, start, false, default)
                });
                SurfacePatch approachPatch = new SurfacePatch { ExactBoundary = true };
                foreach (RoadBand band in layout.Roads)
                    for (int side = 0; side < 2; side++)
                    {
                        bool left = side == 0;
                        SurfacePatch part = CreateJunctionSidePolygon(geometry, layout.Composition, band, left,
                            GetJunctionMouth(edge, approach, layout, band, left, false, start),
                            GetCornerInsets(edge, node, geometry, layout, band, left, false, false));
                        mesh.Parts.Add(part); approachPatch.Parts.Add(part);
                    }
                approachPatches[edge] = approachPatch;
                centers.Add(Lift(geometry.m_Middle.d, (outer.LowHeight + outer.HighHeight) * 0.5f));
                domain.Mouths[edge] = JunctionCrosswalkCut(edge, node, start);
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                Setting setting = Mod.ActiveSetting;
                if (setting != null && PassesSegmentFilter(edge, prefab, setting) && PassesRoadFilter(edge, prefab, setting) &&
                    PassesElevationFilter(edge, prefab, setting) && (domain.Owner == Entity.Null || edge.Index < domain.Owner.Index)) domain.Owner = edge;
            }
            if (outlines.Count < 3 || domain.Owner == Entity.Null) return null;
            SurfacePatch interior = TryTraceJunctionOutline(outlines, out List<float3> boundary) ? TriangulateBoundary(boundary) : new SurfacePatch();
            if (interior.Triangles.Count == 0)
            {
                // Keep the native perimeter. Only the small center join between
                // native middle curves is completed; no outer convex hull.
                SurfacePatch center = TriangulateBoundary(JunctionCenterHull(centers));
                if (center.Triangles.Count > 0) mesh.Parts.Add(center);
                interior = FlattenSurfacePatchMesh(mesh);
            }
            // A crosswalk plane is local to its approach. Intersecting every
            // plane across the entire node removes wedges of OTHER approaches
            // at skewed junctions, without any surface left to own those wedges.
            // Subtract the finite native approach footprints instead. Their
            // exact complement receives the one selected junction finish.
            List<LaneMaskCell> approachCells = new List<LaneMaskCell>();
            foreach (KeyValuePair<Entity, SurfacePatch> entry in approachPatches)
                AddLaneMaskCells(ClipSurfacePatch(entry.Value, domain.Mouths[entry.Key], true), approachCells);
            // Ownership is planar; a different triangulation of the same
            // sloped junction must not defeat the cut's height-overlap test.
            foreach (LaneMaskCell cell in approachCells)
            { cell.Minimum.y = float.NegativeInfinity; cell.Maximum.y = float.PositiveInfinity; }
            interior = SubtractLaneMasks(interior, approachCells);
            List<LaneMaskCell> protectedCells = ReadOrdinaryJunctionSidewalkMasks(node);
            protectedCells.AddRange(GetJunctionPlantMasks(node));
            domain.ProtectedCells.AddRange(protectedCells);
            interior = SubtractLaneMasks(interior, protectedCells);
            MarkLaneMasksApplied(interior); MarkPlantedMasksApplied(interior);
            interior.ExactBoundary = true; interior.ProjectionMaximum = float.PositiveInfinity;
            AddLaneMaskCells(interior, domain.Cells);
            foreach (LaneMaskCell cell in domain.Cells)
            { cell.Minimum.y = float.NegativeInfinity; cell.Maximum.y = float.PositiveInfinity; }
            if (domain.Cells.Count == 0) return null;
            domain.Interior = interior; m_JunctionPavementDomains[node] = domain;
            if (m_LoggedCrosswalkDomains++ < 16)
                Mod.Log.Info("Crosswalk-bounded junction pavement: node=" + node.Index + "; owner=" + domain.Owner.Index +
                    "; approach boundaries=" + domain.Mouths.Count + "; interior areas=" + CountSurfacePatchAreas(interior) + ".");
            return domain;
        }

        private CurbCut JunctionCrosswalkCut(Entity edge, Entity node, bool start)
        {
            Curve curve = EntityManager.GetComponentData<Curve>(edge);
            float2 direction = math.normalizesafe((start ? MathUtils.StartTangent(curve.m_Bezier) : -MathUtils.EndTangent(curve.m_Bezier)).xz);
            EdgeGeometry geometry = EntityManager.GetComponentData<EdgeGeometry>(edge);
            float3 mouth = start ? (geometry.m_Start.m_Left.a + geometry.m_Start.m_Right.a) * 0.5f :
                (geometry.m_End.m_Left.d + geometry.m_End.m_Right.d) * 0.5f;
            CurbCut result = new CurbCut { Normal = direction, Distance = math.dot(direction, mouth.xz) };
            float best = float.PositiveInfinity;
            if (!EntityManager.HasBuffer<SubLane>(node)) return result;
            foreach (SubLane connected in EntityManager.GetBuffer<SubLane>(node, true))
            {
                Entity lane = connected.m_SubLane;
                if (!EntityManager.HasComponent<PedestrianLane>(lane) || !EntityManager.HasComponent<Curve>(lane) ||
                    (EntityManager.GetComponentData<PedestrianLane>(lane).m_Flags & PedestrianLaneFlags.Crosswalk) == 0) continue;
                Bezier4x3 crossing = EntityManager.GetComponentData<Curve>(lane).m_Bezier;
                if (CrosswalkApproach(lane, node, crossing) != edge) continue;
                float2 axis = math.normalizesafe(crossing.d.xz - crossing.a.xz);
                float2 normal = new float2(-axis.y, axis.x);
                if (math.dot(normal, direction) < 0f) normal = -normal;
                float3 position = MathUtils.Position(crossing, 0.5f);
                float score = math.distancesq(position.xz, mouth.xz);
                if (score >= best) continue;
                float width = 0f;
                if (EntityManager.HasComponent<PrefabRef>(lane))
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;
                    if (EntityManager.HasComponent<NetLaneGeometryData>(prefab)) width = EntityManager.GetComponentData<NetLaneGeometryData>(prefab).m_Size.x;
                    if (width <= 0f && EntityManager.HasComponent<NetLaneData>(prefab)) width = EntityManager.GetComponentData<NetLaneData>(prefab).m_Width;
                }
                // The approach finish reaches the outside edge of the marking;
                // the entire crossing and its interior share junction pavement.
                result = new CurbCut { Normal = normal, Distance = math.dot(normal, position.xz) + math.max(0f, width) * 0.5f };
                best = score;
            }
            return result;
        }

        private Entity CrosswalkApproach(Entity lane, Entity node, Bezier4x3 crossing)
        {
            // An acute-angle neighbor's crossing can be closer to the native
            // mouth. Assign each crossing to ONE connected road using native
            // path owners first, then its alignment and mouth position.
            int startOwner = -1, endOwner = -1;
            if (EntityManager.HasComponent<Game.Net.Lane>(lane))
            {
                Game.Net.Lane path = EntityManager.GetComponentData<Game.Net.Lane>(lane);
                startOwner = path.m_StartNode.GetOwnerIndex(); endOwner = path.m_EndNode.GetOwnerIndex();
            }
            List<Entity> roads = new List<Entity>(), owned = new List<Entity>();
            foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                if (IsRoadEdge(connected.m_Edge) && EntityManager.HasComponent<EdgeGeometry>(connected.m_Edge))
                {
                    roads.Add(connected.m_Edge);
                    if (connected.m_Edge.Index == startOwner || connected.m_Edge.Index == endOwner) owned.Add(connected.m_Edge);
                }
            if (owned.Count > 0) roads = owned;
            float2 axis = math.normalizesafe(crossing.d.xz - crossing.a.xz);
            float2 normal = new float2(-axis.y, axis.x);
            float2 position = MathUtils.Position(crossing, 0.5f).xz;
            float bestAlignment = -1f;
            foreach (Entity road in roads)
            {
                Curve curve = EntityManager.GetComponentData<Curve>(road);
                bool start = EntityManager.GetComponentData<Edge>(road).m_Start == node;
                float2 direction = math.normalizesafe((start ? MathUtils.StartTangent(curve.m_Bezier) : -MathUtils.EndTangent(curve.m_Bezier)).xz);
                bestAlignment = math.max(bestAlignment, math.abs(math.dot(normal, direction)));
            }
            Entity result = Entity.Null; float bestDistance = float.PositiveInfinity;
            foreach (Entity road in roads)
            {
                Curve curve = EntityManager.GetComponentData<Curve>(road);
                bool start = EntityManager.GetComponentData<Edge>(road).m_Start == node;
                float2 direction = math.normalizesafe((start ? MathUtils.StartTangent(curve.m_Bezier) : -MathUtils.EndTangent(curve.m_Bezier)).xz);
                if (math.abs(math.dot(normal, direction)) < bestAlignment - 0.02f) continue;
                EdgeGeometry geometry = EntityManager.GetComponentData<EdgeGeometry>(road);
                float2 mouth = (start ? (geometry.m_Start.m_Left.a + geometry.m_Start.m_Right.a) :
                    (geometry.m_End.m_Left.d + geometry.m_End.m_Right.d)).xz * 0.5f;
                float distance = math.distancesq(position, mouth);
                if (distance < bestDistance || (distance == bestDistance && (result == Entity.Null || road.Index < result.Index)))
                { result = road; bestDistance = distance; }
            }
            return result;
        }

        private static bool TryTraceJunctionOutline(List<JunctionOutline> outlines, out List<float3> boundary)
        {
            boundary = new List<float3>(); HashSet<int> visited = new HashSet<int>(); int current = 0;
            while (visited.Add(current))
            {
                JunctionOutline item = outlines[current]; boundary.AddRange(item.Right);
                if (item.Right.Count == 0) return false;
                float3 joint = item.Right[item.Right.Count - 1]; int next = -1; float closest = 0.04f;
                for (int i = 0; i < outlines.Count; i++)
                {
                    if (i == current || outlines[i].Left.Count == 0) continue;
                    float distance = math.distancesq(joint.xz, outlines[i].Left[outlines[i].Left.Count - 1].xz);
                    if (distance < closest) { closest = distance; next = i; }
                }
                if (next < 0) return false;
                for (int i = outlines[next].Left.Count - 1; i >= 0; i--) boundary.Add(outlines[next].Left[i]);
                current = next; if (current == 0) break;
            }
            return current == 0 && visited.Count == outlines.Count;
        }
        private static List<float3> JunctionCenterHull(List<float3> points)
        {
            points.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.z.CompareTo(b.z));
            List<float3> hull = new List<float3>();
            for (int pass = 0; pass < 2; pass++)
            {
                int first = hull.Count;
                for (int i = 0; i < points.Count; i++)
                {
                    float3 point = points[pass == 0 ? i : points.Count - 1 - i];
                    while (hull.Count >= first + 2 && Cross2(hull[hull.Count - 1].xz - hull[hull.Count - 2].xz,
                        point.xz - hull[hull.Count - 1].xz) <= 0f) hull.RemoveAt(hull.Count - 1);
                    hull.Add(point);
                }
                if (hull.Count > 0) hull.RemoveAt(hull.Count - 1);
            }
            return hull;
        }
        private SurfacePatch ClipApproachEdgeAtCrosswalks(Entity edge, SurfacePatch patch)
        {
            Edge ends = EntityManager.GetComponentData<Edge>(edge);
            foreach (Entity node in new[] { ends.m_Start, ends.m_End })
            {
                JunctionPavementDomain domain = GetJunctionPavementDomain(node);
                if (domain != null && domain.Mouths.TryGetValue(edge, out CurbCut cut)) patch = ClipSurfacePatch(patch, cut, true);
                else if (JunctionCoversBusLanes(node))
                    patch = ClipSurfacePatch(patch, JunctionCrosswalkCut(edge, node, ends.m_Start == node), true);
            }
            return patch;
        }
        private void QueueCrosswalkJunction(Entity owner, Entity node, SurfaceChoice surface, JunctionPavementDomain domain,
            EdgeNodeGeometry geometry, SurfaceLayout approach, SurfaceLayout layout, Setting setting, bool start)
        {
            if (surface != null && domain.Owner == owner)
            {
                // Keep the cached ownership domain intact: shoulder fills use
                // it to stop at crosswalks. Dedicated bike and bus paths remain
                // visible through the selected junction pavement.
                SurfacePatch interior = CopySurfacePatch(domain.Interior);
                // QueuePolygon enforces continuous lane exclusions even when
                // crosswalk ownership bypasses broad shoulder-fill masks.
                MarkLaneMasksApplied(interior);
                QueuePolygon(owner, surface.Prefab, interior);
            }
            if (!domain.Mouths.TryGetValue(owner, out CurbCut cut)) return;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab;
            Curve curve = EntityManager.GetComponentData<Curve>(owner);
            SurfaceChoice own = Choose(GetChoices(setting, SurfaceTarget.Roads, prefab, segment: owner), m_PrefabSystem.GetPrefabName(prefab), curve, 0);
            if (own == null) return;
            foreach (RoadBand band in GetRoadPaintBands(layout, setting))
                for (int side = 0; side < 2; side++)
                {
                    bool left = side == 0;
                    SurfacePatch patch = CreateJunctionSidePolygon(geometry, layout.Composition, band, left,
                        GetJunctionMouth(owner, approach, layout, band, left, false, start),
                        GetCornerInsets(owner, node, geometry, layout, band, left, false, false));
                    patch = SubtractLaneMasks(ClipSurfacePatch(patch, cut, true), domain.ProtectedCells);
                    RestoreLaneMaskState(patch, false); MarkPlantedMasksApplied(patch);
                    patch.ProjectionMaximum = float.PositiveInfinity;
                    QueuePolygon(owner, own.Prefab, patch);
                }
        }
    }
}
