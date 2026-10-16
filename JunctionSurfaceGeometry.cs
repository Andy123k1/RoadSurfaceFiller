using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private readonly HashSet<Entity> m_LoggedCurbCuts = new HashSet<Entity>();
        private readonly HashSet<Entity> m_LoggedSharedJunctions = new HashSet<Entity>();
        private int m_LoggedJunctionClipFallbacks;

        private struct CurbCut
        {
            internal float2 Normal;
            internal float Distance;
            internal float Side(float3 position) => math.dot(Normal, position.xz) - Distance;
        }

        private sealed class JunctionOutline
        {
            internal List<float3> Left, Right;
        }

        private bool TryGetCurbCut(Entity edge, Entity node, Entity mainPrefab, out CurbCut cut)
        {
            cut = default;
            if (mainPrefab == Entity.Null || IsHighway(edge) ||
                EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab == mainPrefab ||
                !EntityManager.HasBuffer<ConnectedEdge>(node)) return false;
            Edge endpoints = EntityManager.GetComponentData<Edge>(edge);
            Curve branch = EntityManager.GetComponentData<Curve>(edge);
            float2 branchDirection = math.normalizesafe((endpoints.m_Start == node
                ? MathUtils.StartTangent(branch.m_Bezier) : -MathUtils.EndTangent(branch.m_Bezier)).xz);
            float2 axis = default, normal = default;
            float distance = 0f;
            int count = 0;
            // Use the main road's physical pavement endpoints, including its
            // shoulder. The native branch mouth is not necessarily its curb line.
            foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity main = connection.m_Edge;
                if (!IsRoadEdge(main) || EntityManager.GetComponentData<PrefabRef>(main).m_Prefab != mainPrefab ||
                    !EntityManager.HasComponent<EdgeGeometry>(main)) continue;
                Composition composition = EntityManager.GetComponentData<Composition>(main);
                if (!TryReadSurfaceLayout(composition.m_Edge, out SurfaceLayout layout, highway: IsHighway(main)) || layout.Roads.Count == 0) continue;
                Edge mainEndpoints = EntityManager.GetComponentData<Edge>(main);
                bool start = mainEndpoints.m_Start == node;
                Curve curve = EntityManager.GetComponentData<Curve>(main);
                float2 direction = math.normalizesafe((start ? MathUtils.StartTangent(curve.m_Bezier)
                    : -MathUtils.EndTangent(curve.m_Bezier)).xz);
                if (count == 0)
                {
                    axis = direction;
                    normal = new float2(-axis.y, axis.x);
                    if (math.dot(normal, branchDirection) < 0f) normal = -normal;
                    // A nearly parallel alley merge still needs a cutoff. Its
                    // native mouth is a stable transverse boundary when a curb
                    // plane would run along the alley instead of across it.
                    if (math.dot(normal, branchDirection) < 0.35f)
                    {
                        if (!IsAlleyApproach(edge)) return false;
                        bool branchStart = endpoints.m_Start == node;
                        EdgeGeometry branchGeometry = EntityManager.GetComponentData<EdgeGeometry>(edge);
                        float3 mouthLeft = branchStart ? branchGeometry.m_Start.m_Left.a : branchGeometry.m_End.m_Left.d;
                        float3 mouthRight = branchStart ? branchGeometry.m_Start.m_Right.a : branchGeometry.m_End.m_Right.d;
                        float2 transverse = math.normalizesafe(mouthRight.xz - mouthLeft.xz);
                        float2 outward = new float2(-transverse.y, transverse.x);
                        // Pick the side containing the alley's body, including
                        // curved approaches whose tangent turns before the node.
                        float3 body = branchStart
                            ? (branchGeometry.m_Start.m_Left.b + branchGeometry.m_Start.m_Right.b) * 0.5f
                            : (branchGeometry.m_End.m_Left.c + branchGeometry.m_End.m_Right.c) * 0.5f;
                        float3 mouth = (mouthLeft + mouthRight) * 0.5f;
                        float bodySide = math.dot(outward, body.xz - mouth.xz);
                        if (math.abs(bodySide) < 0.001f) bodySide = math.dot(outward, branchDirection);
                        if (bodySide < 0f) outward = -outward;
                        if (math.lengthsq(outward) < 0.5f) return false;
                        cut = new CurbCut { Normal = outward, Distance = math.dot(outward, mouth.xz) };
                        LogBranchCurbCut(edge, node, mainPrefab, cut, true);
                        return true;
                    }
                }
                else if (math.abs(math.dot(axis, direction)) < 0.85f) continue;
                EdgeGeometry geometry = EntityManager.GetComponentData<EdgeGeometry>(main);
                float3 left = start ? geometry.m_Start.m_Left.a : geometry.m_End.m_Left.d;
                float3 right = start ? geometry.m_Start.m_Right.a : geometry.m_End.m_Right.d;
                float low = layout.Roads[0].Low / layout.Composition.m_Width + 0.5f;
                float high = layout.Roads[layout.Roads.Count - 1].High / layout.Composition.m_Width + 0.5f;
                float a = math.dot(normal, math.lerp(left, right, math.saturate(low)).xz);
                float b = math.dot(normal, math.lerp(left, right, math.saturate(high)).xz);
                distance += math.max(a, b);
                count++;
            }
            if (count == 0 || !math.isfinite(distance)) return false;
            cut = new CurbCut { Normal = normal, Distance = distance / count };
            LogBranchCurbCut(edge, node, mainPrefab, cut, false);
            return true;
        }

        private void LogBranchCurbCut(Entity edge, Entity node, Entity mainPrefab, CurbCut cut, bool nativeMouth)
        {
            if (m_LoggedCurbCuts.Count < 8 && m_LoggedCurbCuts.Add(edge))
                Mod.Log.Info("Branch surface curb cutoff: node=" + node.Index + "; branch='" +
                    m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab) +
                    "'; main='" + m_PrefabSystem.GetPrefabName(mainPrefab) + "'; normal=" + cut.Normal +
                    "; boundary=" + cut.Distance + "; native approach mouth=" + nativeMouth +
                    ". Road and sidewalk rules share the cutoff.");
        }

        private SurfacePatch ClipOrdinaryBranchBody(Entity edge, SurfacePatch patch, Setting setting,
            SurfaceLayout layout, RoadBand band, bool sidewalk)
        {
            if (!setting.IncludeStreets) return patch;
            Edge endpoints = EntityManager.GetComponentData<Edge>(edge);
            for (int side = 0; side < 2; side++)
            {
                bool start = side == 0;
                if (start ? !EntityManager.HasComponent<StartNodeGeometry>(edge) : !EntityManager.HasComponent<EndNodeGeometry>(edge)) continue;
                EdgeNodeGeometry geometry = start ? EntityManager.GetComponentData<StartNodeGeometry>(edge).m_Geometry :
                    EntityManager.GetComponentData<EndNodeGeometry>(edge).m_Geometry;
                if (geometry.m_MiddleRadius > 0.01f) continue;
                Entity node = start ? endpoints.m_Start : endpoints.m_End;
                // Crosswalk ownership supplies the pavement boundary below.
                // Applying the older main-road curb cut as well can stop an
                // angled road/bus approach short of its own crosswalk.
                if (!sidewalk && GetJunctionPavementDomain(node) != null) continue;
                Entity main = GetOrdinaryJunctionReferencePrefab(node);
                if (main == Entity.Null) continue;
                if (!setting.MainRoadJunctionSurfaces && GetPreferredJunctionPrefab(node) == Entity.Null &&
                    !TryGetSavedJunctionSurface(node, out _) && !IsHighwayPrefab(main)) continue;
                if (TryGetCurbCut(edge, node, main, out CurbCut cut))
                {
                    patch.ExactBoundary = true;
                    patch = ClipSurfacePatch(patch, cut, true);
                }
            }
            return patch;
        }

        private void QueueOrdinaryJunctionPart(Entity owner, Entity node, Entity main,
            Entity junctionMaterial, bool shared, SurfacePatch patch, bool sidewalk, int bandSlot, Setting setting,
            SurfaceLayout layout, RoadBand band)
        {
            if (patch.Parts.Count == 0 && (patch.Count < 4 || patch.Triangles.Count == 0)) return;
            if (!sidewalk) LimitDedicatedLaneProjection(patch, layout, math.max(band.LowHeight, band.HighHeight));
            if (main == Entity.Null)
            {
                QueuePolygon(owner, junctionMaterial, patch);
                return;
            }
            if (!TryGetCurbCut(owner, node, main, out CurbCut cut))
            {
                if (sidewalk || !shared) QueuePolygon(owner, junctionMaterial, patch);
                return;
            }
            patch.ExactBoundary = true;
            Entity branchPrefab = EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab;
            Curve branchCurve = EntityManager.GetComponentData<Curve>(owner);
            List<SurfaceChoice> choices = GetChoices(setting, sidewalk ? SurfaceTarget.Sidewalks : SurfaceTarget.Roads, branchPrefab, segment: owner);
            SurfaceChoice branch = Choose(choices, m_PrefabSystem.GetPrefabName(branchPrefab), branchCurve, bandSlot);
            if (sidewalk && QueueMainSidewalkCorner(owner, node, main, patch, cut, band.LowHeight,
                bandSlot, setting, layout, branch == null ? Entity.Null : branch.Prefab)) return;
            if (branch != null) QueuePolygon(owner, branch.Prefab, ClipSurfacePatch(patch, cut, true));
            // A flat alley pedestrian/shoulder band cannot continue across the
            // main carriageway as a sidewalk decal. The common pavement fills it.
            if (!sidewalk && !shared) QueuePolygon(owner, junctionMaterial, ClipSurfacePatch(patch, cut, false));
        }

        private bool QueueMainSidewalkCorner(Entity owner, Entity node, Entity main, SurfacePatch patch,
            CurbCut cut, float sourceHeight, int bandSlot, Setting setting, SurfaceLayout branchLayout, Entity branchMaterial)
        {
            // The cutoff is a pavement line, not the outside of a rounded curb.
            // Preserve the part of a branch ribbon covering the raised corner,
            // but project it only above the main road/sidewalk height midpoint.
            // A flat alley shoulder inside the carriageway therefore stays road.
            foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity edge = connection.m_Edge;
                if (!IsRoadEdge(edge) || EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab != main) continue;
                Composition composition = EntityManager.GetComponentData<Composition>(edge);
                if (!TryReadSurfaceLayout(composition.m_Edge, out SurfaceLayout layout) || layout.Roads.Count == 0) continue;
                float roadHeight = float.NegativeInfinity;
                foreach (RoadBand road in layout.Roads)
                    roadHeight = math.max(roadHeight, math.max(road.LowHeight, road.HighHeight));
                LaneSpan? walk = null;
                foreach (LaneSpan candidate in SelectSidewalks(layout, setting, false, false))
                    if (candidate.Height - roadHeight > 0.12f &&
                        (!walk.HasValue || candidate.Height < walk.Value.Height)) walk = candidate;
                if (!walk.HasValue) continue;
                SurfaceChoice choice = Choose(GetChoices(setting, SurfaceTarget.Sidewalks, main, segment: edge),
                    m_PrefabSystem.GetPrefabName(main), JunctionMaterialSeed(EntityManager.GetComponentData<Curve>(owner), node), bandSlot);
                if (choice == null) return false;
                if (branchMaterial != Entity.Null) QueuePolygon(owner, branchMaterial, ClipSurfacePatch(patch, cut, true));
                SurfacePatch corner = ClipSurfacePatch(patch, cut, false);
                MoveSurfacePatchHeight(corner, walk.Value.Height - sourceHeight);
                corner.ProjectionMinimum = math.max(corner.ProjectionMinimum,
                    (roadHeight - walk.Value.Height) * 0.5f - SurfaceLift);
                QueuePolygon(owner, choice.Prefab, corner);
                return true;
            }
            return false;
        }

        private static void MoveSurfacePatchHeight(SurfacePatch patch, float offset)
        {
            foreach (SurfacePatch part in patch.Parts) MoveSurfacePatchHeight(part, offset);
            for (int i = 0; i < patch.Count; i++)
            {
                AreaNode node = patch.Nodes[i];
                node.m_Position.y += offset;
                patch.Nodes[i] = node;
            }
        }

        private bool TryQueueSharedJunction(Entity owner, Entity node, Entity main, Entity material, Setting setting)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node) || setting.Placement == PlacementMode.NewSegmentsOnly) return false;
            List<JunctionOutline> outlines = new List<JunctionOutline>();
            List<CurbCut> cuts = new List<CurbCut>();
            Entity fillOwner = Entity.Null;
            float projectionMaximum = float.PositiveInfinity;
            foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity edge = connection.m_Edge;
                if (!IsRoadEdge(edge)) continue;
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                // A single owner may supply the junction strips only when all
                // connected approaches belong to the active placement scope.
                // Highway merges retain native approach ribbons. A shallow
                // alley's transverse mouth must not also clip the highway's
                // entire shared fill with an infinite half-plane.
                if (IsHighway(edge) || !PassesRoadFilter(edge, prefab, setting) ||
                    !PassesElevationFilter(edge, prefab, setting) ||
                    (edge != owner && setting.Placement == PlacementMode.NewSegmentsOnly && !m_ManagedEdges.Contains(edge) &&
                     !m_NewRoadEdges.Contains(edge) && !EntityManager.HasComponent<SurfaceSegmentSelected>(edge)) ||
                    !EntityManager.HasComponent<EdgeGeometry>(edge)) return false;
                Edge endpoints = EntityManager.GetComponentData<Edge>(edge);
                bool start = endpoints.m_Start == node;
                if (start ? !EntityManager.HasComponent<StartNodeGeometry>(edge) : !EntityManager.HasComponent<EndNodeGeometry>(edge)) return false;
                EdgeNodeGeometry geometry = start ? EntityManager.GetComponentData<StartNodeGeometry>(edge).m_Geometry :
                    EntityManager.GetComponentData<EndNodeGeometry>(edge).m_Geometry;
                if (geometry.m_MiddleRadius > 0.01f) return false;
                Composition composition = EntityManager.GetComponentData<Composition>(edge);
                if (!TryReadSurfaceLayout(composition.m_Edge, out SurfaceLayout approach, highway: IsHighway(edge)) ||
                    !TryReadSurfaceLayout(start ? composition.m_StartNode : composition.m_EndNode,
                        out SurfaceLayout layout, approach, start, streetContext: true) || layout.Roads.Count != 1) return false;
                RoadBand band = layout.Roads[0];
                SurfacePatch projection = new SurfacePatch();
                LimitDedicatedLaneProjection(projection, layout, math.max(band.LowHeight, band.HighHeight));
                projectionMaximum = math.min(projectionMaximum, projection.ProjectionMaximum);
                if (band.Low >= layout.Composition.m_MiddleOffset || band.High <= layout.Composition.m_MiddleOffset) return false;
                bool branch = TryGetCurbCut(edge, node, main, out CurbCut cut);
                if (branch) cuts.Add(cut);
                outlines.Add(new JunctionOutline
                {
                    Left = JunctionCurbBoundary(edge, node, geometry, approach, layout, band, true, start, branch, cut),
                    Right = JunctionCurbBoundary(edge, node, geometry, approach, layout, band, false, start, branch, cut)
                });
                if (fillOwner == Entity.Null || edge.Index < fillOwner.Index) fillOwner = edge;
            }
            if (outlines.Count < 3 || cuts.Count == 0) return false;
            List<float3> boundary = new List<float3>();
            HashSet<int> visited = new HashSet<int>();
            int current = 0;
            while (visited.Add(current))
            {
                JunctionOutline item = outlines[current];
                boundary.AddRange(item.Right);
                float3 joint = item.Right[item.Right.Count - 1];
                int next = -1;
                float closest = 0.04f;
                for (int i = 0; i < outlines.Count; i++)
                {
                    if (i == current) continue;
                    List<float3> left = outlines[i].Left;
                    float distance = math.distancesq(joint.xz, left[left.Count - 1].xz);
                    if (distance < closest) { closest = distance; next = i; }
                }
                if (next < 0) return false;
                List<float3> adjoining = outlines[next].Left;
                for (int i = adjoining.Count - 1; i >= 0; i--) boundary.Add(adjoining[i]);
                current = next;
                if (current == 0) break;
            }
            if (current != 0 || visited.Count != outlines.Count) return false;
            foreach (CurbCut cut in cuts) boundary = ClipBoundary(boundary, cut, false);
            SurfacePatch fill = TriangulateBoundary(boundary);
            if (fill.Triangles.Count == 0) return false;
            fill.ExactBoundary = true;
            fill.ProjectionMaximum = projectionMaximum;
            if (owner == fillOwner)
            {
                QueuePolygon(owner, material, fill);
                if (m_LoggedSharedJunctions.Count < 8 && m_LoggedSharedJunctions.Add(node))
                    Mod.Log.Info("Curb-bounded junction pavement: node=" + node.Index + "; approaches=" + outlines.Count +
                        "; single area owner=" + fillOwner.Index + "; curb cuts=" + cuts.Count +
                        "; triangles=" + fill.Triangles.Count + "; projection ceiling=" + projectionMaximum +
                        ". Raised curbs remain outside the pavement projection volume.");
            }
            return true;
        }

        private List<float3> JunctionCurbBoundary(Entity edge, Entity node, EdgeNodeGeometry geometry,
            SurfaceLayout approach, SurfaceLayout layout, RoadBand band, bool left, bool start, bool branch, CurbCut cut)
        {
            CornerInsets mouth = GetJunctionMouth(edge, approach, layout, band, left, false, start);
            CornerInsets end = GetCornerInsets(edge, node, geometry, layout, band, left, false, false);
            float3 first = JunctionBandPosition(geometry, layout.Composition, band, left, false, 0f);
            float3 last = JunctionBandPosition(geometry, layout.Composition, band, left, false, 1f);
            if (branch && mouth.HasPositions && cut.Side(mouth.NearPosition) < 0f)
            {
                Edge endpoints = EntityManager.GetComponentData<Edge>(edge);
                Curve curve = EntityManager.GetComponentData<Curve>(edge);
                float3 direction = math.normalizesafe(endpoints.m_Start == node
                    ? MathUtils.StartTangent(curve.m_Bezier) : -MathUtils.EndTangent(curve.m_Bezier));
                float denominator = math.dot(cut.Normal, direction.xz);
                if (denominator > 0.1f) mouth.NearPosition -= direction * (cut.Side(mouth.NearPosition) / denominator);
            }
            Func<float, float3> position = t => JoinBoundary(JunctionBandPosition(geometry,
                layout.Composition, band, left, false, t), first, last, mouth, end, false, t);
            List<float3> result = new List<float3>();
            foreach (float t in SampleMappedParameters(position, position)) result.Add(position(t));
            return result;
        }

        private SurfacePatch ClipSurfacePatch(SurfacePatch source, CurbCut cut, bool outside)
        {
            if (source.Parts.Count > 0)
            {
                SurfacePatch parts = CopySurfacePatchProperties(source);
                foreach (SurfacePatch part in source.Parts)
                {
                    SurfacePatch clipped = ClipSurfacePatch(part, cut, outside);
                    if (clipped.Parts.Count > 0 || clipped.Triangles.Count > 0) parts.Parts.Add(clipped);
                }
                return parts;
            }
            bool anyOutside = false;
            for (int i = 0; i < source.Count - 1; i++)
                if ((outside ? cut.Side(source.Nodes[i].m_Position) : -cut.Side(source.Nodes[i].m_Position)) < -0.0001f)
                    { anyOutside = true; break; }
            if (!anyOutside) return source;
            // Clip the sampled triangles, not just the polygon perimeter. This
            // preserves the original road/curb heights and can keep multiple
            // disconnected pieces of a concave corner instead of dropping it.
            SurfacePatch mesh = ClipJunctionMesh(source, cut, outside, out bool complete);
            if (complete) return mesh;
            if (m_LoggedJunctionClipFallbacks++ < 8)
                Mod.Log.Warn("Junction mesh clipping used a perimeter fallback: nodes=" + source.Count +
                    "; triangles=" + source.Triangles.Count + "; outside=" + outside + ".");
            List<float3> boundary = new List<float3>();
            for (int i = 0; i < source.Count - 1; i++) boundary.Add(source.Nodes[i].m_Position);
            SurfacePatch result = TriangulateBoundary(ClipBoundary(boundary, cut, outside));
            result.ExactBoundary = source.ExactBoundary;
            result.SmallRoundabout = source.SmallRoundabout;
            result.DedicatedLanesClipped = source.DedicatedLanesClipped;
            result.PlantedAreasClipped = source.PlantedAreasClipped;
            result.ProjectionMinimum = source.ProjectionMinimum;
            result.ProjectionMaximum = source.ProjectionMaximum;
            return result;
        }

        private static List<float3> ClipBoundary(List<float3> source, CurbCut cut, bool outside)
        {
            List<float3> result = new List<float3>();
            if (source.Count == 0) return result;
            float3 previous = source[source.Count - 1];
            float previousSide = cut.Side(previous) * (outside ? 1f : -1f);
            foreach (float3 point in source)
            {
                float side = cut.Side(point) * (outside ? 1f : -1f);
                if ((side >= 0f) != (previousSide >= 0f))
                    result.Add(math.lerp(previous, point, math.saturate(previousSide / (previousSide - side))));
                if (side >= 0f) result.Add(point);
                previous = point;
                previousSide = side;
            }
            return result;
        }

        private static SurfacePatch TriangulateBoundary(List<float3> boundary)
        {
            SurfacePatch patch = new SurfacePatch();
            foreach (float3 point in boundary)
            {
                if (!math.all(math.isfinite(point))) return patch;
                if (patch.Count == 0 || math.distancesq(patch.Nodes[patch.Count - 1].m_Position.xz, point.xz) > 0.000001f)
                    patch.Nodes.Add(new AreaNode(point, 0f));
            }
            if (patch.Count > 1 && math.distancesq(patch.Nodes[0].m_Position.xz, patch.Nodes[patch.Count - 1].m_Position.xz) <= 0.000001f)
                patch.Nodes.RemoveAt(patch.Count - 1);
            // Remove collinear samples before ear clipping and before the native
            // renderer normalizes boundary directions. Heights must still agree.
            bool removed;
            do
            {
                removed = false;
                for (int i = 0; patch.Count > 3 && i < patch.Count; i++)
                {
                    float3 a = patch.Nodes[(i + patch.Count - 1) % patch.Count].m_Position;
                    float3 b = patch.Nodes[i].m_Position;
                    float3 c = patch.Nodes[(i + 1) % patch.Count].m_Position;
                    float length = math.distance(a.xz, c.xz);
                    float t = math.dot(b.xz - a.xz, c.xz - a.xz) / math.max(0.000001f, length * length);
                    if (t > 0f && t < 1f && math.abs(Cross2(b.xz - a.xz, c.xz - a.xz)) <= 0.0001f * length &&
                        math.abs(b.y - math.lerp(a.y, c.y, t)) <= 0.001f)
                    { patch.Nodes.RemoveAt(i); removed = true; i--; }
                }
            } while (removed);
            if (patch.Count < 3) return new SurfacePatch();
            double area = 0;
            float2 origin = patch.Nodes[0].m_Position.xz;
            for (int i = 0; i < patch.Count; i++)
                area += Cross2(patch.Nodes[i].m_Position.xz - origin, patch.Nodes[(i + 1) % patch.Count].m_Position.xz - origin);
            if (math.abs((float)area) < 0.00001f) return new SurfacePatch();
            if (area < 0) patch.Nodes.Reverse();
            // Reject a folded outline; a fan or a convex hull would cover the
            // sidewalk. The caller can retain native approach geometry instead.
            for (int i = 0; i < patch.Count; i++)
                for (int j = i + 2; j < patch.Count; j++)
                {
                    if (i == 0 && j == patch.Count - 1) continue;
                    float2 a = patch.Nodes[i].m_Position.xz, b = patch.Nodes[(i + 1) % patch.Count].m_Position.xz;
                    float2 c = patch.Nodes[j].m_Position.xz, d = patch.Nodes[(j + 1) % patch.Count].m_Position.xz;
                    if (Cross2(b - a, c - a) * Cross2(b - a, d - a) < -0.00000001f &&
                        Cross2(d - c, a - c) * Cross2(d - c, b - c) < -0.00000001f) return new SurfacePatch();
                }
            List<int> remaining = new List<int>();
            for (int i = 0; i < patch.Count; i++) remaining.Add(i);
            while (remaining.Count > 3)
            {
                bool found = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int a = remaining[(i + remaining.Count - 1) % remaining.Count], b = remaining[i], c = remaining[(i + 1) % remaining.Count];
                    float2 pa = patch.Nodes[a].m_Position.xz, pb = patch.Nodes[b].m_Position.xz, pc = patch.Nodes[c].m_Position.xz;
                    if (Cross2(pb - pa, pc - pa) <= 0.0000001f) continue;
                    bool contains = false;
                    foreach (int other in remaining)
                    {
                        if (other == a || other == b || other == c) continue;
                        float2 p = patch.Nodes[other].m_Position.xz;
                        if (Cross2(pb - pa, p - pa) >= -0.0000001f && Cross2(pc - pb, p - pb) >= -0.0000001f &&
                            Cross2(pa - pc, p - pc) >= -0.0000001f) { contains = true; break; }
                    }
                    if (contains) continue;
                    patch.Triangles.Add(new int3(a, b, c));
                    remaining.RemoveAt(i);
                    found = true;
                    break;
                }
                if (!found) return new SurfacePatch();
            }
            AddRibbonTriangle(patch, new int3(remaining[0], remaining[1], remaining[2]));
            patch.Nodes.Add(patch.Nodes[0]);
            return patch;
        }

        private static float Cross2(float2 a, float2 b) => a.x * b.y - a.y * b.x;
    }
}
