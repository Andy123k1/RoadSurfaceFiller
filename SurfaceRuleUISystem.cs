using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.UI.Binding;
using Game;
using Game.SceneFlow;
using Game.UI;
using Newtonsoft.Json;

namespace SurfaceNetworkFilter
{
    // Uses the game's UI bindings and GameTopLeft mod hook. Editing a draft
    // never changes placement; a validated Save commits the complete rule.
    internal sealed partial class SurfaceRuleUISystem : UISystemBase
    {
        private const string Group = "SurfaceNetworkFilter";
        private ValueBinding<bool> m_Open;
        private ValueBinding<string> m_State;
        private ValueBinding<bool> m_RoadToolActive;
        private int m_EditorRequest;
        private bool m_RoadReplaceMode;
        private string m_Message = "";
        private int m_Frame;
        private int m_SaveResponse;
        private string m_SavedRuleId = "";
        private bool m_JunctionActionPending;

        private static bool IsInLoadedCity()
        {
            // UISystemBase.gameMode is a supported-mode mask, not the mode
            // currently running. Check the scene manager before editing.
            GameManager manager = GameManager.instance;
            return manager != null && manager.gameMode == GameMode.Game && !manager.isGameLoading;
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            AddBinding(m_Open = new ValueBinding<bool>(Group, "open", false));
            AddBinding(m_State = new ValueBinding<string>(Group, "state", "{}"));
            AddBinding(m_RoadToolActive = new ValueBinding<bool>(Group, "roadToolActive", false));
            AddBinding(new TriggerBinding(Group, "togglePanel", () => { m_EditorRequest = 0; Publish(); m_Open.Update(!m_Open.value); }));
            AddBinding(new TriggerBinding(Group, "closePanel", () => { UpgradeTool().Cancel(); m_Open.Update(false); }));
            AddBinding(new TriggerBinding<string>(Group, "saveRule", SaveRule));
            AddBinding(new TriggerBinding<string>(Group, "deleteRule", DeleteRule));
            AddBinding(new TriggerBinding<string, bool>(Group, "enableRule", EnableRule));
            AddBinding(new TriggerBinding(Group, "refreshCatalog", () => { Mod.RequestSurfaceRefresh(); m_Message = "Surface list refresh requested."; Publish(); }));
            AddBinding(new TriggerBinding(Group, "rebuild", () => { Mod.RequestRebuild(); m_Message = "City surfaces queued for rebuilding."; Publish(); }));
            AddBinding(new TriggerBinding(Group, "applyCityRules", ApplyCityRules));
            AddBinding(new TriggerBinding(Group, "restoreAllRoads", RestoreAllRoads));
            AddBinding(new TriggerBinding<string>(Group, "newRoadPreset", SelectNewRoadPreset));
            AddBinding(new TriggerBinding(Group, "openSegmentPresets", () => { m_EditorRequest++; Publish(); m_Open.Update(true); }));
            AddBinding(new TriggerBinding<int>(Group, "junctionEnd", SetJunctionEnd));
            AddBinding(new TriggerBinding(Group, "junctionNextRoad", () => RequestJunctionAction(JunctionAction.NextRoad)));
            AddBinding(new TriggerBinding(Group, "junctionUseSelected", () => RequestJunctionAction(JunctionAction.UseSelectedRoad)));
            AddBinding(new TriggerBinding(Group, "junctionAutomatic", () => RequestJunctionAction(JunctionAction.Automatic)));
            AddSegmentRuleBindings();
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();
            Game.Tools.ToolSystem tools = World.GetOrCreateSystemManaged<Game.Tools.ToolSystem>();
            bool roadTool = IsInLoadedCity() && tools.activeTool is Game.Tools.NetToolSystem && tools.activePrefab is Game.Prefabs.RoadPrefab;
            bool replacing = roadTool && World.GetOrCreateSystemManaged<Game.Tools.NetToolSystem>().actualMode == Game.Tools.NetToolSystem.Mode.Replace;
            if (m_RoadToolActive.value != roadTool) { m_RoadToolActive.Update(roadTool); if (roadTool) Publish(); }
            if (m_RoadReplaceMode != replacing) { m_RoadReplaceMode = replacing; Publish(); }
            if (++m_Frame % 15 == 0 && (m_Open.value || roadTool))
            {
                if (m_JunctionActionPending && RuntimeState.JunctionRequests.Count == 0)
                {
                    m_JunctionActionPending = false;
                    m_Message = RuntimeState.JunctionMessage;
                }
                Publish();
            }
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context context)
        {
            base.OnGameLoaded(context);
            m_Open.Update(false); m_Message = ""; m_EditorRequest = 0; m_JunctionActionPending = false; Publish();
        }

