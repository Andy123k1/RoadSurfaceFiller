using System.Collections.Generic;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Jobs;

namespace SurfaceNetworkFilter
{
    // Selection is temporary. Shoulder changes and vanilla restoration only
    // affect the selected edge entities after an explicit commit.
    internal sealed partial class SegmentUpgradeToolSystem : ToolBaseSystem
    {
        private readonly HashSet<Entity> m_Selected = new HashSet<Entity>();
        private readonly HashSet<Entity> m_Stroke = new HashSet<Entity>();
        private readonly HashSet<Entity> m_Highlighted = new HashSet<Entity>();
        private Entity m_Hover;
        private SegmentUpgradeMode m_Mode;
        private string m_BusSurface;
        private bool m_RemoveStroke;
        private bool m_InputFocused;
        private bool m_EditableRequestPending;
        public override string toolID => "SurfaceNetworkFilterUpgrade";
        public override PrefabBase GetPrefab() => null;
        public override bool TrySetPrefab(PrefabBase prefab) => false;

        protected override void OnCreate() { base.OnCreate(); requireNet = Layer.Road | Layer.PublicTransportRoad; }
        internal void Begin(SegmentUpgradeMode mode, string busSurface)
        {
            ClearSelection(); m_Mode = mode; RuntimeState.UpgradeMode = mode; m_BusSurface = busSurface ?? "";
            RuntimeState.UpgradeMessage = "";
            RuntimeState.UpgradeLabel = mode == SegmentUpgradeMode.BusShoulders ? "Fill bus-lane shoulders" :
                mode == SegmentUpgradeMode.MakeEditable ? "Make generated surfaces editable" :
                mode == SegmentUpgradeMode.ResumeAutomatic ? "Resume automatic surfaces" :
                mode == SegmentUpgradeMode.RemoveBusShoulders ? "Remove bus-lane shoulder fill" : "Restore vanilla surfaces";
            RuntimeState.UpgradeActive = true; m_ToolSystem.activeTool = this;
        }
        internal void Cancel()
        {
            ClearSelection(); RuntimeState.UpgradeActive = false;
            if (m_ToolSystem.activeTool == this) m_ToolSystem.activeTool = m_DefaultToolSystem;
        }
        internal void Commit()
        {
            if (!RuntimeState.UpgradeActive || m_ToolSystem.activeTool != this || m_Selected.Count == 0) return;
            RuntimeState.UpgradeRequests.Add(new RuntimeState.SegmentUpgradeRequest {
                Edges = new List<Entity>(m_Selected), Mode = m_Mode, BusSurface = m_BusSurface
            });
            RuntimeState.UpgradeMessage = "Applying changes to selected roads…";
            Cancel();
        }
        internal void ClearSelection()
        {
            m_EditableRequestPending = false;
            Dependency.Complete();
            foreach (Entity edge in new List<Entity>(m_Highlighted)) Highlight(edge, false);
            m_Selected.Clear(); m_Stroke.Clear(); m_Hover = Entity.Null;
            RuntimeState.UpgradeSelectionCount = 0;
        }
        internal void EditableRoadRequestComplete() => m_EditableRequestPending = false;
        protected override void OnStartRunning() { base.OnStartRunning(); m_InputFocused = true; EnableUpgradeActions(); }
        protected override void OnStopRunning() { ClearSelection(); RuntimeState.UpgradeActive = false; m_InputFocused = false; base.OnStopRunning(); }
        protected override void OnFocusChanged(bool focus) { m_InputFocused = focus; EnableUpgradeActions(); }
        private void EnableUpgradeActions()
        {
            applyAction.shouldBeEnabled = m_InputFocused && RuntimeState.UpgradeActive;
            cancelAction.shouldBeEnabled = m_InputFocused && RuntimeState.UpgradeActive;
        }
        public override void InitializeRaycast()
        {
            base.InitializeRaycast(); m_ToolRaycastSystem.typeMask = TypeMask.Net;
            m_ToolRaycastSystem.netLayerMask = Layer.Road | Layer.PublicTransportRoad;
        }
        private bool RoadEdge(Entity edge) => edge != Entity.Null && EntityManager.Exists(edge) &&
            EntityManager.HasComponent<Edge>(edge) && EntityManager.HasComponent<Curve>(edge) &&
            EntityManager.HasComponent<PrefabRef>(edge) && EntityManager.HasComponent<Composition>(edge) &&
            !EntityManager.HasComponent<Deleted>(edge) && !EntityManager.HasComponent<Temp>(edge) &&
            EntityManager.HasComponent<RoadData>(EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab);
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            inputDeps.Complete();
            EnableUpgradeActions();
            if (cancelAction.WasPressedThisFrame()) { Cancel(); return default; }
            Entity hover = Entity.Null;
            if (GetRaycastResult(out Entity hit, out RaycastHit _) && RoadEdge(hit)) hover = hit;
            if (m_Hover != hover)
            {
                if (!m_Selected.Contains(m_Hover)) Highlight(m_Hover, false);
                m_Hover = hover; Highlight(hover, true);
            }
            if (m_Mode == SegmentUpgradeMode.MakeEditable)
            {
                if (!m_EditableRequestPending && applyAction.WasPressedThisFrame() && hover != Entity.Null)
                {
                    m_EditableRequestPending = true;
                    RuntimeState.UpgradeMessage = "Preparing this road's surfaces for editing…";
                    RuntimeState.UpgradeRequests.Add(new RuntimeState.SegmentUpgradeRequest {
                        Edges = new List<Entity> { hover }, Mode = m_Mode, BusSurface = ""
                    });
                }
                return default;
            }
            if (applyAction.WasPressedThisFrame())
            { m_Stroke.Clear(); m_RemoveStroke = m_Selected.Contains(hover); }
            if ((applyAction.IsPressed() || applyAction.WasPressedThisFrame()) && hover != Entity.Null && m_Stroke.Add(hover))
            {
                if (m_RemoveStroke) { m_Selected.Remove(hover); Highlight(hover, false); }
                else { m_Selected.Add(hover); Highlight(hover, true); }
                RuntimeState.UpgradeSelectionCount = m_Selected.Count;
            }
            return default;
        }
        private void Highlight(Entity edge, bool show)
        {
            if (edge == Entity.Null || !EntityManager.Exists(edge)) { m_Highlighted.Remove(edge); return; }
            if (show && !EntityManager.HasComponent<Highlighted>(edge))
            { EntityManager.AddComponent<Highlighted>(edge); m_Highlighted.Add(edge); }
            else if (!show && m_Highlighted.Remove(edge)) EntityManager.RemoveComponent<Highlighted>(edge);
            else return;
            if (!EntityManager.HasComponent<BatchesUpdated>(edge)) EntityManager.AddComponent<BatchesUpdated>(edge);
        }
    }
}
