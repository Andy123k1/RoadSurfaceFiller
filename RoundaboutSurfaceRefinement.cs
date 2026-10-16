using System.Collections.Generic;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private readonly HashSet<Entity> m_LoggedRoundaboutSidewalks = new HashSet<Entity>();
        private readonly HashSet<Entity> m_LoggedSmallRoundabouts = new HashSet<Entity>();
        private readonly HashSet<Entity> m_LoggedRoundaboutShoulders = new HashSet<Entity>();
        private readonly HashSet<Entity> m_LoggedRoundaboutCurbProjection = new HashSet<Entity>();
        private bool m_LoggedRoundaboutBevels;
        private readonly Dictionary<Entity, List<LaneMaskCell>> m_RoundaboutWalkMasks = new Dictionary<Entity, List<LaneMaskCell>>();
        private int m_RoundaboutWalkMaskFrame = -1;
        private int m_LoggedRoundaboutWalkClips;

        private int MixRoundaboutReference(int hash, Entity node, EdgeNodeGeometry geometry)
        {
            if (geometry.m_MiddleRadius <= 0.01f || !EntityManager.HasBuffer<ConnectedEdge>(node)) return hash;
            foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity other = connection.m_Edge;
                if (!IsRoadEdge(other) || IsHighway(other) || !EntityManager.HasComponent<Composition>(other)) continue;
                Edge endpoints = EntityManager.GetComponentData<Edge>(other);
                Composition composition = EntityManager.GetComponentData<Composition>(other);
                Entity prefab = endpoints.m_Start == node ? composition.m_StartNode : composition.m_EndNode;
                hash = Mix(Mix(hash, prefab.Index), prefab.Version);
            }
            return hash;
        }

        private void LogSmallRoundabout(Entity node, EdgeNodeGeometry geometry, SurfaceLayout layout)
        {
            if (geometry.m_MiddleRadius > 4.01f || m_LoggedSmallRoundabouts.Count >= 8 || !m_LoggedSmallRoundabouts.Add(node)) return;
            Mod.Log.Info("Small roundabout surface geometry: node=" + node.Index + "; island radius=" + geometry.m_MiddleRadius +
                "; composition width=" + layout.Composition.m_Width + "; sidewalk bands=" + layout.Sidewalks.Count +
                "; highway approach=" + layout.HighwaySource + ". Folded strips split into simple areas; unused tails removed; native corner reach is bounded before rendering.");
        }

        private void RecoverRoundaboutSidewalks(Entity node, EdgeNodeGeometry geometry, SurfaceLayout layout)
        {
            if (!layout.HighwaySource || !EntityManager.HasBuffer<ConnectedEdge>(node)) return;
            bool recovered = false;
            for (int side = 0; side < 2; side++)
            {
                bool left = side == 0;
                bool present = false;
                foreach (LaneSpan span in layout.Sidewalks)
                    present |= left ? span.Low < layout.Composition.m_MiddleOffset : span.High > layout.Composition.m_MiddleOffset;
                if (present || !TryGetStreetRoundaboutSidewalk(node, geometry, left, out CornerInsets profile, out float height))
                    continue;
                float half = layout.Composition.m_Width * 0.5f;
                // Keep the street's physical sidewalk width. Scaling it by the
                // highway width would turn a shoulder into an oversized sidewalk.
                float low = left ? -half + profile.Near : half - profile.Far;
                float high = left ? -half + profile.Far : half - profile.Near;
                low = math.max(left ? -half : layout.Composition.m_MiddleOffset, low);
                high = math.min(left ? layout.Composition.m_MiddleOffset : half, high);
                if (high <= low + 0.01f) continue;
                layout.Sidewalks.Add(new LaneSpan(low, high, height));
                layout.RecoveredRoundaboutSides |= left ? 1 : 2;
                recovered = true;
            }
            if (!recovered) return;
            MergeSidewalkSpans(layout.Sidewalks);
            // The recovered curb is also the pavement boundary. Both materials
            // must use the same mask on the highway-owned portion of the ring.
            List<RoadBand> pavement = new List<RoadBand>(layout.Roads);
            foreach (LaneSpan sidewalk in layout.Sidewalks)
            {
                for (int i = pavement.Count - 1; i >= 0; i--)
                {
                    RoadBand band = pavement[i];
                    if (sidewalk.High <= band.Low || sidewalk.Low >= band.High) continue;
                    pavement.RemoveAt(i);
                    if (sidewalk.Low > band.Low + 0.01f)
                        pavement.Insert(i, new RoadBand(band.Low, sidewalk.Low, band.LowHeight, PavementHeight(band, sidewalk.Low)));
                    if (sidewalk.High < band.High - 0.01f)
                        pavement.Insert(i, new RoadBand(sidewalk.High, band.High, PavementHeight(band, sidewalk.High), band.HighHeight));
                }
            }
            pavement.Sort((a, b) => a.Low.CompareTo(b.Low));
            layout.Roads.Clear();
            layout.Roads.AddRange(pavement);
            if (m_LoggedRoundaboutSidewalks.Count < 8 && m_LoggedRoundaboutSidewalks.Add(node))
                Mod.Log.Info("Recovered street sidewalk boundaries on a highway-connected roundabout. Node=" + node.Index +
                    "; island radius=" + geometry.m_MiddleRadius + "; sidewalk bands=" + layout.Sidewalks.Count + ".");
        }

        private static float PavementHeight(RoadBand band, float lateral)
        {
            return math.lerp(band.LowHeight, band.HighHeight, math.saturate((lateral - band.Low) / math.max(0.01f, band.High - band.Low)));
        }

        private bool HasRoundaboutHighwayConnection(Entity node)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return false;
            foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                if (IsRoadEdge(connection.m_Edge) && IsHighway(connection.m_Edge)) return true;
            return false;
        }

        private SurfacePatch CreateRoundaboutPavement(Entity owner, Entity node, EdgeNodeGeometry geometry,
            SurfaceLayout layout, SurfaceLayout approach, float3 center, RoadBand band, bool left, bool start)
        {
            // Start with the complete native pavement/side footprint, then
            // remove the same physical walking footprints used by rendering.
            // Starting at a lane-profile inset can discard real shoulder rim
            // before clipping has a chance to find the physical curb.
            float curb = (left ? -0.5f : 0.5f) * layout.Composition.m_Width;
            float height = left ? band.LowHeight : band.HighHeight;
            bool raised = TryGetRaisedRoundaboutCurb(layout, left, height, out float sidewalkHeight);
            bool recovered = (layout.RecoveredRoundaboutSides & (left ? 1 : 2)) != 0;
            // Keep the recovered highway entry's physical pavement endpoints.
            // Every other curb is resolved by subtracting walking footprints.
            CornerInsets? mouth = GetRoundaboutMouth(owner, node, approach, layout, band, left, false, start);
            Entity roadPrefab = EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab;
            float defaultHeight = EntityManager.HasComponent<NetGeometryData>(roadPrefab) ?
                EntityManager.GetComponentData<NetGeometryData>(roadPrefab).m_DefaultHeightRange.max : height;
            float island = PhysicalRoundaboutIslandRadius(node, geometry.m_MiddleRadius, defaultHeight);
            SurfacePatch patch = CreateRoundaboutRoadPolygon(geometry, layout.Composition, center, curb, height, left, 0f,
                mouth, physicalIslandRadius: island);
            // Protect physical sidewalks even when no sidewalk rule exists or
            // its material draws below the pavement material. Use the same
            // native contours as the sidewalk renderer, in addition to height.
            List<LaneMaskCell> masks = new List<LaneMaskCell>(GetRoundaboutSidewalkMasks(node));
            if (m_ActiveJunctionLaneProfile != null) masks.AddRange(m_ActiveJunctionLaneProfile.Cells);
            if (m_ActiveJunctionPlantMasks != null) masks.AddRange(m_ActiveJunctionPlantMasks);
            // One mesh-union / difference pass removes internal ribbon edges
            // before clipping. Folded approach meshes are valid union inputs.
            SurfacePatch sweepInput = patch.Parts.Count > 0 ? FlattenSurfacePatchMesh(patch) : patch;
            patch = TryClipSurfaceFootprint(patch, masks, out SurfacePatch compact) ||
                TrySubtractLaneContoursLegacy(sweepInput, masks, out compact) ||
                TrySweepLaneMasks(sweepInput, masks, out compact) ? compact : SubtractLaneMasks(patch, masks);
            MarkLaneMasksApplied(patch);
            MarkPlantedMasksApplied(patch);
            if (m_LoggedRoundaboutWalkClips++ < 16)
                Mod.Log.Info("Roundabout curb clipping: node=" + node.Index + "; left=" + left + "; sidewalk mask cells=" +
                    GetRoundaboutSidewalkMasks(node).Count + "; recovered highway curb=" + recovered +
                    "; compact pavement areas=" + CountSurfacePatchAreas(patch) + ".");
            if (raised && !layout.MixedRoundabout)
            {
                // Geometry already excludes the walking plateau. A midpoint
                // height cutoff also rejects the native raised asphalt rim and
                // leaves an unpainted band even with matching curb contours.
                // Stop immediately below the walking plateau, rather than
                // midway up the curb. This reaches the lower asphalt apron
                // without relying on material priority to protect sidewalks.
                patch.ProjectionMaximum = sidewalkHeight - height - SurfaceLift - 0.003f;
                if (m_LoggedRoundaboutCurbProjection.Count < 8 && m_LoggedRoundaboutCurbProjection.Add(node))
                    Mod.Log.Info("Roundabout protects native sidewalk contours and separate curb projection volumes. Node=" + node.Index +
                        "; road height=" + height + "; sidewalk height=" + sidewalkHeight +
                        "; curb separation=" + (sidewalkHeight - height) +
                        " m. Pavement reaches the native apron and stops below the raised walking deck.");
            }
            // Mixed highway nodes can have a flat paved shoulder under their
            // recovered sidewalk. Its protection comes from the shared planar
            // footprint, independent of the selected material's height behavior.
            if (m_LoggedPhysicalRoundaboutCurbs.Count < 8 && m_LoggedPhysicalRoundaboutCurbs.Add(node))
                Mod.Log.Info("Physical roundabout pavement curb: node=" + node.Index + "; traffic clearance radius=" +
                    geometry.m_MiddleRadius + "; physical island radius=" + island +
                    "; native left curb targets=" + geometry.m_SyncVertexTargetsLeft +
                    "; native right curb targets=" + geometry.m_SyncVertexTargetsRight + ".");
            return patch;
        }

        private static bool TryGetRaisedRoundaboutCurb(SurfaceLayout layout, bool left, float roadHeight, out float sidewalkHeight)
        {
            sidewalkHeight = float.PositiveInfinity;
            foreach (LaneSpan span in layout.Sidewalks)
                if (left ? span.Low < layout.Composition.m_MiddleOffset : span.High > layout.Composition.m_MiddleOffset)
                    sidewalkHeight = math.min(sidewalkHeight, span.Height);
            // Flat pedestrian/service lanes cannot separate two materials by
            // height. Retain the bounded geometric mask for those custom assets.
            return math.isfinite(sidewalkHeight) && sidewalkHeight - roadHeight > 0.12f;
        }

        private static void RestoreLaneMaskState(SurfacePatch patch, bool clipped)
        {
            patch.DedicatedLanesClipped = clipped;
            foreach (SurfacePatch part in patch.Parts) RestoreLaneMaskState(part, clipped);
        }

        private static int CountSurfacePatchAreas(SurfacePatch patch)
        {
            if (patch.Parts.Count == 0) return patch.Triangles.Count > 0 ? 1 : 0;
            int count = 0; foreach (SurfacePatch part in patch.Parts) count += CountSurfacePatchAreas(part); return count;
        }

        private List<LaneMaskCell> GetRoundaboutSidewalkMasks(Entity node)
        {
            if (m_RoundaboutWalkMaskFrame != m_Frame)
            {
                m_RoundaboutWalkMasks.Clear(); m_RoundaboutWalkMaskFrame = m_Frame;
            }
            if (m_RoundaboutWalkMasks.TryGetValue(node, out List<LaneMaskCell> cached)) return cached;
            List<LaneMaskCell> cells = new List<LaneMaskCell>();
            m_RoundaboutWalkMasks[node] = cells;
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return cells;
            foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity edge = connected.m_Edge;
                if (!IsRoadEdge(edge) || !EntityManager.HasComponent<Composition>(edge)) continue;
                bool start = EntityManager.GetComponentData<Edge>(edge).m_Start == node;
                if (start ? !EntityManager.HasComponent<StartNodeGeometry>(edge) : !EntityManager.HasComponent<EndNodeGeometry>(edge)) continue;
                Composition composition = EntityManager.GetComponentData<Composition>(edge);
                if (!TryReadSurfaceLayout(composition.m_Edge, out SurfaceLayout approach, highway: IsHighway(edge), streetContext: true) ||
                    !TryReadSurfaceLayout(start ? composition.m_StartNode : composition.m_EndNode, out SurfaceLayout layout,
                        approach, start, streetContext: true)) continue;
                EdgeNodeGeometry geometry = start ? EntityManager.GetComponentData<StartNodeGeometry>(edge).m_Geometry :
                    EntityManager.GetComponentData<EndNodeGeometry>(edge).m_Geometry;
                if (geometry.m_MiddleRadius <= 0.01f) continue;
                layout.MixedRoundabout = HasRoundaboutHighwayConnection(node);
                RecoverRoundaboutSidewalks(node, geometry, layout);
                foreach (LaneSpan sidewalk in layout.Sidewalks)
                {
                    if (layout.Roads.Count > 0 && sidewalk.High > layout.Roads[0].Low + 0.01f &&
                        sidewalk.Low < layout.Roads[layout.Roads.Count - 1].High - 0.01f) continue;
                    bool left = sidewalk.Low + sidewalk.High < layout.Composition.m_MiddleOffset * 2f;
                    RoadBand band = new RoadBand(sidewalk.Low, sidewalk.High, sidewalk.Height, sidewalk.Height);
                    // Paint and exclusion must use identical world contours,
                    // including flat highway portions and every cycler choice.
                    SurfacePatch walk = CreateRoundaboutWalkFootprint(edge, node, geometry, approach, layout, band, left, start);
                    AddLaneMaskCells(walk, cells);
                }
            }
            foreach (LaneMaskCell cell in cells)
            {
                cell.Minimum.y = float.NegativeInfinity; cell.Maximum.y = float.PositiveInfinity;
            }
            return cells;
        }

        private static void LimitRoundaboutSidewalkProjection(SurfacePatch patch, SurfaceLayout layout, RoadBand band, bool left)
        {
            if (layout.MixedRoundabout) return;
            if (layout.Roads.Count == 0) return;
            float roadHeight = left ? layout.Roads[0].LowHeight : layout.Roads[layout.Roads.Count - 1].HighHeight;
            if (TryGetRaisedRoundaboutCurb(layout, left, roadHeight, out float sidewalkHeight))
                patch.ProjectionMinimum = (roadHeight + sidewalkHeight) * 0.5f - band.LowHeight - SurfaceLift;
        }

        private CornerInsets? GetRoundaboutMouth(Entity owner, Entity node, SurfaceLayout approach, SurfaceLayout layout,
            RoadBand band, bool left, bool sidewalk, bool start)
        {
            if ((layout.RecoveredRoundaboutSides & (left ? 1 : 2)) == 0) return null;
            // The street profile describes the circular sidewalk, while the
            // highway approach still has full-width pavement at the handoff.
            // Pin both materials to the same physical edge endpoint there.
            CornerInsets mouth = GetJunctionMouth(owner, approach, layout, band, left, false, start);
            float3 position = mouth.NearPosition;
            if (sidewalk && layout.Roads.Count > 0)
            {
                float pavementHeight = left ? layout.Roads[0].LowHeight : layout.Roads[layout.Roads.Count - 1].HighHeight;
                position.y += band.LowHeight - pavementHeight;
            }
            mouth.Near = mouth.Far = 0f;
            mouth.NearPosition = mouth.FarPosition = position;
            if (m_LoggedRoundaboutShoulders.Count < 8 && m_LoggedRoundaboutShoulders.Add(node))
                Mod.Log.Info("Roundabout highway entry preserves full pavement width at the material handoff and tapers to the ring curb. Node=" +
                    node.Index + "; highway composition width=" + approach.Composition.m_Width + "; mouth curb inset=0 m.");
            return mouth;
        }

        private bool TryGetStreetRoundaboutSidewalk(Entity node, EdgeNodeGeometry geometry, bool left,
            out CornerInsets profile, out float height)
        {
            profile = default;
            height = 0f;
            float3 joint = left ? geometry.m_Right.m_Left.d : geometry.m_Right.m_Right.d;
            float bestScore = float.MinValue;
            foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity other = connection.m_Edge;
                if (!IsRoadEdge(other) || IsHighway(other) || !EntityManager.HasComponent<Composition>(other)) continue;
                Edge endpoints = EntityManager.GetComponentData<Edge>(other);
                bool start = endpoints.m_Start == node;
                if (!start && endpoints.m_End != node) continue;
                if (start ? !EntityManager.HasComponent<StartNodeGeometry>(other) : !EntityManager.HasComponent<EndNodeGeometry>(other)) continue;
                EdgeNodeGeometry otherGeometry = start ? EntityManager.GetComponentData<StartNodeGeometry>(other).m_Geometry :
                    EntityManager.GetComponentData<EndNodeGeometry>(other).m_Geometry;
                Composition composition = EntityManager.GetComponentData<Composition>(other);
                TryReadSurfaceLayout(composition.m_Edge, out SurfaceLayout approach);
                if (!TryReadSurfaceLayout(start ? composition.m_StartNode : composition.m_EndNode,
                    out SurfaceLayout street, approach, start, streetContext: true)) continue;
                for (int side = 0; side < 2; side++)
                {
                    bool otherLeft = side == 0;
                    float3 otherJoint = otherLeft ? otherGeometry.m_Right.m_Left.d : otherGeometry.m_Right.m_Right.d;
                    float distance = math.distancesq(joint.xz, otherJoint.xz);
                    // Prefer the street sharing this exact perimeter join. If a
                    // highway borders another highway, use a connected street's
                    // outside sidewalk profile for that portion of the same ring.
                    float score = (distance < 0.04f ? 1000000f : 0f) + street.Composition.m_Width * 1000f - distance;
                    if (score <= bestScore) continue;
                    LaneSpan? outer = null;
                    foreach (LaneSpan sidewalk in street.Sidewalks)
                    {
                        if (otherLeft ? sidewalk.Low >= street.Composition.m_MiddleOffset : sidewalk.High <= street.Composition.m_MiddleOffset) continue;
                        if (!outer.HasValue || (otherLeft ? sidewalk.Low < outer.Value.Low : sidewalk.High > outer.Value.High)) outer = sidewalk;
                    }
                    if (!outer.HasValue) continue;
                    LaneSpan span = outer.Value;
                    profile = BandInsets(street.Composition, new RoadBand(span.Low, span.High, span.Height, span.Height), otherLeft);
                    // Borrow the rendered width at this shared world-space
                    // join, rather than the street's nominal cross-section.
                    // Native roundabout vertex targets deform that width.
                    float3 center = EntityManager.GetComponentData<Game.Net.Node>(node).m_Position;
                    float radius = math.distance(otherJoint.xz, center.xz);
                    float near = RoundaboutMappedCurbInset(otherGeometry, street.Composition, otherLeft, profile.Near, radius);
                    float far = RoundaboutMappedCurbInset(otherGeometry, street.Composition, otherLeft, profile.Far, radius);
                    if (math.isfinite(near) && math.isfinite(far) && far > near + 0.01f)
                    { profile.Near = near; profile.Far = far; }
                    height = span.Height;
                    bestScore = score;
                }
            }
            return bestScore > float.MinValue;
        }

        private static SurfacePatch BevelRoundaboutTips(SurfacePatch source, float sharpDot = 0.97f)
        {
            int count = source.Nodes.Count - 1; // CloseStrip repeats the first node.
            if (count < 3 || source.Triangles.Count == 0) return source;
            int[] first = new int[count], second = new int[count];
            bool[] bevel = new bool[count];
            SurfacePatch result = new SurfacePatch
            {
                ProjectionMinimum = source.ProjectionMinimum,
                ProjectionMaximum = source.ProjectionMaximum,
                SmallRoundabout = source.SmallRoundabout,
                ExactBoundary = source.ExactBoundary
            };
            for (int i = 0; i < count; i++)
            {
                float3 point = source.Nodes[i].m_Position;
                float3 previous = source.Nodes[(i + count - 1) % count].m_Position;
                float3 next = source.Nodes[(i + 1) % count].m_Position;
                float2 toPrevious = previous.xz - point.xz, toNext = next.xz - point.xz;
                float previousLength = math.length(toPrevious), nextLength = math.length(toNext);
                float3 a = point, b = point;
                // The native renderer adds expandAmount / tan(angle / 2) at
                // boundary tips. A nearly parallel return can extend hundreds
                // of metres on a small island. Replace it with a short bevel.
                if (previousLength > 0.001f && nextLength > 0.001f &&
                    math.dot(toPrevious / previousLength, toNext / nextLength) > sharpDot)
                {
                    float cut = math.min(0.12f, math.min(previousLength, nextLength) * 0.4f);
                    a = math.lerp(point, previous, cut / previousLength);
                    b = math.lerp(point, next, cut / nextLength);
                    bevel[i] = math.distancesq(a.xz, b.xz) > 0.00000001f &&
                        math.distancesq(a.xz, point.xz) > 0.00000001f && math.distancesq(b.xz, point.xz) > 0.00000001f;
                }
                first[i] = result.Nodes.Count;
                result.Nodes.Add(new AreaNode(bevel[i] ? a : point, 0f));
                second[i] = first[i];
                if (bevel[i])
                {
                    second[i] = result.Nodes.Count;
                    result.Nodes.Add(new AreaNode(b, 0f));
                    result.BevelCount++;
                }
            }
            if (result.BevelCount == 0) return source;
            foreach (int3 triangle in source.Triangles)
                result.Triangles.Add(new int3(first[triangle.x], first[triangle.y], first[triangle.z]));
            for (int i = 0; i < count; i++)
            {
                if (!bevel[i]) continue;
                int a = first[i], b = second[i], next = first[(i + 1) % count];
                for (int j = 0; j < result.Triangles.Count; j++)
                {
                    int3 triangle = result.Triangles[j];
                    if (!ContainsIndex(triangle, a) || !ContainsIndex(triangle, next)) continue;
                    int third = triangle.x != a && triangle.x != next ? triangle.x : triangle.y != a && triangle.y != next ? triangle.y : triangle.z;
                    result.Triangles.RemoveAt(j);
                    // Retain the original local ribbon triangulation, splitting
                    // only the triangle on this newly shortened boundary edge.
                    AddBevelTriangle(result, new int3(a, b, third));
                    AddBevelTriangle(result, new int3(b, next, third));
                    break;
                }
            }
            for (int i = result.Triangles.Count - 1; i >= 0; i--)
            {
                int3 triangle = result.Triangles[i];
                float2 a = result.Nodes[triangle.y].m_Position.xz - result.Nodes[triangle.x].m_Position.xz;
                float2 b = result.Nodes[triangle.z].m_Position.xz - result.Nodes[triangle.x].m_Position.xz;
                float cross = a.x * b.y - a.y * b.x;
                if (math.abs(cross) <= 0.000000001f) result.Triangles.RemoveAt(i);
                else if (cross < 0f) result.Triangles[i] = triangle.xzy;
            }
            result.Nodes.Add(result.Nodes[0]);
            return result;
        }

        private static bool ContainsIndex(int3 triangle, int index)
        {
            return triangle.x == index || triangle.y == index || triangle.z == index;
        }

        private static void AddBevelTriangle(SurfacePatch patch, int3 indices)
        {
            float2 a = patch.Nodes[indices.y].m_Position.xz - patch.Nodes[indices.x].m_Position.xz;
            float2 b = patch.Nodes[indices.z].m_Position.xz - patch.Nodes[indices.x].m_Position.xz;
            float cross = a.x * b.y - a.y * b.x;
            if (math.abs(cross) > 0.000000001f) patch.Triangles.Add(cross > 0f ? indices : indices.xzy);
        }
    }
}