        private void SetJunctionEnd(int value)
        {
            Setting setting = Mod.ActiveSetting;
            if (setting == null || !IsInLoadedCity() || !Enum.IsDefined(typeof(JunctionEnd), value)) return;
            setting.JunctionEnd = (JunctionEnd)value;
            setting.ApplyAndSave();
            Publish();
        }

        private void RequestJunctionAction(JunctionAction action)
        {
            Setting setting = Mod.ActiveSetting;
            if (setting == null || !IsInLoadedCity()) return;
            // Wait for the main-loop selection snapshot after changing ends,
            // so a quick follow-up click cannot edit the previous junction.
            if (!RuntimeState.JunctionEditor.Available || RuntimeState.JunctionEditor.End != setting.JunctionEnd)
            {
                m_Message = "Select a connected road near the junction with the Info tool first.";
                Publish(); return;
            }
            RuntimeState.RequestJunctionAction(action);
            m_JunctionActionPending = true;
            m_Message = "Junction pavement change queued.";
            Publish();
        }

        private void SaveRule(string json)
        {
            Setting setting = Mod.ActiveSetting;
            if (setting == null || !IsInLoadedCity()) return;
            m_SaveResponse++; m_SavedRuleId = "";
            try
            {
                if (json == null || json.Length > 16000) throw new ArgumentException("The rule could not be read.");
                SurfaceRule rule = JsonConvert.DeserializeObject<SurfaceRule>(json);
                if (rule == null || rule.Assignments == null || rule.Assignments.Count > 4 ||
                    !Enum.IsDefined(typeof(RoadCategory), rule.Category))
                    throw new ArgumentException("Choose valid surface targets and a road category.");
                rule.SeparateBikeLanes = rule.SeparateBikeLanes ?? false;
                rule.SeparateBusLanes = rule.SeparateBusLanes ?? false;
                if (rule.FillBusShoulders) rule.SeparateBusLanes = true;
                rule.LegacyLaneFallback = false;
                if (rule.Assignments.Count == 0 && rule.SeparateBikeLanes != true && rule.SeparateBusLanes != true)
                    throw new ArgumentException("Select at least one surface target or lane separation option.");
                HashSet<SurfaceTarget> targets = new HashSet<SurfaceTarget>();
                foreach (SurfaceAssignment item in rule.Assignments)
                {
                    if (item == null || !Enum.IsDefined(typeof(SurfaceTarget), item.Target) || item.Target == SurfaceTarget.Both ||
                        !targets.Add(item.Target)) throw new ArgumentException("Each surface target can be selected once.");
                    item.Surface = item.Surface ?? "";
                    bool lane = item.Target == SurfaceTarget.BikeLanes || item.Target == SurfaceTarget.BusLanes;
                    if (!lane && string.IsNullOrWhiteSpace(item.Surface))
                        throw new ArgumentException("Choose a surface for every selected road or sidewalk target.");
                    if (rule.Enabled && rule.TargetActive(item.Target) && item.Surface.Length > 0 &&
                        !RuntimeState.TryGetSurface(item.Surface, out _))
                        throw new ArgumentException("A selected surface is unavailable. Refresh the list or choose another surface.");
                }
                if (rule.FillBusShoulders && string.IsNullOrWhiteSpace(rule.SurfaceFor(SurfaceTarget.BusLanes)))
                    throw new ArgumentException("Choose a bus-lane surface to fill the paved shoulders.");
                rule.RoadPrefab = rule.RoadPrefab ?? "";
                if (rule.RoadPrefab.Length > 0 && !RuntimeState.RoadNames.Contains(rule.RoadPrefab))
                    throw new ArgumentException("That road type is unavailable. Choose an installed road type.");
                rule.Name = (rule.Name ?? "").Trim();
                if (rule.Name.Length == 0) rule.Name = "Surface rule — " + (rule.RoadPrefab.Length > 0 ? rule.RoadPrefab : rule.Category.ToString());
                if (rule.Name.Length > 120) rule.Name = rule.Name.Substring(0, 120);
                List<SurfaceRule> rules = new List<SurfaceRule>(setting.GetRules());
                int index = rules.FindIndex(existing => existing.Id == rule.Id);
                if (index >= 0) rules[index] = rule;
                else
                {
                    rule.Id = Guid.NewGuid().ToString("N");
                    rules.Add(rule);
                }
                setting.StoreRules(rules);
                m_SavedRuleId = rule.Id;
                m_Message = "Saved “" + rule.Name + "”. Use Apply city rules to update existing roads.";
                Publish();
            }
            catch (Exception exception) when (exception is JsonException || exception is ArgumentException)
                { m_Message = exception.Message; Publish(); }
        }

