using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Jobs;

namespace SurfaceNetworkFilter
{
    internal static partial class RuntimeState
    {
        internal static bool JunctionCycleActive;
        internal static bool JunctionRestoreAutomatic;
        internal static Entity JunctionCycleTarget;
    }

    // Each press queues one explicit node. The existing junction-choice code
    // still owns saved finishes, bus-lane choices and connected-road rebuilds.
    internal sealed partial class JunctionCycleToolSystem : ToolBaseSystem
    {
        private SurfaceNetworkFilterSystem m_Surfaces;
        private Entity m_Hover;
        private bool m_OwnHighlight;
        private bool m_InputFocused;
        public override string toolID => "SurfaceNetworkFilterJunctionCycle";
        public override PrefabBase GetPrefab() => null;
        public override bool TrySetPrefab(PrefabBase prefab) => false;

        protected override void OnCreate()
        {
            base.OnCreate();
            requireNet = Layer.Road | Layer.PublicTransportRoad;
            m_Surfaces = World.GetOrCreateSystemManaged<SurfaceNetworkFilterSystem>();
        }

        internal void Begin(bool restoreAutomatic = false)
        {
            RuntimeState.JunctionMessage = "";
            RuntimeState.JunctionCycleTarget = Entity.Null;
            RuntimeState.SelectedJunction = Entity.Null;
            RuntimeState.JunctionEditor = new JunctionEditorState();
            RuntimeState.JunctionRestoreAutomatic = restoreAutomatic;
            RuntimeState.JunctionCycleActive = true;
            m_ToolSystem.activeTool = this;
        }

        internal void Cancel()
        {
            Dependency.Complete();
            SetHover(Entity.Null);
            RuntimeState.JunctionCycleActive = false;
            RuntimeState.JunctionRestoreAutomatic = false;
            RuntimeState.JunctionCycleTarget = Entity.Null;
            if (m_ToolSystem.activeTool == this) m_ToolSystem.activeTool = m_DefaultToolSystem;
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_InputFocused = true;
            EnableActions();
        }

        protected override void OnStopRunning()
        {
            Dependency.Complete();
            SetHover(Entity.Null);
            RuntimeState.JunctionCycleActive = false;
            RuntimeState.JunctionRestoreAutomatic = false;
            RuntimeState.JunctionCycleTarget = Entity.Null;
            m_InputFocused = false;
            base.OnStopRunning();
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context context)
        {
            base.OnGameLoaded(context);
            // Entity IDs from another city must not be used to clear highlights.
            m_Hover = Entity.Null;
            m_OwnHighlight = false;
            RuntimeState.JunctionCycleActive = false;
            RuntimeState.JunctionRestoreAutomatic = false;
            RuntimeState.JunctionCycleTarget = Entity.Null;
            if (m_ToolSystem.activeTool == this) m_ToolSystem.activeTool = m_DefaultToolSystem;
        }

        protected override void OnFocusChanged(bool focus) { m_InputFocused = focus; EnableActions(); }
        private void EnableActions()
        {
            applyAction.shouldBeEnabled = m_InputFocused && RuntimeState.JunctionCycleActive;
            cancelAction.shouldBeEnabled = m_InputFocused && RuntimeState.JunctionCycleActive;
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Net;
            m_ToolRaycastSystem.netLayerMask = Layer.Road | Layer.PublicTransportRoad;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            inputDeps.Complete();
            EnableActions();
            if (cancelAction.WasPressedThisFrame()) { Cancel(); return default; }

            Entity target = Entity.Null;
            if (GetRaycastResult(out Entity hit, out RaycastHit raycast))
            {
                target = m_Surfaces.FindJunctionCycleTarget(raycast.m_HitEntity, raycast.m_HitPosition);
                if (target == Entity.Null) target = m_Surfaces.FindJunctionCycleTarget(hit, raycast.m_HitPosition);
            }
            SetHover(target);
            if (target != Entity.Null) RuntimeState.JunctionCycleTarget = target;

            // Do not cycle continuously while dragging or holding the mouse.
            if (applyAction.WasPressedThisFrame())
            {
                if (target == Entity.Null)
                    RuntimeState.JunctionMessage = RuntimeState.JunctionRestoreAutomatic
                        ? "Point at a junction or roundabout, then left-click to restore its automatic finish."
                        : "Point at a junction or roundabout, then left-click to cycle its surface.";
                else
                    RuntimeState.JunctionRequests.Add(new RuntimeState.JunctionRequest(target, Entity.Null,
                        RuntimeState.JunctionRestoreAutomatic ? JunctionAction.Automatic : JunctionAction.NextRoad));
            }
            return default;
        }

        private void SetHover(Entity target)
        {
            if (target == m_Hover) return;
            if (m_OwnHighlight && EntityManager.Exists(m_Hover))
            {
                EntityManager.RemoveComponent<Highlighted>(m_Hover);
                MarkHighlightChanged(m_Hover);
            }
            m_Hover = target;
            m_OwnHighlight = false;
            if (target == Entity.Null || !EntityManager.Exists(target) || EntityManager.HasComponent<Highlighted>(target)) return;
            EntityManager.AddComponent<Highlighted>(target);
            m_OwnHighlight = true;
            MarkHighlightChanged(target);
        }

        private void MarkHighlightChanged(Entity target)
        {
            if (!EntityManager.HasComponent<BatchesUpdated>(target)) EntityManager.AddComponent<BatchesUpdated>(target);
        }
    }
}
