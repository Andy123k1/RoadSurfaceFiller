using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI.Widgets;
using System.Collections.Generic;

namespace SurfaceNetworkFilter
{
    public enum SurfaceTarget
    {
        Roads,
        Sidewalks,
        Both,
        BikeLanes,
        BusLanes
    }

    public enum PlacementMode
    {
        ExistingAndNewSegments,
        NewSegmentsOnly
    }

    public enum SideSelection
    {
        Left,
        Right,
        Both
    }

    public enum RoadCategory { AllRoads, Streets, Highways, SmallStreets, MediumStreets, LargeStreets }
    public enum NetworkScope { AllEligibleRoads, SelectedSegments }

    [FileLocation("ModsSettings/SurfaceNetworkFilter")]
    [SettingsUITabOrder(FilterSection)]
    [SettingsUIGroupOrder("General", "Scope", "Search", "Rule1", "Rule2", "Rule3", "Rule4", "Rule5", "Rule6", "Rule7", "Rule8", "RoadTypes", "Roads", "Highways", "Sidewalks", "Projection", "Selection", "Exclusions", "JunctionPriority", "Actions", "Status")]
    [SettingsUIShowGroupName("General", "Scope", "Search", "Rule1", "Rule2", "Rule3", "Rule4", "Rule5", "Rule6", "Rule7", "Rule8", "RoadTypes", "Roads", "Highways", "Sidewalks", "Projection", "Selection", "Exclusions", "JunctionPriority", "Actions", "Status")]
    public sealed partial class Setting : ModSetting
    {
        public const string GeneralSection = "General";
        public const string SurfaceSection = "Surfaces";
        public const string FilterSection = "NetworkFilters";
        public const string DiagnosticsSection = "Diagnostics";
        public const string SegmentSection = "Segments";
        public const string JunctionSection = "Junctions";

        private string m_SurfaceSearch = string.Empty;
        private string m_Surface1 = string.Empty;
        private string m_Surface2 = string.Empty;
        private string m_Surface3 = string.Empty;
        private string m_Surface4 = string.Empty;
        private string m_Surface5 = string.Empty;
        private string m_Surface6 = string.Empty;
        private string m_Surface7 = string.Empty;
        private string m_Surface8 = string.Empty;

        public Setting(IMod mod) : base(mod) { SetDefaults(); }

        // Compatibility sinks for retired global controls. Existing settings
        // still deserialize, while maintenance stays active and rule/preset
        // targets determine road eligibility without hidden global overrides.
        [SettingsUIHidden]
        public bool Enabled { get { return true; } set { } }

        [SettingsUIHidden]
        public bool SeparateBikeLaneSurfaces { get; set; }
        [SettingsUIHidden]
        public bool SeparateBusLaneSurfaces { get; set; }

        [SettingsUIHidden]
        public PlacementMode Placement { get; set; }

        [SettingsUIHidden]
        public NetworkScope Scope { get { return NetworkScope.AllEligibleRoads; } set { } }
        [SettingsUIHidden]
        public bool IncludeStreets { get { return true; } set { } }
        [SettingsUIHidden]
        public bool IncludeHighways { get { return true; } set { } }
        [SettingsUIHidden]
        public string RoadPrefabFilter { get { return ""; } set { } }
        [SettingsUISection(FilterSection, "Highways")]
        public bool IncludeHighwaySidewalks { get; set; }
        [SettingsUIHidden]
        public bool MainRoadJunctionSurfaces { get { return true; } set { } }
        [SettingsUISection(FilterSection, "Projection")]
        [SettingsUISlider(min = 0.05f, max = 2f, step = 0.05f)]
        public float ProjectionDepth { get; set; } = 0.3f;

