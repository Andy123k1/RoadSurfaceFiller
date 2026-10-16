using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using SubLane = Game.Net.SubLane;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private const int LaneRingSteps = 192;
        private const float LaneRingStep = math.PI * 2f / LaneRingSteps;

        private sealed class RoundaboutLaneRing
        {
            internal float3 Center;
            internal float Radius, Width;
            internal float[] Heights;
            internal JunctionLaneRoad Source;
            internal SurfaceChoice Surface;
        }

        private RoundaboutLaneRing ReadRoundaboutBikeRing(Entity node, List<JunctionLaneRoad> roads,
            Setting setting, float islandRadius)
        {
            if (!EntityManager.HasComponent<Game.Net.Node>(node)) return null;
            float3 center = EntityManager.GetComponentData<Game.Net.Node>(node).m_Position;
            List<float3> samples = new List<float3>();
            List<float> radii = new List<float>(), widths = new List<float>();
            List<Bezier4x3> arcs = new List<Bezier4x3>();
            List<float> arcRadii = new List<float>();
            bool[] coverage = new bool[LaneRingSteps];
            foreach (SubLane subLane in EntityManager.GetBuffer<SubLane>(node, true))
            {
                Entity lane = subLane.m_SubLane;
                if (!TryReadJunctionLane(lane, setting, out SurfaceTarget target, out Bezier4x3 curve, out float2 width) ||
                    target != SurfaceTarget.BikeLanes ||
                    !TryReadCirculatingBikeArc(lane, curve, width, center, islandRadius, out float radius)) continue;
                radii.Add(radius); widths.Add((width.x + width.y) * 0.5f);
                arcs.Add(curve); arcRadii.Add(radius);
            }
            if (radii.Count == 0) return null;
            radii.Sort(); widths.Sort();
            float ringRadius = radii[radii.Count / 2], ringWidth = widths[widths.Count / 2];
            if (ringRadius - ringWidth * 0.5f <= islandRadius + 0.1f) return null;
            for (int arc = 0; arc < arcs.Count; arc++)
            {
                if (math.abs(arcRadii[arc] - ringRadius) > math.max(0.4f, ringWidth * 0.35f)) continue;
                Bezier4x3 curve = arcs[arc];
                float2 previous = default;
                for (int i = 0; i <= 32; i++)
                {
                    float3 point = MathUtils.Position(curve, i / 32f);
                    samples.Add(point);
                    float2 direction = math.normalizesafe(point.xz - center.xz);
                    float angle = PositiveRingAngle(direction);
                    if (i == 0) coverage[(int)math.floor(angle / LaneRingStep) % LaneRingSteps] = true;
                    else
                    {
                        float start = PositiveRingAngle(previous);
                        float delta = math.atan2(Cross2(previous, direction), math.dot(previous, direction));
                        int steps = math.max(1, (int)math.ceil(math.abs(delta) / LaneRingStep));
                        for (int j = 0; j <= steps; j++)
                        {
                            float sampleAngle = start + delta * j / steps;
                            int index = (int)math.floor(sampleAngle / LaneRingStep);
                            coverage[(index % LaneRingSteps + LaneRingSteps) % LaneRingSteps] = true;
                        }
                    }
                    previous = direction;
                }
            }
            int covered = 0; foreach (bool value in coverage) if (value) covered++;
            // Recover a circular finish from the native circulating lanes,
            // never from an arbitrary short entry turn or an ordinary junction.
            if (samples.Count == 0 || covered < LaneRingSteps / 3) return null;
            JunctionLaneRoad source = null;
            Entity preferred = GetPreferredJunctionRoad(node);
            foreach (JunctionLaneRoad road in roads)
            {
                if (!road.Layout.SeparateBikeLanes || road.Layout.BikeLanes.Count == 0) continue;
                if (source == null || road.Edge == preferred || (source.Edge != preferred &&
                    (road.Layout.Composition.m_Width > source.Layout.Composition.m_Width ||
                    (road.Layout.Composition.m_Width == source.Layout.Composition.m_Width && road.Edge.Index < source.Edge.Index))))
                    source = road;
            }
            if (source == null) return null;
            RoundaboutLaneRing result = new RoundaboutLaneRing {
                Center = center, Radius = ringRadius, Width = ringWidth, Source = source,
                Heights = new float[LaneRingSteps]
            };
            // The geometry describes a full painted ring even where a
            // non-bicycle approach interrupts the navigation lane graph.
            // Keep the native deck elevations; interpolate through that gap.
            samples.Sort((a, b) => PositiveRingAngle(a.xz - center.xz).CompareTo(PositiveRingAngle(b.xz - center.xz)));
            int next = 0;
            for (int i = 0; i < LaneRingSteps; i++)
            {
                float angle = i * LaneRingStep;
                while (next < samples.Count && PositiveRingAngle(samples[next].xz - center.xz) < angle) next++;
                float3 b = samples[next % samples.Count], a = samples[(next + samples.Count - 1) % samples.Count];
                float aa = PositiveRingAngle(a.xz - center.xz), ab = PositiveRingAngle(b.xz - center.xz);
                if (next == 0) aa -= math.PI * 2f;
                if (next == samples.Count) ab += math.PI * 2f;
                result.Heights[i] = math.lerp(a.y, b.y, math.saturate((angle - aa) / math.max(0.00001f, ab - aa))) + SurfaceLift;
            }
            if (source.Eligible)
                result.Surface = Choose(GetChoices(setting, SurfaceTarget.BikeLanes, source.Prefab, segment: source.Edge),
                    m_PrefabSystem.GetPrefabName(source.Prefab), JunctionMaterialSeed(source.Curve, node), 3);
            return result;
        }

        private bool TryReadCirculatingBikeArc(Entity lane, Bezier4x3 curve, float2 widths,
            float3 center, float islandRadius, out float radius)
        {
            radius = 0f;
            if (!EntityManager.HasComponent<Game.Net.CarLane>(lane))
                return IsRoundaboutRingCurve(curve, widths, center, out radius);
            CarLaneFlags flags = EntityManager.GetComponentData<Game.Net.CarLane>(lane).m_Flags;
            if ((flags & (CarLaneFlags.Forward | CarLaneFlags.Roundabout)) !=
                (CarLaneFlags.Forward | CarLaneFlags.Roundabout) ||
                (flags & (CarLaneFlags.Unsafe | CarLaneFlags.TurnLeft | CarLaneFlags.TurnRight |
                    CarLaneFlags.GentleTurnLeft | CarLaneFlags.GentleTurnRight | CarLaneFlags.UTurnLeft | CarLaneFlags.UTurnRight)) != 0 ||
                math.abs(widths.x - widths.y) > 0.05f) return false;
            // Native circulation endpoints sit on the ring, but PresetCurve
            // adjusts their Bezier handles for traffic flow. Requiring every
            // point of that routing curve to be a perfect circle rejects valid
            // arcs. Recover the painted radius from the native ring anchors.
            float2 a = curve.a.xz - center.xz, b = curve.d.xz - center.xz;
            float ra = math.length(a), rb = math.length(b);
            if (math.min(ra, rb) <= islandRadius + widths.x * 0.5f + 0.1f ||
                math.abs(ra - rb) > math.max(0.4f, widths.x * 0.35f) ||
                math.abs(math.dot(math.normalizesafe(a), math.normalizesafe(MathUtils.Tangent(curve, 0f).xz))) > 0.35f ||
                math.abs(math.dot(math.normalizesafe(b), math.normalizesafe(MathUtils.Tangent(curve, 1f).xz))) > 0.35f) return false;
            float2 from = math.normalizesafe(a), to = math.normalizesafe(b);
            if (math.abs(math.atan2(Cross2(from, to), math.dot(from, to))) <= 0.04f) return false;
            for (int i = 1; i < 8; i++)
                if (math.distance(MathUtils.Position(curve, i / 8f).xz, center.xz) <= islandRadius + 0.05f) return false;
            radius = (ra + rb) * 0.5f;
            return true;
        }

        private static bool IsRoundaboutRingCurve(Bezier4x3 curve, float2 widths, float3 center, out float radius)
        {
            radius = 0f;
            if (math.abs(widths.x - widths.y) > 0.05f) return false;
            float minimum = float.PositiveInfinity, maximum = 0f;
            for (int i = 0; i <= 8; i++)
            {
                float t = i / 8f;
                float2 offset = MathUtils.Position(curve, t).xz - center.xz;
                float distance = math.length(offset);
                minimum = math.min(minimum, distance); maximum = math.max(maximum, distance); radius += distance / 9f;
                float2 tangent = math.normalizesafe(MathUtils.Tangent(curve, t).xz);
                if (math.abs(math.dot(math.normalizesafe(offset), tangent)) > 0.15f) return false;
            }
            float2 from = math.normalizesafe(curve.a.xz - center.xz), to = math.normalizesafe(curve.d.xz - center.xz);
            return maximum - minimum <= math.max(0.15f, widths.x * 0.1f) &&
                math.abs(math.atan2(Cross2(from, to), math.dot(from, to))) > 0.04f;
        }

        private JunctionLaneStrip CreateRoundaboutBikeRingStrip(RoundaboutLaneRing ring, float islandRadius, List<LaneDeckFace> deck)
        {
            SurfacePatch patch = new SurfacePatch { ExactBoundary = true, DedicatedLanesClipped = true };
            for (int sector = 0; sector < 4; sector++)
            {
                List<float3> outer = new List<float3>(), inner = new List<float3>();
                for (int i = sector * LaneRingSteps / 4; i <= (sector + 1) * LaneRingSteps / 4; i++)
                {
                    int index = i % LaneRingSteps;
                    float angle = index * LaneRingStep;
                    float3 point = ring.Center;
                    point.y = ring.Heights[index];
                    float2 direction = new float2(math.cos(angle), math.sin(angle));
                    float3 a = point, b = point;
                    a.xz += direction * (ring.Radius + ring.Width * 0.5f);
                    b.xz += direction * (ring.Radius - ring.Width * 0.5f);
                    AppendPair(outer, inner, a, b);
                }
                SurfacePatch part = CloseStrip(outer, inner);
                SetLanePatchFlags(part, islandRadius <= 4.01f);
                part.DedicatedLanesClipped = true;
                patch.Parts.Add(part);
            }
            if (ring.Source.Layout.BikeLanes.Count > 0)
                LimitDedicatedLaneProjection(patch, ring.Source.Layout, ring.Source.Layout.BikeLanes[0].Height);
            LaneDeckAlignment alignment = SnapJunctionLaneDeck(patch, deck);
            JunctionLaneStrip strip = new JunctionLaneStrip {
                Owner = ring.Source.Eligible ? ring.Source.Edge : Entity.Null,
                Target = SurfaceTarget.BikeLanes, Surface = ring.Surface, Patch = patch, DeckAlignment = alignment
            };
            AddLaneMaskCells(patch, strip.Cells);
            return strip;
        }

        private static float PositiveRingAngle(float2 direction)
        {
            float angle = math.atan2(direction.y, direction.x);
            return angle < 0f ? angle + math.PI * 2f : angle;
        }

        private static float LaneRingBoundaryRadius(RoundaboutLaneRing ring, float2 direction, bool outside)
        {
            float angle = PositiveRingAngle(direction);
            float middle = (math.floor(angle / LaneRingStep) + 0.5f) * LaneRingStep;
            // Intersect the same polygonal circle used by the lane area. Road
            // and lane therefore share an edge even at extra native samples.
            return (ring.Radius + (outside ? 0.5f : -0.5f) * ring.Width) *
                math.cos(LaneRingStep * 0.5f) / math.cos(angle - middle);
        }

        private static SurfacePatch CreateSeparatedRoundaboutRibbon(List<float3> perimeter, float3 mouth,
            float3 center, float islandRadius, float height, RoundaboutLaneRing ring)
        {
            List<float3> outer = new List<float3> { mouth }; outer.AddRange(perimeter);
            List<float3> samples = new List<float3>();
            for (int i = 0; i + 1 < outer.Count; i++)
            {
                float3 a = outer[i], b = outer[i + 1];
                float start = PositiveRingAngle(a.xz - center.xz);
                float2 from = math.normalizesafe(a.xz - center.xz), to = math.normalizesafe(b.xz - center.xz);
                float delta = math.atan2(Cross2(from, to), math.dot(from, to));
                List<float> parameters = new List<float> { 0f, 1f };
                float low = math.min(start, start + delta), high = math.max(start, start + delta);
                for (int j = (int)math.floor(low / LaneRingStep) + 1; j * LaneRingStep < high - 0.000001f; j++)
                {
                    float angle = j * LaneRingStep;
                    float2 ray = new float2(math.cos(angle), math.sin(angle));
                    double ca = LaneCellSide(a, center, new float3(center.x + ray.x, center.y, center.z + ray.y));
                    double cb = LaneCellSide(b, center, new float3(center.x + ray.x, center.y, center.z + ray.y));
                    if (math.abs((float)(ca - cb)) > 0.0000001f)
                    {
                        float t = (float)(ca / (ca - cb));
                        if (t > 0.000001f && t < 0.999999f) parameters.Add(t);
                    }
                }
                AddRingBoundaryCrossings(parameters, a, b, center, ring, false);
                AddRingBoundaryCrossings(parameters, a, b, center, ring, true);
                parameters.Sort();
                foreach (float t in parameters) AddDistinct(samples, math.lerp(a, b, t));
            }
            SurfacePatch result = new SurfacePatch { ExactBoundary = true, DedicatedLanesClipped = true };
            // Two continuous road bands, separated by the native bicycle ring.
            // Runs are split only when a band pinches off or changes direction.
            for (int band = 0; band < 2; band++)
            {
                List<float3> a = new List<float3>(), b = new List<float3>();
                for (int i = 0; i < samples.Count; i++)
                {
                    float3 outside = samples[i];
                    float distance = math.distance(outside.xz, center.xz);
                    float2 direction = math.normalizesafe(outside.xz - center.xz);
                    float innerRing = LaneRingBoundaryRadius(ring, direction, false), outerRing = LaneRingBoundaryRadius(ring, direction, true);
                    float inner = band == 0 ? islandRadius / math.cos(LaneRingStep * 0.5f) + 0.01f : outerRing;
                    float high = band == 0 ? math.min(distance, innerRing) : distance;
                    if (high < inner - 0.001f)
                    {
                        FlushRoundaboutLaneBand(result, a, b, center);
                        continue;
                    }
                    high = math.max(high, inner);
                    float3 lowPoint = Lift(center, height), highPoint = lowPoint;
                    float deck = lowPoint.y;
                    lowPoint.xz += direction * inner; highPoint.xz += direction * high;
                    lowPoint.y = math.lerp(deck, outside.y, math.saturate((inner - islandRadius) / math.max(0.001f, distance - islandRadius)));
                    highPoint.y = math.lerp(deck, outside.y, math.saturate((high - islandRadius) / math.max(0.001f, distance - islandRadius)));
                    AppendPair(a, b, highPoint, lowPoint);
                }
                FlushRoundaboutLaneBand(result, a, b, center);
            }
            return result;
        }

        private static void AddRingBoundaryCrossings(List<float> parameters, float3 a, float3 b,
            float3 center, RoundaboutLaneRing ring, bool outside)
        {
            // Perimeter/mouth chains may cross the reserved ring. Add the exact
            // polygon intersection so the shoulder band ends on the lane edge.
            float radius = ring.Radius + (outside ? 0.5f : -0.5f) * ring.Width;
            float2 direction = b.xz - a.xz;
            for (int i = 0; i < LaneRingSteps; i++)
            {
                float2 p = center.xz + radius * new float2(math.cos(i * LaneRingStep), math.sin(i * LaneRingStep));
                float2 q = center.xz + radius * new float2(math.cos(((i + 1) % LaneRingSteps) * LaneRingStep),
                    math.sin(((i + 1) % LaneRingSteps) * LaneRingStep));
                float denominator = Cross2(direction, q - p);
                if (math.abs(denominator) < 0.000001f) continue;
                float t = Cross2(p - a.xz, q - p) / denominator;
                float u = Cross2(p - a.xz, direction) / denominator;
                if (t > 0.000001f && t < 0.999999f && u >= 0f && u <= 1f) parameters.Add(t);
            }
        }

        private static void FlushRoundaboutLaneBand(SurfacePatch result, List<float3> a, List<float3> b, float3 center)
        {
            if (a.Count > 1)
            {
                List<float3> outer = new List<float3> { a[0] }, inner = new List<float3> { b[0] };
                float direction = 0f;
                for (int i = 1; i < a.Count; i++)
                {
                    float2 from = math.normalizesafe(a[i - 1].xz - center.xz), to = math.normalizesafe(a[i].xz - center.xz);
                    float angle = math.atan2(Cross2(from, to), math.dot(from, to));
                    // Split at actual reentrant turns, rather than repeatedly
                    // testing and fragmenting every prefix of a long arc.
                    if (math.abs(angle) > 0.00001f)
                    {
                        if (direction != 0f && angle * direction < 0f)
                        {
                            AddCompactRoundaboutBand(result, outer, inner);
                            outer.Clear(); inner.Clear(); outer.Add(a[i - 1]); inner.Add(b[i - 1]);
                        }
                        direction = math.sign(angle);
                    }
                    AppendPair(outer, inner, a[i], b[i]);
                }
                AddCompactRoundaboutBand(result, outer, inner);
            }
            a.Clear(); b.Clear();
        }

        private static void AddCompactRoundaboutBand(SurfacePatch result, List<float3> outer, List<float3> inner)
        {
            SurfacePatch part = RemoveUnusedSurfaceNodes(CloseStrip(new List<float3>(outer), new List<float3>(inner)));
            if (part.Triangles.Count > 0 && !IsSimpleSmallRoundaboutPatch(part)) part = CreateSmallRoundaboutStrip(outer, inner);
            SetLanePatchFlags(part, false); MarkLaneMasksApplied(part);
            if (part.Triangles.Count > 0 || part.Parts.Count > 0) result.Parts.Add(part);
        }

        private static void MarkLaneMasksApplied(SurfacePatch patch)
        {
            patch.DedicatedLanesClipped = true;
            foreach (SurfacePatch part in patch.Parts) MarkLaneMasksApplied(part);
        }
    }
}
