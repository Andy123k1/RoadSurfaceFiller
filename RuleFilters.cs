using Game.Settings;
using System.Collections.Generic;

namespace SurfaceNetworkFilter
{
    public sealed partial class Setting
    {
        [SettingsUIHidden]
        public RoadCategory Surface1Category { get; set; }
        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetRoadItems")]
        public string Surface1RoadPrefab { get; set; } = "";

        [SettingsUIHidden]
        public RoadCategory Surface2Category { get; set; }
        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetRoadItems")]
        public string Surface2RoadPrefab { get; set; } = "";

        [SettingsUIHidden]
        public RoadCategory Surface3Category { get; set; }
        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetRoadItems")]
        public string Surface3RoadPrefab { get; set; } = "";

        [SettingsUIHidden]
        public RoadCategory Surface4Category { get; set; }
        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetRoadItems")]
        public string Surface4RoadPrefab { get; set; } = "";

        [SettingsUIHidden]
        public RoadCategory Surface5Category { get; set; }
        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetRoadItems")]
        public string Surface5RoadPrefab { get; set; } = "";

        [SettingsUIHidden]
        public RoadCategory Surface6Category { get; set; }
        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetRoadItems")]
        public string Surface6RoadPrefab { get; set; } = "";

        [SettingsUIHidden]
        public RoadCategory Surface7Category { get; set; }
        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetRoadItems")]
        public string Surface7RoadPrefab { get; set; } = "";

        [SettingsUIHidden]
        public RoadCategory Surface8Category { get; set; }
        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetRoadItems")]
        public string Surface8RoadPrefab { get; set; } = "";

        internal RoadCategory GetCategory(int slot) => GetRule(slot)?.Category ?? RoadCategory.AllRoads;
        internal string GetRoadPrefab(int slot) => GetRule(slot)?.RoadPrefab ?? "";

        internal string RuleFiltersFingerprint()
        {
            List<string> values = new List<string>();
            for (int i = 0; i < RuleCount; i++) values.Add(GetCategory(i) + ":" + GetRoadPrefab(i));
            return string.Join("|", values);
        }

        private void ResetRuleFilters()
        {
            Surface1Category = RoadCategory.AllRoads; Surface1RoadPrefab = "";
            Surface2Category = RoadCategory.AllRoads; Surface2RoadPrefab = "";
            Surface3Category = RoadCategory.AllRoads; Surface3RoadPrefab = "";
            Surface4Category = RoadCategory.AllRoads; Surface4RoadPrefab = "";
            Surface5Category = RoadCategory.AllRoads; Surface5RoadPrefab = "";
            Surface6Category = RoadCategory.AllRoads; Surface6RoadPrefab = "";
            Surface7Category = RoadCategory.AllRoads; Surface7RoadPrefab = "";
            Surface8Category = RoadCategory.AllRoads; Surface8RoadPrefab = "";
        }
    }
}