        // Retain these legacy properties for compatibility. Segment work now
        // uses the road-tool preset picker, Replace and the in-game Tools panel.
        [SettingsUIHidden]
        [SettingsUIMultilineText]
        public string SegmentHelp { get { return "Select a road segment with the game's Info tool, then open these controls. Add segments to a saved selection, restrict placement to that selection, or exclude individual segments. Junction patches are included with their approaching segment."; } set { } }
        [SettingsUIHidden]
        [SettingsUIMultilineText]
        public string SelectedSegment { get { return RuntimeState.SelectionStatus; } set { } }
        [SettingsUIHidden]
        [SettingsUIButton]
        public bool AddSelectedSegment { set { RuntimeState.RequestSegmentAction(SegmentAction.Add); } }
        [SettingsUIHidden]
        [SettingsUIButton]
        public bool OnlySelectedSegments { set { RuntimeState.RequestSegmentAction(SegmentAction.OnlySelection); } }
        [SettingsUIHidden]
        [SettingsUIButton]
        public bool RemoveSelectedSegment { set { RuntimeState.RequestSegmentAction(SegmentAction.Remove); } }
        [SettingsUIHidden]
        [SettingsUIButton]
        public bool ExcludeSelectedSegment { set { RuntimeState.RequestSegmentAction(SegmentAction.Exclude); } }
        [SettingsUIHidden]
        [SettingsUIButton]
        public bool RestoreSelectedSegment { set { RuntimeState.RequestSegmentAction(SegmentAction.Restore); } }
        [SettingsUIHidden]
        [SettingsUIButton]
        public bool UseSelectedRoadType { set { RuntimeState.RequestSegmentAction(SegmentAction.UseRoadType); } }
        [SettingsUIHidden]
        [SettingsUIButton]
        public bool RebuildSelectedSegment { set { RuntimeState.RequestSegmentAction(SegmentAction.Rebuild); } }
        [SettingsUIHidden]
        [SettingsUIButton]
        public bool ClearSegmentSelection { set { RuntimeState.RequestSegmentAction(SegmentAction.Clear); } }

        [SettingsUIHidden]
        [SettingsUITextInput]
        public string SurfaceSearch
        {
            get { return m_SurfaceSearch ?? string.Empty; }
            set { m_SurfaceSearch = value ?? string.Empty; }
        }

        [SettingsUIHidden]
        [SettingsUIButton]
        public bool RefreshSurfaceList
        {
            set { Mod.RequestSurfaceRefresh(); }
        }

        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetSurfaceItems")]
        public string Surface1
        {
            get { return m_Surface1 ?? string.Empty; }
            set { m_Surface1 = value ?? string.Empty; }
        }
        [SettingsUIHidden]
        public bool Surface1Enabled { get; set; }
        [SettingsUIHidden]
        public SurfaceTarget Surface1Target { get; set; }

        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetSurfaceItems")]
        public string Surface2
        {
            get { return m_Surface2 ?? string.Empty; }
            set { m_Surface2 = value ?? string.Empty; }
        }
        [SettingsUIHidden]
        public bool Surface2Enabled { get; set; }
        [SettingsUIHidden]
        public SurfaceTarget Surface2Target { get; set; }

        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetSurfaceItems")]
        public string Surface3
        {
            get { return m_Surface3 ?? string.Empty; }
            set { m_Surface3 = value ?? string.Empty; }
        }
        [SettingsUIHidden]
        public bool Surface3Enabled { get; set; }
        [SettingsUIHidden]
        public SurfaceTarget Surface3Target { get; set; }

        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetSurfaceItems")]
        public string Surface4
        {
            get { return m_Surface4 ?? string.Empty; }
            set { m_Surface4 = value ?? string.Empty; }
        }
        [SettingsUIHidden]
        public bool Surface4Enabled { get; set; }
        [SettingsUIHidden]
        public SurfaceTarget Surface4Target { get; set; }

        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetSurfaceItems")]
        public string Surface5
        {
            get { return m_Surface5 ?? string.Empty; }
            set { m_Surface5 = value ?? string.Empty; }
        }
        [SettingsUIHidden]
        public bool Surface5Enabled { get; set; }
        [SettingsUIHidden]
        public SurfaceTarget Surface5Target { get; set; }

        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetSurfaceItems")]
        public string Surface6
        {
            get { return m_Surface6 ?? string.Empty; }
            set { m_Surface6 = value ?? string.Empty; }
        }
        [SettingsUIHidden]
        public bool Surface6Enabled { get; set; }
        [SettingsUIHidden]
        public SurfaceTarget Surface6Target { get; set; }

        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetSurfaceItems")]
        public string Surface7
        {
            get { return m_Surface7 ?? string.Empty; }
            set { m_Surface7 = value ?? string.Empty; }
        }
        [SettingsUIHidden]
        public bool Surface7Enabled { get; set; }
        [SettingsUIHidden]
        public SurfaceTarget Surface7Target { get; set; }

