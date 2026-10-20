using System;
using System.Collections.Generic;
using System.Linq;
using Game.Settings;
using Newtonsoft.Json;

namespace SurfaceNetworkFilter
{
    internal sealed class SurfaceAssignment
    {
        public SurfaceTarget Target { get; set; }
        public string Surface { get; set; } = "";
        public bool UseRoadPavement { get; set; }
        public bool ShouldSerializeUseRoadPavement() => UseRoadPavement;
    }

    internal sealed class SurfaceRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Surface rule";
        public string Surface { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public SurfaceTarget Target { get; set; } = SurfaceTarget.Roads;
        public RoadCategory Category { get; set; } = RoadCategory.AllRoads;
        public string RoadPrefab { get; set; } = "";
        public List<SurfaceAssignment> Assignments { get; set; }
        public bool? SeparateBikeLanes { get; set; }
        public bool? SeparateBusLanes { get; set; }
        public bool FillBusShoulders { get; set; }
        public bool LegacyLaneFallback { get; set; }

        // Read the previous single-target schema, but save only assignments.
        public bool ShouldSerializeSurface() => Assignments == null;
        public bool ShouldSerializeTarget() => Assignments == null;
        internal bool TargetActive(SurfaceTarget target) => target == SurfaceTarget.BikeLanes ? SeparateBikeLanes == true :
            target != SurfaceTarget.BusLanes || SeparateBusLanes == true;
        internal string SurfaceFor(SurfaceTarget target) => Assignments?.Find(item => item.Target == target)?.Surface ?? "";
        internal IEnumerable<SurfaceAssignment> ActiveAssignments => (Assignments ?? new List<SurfaceAssignment>())
            .Where(item => TargetActive(item.Target));
    }

    public sealed partial class Setting
    {
        private string m_RulesJson = "";
        private List<SurfaceRule> m_Rules;
        private List<SurfaceRule> m_AppliedRules = new List<SurfaceRule>();
        private string m_PlacementFingerprint;
        private bool m_RulesMigrated, m_RuleReadFailed;

        // Keep the list in the native mod settings store. The old eight slots
        // remain hidden properties solely for upgrading existing settings.
        [SettingsUIHidden]
        public string RulesJson
        {
            get { return m_RulesJson ?? ""; }
            set { m_RulesJson = value ?? ""; m_Rules = null; m_PlacementFingerprint = null; }
        }

        // Definitions are a library. Rendering reads the snapshot explicitly
        // applied to the loaded city, never an unfinished library edit.
        internal List<SurfaceRule> PlacementRules => m_AppliedRules;
        internal int RuleCount => m_AppliedRules.Count;
        internal SurfaceRule GetRule(int slot) => slot >= 0 && slot < RuleCount ? m_AppliedRules[slot] : null;
        internal void SetAppliedRules(List<SurfaceRule> rules)
        { m_AppliedRules = rules; m_PlacementFingerprint = null; }

        internal string SurfaceRulesFingerprint()
        {
            if (m_PlacementFingerprint == null)
                m_PlacementFingerprint = JsonConvert.SerializeObject(PlacementRules.Select(rule => new {
                    rule.Enabled, rule.Assignments, rule.SeparateBikeLanes, rule.SeparateBusLanes, rule.FillBusShoulders,
                    rule.LegacyLaneFallback, rule.Category, rule.RoadPrefab
                }).ToArray());
            return m_PlacementFingerprint;
        }

        internal List<SurfaceRule> GetRules()
        {
            if (m_CityStylingExpected) return ActiveStylingProfile?.GeneralRules ?? new List<SurfaceRule>();
            return GetLegacyRules();
        }

