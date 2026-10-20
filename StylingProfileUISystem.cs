using System;
using System.Linq;
using Colossal.UI.Binding;
using Newtonsoft.Json;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceRuleUISystem
    {
        private sealed class StylingProfileAction
        {
            public string Action { get; set; }
            public string Name { get; set; }
            public string SourceId { get; set; }
            public string TargetId { get; set; }
            public string Kind { get; set; }
            public string RuleId { get; set; }
        }

        private void AddStylingProfileBindings() =>
            AddBinding(new TriggerBinding<string>(Group, "stylingProfileAction", EditStylingProfiles));

        private void EditStylingProfiles(string json)
        {
            Setting setting = Mod.ActiveSetting;
            if (setting == null || !IsInLoadedCity()) return;
            try
            {
                if (json == null || json.Length > 2048) throw new ArgumentException("The profile action could not be read.");
                StylingProfileAction request = JsonConvert.DeserializeObject<StylingProfileAction>(json);
                if (request == null) throw new ArgumentException("Choose a styling profile action.");
                if (request.Action == "select" || request.Action == "create" || request.Action == "delete")
                {
                    if (RuntimeState.ApplyCityRulesRequested || RuntimeState.RestoreAllRoadsRequested || RuntimeState.RestoringAllRoads)
                        throw new ArgumentException("Wait for the current city-wide action before changing profiles.");
                    World.GetOrCreateSystemManaged<ManualSurfaceEditorSystem>().Stop();
                    UpgradeTool().Cancel(); JunctionCycleTool().Cancel();
                }
                switch (request.Action)
                {
                    case "select":
                        setting.SelectStylingProfile(request.TargetId);
                        m_Message = "Selected “" + setting.ActiveStylingProfile.Name + "”. Existing roads keep their finishes. Use Apply city rules or the road tool when ready.";
                        break;
                    case "create":
                        setting.CreateStylingProfile(request.Name, request.SourceId);
                        m_Message = "Created “" + setting.ActiveStylingProfile.Name + "” for this city. Copied rules are independent; existing roads keep their finishes.";
                        break;
                    case "rename":
                        setting.RenameStylingProfile(request.Name); m_Message = "Styling profile renamed for this city.";
                        break;
                    case "delete":
                        setting.DeleteStylingProfile(); m_Message = "Profile removed. Applied road finishes and independent areas are kept.";
                        break;
                    case "copyRule":
                        setting.CopyStylingRule(request.SourceId, request.TargetId, request.Kind, request.RuleId);
                        m_Message = "Rule copied to “" + setting.CityStylingLibrary.Profiles.Find(profile => profile.Id == request.TargetId).Name + "”. The copy is independent.";
                        break;
                    default: throw new ArgumentException("Choose a styling profile action.");
                }
            }
            catch (Exception exception) when (exception is JsonException || exception is ArgumentException)
            { m_Message = exception.Message; }
            Publish();
        }

        private object StylingProfilesState()
        {
            Setting setting = Mod.ActiveSetting;
            CityStylingLibrary library = setting.CityStylingLibrary;
            return new {
                ready = setting.CityStylingReady,
                cityId = library?.CityId ?? "", cityName = library?.CityName ?? "",
                activeId = library?.ActiveProfileId ?? "", message = RuntimeState.StylingProfilesMessage,
                items = library?.Profiles.Select(profile => new { id = profile.Id, name = profile.Name,
                    generalCount = profile.GeneralRules.Count, segmentCount = profile.SegmentRules.Count }).ToArray(),
                sources = setting.GetStylingProfileSources().Select(item => new {
                    id = item.Profile.Id, name = item.Profile.Name, cityName = item.CityName,
                    generalRules = item.Profile.GeneralRules.Select(rule => new { id = rule.Id, name = rule.Name }).ToArray(),
                    segmentRules = item.Profile.SegmentRules.Select(preset => new { id = preset.Rule.Id, name = preset.Rule.Name }).ToArray()
                }).ToArray()
            };
        }
    }
}