        [SettingsUIHidden]
        [SettingsUIDropdown(typeof(Setting), "GetSurfaceItems")]
        public string Surface8
        {
            get { return m_Surface8 ?? string.Empty; }
            set { m_Surface8 = value ?? string.Empty; }
        }
        [SettingsUIHidden]
        public bool Surface8Enabled { get; set; }
        [SettingsUIHidden]
        public SurfaceTarget Surface8Target { get; set; }

        [SettingsUISection(FilterSection, "Roads")]
        public bool IncludeElevatedRoads { get; set; }
        [SettingsUISection(FilterSection, "Roads")]
        public bool IncludeTunnels { get; set; }
        [SettingsUISection(FilterSection, "Sidewalks")]
        public SideSelection SidewalkSides { get; set; }
        [SettingsUISection(FilterSection, "Sidewalks")]
        public bool IncludeInternalRoadPedestrianLanes { get; set; }

        [SettingsUIHidden]
        [SettingsUIButton]
        public bool RebuildNow
        {
            set { Mod.RequestRebuild(); }
        }

        [SettingsUIHidden]
        [SettingsUIMultilineText]
        public string Status
        {
            get { return RuntimeState.Status; }
            set { }
        }

        public DropdownItem<string>[] GetSurfaceItems()
        {
            List<DropdownItem<string>> items = new List<DropdownItem<string>>();
            items.Add(new DropdownItem<string> { value = "", displayName = "(none)" });
            foreach (string surfaceName in RuntimeState.GetSurfaceNames(SurfaceSearch))
            {
                items.Add(new DropdownItem<string> { value = surfaceName, displayName = surfaceName });
            }
            return items.ToArray();
        }

        public DropdownItem<string>[] GetRoadItems()
        {
            List<DropdownItem<string>> items = new List<DropdownItem<string>> {
                new DropdownItem<string> { value = "", displayName = "All road prefabs" }
            };
            foreach (string name in RuntimeState.RoadNames)
                items.Add(new DropdownItem<string> { value = name, displayName = name });
            return items.ToArray();
        }

        internal bool IsRuleEnabled(int slot) => GetRule(slot)?.Enabled ?? false;

        internal string ApplyFingerprint()
        {
            return string.Join("|", new string[] {
                SurfaceRulesFingerprint(),
                IncludeElevatedRoads.ToString(), IncludeTunnels.ToString(),
                SidewalkSides.ToString(), IncludeInternalRoadPedestrianLanes.ToString(),
                IncludeHighwaySidewalks.ToString(), ProjectionDepth.ToString(), RuleFiltersFingerprint()
            });
        }

