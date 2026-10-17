using System.Collections.Generic;
using Game;
using Game.Areas;
using Game.Common;
using Game.Input;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace SurfaceNetworkFilter
{
    internal static partial class RuntimeState
    {
        internal static bool ManualSurfaceEditorActive;
        internal static string ManualSurfaceEditorMessage = "";
    }

    // The native editor handles vertices and preview geometry. Its definitions
    // normally use one selected prefab for every touched area. Restore each
    // converted area's own prefab before native preview/materialization.
    internal sealed partial class ManualSurfaceEditorSystem : GameSystemBase
    {
        private ToolSystem m_Tools;
        private AreaToolSystem m_Editor;
        private EntityQuery m_Definitions, m_TempAreas;
        private IProxyAction m_Cancel, m_MouseCancel;
        private int m_CancelDragFrame = -1;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Tools = World.GetOrCreateSystemManaged<ToolSystem>();
            m_Editor = World.GetOrCreateSystemManaged<AreaToolSystem>();
            m_Definitions = GetEntityQuery(ComponentType.ReadWrite<CreationDefinition>(), ComponentType.ReadOnly<Node>());
            m_TempAreas = GetEntityQuery(ComponentType.ReadOnly<Area>(), ComponentType.ReadOnly<Temp>(),
                ComponentType.Exclude<Deleted>());
            if (InputManager.instance.TryFindAction("Tool", "Cancel", out ProxyAction cancel)) m_Cancel = cancel;
            if (InputManager.instance.TryFindAction("Tool", "Mouse Cancel", out ProxyAction mouseCancel)) m_MouseCancel = mouseCancel;
        }

        internal void Stop()
        {
            bool wasActive = RuntimeState.ManualSurfaceEditorActive;
            RuntimeState.ManualSurfaceEditorActive = false;
            if (wasActive && m_Tools.activeTool == m_Editor)
                m_Tools.activeTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context context)
        {
            base.OnGameLoaded(context);
            RuntimeState.ManualSurfaceEditorActive = false;
            RuntimeState.ManualSurfaceEditorMessage = "";
            m_CancelDragFrame = -1;
        }

        private Entity EditableOriginal(Entity area)
        {
            for (int depth = 0; depth < 4 && EntityManager.Exists(area); depth++)
            {
                if (EntityManager.HasComponent<EditableGeneratedSurface>(area)) return area;
                if (!EntityManager.HasComponent<Temp>(area)) break;
                area = EntityManager.GetComponentData<Temp>(area).m_Original;
            }
            return Entity.Null;
        }

        protected override void OnUpdate()
        {
            // Carry provenance onto native replacement previews before Apply
            // keeps their entity and removes Temp. This also works after load.
            using (NativeArray<Entity> areas = m_TempAreas.ToEntityArray(Allocator.Temp))
                foreach (Entity area in areas)
                    if (!EntityManager.HasComponent<EditableGeneratedSurface>(area) &&
                        EditableOriginal(EntityManager.GetComponentData<Temp>(area).m_Original) != Entity.Null)
                        EntityManager.AddComponent<EditableGeneratedSurface>(area);

            if (!RuntimeState.ManualSurfaceEditorActive) return;
            if (m_Tools.activeTool != m_Editor) { RuntimeState.ManualSurfaceEditorActive = false; return; }
            if ((m_Cancel?.WasPressedThisFrame() ?? false) || (m_MouseCancel?.WasPressedThisFrame() ?? false))
            {
                if (m_Editor.state != AreaToolSystem.State.Default) m_CancelDragFrame = UnityEngine.Time.frameCount;
                else if (m_CancelDragFrame != UnityEngine.Time.frameCount) { Stop(); return; }
            }

            // Only filter definitions attributable to the area tool's current
            // gesture. Other systems can update areas during the same frame.
            NativeList<ControlPoint> points = m_Editor.GetControlPoints(out NativeList<ControlPoint> starts, out Unity.Jobs.JobHandle pending);
            pending.Complete();
            HashSet<Entity> touched = new HashSet<Entity>();
            foreach (ControlPoint point in points)
                if (point.m_OriginalEntity != Entity.Null) touched.Add(point.m_OriginalEntity);
            foreach (ControlPoint point in starts)
                if (point.m_OriginalEntity != Entity.Null) touched.Add(point.m_OriginalEntity);

            using (NativeArray<Entity> definitions = m_Definitions.ToEntityArray(Allocator.Temp))
                foreach (Entity entity in definitions)
                {
                    CreationDefinition definition = EntityManager.GetComponentData<CreationDefinition>(entity);
                    // Automatic road generation uses permanent definitions and
                    // continues independently while the native tool is active.
                    if ((definition.m_Flags & CreationFlags.Permanent) != 0) continue;
                    if (definition.m_Original != Entity.Null && !touched.Contains(definition.m_Original)) continue;
                    Entity original = EditableOriginal(definition.m_Original);
                    if (original == Entity.Null)
                    {
                        // This session reshapes converted areas only. Ignore
                        // new-area gestures and edits of unrelated native paint.
                        if (definition.m_Original != Entity.Null ||
                            (m_Editor.state == AreaToolSystem.State.Create && definition.m_Owner == Entity.Null))
                        {
                            EntityManager.DestroyEntity(entity);
                            RuntimeState.ManualSurfaceEditorMessage = "Edit a converted surface boundary. To unlock another road, choose Edit another segment.";
                        }
                        continue;
                    }
                    if (!EntityManager.HasComponent<PrefabRef>(original)) continue;
                    definition.m_Prefab = EntityManager.GetComponentData<PrefabRef>(original).m_Prefab;
                    EntityManager.SetComponentData(entity, definition);
                }
        }
    }
}