        private void DeleteRule(string id)
        {
            Setting setting = Mod.ActiveSetting;
            if (setting == null || !IsInLoadedCity()) return;
            List<SurfaceRule> rules = new List<SurfaceRule>(setting.GetRules());
            if (rules.RemoveAll(rule => rule.Id == id) == 0) return;
            setting.StoreRules(rules);
            m_Message = "Rule deleted from the library. Use Apply city rules to update the city.";
            Publish();
        }

        private void EnableRule(string id, bool enabled)
        {
            Setting setting = Mod.ActiveSetting;
            if (setting == null || !IsInLoadedCity()) return;
            List<SurfaceRule> rules = new List<SurfaceRule>(setting.GetRules());
            SurfaceRule rule = rules.Find(item => item.Id == id);
            if (rule == null) return;
            if (enabled && rule.ActiveAssignments.Any(item => item.Surface.Length > 0 && !RuntimeState.TryGetSurface(item.Surface, out _)))
                { m_Message = "This surface is unavailable. Edit the rule and choose an installed surface."; Publish(); return; }
            rule.Enabled = enabled;
            setting.StoreRules(rules);
            m_Message = (enabled ? "Rule enabled." : "Rule disabled.") + " Use Apply city rules to update existing roads.";
            Publish();
        }

        private void ApplyCityRules()
        {
            Setting setting = Mod.ActiveSetting;
            if (setting == null || !IsInLoadedCity() || RuntimeState.RestoreAllRoadsRequested || RuntimeState.RestoringAllRoads) return;
            if (setting.GetRules().Any(rule => rule.Enabled && rule.ActiveAssignments.Any(item =>
                item.Surface.Length > 0 && !RuntimeState.TryGetSurface(item.Surface, out _))))
            { m_Message = "An enabled city rule uses an unavailable surface. Edit it or disable it before applying."; Publish(); return; }
            RuntimeState.ApplyCityRulesRequested = true;
            m_Message = "Applying city rules to existing roads. Segment overrides stay in place.";
            Publish();
        }

        private void RestoreAllRoads()
        {
            if (!IsInLoadedCity() || Mod.ActiveSetting == null || RuntimeState.RestoreAllRoadsRequested || RuntimeState.RestoringAllRoads) return;
            World.GetOrCreateSystemManaged<SegmentUpgradeToolSystem>().Cancel();
            RuntimeState.ApplyCityRulesRequested = false;
            RuntimeState.RestoreAllRoadsRequested = true;
            RuntimeState.UpgradeMessage = "Restoring all existing roads to vanilla surfaces…";
            m_Message = "";
            Publish();
        }