        internal List<SurfaceRule> GetLegacyRules()
        {
            if (m_Rules != null) return m_Rules;
            m_RulesMigrated = m_RuleReadFailed = false;
            try
            {
                m_Rules = string.IsNullOrWhiteSpace(RulesJson)
                    ? ImportLegacyRules() : JsonConvert.DeserializeObject<List<SurfaceRule>>(RulesJson) ?? new List<SurfaceRule>();
                m_Rules.RemoveAll(rule => rule == null);
                HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (SurfaceRule rule in m_Rules)
                {
                    if (string.IsNullOrEmpty(rule.Id) || !ids.Add(rule.Id))
                        { rule.Id = Guid.NewGuid().ToString("N"); ids.Add(rule.Id); }
                    rule.Name = rule.Name ?? "Surface rule";
                    rule.Surface = rule.Surface ?? "";
                    rule.RoadPrefab = rule.RoadPrefab ?? "";
                    bool legacy = rule.Assignments == null;
                    if (legacy)
                    {
                        rule.Assignments = rule.Target == SurfaceTarget.Both
                            ? new List<SurfaceAssignment> { new SurfaceAssignment { Target = SurfaceTarget.Roads, Surface = rule.Surface },
                                new SurfaceAssignment { Target = SurfaceTarget.Sidewalks, Surface = rule.Surface } }
                            : new List<SurfaceAssignment> { new SurfaceAssignment { Target = rule.Target, Surface = rule.Surface } };
                        m_RulesMigrated = true;
                        rule.LegacyLaneFallback = true;
                    }
                    rule.SeparateBikeLanes = rule.SeparateBikeLanes ?? (legacy && SeparateBikeLaneSurfaces);
                    rule.SeparateBusLanes = rule.SeparateBusLanes ?? (legacy && SeparateBusLaneSurfaces);
                    rule.Assignments.RemoveAll(item => item == null);
                    foreach (SurfaceAssignment item in rule.Assignments) item.Surface = item.Surface ?? "";
                }
            }
            catch (JsonException exception)
            {
                Mod.Log.Error("Unable to read saved surface rules: " + exception.Message);
                m_Rules = new List<SurfaceRule>();
                m_RuleReadFailed = true;
            }
            return m_Rules;
        }

        internal void InitializeRules()
        {
            bool empty = string.IsNullOrWhiteSpace(RulesJson);
            List<SurfaceRule> imported = GetRules();
            if (m_RuleReadFailed || (!empty && !m_RulesMigrated)) return;
            StoreRules(imported);
            if (imported.Count > 0) Mod.Log.Info("Migrated " + imported.Count + " existing surface rule(s) to per-target surfaces and per-rule lane settings.");
        }

        internal void StoreRules(List<SurfaceRule> rules)
        {
            if (m_CityStylingExpected)
            {
                if (ActiveStylingProfile == null) throw new ArgumentException("Wait for this city's styling profiles to load.");
                ActiveStylingProfile.GeneralRules = rules;
                PersistCityStyling();
                return;
            }
            m_Rules = rules;
            m_PlacementFingerprint = null;
            m_RulesJson = JsonConvert.SerializeObject(rules);
            ApplyAndSave();
        }

        private List<SurfaceRule> ImportLegacyRules()
        {
            string[] surfaces = { Surface1, Surface2, Surface3, Surface4, Surface5, Surface6, Surface7, Surface8 };
            bool[] enabled = { Surface1Enabled, Surface2Enabled, Surface3Enabled, Surface4Enabled, Surface5Enabled, Surface6Enabled, Surface7Enabled, Surface8Enabled };
            SurfaceTarget[] targets = { Surface1Target, Surface2Target, Surface3Target, Surface4Target, Surface5Target, Surface6Target, Surface7Target, Surface8Target };
            RoadCategory[] categories = { Surface1Category, Surface2Category, Surface3Category, Surface4Category, Surface5Category, Surface6Category, Surface7Category, Surface8Category };
            string[] prefabs = { Surface1RoadPrefab, Surface2RoadPrefab, Surface3RoadPrefab, Surface4RoadPrefab, Surface5RoadPrefab, Surface6RoadPrefab, Surface7RoadPrefab, Surface8RoadPrefab };
            List<SurfaceRule> rules = new List<SurfaceRule>();
            for (int slot = 0; slot < surfaces.Length; slot++)
                if (!string.IsNullOrEmpty(surfaces[slot]))
                    rules.Add(new SurfaceRule { Name = "Surface rule " + (slot + 1), Surface = surfaces[slot],
                        Enabled = enabled[slot], Target = targets[slot], Category = categories[slot], RoadPrefab = prefabs[slot] ?? "" });
            return rules;
        }
    }
}