        public override void SetDefaults()
        {
            Enabled = false;
            SeparateBikeLaneSurfaces = SeparateBusLaneSurfaces = false;
            RulesJson = "";
              SegmentPresetsJson = "";
            Placement = PlacementMode.ExistingAndNewSegments;
            SurfaceSearch = "";
            Surface1 = Surface2 = Surface3 = Surface4 = "";
            Surface5 = Surface6 = Surface7 = Surface8 = "";
            Surface1Enabled = Surface2Enabled = Surface3Enabled = Surface4Enabled = false;
            Surface5Enabled = Surface6Enabled = Surface7Enabled = Surface8Enabled = false;
            Surface1Target = Surface2Target = Surface3Target = Surface4Target = SurfaceTarget.Both;
            Surface5Target = Surface6Target = Surface7Target = Surface8Target = SurfaceTarget.Both;
            IncludeElevatedRoads = true;
            IncludeTunnels = true;
            SidewalkSides = SideSelection.Both;
            IncludeInternalRoadPedestrianLanes = false;
            Scope = NetworkScope.AllEligibleRoads;
            IncludeStreets = IncludeHighways = true;
            IncludeHighwaySidewalks = false;
            MainRoadJunctionSurfaces = true;
            JunctionEnd = global::SurfaceNetworkFilter.JunctionEnd.Nearest;
            RoadPrefabFilter = "";
            ProjectionDepth = 0.3f;
            ResetRuleFilters();
        }
    }

    internal sealed class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;
        internal LocaleEN(Setting setting) { m_Setting = setting; }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            values[m_Setting.GetSettingsLocaleID()] = "Road Surface Filler";
            values[m_Setting.GetOptionTabLocaleID(Setting.GeneralSection)] = "General";
            values[m_Setting.GetOptionTabLocaleID(Setting.SurfaceSection)] = "Surfaces";
            values[m_Setting.GetOptionTabLocaleID(Setting.FilterSection)] = "Network filters";
            values[m_Setting.GetOptionTabLocaleID(Setting.DiagnosticsSection)] = "Status";
            values[m_Setting.GetOptionTabLocaleID(Setting.SegmentSection)] = "Segments";
            values[m_Setting.GetOptionTabLocaleID(Setting.JunctionSection)] = "Junctions";

            Add(values, "JunctionHelp", "Junction pavement priority", "Choose which connected road supplies the pavement rules for this junction or roundabout. Sidewalks, the curb outline and the roundabout island keep their existing geometry.");
            Add(values, "JunctionEnd", "Junction to change", "Target the junction nearest your last road selection, or the junction at the other end of that segment.");
            Add(values, "SelectedJunction", "Current junction", "Shows the targeted junction, its pavement source and the connected road types.");
            Add(values, "UseSelectedRoadAtJunction", "Use selected road's surface", "Use this connected road's pavement rules across the junction or roundabout. A selected highway uses its highway surface rules. The preference is saved in the city and connected segments rebuild automatically.");
            Add(values, "NextJunctionRoad", "Use next connected road type", "Switch pavement priority to the next connected road type. The current junction display shows the chosen source.");
            Add(values, "AutomaticJunctionRoad", "Restore automatic pavement priority", "Remove this junction's saved preference and use the automatic road choice again.");
            values[m_Setting.GetOptionGroupLocaleID("JunctionPriority")] = "Pavement priority";
            values[m_Setting.GetEnumValueLocaleID(JunctionEnd.Nearest)] = "Nearest junction";
            values[m_Setting.GetEnumValueLocaleID(JunctionEnd.Other)] = "Other end of selected segment";

