using System;
using System.Collections.Generic;
using System.Reflection;
using Game;
using Game.Common;
using Game.Input;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;

namespace SurfaceNetworkFilter
{
    // Observe the native Replace input and hit result without changing its
    // tool, road type, raycast masks, input bindings or upgrade controls.
    internal sealed partial class RoadSurfaceReplaceSystem : GameSystemBase
    {
        // The getter is protected, so read the existing native action rather
        // than creating a second input binding or changing its enabled state.
        private static readonly PropertyInfo NativeApplyAction = typeof(ToolBaseSystem).GetProperty("applyAction",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo NativeIncorrectApply = typeof(NetToolSystem).GetField("m_IncorrectApply",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly HashSet<Entity> m_Stroke = new HashSet<Entity>();
        private string m_LastPreset;

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context context)
        { base.OnGameLoaded(context); m_Stroke.Clear(); m_LastPreset = null; }

        protected override void OnUpdate()
        {
            ToolSystem tools = World.GetOrCreateSystemManaged<ToolSystem>();
            NetToolSystem net = World.GetOrCreateSystemManaged<NetToolSystem>();
            if (tools.activeTool != net || !(tools.activePrefab is RoadPrefab) || net.actualMode != NetToolSystem.Mode.Replace)
            { m_Stroke.Clear(); return; }
            if (InputManager.instance.mouseOverUI || InputManager.instance.hasInputFieldFocus)
            { m_Stroke.Clear(); return; }
            IProxyAction apply = NativeApplyAction?.GetValue(net) as IProxyAction;
            IProxyAction alreadyThisType = NativeIncorrectApply?.GetValue(net) as IProxyAction;
            bool pressed = Pressed(apply) || Pressed(alreadyThisType);
            if (!pressed) { m_Stroke.Clear(); return; }
            string id = RuntimeState.NewRoadPresetId;
            if (id != m_LastPreset) { m_LastPreset = id; m_Stroke.Clear(); }
            if (!World.GetOrCreateSystemManaged<ToolRaycastSystem>().GetRaycastResult(out RaycastResult hit)) return;
            SurfaceNetworkFilterSystem surfaces = World.GetOrCreateSystemManaged<SurfaceNetworkFilterSystem>();
            Entity edge = surfaces.ReplaceSurfaceRoad(hit.m_Owner, hit.m_Hit.m_HitEntity);
            if (edge == Entity.Null || m_Stroke.Contains(edge)) return;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
            bool sameType = World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefabName(prefab) == tools.activePrefab.name;
            // A surface-only change to the same prefab is valid even when the
            // native tool reports "already this type". A road-type change must
            // first be accepted by the native replacement tool.
            bool nativeApply = tools.applyMode == ApplyMode.Apply;
            if (!sameType && !nativeApply) return;
            if (surfaces.RequestSurfaceReplace(edge, id, nativeApply)) m_Stroke.Add(edge);
        }

        private static bool Pressed(IProxyAction action) => action != null && action.enabled &&
            (action.IsPressed() || action.WasPressedThisFrame());
    }

    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private sealed class SurfaceReplaceRequest
        {
            internal Entity Original;
            internal int Frame;
            internal SegmentUpgradePreset Preset;
            internal bool NativePending;
            internal readonly HashSet<Entity> NativeTargets = new HashSet<Entity>();
        }
        private readonly Dictionary<Entity, SurfaceReplaceRequest> m_SurfaceReplaceRequests = new Dictionary<Entity, SurfaceReplaceRequest>();

        internal Entity ReplaceSurfaceRoad(Entity owner, Entity hit)
        {
            if (IsRoadEdge(owner)) return owner;
            if (IsRoadEdge(hit)) return hit;
            if (EntityManager.HasComponent<Temp>(owner))
            { Entity original = EntityManager.GetComponentData<Temp>(owner).m_Original; if (IsRoadEdge(original)) return original; }
            return Entity.Null;
        }

        internal bool RequestSurfaceReplace(Entity edge, string id, bool nativePending)
        {
            Setting setting = Mod.ActiveSetting;
            if (setting == null || !IsInLoadedGame() || !IsRoadEdge(edge)) return false;
            SegmentUpgradePreset preset = setting.GetSegmentPresets().Find(item => item.Rule.Id == id);
            if (!string.IsNullOrEmpty(id) && preset == null) return false;
            m_SurfaceReplaceRequests[edge] = new SurfaceReplaceRequest { Original = edge, Frame = m_Frame,
                Preset = preset, NativePending = nativePending };
            return true;
        }

        private void ApplySurfaceReplaceRequests(Setting setting)
        {
            ToolSystem tools = World.GetOrCreateSystemManaged<ToolSystem>();
            NetToolSystem net = World.GetOrCreateSystemManaged<NetToolSystem>();
            bool replacing = tools.activeTool == net && tools.activePrefab is RoadPrefab && net.actualMode == NetToolSystem.Mode.Replace;
            foreach (Entity original in new List<Entity>(m_SurfaceReplaceRequests.Keys))
            {
                SurfaceReplaceRequest request = m_SurfaceReplaceRequests[original];
                // Only targets captured by the accepted Replace transaction
                // may outlive that transaction. An unresolved request must not
                // attach to a later Straight/Curve drawing operation.
                if (request.NativePending && request.NativeTargets.Count == 0 && (!replacing || m_Frame - request.Frame > 2))
                { m_SurfaceReplaceRequests.Remove(original); continue; }
                if (!request.NativePending) request.NativeTargets.Add(original);
                if (request.NativeTargets.Count == 0)
                {
                    if (m_Frame - request.Frame > 240) m_SurfaceReplaceRequests.Remove(original);
                    continue;
                }
                bool waiting = false;
                foreach (Entity target in new List<Entity>(request.NativeTargets))
                {
                    if (!IsRoadEdge(target)) { waiting = true; continue; }
                    try
                    {
                        if (request.Preset == null)
                        {
                            Mod.Log.Info("Explicit Replace restored vanilla surfaces: edge=" + target.Index + "; original=" + original.Index + ".");
                            RestoreRoadToVanilla(target);
                        }
                        else SaveSegmentRule(target, new SegmentRuleDraft { Rule = request.Preset.Rule,
                            FillBusShoulders = request.Preset.FillBusShoulders });
                        if (!setting.Enabled) { setting.Enabled = true; setting.ApplyAndSave(); }
                        QueueRoadStyleRebuild(target);
                        request.NativeTargets.Remove(target);
                        RuntimeState.CityRulesMessage = request.Preset == null ? "Road restored to vanilla surfaces." :
                            "Applied “" + request.Preset.Rule.Name + "” with Replace. Connected road surfaces retained.";
                    }
                    catch (ArgumentException exception)
                    { RuntimeState.CityRulesMessage = "Surface replacement could not be applied: " + exception.Message; request.NativeTargets.Remove(target); }
                }
                if (!waiting || m_Frame - request.Frame > 240) m_SurfaceReplaceRequests.Remove(original);
            }
        }
    }
}
