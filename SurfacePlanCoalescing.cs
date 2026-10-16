using System;
using System.Collections.Generic;
using Clipper2Lib;
using Unity.Entities;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private const double SurfaceMergeHeightTolerance = 0.005d;
        private int m_SurfacePlanAreasBeforeMerge, m_SurfacePlanAreasAfterMerge;

        private struct SurfacePaintKey : IEquatable<SurfacePaintKey>
        {
            internal Entity Prefab;
            internal float Minimum, Maximum;
            internal bool Exact, Roundabout;
            internal SurfacePaintKey(PlannedSurfaceArea plan)
            {
                Prefab = plan.Prefab; Minimum = plan.Patch.ProjectionMinimum; Maximum = plan.Patch.ProjectionMaximum;
                Exact = plan.Patch.ExactBoundary; Roundabout = plan.Patch.SmallRoundabout;
            }
            public bool Equals(SurfacePaintKey other) => Prefab == other.Prefab && Minimum.Equals(other.Minimum) &&
                Maximum.Equals(other.Maximum) && Exact == other.Exact && Roundabout == other.Roundabout;
            public override bool Equals(object other) => other is SurfacePaintKey key && Equals(key);
            public override int GetHashCode()
            {
                unchecked { return (((Prefab.GetHashCode() * 397 ^ Minimum.GetHashCode()) * 397 ^ Maximum.GetHashCode()) * 397 ^
                    (Exact ? 1 : 0)) * 397 ^ (Roundabout ? 1 : 0); }
            }
        }

        private struct SurfaceMergePlane
        {
            internal float3 Origin;
            internal double X, Z;
            internal double Height(float2 point) => Origin.y + ((double)point.x - Origin.x) * X + ((double)point.y - Origin.z) * Z;
        }

        private sealed class SurfacePaintGroup
        {
            internal SurfacePaintKey Key;
            internal SurfaceMergePlane Plane;
            internal readonly List<int> Indices = new List<int>();
        }

        private sealed class SurfacePlanPlane
        {
            internal int Index;
            internal SurfaceMergePlane Plane;
            internal double Area;
        }

        private void CoalesceSurfacePlan()
        {
            m_SurfacePlanAreasBeforeMerge += m_PlannedSurfaceAreas.Count;
            Dictionary<SurfacePaintKey, List<SurfacePaintGroup>> paints = new Dictionary<SurfacePaintKey, List<SurfacePaintGroup>>();
            List<SurfacePaintGroup> groups = new List<SurfacePaintGroup>();
            List<SurfacePlanPlane> planes = new List<SurfacePlanPlane>();
            for (int i = 0; i < m_PlannedSurfaceAreas.Count; i++)
            {
                if (TryGetSurfaceMergePlane(m_PlannedSurfaceAreas[i].Patch, out SurfaceMergePlane plane, out double area))
                    planes.Add(new SurfacePlanPlane { Index = i, Plane = plane, Area = area });
            }
            // Broad triangles provide stable height planes. Let tiny slivers
            // join those groups by their actual node heights, rather than
            // extrapolating an ill-conditioned sliver plane across a junction.
            planes.Sort((a, b) => a.Area != b.Area ? b.Area.CompareTo(a.Area) : a.Index.CompareTo(b.Index));
            foreach (SurfacePlanPlane geometry in planes)
            {
                int i = geometry.Index;
                PlannedSurfaceArea plan = m_PlannedSurfaceAreas[i];
                SurfacePaintKey key = new SurfacePaintKey(plan);
                if (!paints.TryGetValue(key, out List<SurfacePaintGroup> candidates))
                    paints.Add(key, candidates = new List<SurfacePaintGroup>());
                SurfacePaintGroup group = null;
                foreach (SurfacePaintGroup candidate in candidates)
                    if (SurfacePatchFollowsMergePlane(plan.Patch, candidate.Plane))
                    { group = candidate; break; }
                if (group == null)
                {
                    group = new SurfacePaintGroup { Key = key, Plane = geometry.Plane };
                    candidates.Add(group); groups.Add(group);
                }
                group.Indices.Add(i);
            }

            Dictionary<int, List<PlannedSurfaceArea>> replacements = new Dictionary<int, List<PlannedSurfaceArea>>();
            HashSet<int> replaced = new HashSet<int>();
            foreach (SurfacePaintGroup group in groups)
            {
                if (group.Indices.Count < 2) continue;
                group.Indices.Sort();
                SurfacePatch source = CopySurfacePatchProperties(m_PlannedSurfaceAreas[group.Indices[0]].Patch);
                foreach (int index in group.Indices) source.Parts.Add(m_PlannedSurfaceAreas[index].Patch);
                if (!TryClipSurfaceFootprint(source, new List<LaneMaskCell>(), out SurfacePatch joined)) continue;
                List<SurfacePatch> parts = new List<SurfacePatch>();
                CollectSurfacePlanParts(joined, parts);
                if (parts.Count == 0 || parts.Count >= group.Indices.Count) continue;
                List<PlannedSurfaceArea> plans = new List<PlannedSurfaceArea>();
                bool valid = true;
                foreach (SurfacePatch part in parts)
                {
                    // A new outside contour needs the same native expansion
                    // guard as an independently generated corner did.
                    SurfacePatch guarded = GuardPlannedSurfacePatch(group.Key.Prefab, part);
                    if (guarded.Count < 4 || guarded.Triangles.Count == 0) { valid = false; break; }
                    plans.Add(new PlannedSurfaceArea(group.Key.Prefab, guarded, guarded: true));
                }
                if (!valid || !SurfaceMergeGuardsRetainFootprint(parts, plans)) continue;
                replacements.Add(group.Indices[0], plans);
                foreach (int index in group.Indices) replaced.Add(index);
            }
            if (replacements.Count > 0)
            {
                List<PlannedSurfaceArea> plans = new List<PlannedSurfaceArea>();
                for (int i = 0; i < m_PlannedSurfaceAreas.Count; i++)
                    if (replacements.TryGetValue(i, out List<PlannedSurfaceArea> replacement)) plans.AddRange(replacement);
                    else if (!replaced.Contains(i)) plans.Add(m_PlannedSurfaceAreas[i]);
                m_PlannedSurfaceAreas.Clear(); m_PlannedSurfaceAreas.AddRange(plans);
            }
            m_SurfacePlanAreasAfterMerge += m_PlannedSurfaceAreas.Count;
        }

        private void GuardSurfacePlan(Entity owner)
        {
            for (int i = m_PlannedSurfaceAreas.Count - 1; i >= 0; i--)
            {
                PlannedSurfaceArea plan = m_PlannedSurfaceAreas[i];
                SurfacePatch patch = plan.Guarded ? plan.Patch : GuardPlannedSurfacePatch(plan.Prefab, plan.Patch);
                if (patch.Count < 4 || patch.Triangles.Count == 0) { m_PlannedSurfaceAreas.RemoveAt(i); continue; }
                if (!plan.Guarded) m_PlannedSurfaceAreas[i] = new PlannedSurfaceArea(plan.Prefab, patch, guarded: true);
                LogFinalPlannedSurface(owner, plan.Prefab, patch);
            }
        }

        private static void CollectSurfacePlanParts(SurfacePatch patch, List<SurfacePatch> parts)
        {
            foreach (SurfacePatch part in patch.Parts) CollectSurfacePlanParts(part, parts);
            if (patch.Triangles.Count > 0) parts.Add(patch);
        }

        private static bool SurfaceMergeGuardsRetainFootprint(List<SurfacePatch> parts, List<PlannedSurfaceArea> plans)
        {
            Paths64 desired = new Paths64(), guarded = new Paths64();
            foreach (SurfacePatch part in parts) AddSurfacePolygonPaths(part, desired);
            foreach (PlannedSurfaceArea plan in plans) AddSurfacePolygonPaths(plan.Patch, guarded);
            double scale = SurfacePolygonScale * SurfacePolygonScale;
            double area = Math.Abs(Clipper.Area(Clipper.Union(desired, FillRule.NonZero))) / scale;
            double difference = Math.Abs(Clipper.Area(Clipper.Xor(desired, guarded, FillRule.NonZero))) / scale;
            // If a newly joined acute corner needs a substantial guard repair,
            // retain the original already-guarded pieces instead of losing it.
            return difference <= Math.Max(0.002d, area * 0.0001d);
        }

        private static bool TryGetSurfaceMergePlane(SurfacePatch patch, out SurfaceMergePlane plane, out double largest)
        {
            plane = default;
            largest = 0d;
            foreach (int3 face in patch.Triangles)
            {
                float3 a = patch.Nodes[face.x].m_Position, b = patch.Nodes[face.y].m_Position, c = patch.Nodes[face.z].m_Position;
                double bx = (double)b.x - a.x, bz = (double)b.z - a.z, cx = (double)c.x - a.x, cz = (double)c.z - a.z;
                double cross = bx * cz - bz * cx;
                if (Math.Abs(cross) <= largest) continue;
                largest = Math.Abs(cross);
                plane = new SurfaceMergePlane { Origin = a, X = (((double)b.y - a.y) * cz - ((double)c.y - a.y) * bz) / cross,
                    Z = (bx * ((double)c.y - a.y) - cx * ((double)b.y - a.y)) / cross };
            }
            if (largest < 0.00001d || double.IsNaN(plane.X) || double.IsInfinity(plane.X) ||
                double.IsNaN(plane.Z) || double.IsInfinity(plane.Z)) return false;
            foreach (var node in patch.Nodes)
            {
                float3 point = node.m_Position;
                if (!math.all(math.isfinite(point)) || Math.Abs(plane.Height(point.xz) - point.y) > SurfaceMergeHeightTolerance) return false;
            }
            return true;
        }

        private static bool SurfacePatchFollowsMergePlane(SurfacePatch patch, SurfaceMergePlane plane)
        {
            foreach (var node in patch.Nodes)
                if (Math.Abs(plane.Height(node.m_Position.xz) - node.m_Position.y) > SurfaceMergeHeightTolerance) return false;
            return true;
        }
    }
}