            Add(values, "Enabled", "Surface updates", "Keep applied city surfaces and segment upgrades updated when road geometry changes. New roads use the road-tool Surface rule picker. General rules apply only when Apply city rules is clicked.");
            Add(values, "Placement", "Placement mode", "Choose existing and new segments, or only segments created after the current city has been read. New segments seen while disabled wait until the mod is enabled.");
            Add(values, "SurfaceSearch", "Search installed surfaces", "Type part of a surface name, then open a surface selector.");
            Add(values, "RefreshSurfaceList", "Refresh surface list", "Reload available SurfacePrefab entries from the current game world.");
            Add(values, "RebuildNow", "Rebuild applied surfaces", "Rebuild the city's last applied rules and saved segment overrides. This does not apply library edits or opt new vanilla roads into city rules.");
            Add(values, "IncludeElevatedRoads", "Include elevated roads and bridges", "Include road segments above ground. Elevation is read from the segment data.");
            Add(values, "IncludeTunnels", "Include tunnels", "Include road segments whose network elevation is below ground.");
            Add(values, "SidewalkSides", "Sidewalk side", "Choose which outer road pedestrian lanes receive sidewalk surfaces.");
            Add(values, "IncludeInternalRoadPedestrianLanes", "Treat internal road pedestrian lanes as sidewalks", "Also include pedestrian lanes inside a road cross-section. The game does not expose a dedicated sidewalk flag.");
            Add(values, "Status", "Status", "Current surface catalog and rebuild progress.");
            Add(values, "Scope", "Apply to", "Apply matching rules to all eligible roads, or only to the segment selection saved in this city.");
            Add(values, "IncludeStreets", "Include streets", "Allow ordinary road prefabs. Individual rules can filter by street size or exact prefab.");
            Add(values, "IncludeHighways", "Include highways", "Allow prefabs using the game's highway rules, including highway ramps.");
            Add(values, "RoadPrefabFilter", "Road prefab", "Limit all rules to one exact installed road prefab, or choose All road prefabs.");
            Add(values, "IncludeHighwaySidewalks", "Allow sidewalk rules on highways", "Off by default: highway pedestrian/service shoulders receive the road material. Enable only for highway assets with intentional sidewalks.");
            Add(values, "MainRoadJunctionSurfaces", "Use main road surface at junctions", "The widest connected street supplies the junction pavement. Equal widths prefer the road continuing straight through. An alley meeting a highway yields to the highway. Side-road pavement ends at the main road boundary. Use the junction controls in the surface rule panel to override pavement priority at one junction or roundabout. Turn off to let street approaches choose their own pavement unless a local preference is saved.");
            Add(values, "ProjectionDepth", "Surface projection depth (m)", "Half-depth of the decal volume around the road deck. A narrow range prevents surfaces from projecting onto terrain or another road above/below. Increase for a custom road with an unusually offset deck.");
            Add(values, "SegmentHelp", "How to select segments", "Segment selections and exclusions are saved with this city.");
            Add(values, "SelectedSegment", "Current segment", "The last road segment selected with the Info tool. An aggregate selection resolves to the segment nearest the selection position.");
            Add(values, "AddSelectedSegment", "Add current segment to selection", "Save the current segment in this city's surface selection.");
            Add(values, "OnlySelectedSegments", "Apply only to selected segments", "Add the current segment and change Apply to to the saved segment selection. Existing mod areas outside it are removed.");
            Add(values, "RemoveSelectedSegment", "Remove current segment from selection", "Remove the current segment from the selection; its surfaces are removed when selection scope is active.");
            Add(values, "ExcludeSelectedSegment", "Exclude current segment", "Remove the mod's surfaces here and prevent automatic placement on this segment in either scope.");
            Add(values, "RestoreSelectedSegment", "Restore current segment eligibility", "Clear its exclusion and rebuild it if it matches the current scope and road filters.");
            Add(values, "UseSelectedRoadType", "Filter by current segment's road prefab", "Set the global road prefab filter to the exact road type of the current segment.");
            Add(values, "RebuildSelectedSegment", "Rebuild current segment", "Rebuild this segment using the active rules and filters.");
            Add(values, "ClearSegmentSelection", "Clear saved segment selection", "Empty this city's segment selection. Exclusions remain in place.");