        private void SelectNewRoadPreset(string id)
        {
            if (!IsInLoadedCity()) return;
            SegmentUpgradePreset preset = Mod.ActiveSetting?.GetSegmentPresets().Find(item => item.Rule.Id == id);
            if (!string.IsNullOrEmpty(id) && (preset == null || preset.Rule.ActiveAssignments.Any(item =>
                item.Surface.Length > 0 && !RuntimeState.TryGetSurface(item.Surface, out _))))
            { m_Message = "That preset is unavailable. Choose another preset or edit its surfaces."; Publish(); return; }
            RuntimeState.NewRoadPresetId = id ?? "";
            Publish();
        }

        private void Publish()
        {
            Setting setting = Mod.ActiveSetting;
            if (setting == null) return;
            JunctionEditorState junction = RuntimeState.JunctionEditor;
            bool junctionReady = IsInLoadedCity() && junction.End == setting.JunctionEnd;
            m_State.Update(JsonConvert.SerializeObject(new {
                enabled = setting.Enabled,
                newRoadPresetId = RuntimeState.NewRoadPresetId,
                roadToolReplace = m_RoadReplaceMode,
                editorRequest = m_EditorRequest,
                cityRulesPending = RuntimeState.CityRulesPending,
                restoringAllRoads = RuntimeState.RestoreAllRoadsRequested || RuntimeState.RestoringAllRoads,
                appliedCityRuleCount = setting.RuleCount,
                cityMessage = RuntimeState.CityRulesMessage,
                rules = setting.GetRules().Select(rule => new {
                    id = rule.Id, name = rule.Name, enabled = rule.Enabled,
                    category = (int)rule.Category, roadPrefab = rule.RoadPrefab,
                    separateBikeLanes = rule.SeparateBikeLanes == true, separateBusLanes = rule.SeparateBusLanes == true,
                    fillBusShoulders = rule.FillBusShoulders,
                    assignments = rule.Assignments.Select(item => new { target = (int)item.Target, surface = item.Surface,
                        available = item.Surface.Length == 0 || RuntimeState.TryGetSurface(item.Surface, out _) }).ToArray(),
                    available = rule.ActiveAssignments.All(item => item.Surface.Length == 0 || RuntimeState.TryGetSurface(item.Surface, out _))
                }).ToArray(),
                surfaces = RuntimeState.GetSurfaceNames("").ToArray(),
                roadTypes = RuntimeState.RoadNames.ToArray(),
                status = RuntimeState.Status,
                message = m_Message,
                saveResponse = m_SaveResponse,
                savedRuleId = m_SavedRuleId,
                segment = SegmentRuleState(),
                segmentPresets = setting.GetSegmentPresets().Select(item => new {
                    id = item.Rule.Id, name = item.Rule.Name, enabled = true, fillBusShoulders = item.FillBusShoulders,
                    updateExistingSegments = item.UpdateExistingSegments,
                    separateBikeLanes = item.Rule.SeparateBikeLanes, separateBusLanes = item.Rule.SeparateBusLanes,
                    assignments = item.Rule.Assignments.Select(target => new { target = (int)target.Target, surface = target.Surface }).ToArray(),
                    available = item.Rule.Assignments.All(target => target.Surface.Length == 0 || RuntimeState.TryGetSurface(target.Surface, out _))
                }).ToArray(),
                upgrade = new { active = RuntimeState.UpgradeActive, count = RuntimeState.UpgradeSelectionCount, mode = (int)RuntimeState.UpgradeMode,
                    label = RuntimeState.UpgradeLabel, message = RuntimeState.UpgradeMessage },
                segmentRuleCount = RuntimeState.SegmentRuleCount,
                activeSegmentRuleCount = RuntimeState.ActiveSegmentRuleCount,
                junction = new {
                    available = junctionReady && junction.Available,
                    end = (int)setting.JunctionEnd,
                    canCycle = junctionReady && junction.CanCycle,
                    canUseSelected = junctionReady && junction.CanUseSelected,
                    hasOverride = junctionReady && junction.HasOverride,
                    kind = junction.Kind, source = junctionReady ? junction.Source : "",
                    surface = junctionReady ? junction.Surface : "",
                    status = RuntimeState.JunctionStatus,
                    pending = m_JunctionActionPending
                },
                version = typeof(Mod).Assembly.GetName().Version.ToString(3)
            }));
        }
    }
}
