using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.UI.Binding;
using Newtonsoft.Json;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceRuleUISystem
    {
        private void AddSegmentRuleBindings()
        {
            AddBinding(new TriggerBinding<string>(Group, "saveSegmentPreset", SaveSegmentRuleDraft));
            AddBinding(new TriggerBinding<string>(Group, "copyRuleToSegments", id => {
                SurfaceRule rule = Mod.ActiveSetting?.GetRules().Find(item => item.Id == id);
                if (rule == null || !IsInLoadedCity()) return;
                SurfaceRule copy = JsonConvert.DeserializeObject<SurfaceRule>(JsonConvert.SerializeObject(rule));
                copy.Id = Guid.NewGuid().ToString("N"); copy.Name = rule.Name.Substring(0, Math.Min(110, rule.Name.Length)) + " (segment)";
                copy.Category = RoadCategory.AllRoads; copy.RoadPrefab = ""; copy.Enabled = true;
                List<SegmentUpgradePreset> presets = Mod.ActiveSetting.GetSegmentPresets();
                presets.Add(new SegmentUpgradePreset { Rule = copy, FillBusShoulders = copy.FillBusShoulders }); Mod.ActiveSetting.StoreSegmentPresets(presets);
                RuntimeState.NotifySegmentPresetsChanged();
                m_Message = "Copied to Segment rules. Choose it in the road tool's Surface rule picker when drawing or using Replace."; Publish();
            }));
            AddBinding(new TriggerBinding<string>(Group, "deleteSegmentPreset", id => {
                if (!IsInLoadedCity() || Mod.ActiveSetting == null) return;
                List<SegmentUpgradePreset> presets = Mod.ActiveSetting.GetSegmentPresets();
                SegmentUpgradePreset previous = presets.Find(item => item.Rule.Id == id);
                presets.RemoveAll(item => item.Rule.Id == id); Mod.ActiveSetting.StoreSegmentPresets(presets);
                RuntimeState.NotifySegmentPresetsChanged(previous);
                if (RuntimeState.NewRoadPresetId == id) RuntimeState.NewRoadPresetId = "";
                m_Message = "Segment rule deleted. Previously upgraded segments keep their saved surfaces."; Publish();
            }));
            AddBinding(new TriggerBinding<string>(Group, "startUpgrade", StartSegmentUpgrade));
            AddBinding(new TriggerBinding(Group, "applyUpgrade", () => { UpgradeTool().Commit(); Publish(); }));
            AddBinding(new TriggerBinding(Group, "cancelUpgrade", () => { UpgradeTool().Cancel(); Publish(); }));
            AddBinding(new TriggerBinding(Group, "clearUpgradeSelection", () => { UpgradeTool().ClearSelection(); Publish(); }));
        }

        private SegmentUpgradeToolSystem UpgradeTool() => World.GetOrCreateSystemManaged<SegmentUpgradeToolSystem>();
        private sealed class UpgradeToolDraft
        {
            public int Mode { get; set; }
            public string BusSurface { get; set; }
        }
        private void StartSegmentUpgrade(string json)
        {
            if (!IsInLoadedCity() || Mod.ActiveSetting == null) return;
            try
            {
                UpgradeToolDraft draft = JsonConvert.DeserializeObject<UpgradeToolDraft>(json);
                if (draft == null || !Enum.IsDefined(typeof(SegmentUpgradeMode), draft.Mode)) throw new ArgumentException("Choose a road tool.");
                if (draft.Mode == (int)SegmentUpgradeMode.BusShoulders && !string.IsNullOrEmpty(draft.BusSurface) && !RuntimeState.TryGetSurface(draft.BusSurface, out _))
                    throw new ArgumentException("Choose an installed bus-lane surface.");
                UpgradeTool().Begin((SegmentUpgradeMode)draft.Mode, draft.BusSurface); Publish();
            }
            catch (Exception exception) when (exception is JsonException || exception is ArgumentException)
            { RuntimeState.UpgradeMessage = exception.Message; Publish(); }
        }

        private void SaveSegmentRuleDraft(string json)
        {
            if (!IsInLoadedCity() || Mod.ActiveSetting == null) return;
            m_SaveResponse++; m_SavedRuleId = "";
            try
            {
                if (json == null || json.Length > 20000) throw new ArgumentException("The segment rule could not be read.");
                SegmentUpgradePreset draft = JsonConvert.DeserializeObject<SegmentUpgradePreset>(json);
                if (draft?.Rule == null) throw new ArgumentException("The segment rule could not be read.");
                SurfaceRule rule = draft.Rule;
                rule.FillBusShoulders = draft.FillBusShoulders;
                rule.Enabled = true; rule.Category = RoadCategory.AllRoads; rule.RoadPrefab = ""; rule.LegacyLaneFallback = false;
                if (rule.Assignments == null || rule.Assignments.Count > 4) throw new ArgumentException("Choose valid surface targets.");
                if (rule.Assignments.Count == 0 && !rule.SeparateBikeLanes.HasValue && !rule.SeparateBusLanes.HasValue && !draft.FillBusShoulders)
                    throw new ArgumentException("Choose a surface override or lane option for this segment.");
                HashSet<SurfaceTarget> targets = new HashSet<SurfaceTarget>();
                foreach (SurfaceAssignment item in rule.Assignments)
                {
                    if (item == null || !Enum.IsDefined(typeof(SurfaceTarget), item.Target) || item.Target == SurfaceTarget.Both || !targets.Add(item.Target))
                        throw new ArgumentException("Each surface target can be selected once.");
                    item.Surface = item.Surface ?? "";
                    if (System.Text.Encoding.UTF8.GetByteCount(item.Surface) > 4000) throw new ArgumentException("The surface name is too long to save.");
                    if (rule.Enabled && item.Surface.Length > 0 && !RuntimeState.TryGetSurface(item.Surface, out _))
                        throw new ArgumentException("A selected surface is unavailable. Refresh the surface list.");
                }
                rule.Name = (rule.Name ?? "").Trim();
                if (rule.Name.Length == 0) rule.Name = "Segment rule";
                if (rule.Name.Length > 120) rule.Name = rule.Name.Substring(0, 120);
                List<SegmentUpgradePreset> presets = Mod.ActiveSetting.GetSegmentPresets();
                int index = presets.FindIndex(item => item.Rule.Id == rule.Id);
                SegmentUpgradePreset previous = index < 0 ? null : presets[index];
                if (index < 0) { rule.Id = Guid.NewGuid().ToString("N"); presets.Add(draft); } else presets[index] = draft;
                Mod.ActiveSetting.StoreSegmentPresets(presets); m_SavedRuleId = rule.Id;
                RuntimeState.NotifySegmentPresetsChanged(previous);
                m_Message = draft.UpdateExistingSegments ?
                    "Segment rule saved. Roads using this preset are queued to update. Independent customizations keep their finishes." :
                    "Segment rule saved. Placed roads keep their finishes. Use the road tool's Surface rule picker when drawing or using Replace.";
                Publish();
            }
            catch (Exception exception) when (exception is JsonException || exception is ArgumentException)
            {
                m_Message = exception.Message; Publish();
            }
        }

        private object SegmentRuleState()
        {
            SurfaceRule rule = RuntimeState.SegmentRule;
            return new {
                available = IsInLoadedCity() && RuntimeState.SegmentRuleKey.Length > 0,
                key = RuntimeState.SegmentRuleKey, road = RuntimeState.SegmentRuleRoad, hasOverride = rule != null,
                inheritedBikeLanes = RuntimeState.SegmentInheritedBike, inheritedBusLanes = RuntimeState.SegmentInheritedBus,
                fillBusShoulders = RuntimeState.SegmentFillBusShoulders,
                rule = rule == null ? null : new {
                    id = rule.Id, name = rule.Name, enabled = rule.Enabled,
                    separateBikeLanes = rule.SeparateBikeLanes, separateBusLanes = rule.SeparateBusLanes,
                    assignments = rule.Assignments.Select(item => new { target = (int)item.Target, surface = item.Surface }).ToArray()
                },
                response = RuntimeState.SegmentRuleResponse, savedKey = RuntimeState.SavedSegmentRuleKey, message = RuntimeState.SegmentRuleMessage
            };
        }
    }
}