            values[m_Setting.GetOptionGroupLocaleID("General")] = "General";
            values[m_Setting.GetOptionGroupLocaleID("Search")] = "Surface picker";
            values[m_Setting.GetOptionGroupLocaleID("Actions")] = "Apply";
            values[m_Setting.GetOptionGroupLocaleID("Roads")] = "Roads";
            values[m_Setting.GetOptionGroupLocaleID("Sidewalks")] = "Sidewalks";
            values[m_Setting.GetOptionGroupLocaleID("Status")] = "Status";
            values[m_Setting.GetOptionGroupLocaleID("Scope")] = "Placement scope";
            values[m_Setting.GetOptionGroupLocaleID("RoadTypes")] = "Road types";
            values[m_Setting.GetOptionGroupLocaleID("Highways")] = "Highway shoulders";
            values[m_Setting.GetOptionGroupLocaleID("Projection")] = "Bridge and deck projection";
            values[m_Setting.GetOptionGroupLocaleID("Selection")] = "Saved segment selection";
            values[m_Setting.GetOptionGroupLocaleID("Exclusions")] = "Segment exclusions";

            for (int i = 1; i <= 8; i++)
            {
                string group = "Rule" + i;
                values[m_Setting.GetOptionGroupLocaleID(group)] = "Surface rule " + i;
                Add(values, "Surface" + i, "Surface", "Choose an installed surface. Use Search installed surfaces to filter this list.");
                Add(values, "Surface" + i + "Enabled", "Enabled", "Apply this surface rule.");
                Add(values, "Surface" + i + "Target", "Target", "Choose roads, sidewalks, or both for this surface.");
                Add(values, "Surface" + i + "Category", "Road category", "Choose all roads, streets, highways, or a street size. Size uses prefab width: small ≤16 m, medium ≤32 m, large >32 m. Matching specific rules take priority over All roads rules.");
                Add(values, "Surface" + i + "RoadPrefab", "Road prefab", "Limit this surface rule to one exact road prefab. Exact prefab rules take priority over category rules.");
            }

            values[m_Setting.GetEnumValueLocaleID(PlacementMode.ExistingAndNewSegments)] = "Existing and new segments";
            values[m_Setting.GetEnumValueLocaleID(PlacementMode.NewSegmentsOnly)] = "New segments only";
            values[m_Setting.GetEnumValueLocaleID(SurfaceTarget.Roads)] = "Roads";
            values[m_Setting.GetEnumValueLocaleID(SurfaceTarget.Sidewalks)] = "Sidewalks";
            values[m_Setting.GetEnumValueLocaleID(SurfaceTarget.Both)] = "Both";
            values[m_Setting.GetEnumValueLocaleID(SurfaceTarget.BikeLanes)] = "Bike lanes";
            values[m_Setting.GetEnumValueLocaleID(SurfaceTarget.BusLanes)] = "Bus lanes";
            values[m_Setting.GetEnumValueLocaleID(SideSelection.Left)] = "Left";
            values[m_Setting.GetEnumValueLocaleID(SideSelection.Right)] = "Right";
            values[m_Setting.GetEnumValueLocaleID(SideSelection.Both)] = "Both";
            values[m_Setting.GetEnumValueLocaleID(NetworkScope.AllEligibleRoads)] = "All eligible roads";
            values[m_Setting.GetEnumValueLocaleID(NetworkScope.SelectedSegments)] = "Saved segment selection";
            foreach (RoadCategory category in System.Enum.GetValues(typeof(RoadCategory)))
                values[m_Setting.GetEnumValueLocaleID(category)] = category == RoadCategory.AllRoads ? "All roads" :
                    category == RoadCategory.SmallStreets ? "Small streets (≤16 m)" :
                    category == RoadCategory.MediumStreets ? "Medium streets (16–32 m)" :
                    category == RoadCategory.LargeStreets ? "Large streets (>32 m)" : category.ToString();
            return values;
        }

        private void Add(Dictionary<string, string> values, string property, string label, string description)
        {
            values[m_Setting.GetOptionLabelLocaleID(property)] = label;
            values[m_Setting.GetOptionDescLocaleID(property)] = description;
        }

        public void Unload() { }
    }
}
