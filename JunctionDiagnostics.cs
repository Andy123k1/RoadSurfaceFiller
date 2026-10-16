using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Game.Net;
using Game.Prefabs;
using Unity.Entities;
using Unity.Mathematics;
using AreaNode = Game.Areas.Node;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private readonly Dictionary<Entity, HashSet<Entity>> m_JunctionDiagnosticOwners = new Dictionary<Entity, HashSet<Entity>>();
        private readonly HashSet<Entity> m_JunctionDiagnosticEdges = new HashSet<Entity>();
        private int m_LoggedJunctionPatchCoordinates, m_LoggedJunctionMaterialization;

        private void LogJunctionGeometry(Entity owner, Entity node, Entity main, EdgeNodeGeometry geometry,
            SurfaceLayout approach, SurfaceLayout layout, bool start)
        {
            if (main == Entity.Null || geometry.m_MiddleRadius > 0.01f || IsHighway(owner)) return;
            if (!m_JunctionDiagnosticOwners.TryGetValue(node, out HashSet<Entity> owners))
            {
                if (m_JunctionDiagnosticOwners.Count >= 4) return;
                bool mixed = false;
                foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                    if (IsRoadEdge(connection.m_Edge) && EntityManager.GetComponentData<PrefabRef>(connection.m_Edge).m_Prefab != main)
                        mixed = true;
                if (!mixed) return;
                m_JunctionDiagnosticOwners.Add(node, owners = new HashSet<Entity>());
            }
            if (!owners.Add(owner)) return;
            m_JunctionDiagnosticEdges.Add(owner);
            StringBuilder text = new StringBuilder("Junction geometry coordinates: {");
            text.Append("\"node\":").Append(node.Index).Append(",\"owner\":").Append(owner.Index);
            text.Append(",\"road\":").Append(DiagnosticQuote(m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab)));
            text.Append(",\"start\":").Append(start ? "true" : "false");
            text.Append(",\"width\":").Append(DiagnosticFloat(layout.Composition.m_Width));
            text.Append(",\"middleOffset\":").Append(DiagnosticFloat(layout.Composition.m_MiddleOffset));
            text.Append(",\"edgeHeights\":").Append(DiagnosticVector(layout.Composition.m_EdgeHeights));
            text.Append(",\"approachEdgeHeights\":").Append(DiagnosticVector(approach.Composition.m_EdgeHeights));
            text.Append(",\"syncSourceLeft\":").Append(DiagnosticVector(layout.Composition.m_SyncVertexOffsetsLeft));
            text.Append(",\"syncTargetLeft\":").Append(DiagnosticVector(geometry.m_SyncVertexTargetsLeft));
            text.Append(",\"syncSourceRight\":").Append(DiagnosticVector(layout.Composition.m_SyncVertexOffsetsRight));
            text.Append(",\"syncTargetRight\":").Append(DiagnosticVector(geometry.m_SyncVertexTargetsRight));
            text.Append(",\"leftOuter\":").Append(DiagnosticCurve(geometry.m_Left.m_Left));
            text.Append(",\"leftInner\":").Append(DiagnosticCurve(geometry.m_Left.m_Right));
            text.Append(",\"rightOuter\":").Append(DiagnosticCurve(geometry.m_Right.m_Right));
            text.Append(",\"rightInner\":").Append(DiagnosticCurve(geometry.m_Right.m_Left));
            text.Append(",\"middle\":").Append(DiagnosticCurve(geometry.m_Middle));
            if (EntityManager.HasComponent<NodeGeometry>(node))
                text.Append(",\"nodeHeight\":").Append(DiagnosticFloat(EntityManager.GetComponentData<NodeGeometry>(node).m_Position));
            text.Append(",\"roadBands\":[");
            for (int i = 0; i < layout.Roads.Count; i++)
            {
                if (i > 0) text.Append(',');
                RoadBand band = layout.Roads[i];
                text.Append(DiagnosticVector(new float4(band.Low, band.High, band.LowHeight, band.HighHeight)));
            }
            text.Append("],\"walkBands\":[");
            for (int i = 0; i < layout.Sidewalks.Count; i++)
            {
                if (i > 0) text.Append(',');
                LaneSpan band = layout.Sidewalks[i];
                text.Append(DiagnosticVector(new float3(band.Low, band.High, band.Height)));
            }
            text.Append("]}");
            Mod.Log.Info(text.ToString());
        }

        private void LogJunctionPatchCoordinates(Entity owner, Entity material, SurfacePatch patch)
        {
            if (patch.SmallRoundabout || !m_JunctionDiagnosticEdges.Contains(owner) || m_LoggedJunctionPatchCoordinates >= 48) return;
            m_LoggedJunctionPatchCoordinates++;
            StringBuilder text = new StringBuilder("Junction final patch coordinates: {");
            text.Append("\"owner\":").Append(owner.Index).Append(",\"material\":").Append(DiagnosticQuote(m_PrefabSystem.GetPrefabName(material)));
            text.Append(",\"exact\":").Append(patch.ExactBoundary ? "true" : "false");
            text.Append(",\"projectionMin\":").Append(DiagnosticFloat(patch.ProjectionMinimum));
            text.Append(",\"projectionMax\":").Append(DiagnosticFloat(patch.ProjectionMaximum));
            text.Append(",\"nodes\":[");
            for (int i = 0; i < patch.Count - 1; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(DiagnosticVector(patch.Nodes[i].m_Position));
            }
            text.Append("],\"triangles\":[");
            for (int i = 0; i < patch.Triangles.Count; i++)
            {
                if (i > 0) text.Append(',');
                int3 triangle = patch.Triangles[i];
                text.Append('[').Append(triangle.x).Append(',').Append(triangle.y).Append(',').Append(triangle.z).Append(']');
            }
            text.Append("]}");
            Mod.Log.Info(text.ToString());
        }

        private void LogJunctionMaterialization(Entity area, PendingArea pending)
        {
            if (pending.Patch.SmallRoundabout || !m_JunctionDiagnosticEdges.Contains(pending.Owner) || m_LoggedJunctionMaterialization >= 48) return;
            m_LoggedJunctionMaterialization++;
            var nodes = EntityManager.GetBuffer<AreaNode>(area, true);
            int expected = pending.Patch.Count - 1;
            float horizontalChange = 0f, verticalChange = 0f;
            for (int i = 0; i < math.min(expected, nodes.Length); i++)
            {
                horizontalChange = math.max(horizontalChange, math.distance(nodes[i].m_Position.xz, pending.Patch.Nodes[i].m_Position.xz));
                verticalChange = math.max(verticalChange, math.abs(nodes[i].m_Position.y - pending.Patch.Nodes[i].m_Position.y));
            }
            Mod.Log.Info("Junction native materialization: owner=" + pending.Owner.Index + "; area=" + area.Index +
                "; expected nodes=" + expected + "; native nodes=" + nodes.Length + "; max XZ change=" + DiagnosticFloat(horizontalChange) +
                " m; max Y change=" + DiagnosticFloat(verticalChange) + " m.");
        }

        private static string DiagnosticQuote(string value) => "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        private static string DiagnosticFloat(float value) => math.isfinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : "null";
        private static string DiagnosticVector(float3 value) => "[" + DiagnosticFloat(value.x) + "," + DiagnosticFloat(value.y) + "," + DiagnosticFloat(value.z) + "]";
        private static string DiagnosticVector(float4 value) => "[" + DiagnosticFloat(value.x) + "," + DiagnosticFloat(value.y) + "," + DiagnosticFloat(value.z) + "," + DiagnosticFloat(value.w) + "]";
        private static string DiagnosticCurve(Colossal.Mathematics.Bezier4x3 value) => "[" + DiagnosticVector(value.a) + "," + DiagnosticVector(value.b) + "," + DiagnosticVector(value.c) + "," + DiagnosticVector(value.d) + "]";
    }
}
