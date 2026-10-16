using Unity.Entities;

namespace SurfaceNetworkFilter
{
    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private static int RulePriority(SurfaceRule rule) => !string.IsNullOrEmpty(rule.RoadPrefab) ? 2 :
            rule.Category == RoadCategory.AllRoads ? 0 : 1;

        private bool HasAnyLaneSeparation(Setting setting)
        {
            foreach (SurfaceRule rule in setting.PlacementRules)
                if (rule.Enabled && (rule.SeparateBikeLanes == true || rule.SeparateBusLanes == true)) return true;
            foreach (Entity record in m_SegmentRuleRecords.Values)
                if (EntityManager.Exists(record))
                {
                    SavedSegmentSurfaceRule local = EntityManager.GetComponentData<SavedSegmentSurfaceRule>(record);
                    if (local.Enabled && (local.BikeMode == 2 || local.BusMode == 2)) return true;
                }
            return false;
        }

        private int GetRuleLaneSettings(Setting setting, Entity roadPrefab, bool streetContext,
            out bool bicycle, out bool bus)
            => GetRuleLaneSettings(setting, roadPrefab, streetContext, out bicycle, out bus, out _);

        private int GetRuleLaneSettings(Setting setting, Entity roadPrefab, bool streetContext,
            out bool bicycle, out bool bus, out bool legacyFallback)
            => GetRuleLaneSettings(setting, roadPrefab, streetContext, out bicycle, out bus, out legacyFallback, out _);

        private int GetRuleLaneSettings(Setting setting, Entity roadPrefab, bool streetContext,
            out bool bicycle, out bool bus, out bool legacyFallback, out bool fillBusShoulders)
        {
            bicycle = bus = fillBusShoulders = false;
            legacyFallback = true;
            int priority = -1;
            for (int slot = 0; slot < setting.RuleCount; slot++)
            {
                SurfaceRule rule = setting.GetRule(slot);
                if (!rule.Enabled || !RuleMatchesRoadContext(setting, slot, roadPrefab, streetContext)) continue;
                int score = RulePriority(rule);
                if (score < priority) continue;
                if (score > priority) { bicycle = bus = fillBusShoulders = false; legacyFallback = true; priority = score; }
                bicycle |= rule.SeparateBikeLanes == true;
                bus |= rule.SeparateBusLanes == true;
                fillBusShoulders |= rule.SeparateBusLanes == true && rule.FillBusShoulders;
                // Old global separation allowed a general lane material to
                // accompany a more specific road/sidewalk rule. Preserve that
                // selection until the controlling rules are saved in the new UI.
                legacyFallback &= rule.LegacyLaneFallback;
            }
            return priority;
        }
    }
}
