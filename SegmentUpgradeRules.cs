using System;
using System.Collections.Generic;
using System.Linq;
using Game.Settings;
using Newtonsoft.Json;
using Unity.Entities;
using Game.Prefabs;

namespace SurfaceNetworkFilter
{
    internal sealed class SegmentUpgradePreset
    {
        public SurfaceRule Rule { get; set; }
        public bool FillBusShoulders { get; set; }
        public bool UpdateExistingSegments { get; set; }
    }

    public sealed partial class Setting
    {
        // The road tool copies presets into city-saved records. Presets can
        // optionally update their linked roads after subsequent edits.
        [SettingsUIHidden]
        public string SegmentPresetsJson { get; set; } = "";
        internal List<SegmentUpgradePreset> GetSegmentPresets()
        {
            if (m_CityStylingExpected) return ActiveStylingProfile?.SegmentRules ?? new List<SegmentUpgradePreset>();
            return GetLegacySegmentPresets();
        }
        internal IEnumerable<SegmentUpgradePreset> GetCitySegmentPresets() => m_CityStylingExpected
            ? (CityStylingLibrary?.Profiles ?? new List<StylingProfile>()).SelectMany(profile => profile.SegmentRules)
            : GetLegacySegmentPresets();
        internal List<SegmentUpgradePreset> GetLegacySegmentPresets()
        {
            try
            {
                return (JsonConvert.DeserializeObject<List<SegmentUpgradePreset>>(SegmentPresetsJson ?? "") ??
                    new List<SegmentUpgradePreset>()).Where(item => item?.Rule?.Assignments != null).ToList();
            }
            catch (JsonException) { return new List<SegmentUpgradePreset>(); }
        }
        internal void StoreSegmentPresets(List<SegmentUpgradePreset> presets)
        {
            if (m_CityStylingExpected)
            {
                if (ActiveStylingProfile == null) throw new ArgumentException("Wait for this city's styling profiles to load.");
                ActiveStylingProfile.SegmentRules = presets; PersistCityStyling();
            }
            else { SegmentPresetsJson = JsonConvert.SerializeObject(presets); ApplyAndSave(); }
        }
    }

    internal enum SegmentUpgradeMode { BusShoulders = 1, RestoreVanilla = 2, RemoveBusShoulders = 3, MakeEditable = 4, ResumeAutomatic = 5 }
    internal static partial class RuntimeState
    {
        internal static bool UpgradeActive;
        internal static int UpgradeSelectionCount;
        internal static SegmentUpgradeMode UpgradeMode;
        internal static string UpgradeLabel = "", UpgradeMessage = "";
        internal static readonly List<SegmentUpgradeRequest> UpgradeRequests = new List<SegmentUpgradeRequest>();
        internal sealed class SegmentUpgradeRequest
        {
            internal List<Entity> Edges;
            internal SegmentUpgradeMode Mode;
            internal string BusSurface;
        }
    }

    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private void UpdateSegmentUpgrades(Setting setting)
        {
            foreach (RuntimeState.SegmentUpgradeRequest request in RuntimeState.UpgradeRequests)
            {
                int applied = 0, skipped = 0;
                string reason = "";
                foreach (Entity edge in request.Edges)
                {
                    try
                    {
                        if (!IsRoadEdge(edge)) throw new ArgumentException("Some selected roads no longer exist.");
                        if (request.Mode == SegmentUpgradeMode.MakeEditable)
                        {
                            MakeRoadSurfacesEditable(edge);
                            World.GetOrCreateSystemManaged<SegmentUpgradeToolSystem>().Cancel();
                            OpenManualSurfaceEditor(); applied++; continue;
                        }
                        if (request.Mode == SegmentUpgradeMode.ResumeAutomatic)
                        { ResumeAutomaticRoadSurfaces(edge); applied++; continue; }
                        if (request.Mode == SegmentUpgradeMode.RestoreVanilla) RestoreRoadToVanilla(edge);
                        else if (request.Mode == SegmentUpgradeMode.RemoveBusShoulders) RemoveBusShoulderFill(edge);
                        else SaveSegmentRule(edge, CreateBusShoulderUpgrade(edge, request.BusSurface, setting));
                        QueueRoadStyleRebuild(edge); applied++;
                    }
                    catch (ArgumentException exception) { skipped++; reason = exception.Message; }
                    finally
                    {
                        if (request.Mode == SegmentUpgradeMode.MakeEditable)
                            World.GetOrCreateSystemManaged<SegmentUpgradeToolSystem>().EditableRoadRequestComplete();
                    }
                }
                if (applied > 0 && !setting.Enabled) { setting.Enabled = true; setting.ApplyAndSave(); }
                RuntimeState.UpgradeMessage = request.Mode == SegmentUpgradeMode.MakeEditable
                    ? (applied > 0 ? "Surface editor opened. Each converted surface keeps its existing finish. Drag boundary points or add points along edges." : reason)
                    : request.Mode == SegmentUpgradeMode.ResumeAutomatic
                    ? applied + " segments returned to automatic updates. Independent areas are kept; remove them manually if they overlap."
                    : applied + (request.Mode == SegmentUpgradeMode.RestoreVanilla ? " segments restored to vanilla surfaces." :
                    request.Mode == SegmentUpgradeMode.RemoveBusShoulders ? " segments set to bus-lane shoulders off. Surfaces queued for updating." :
                    " segments updated. Surfaces queued for updating.");
                if (skipped > 0 && (request.Mode != SegmentUpgradeMode.MakeEditable || applied > 0))
                    RuntimeState.UpgradeMessage += " " + skipped + " skipped. " + reason;
            }
            RuntimeState.UpgradeRequests.Clear();
        }

        private void RemoveBusShoulderFill(Entity edge)
        {
            if (TryGetSegmentRule(edge, out Entity record, out SavedSegmentSurfaceRule saved) && saved.Enabled)
            {
                // Keep all existing targets and lane modes, even when a saved
                // surface asset is unavailable. Only the shoulder switch changes.
                saved.FillBusShoulders = false;
                EntityManager.SetComponentData(record, saved);
                SetSegmentPresetLink(record, "");
                return;
            }
            if (!UsesCityRules(edge)) return;
            Game.Net.Composition composition = EntityManager.GetComponentData<Game.Net.Composition>(edge);
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
            if (!TryReadSurfaceLayout(composition.m_Edge, out SurfaceLayout layout, highway: IsHighway(edge), roadPrefab: prefab)) return;
            ApplySegmentLaneSettings(layout, edge);
            if (!layout.FillBusShoulders) return;

            // An empty target list inherits the city's finishes and lane modes,
            // while the local shoulder-off switch takes priority. An inactive
            // local rule is replaced without reactivating its dormant targets.
            SaveSegmentRule(edge, new SegmentRuleDraft {
                Rule = new SurfaceRule { Name = "Bus-lane shoulders off", Assignments = new List<SurfaceAssignment>() },
                FillBusShoulders = false
            });
        }

        private SegmentRuleDraft CreateBusShoulderUpgrade(Entity edge, string surface, Setting setting)
        {
            Game.Net.Composition composition = EntityManager.GetComponentData<Game.Net.Composition>(edge);
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
            if (!TryReadSurfaceLayout(composition.m_Edge, out SurfaceLayout layout, highway: IsHighway(edge), roadPrefab: prefab) ||
                layout.BusLanes.Count == 0) throw new ArgumentException("The shoulder tool needs a road with a bus lane.");
            SurfaceRule rule = new SurfaceRule { Name = "Bus lane and shoulders", Assignments = new List<SurfaceAssignment>() };
            if (TryGetSegmentRule(edge, out Entity record, out SavedSegmentSurfaceRule saved))
            {
                rule.Name = saved.Name.ToString();
                rule.SeparateBikeLanes = saved.BikeMode == 0 ? (bool?)null : saved.BikeMode == 2;
                foreach (SavedSegmentSurfaceTarget target in EntityManager.GetBuffer<SavedSegmentSurfaceTarget>(record, true))
                    rule.Assignments.Add(new SurfaceAssignment { Target = target.Target, Surface = target.SurfaceName.ToString(),
                        UseRoadPavement = SegmentUsesRoadPavement(record, target.Target) });
            }
            if (string.IsNullOrEmpty(surface))
            {
                surface = rule.SurfaceFor(SurfaceTarget.BusLanes);
                if (string.IsNullOrEmpty(surface))
                {
                    int priority = -1;
                    for (int i = 0; i < setting.RuleCount; i++)
                    {
                        SurfaceRule candidate = setting.GetRule(i);
                        if (!candidate.Enabled || !RuleMatchesRoadContext(setting, i, prefab, false) ||
                            candidate.SeparateBusLanes != true || candidate.SurfaceFor(SurfaceTarget.BusLanes).Length == 0 || RulePriority(candidate) < priority) continue;
                        surface = candidate.SurfaceFor(SurfaceTarget.BusLanes); priority = RulePriority(candidate);
                    }
                }
            }
            if (string.IsNullOrEmpty(surface) || !RuntimeState.TryGetSurface(surface, out _))
                throw new ArgumentException("Choose an installed bus-lane surface in Tools for roads without a current bus-lane surface.");
            rule.Assignments.RemoveAll(item => item.Target == SurfaceTarget.BusLanes);
            rule.Assignments.Add(new SurfaceAssignment { Target = SurfaceTarget.BusLanes, Surface = surface });
            rule.SeparateBusLanes = true;
            return new SegmentRuleDraft { Rule = rule, FillBusShoulders = true };
        }
    }
}
