using System;
using System.Collections.Generic;
using System.Text;
using Colossal.Serialization.Entities;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;

namespace SurfaceNetworkFilter
{
    // Membership is deliberately finite: applying the city rules includes the
    // roads present at that moment. Future vanilla roads are not opted in.
    public struct CitySurfaceRuleMember : IComponentData, IEmptySerializable { }
    public struct SavedCitySurfaceRules : IComponentData, IEmptySerializable { }
    public struct CitySurfaceRuleJson : IBufferElementData, ISerializable
    {
        public FixedString4096Bytes Text;
        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter => writer.Write(Text.ToString());
        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        { reader.Read(out string text); Text = new FixedString4096Bytes(text); }
    }

    internal static partial class RuntimeState
    {
        internal static bool ApplyCityRulesRequested;
        internal static string NewRoadPresetId = "";
        internal static string CityRulesMessage = "";
        internal static bool CityRulesPending;
        internal static int CityRuleMemberCount;
    }

    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private bool m_CityRulesInitialized;
        private Entity m_CityRulesRecord;
        private readonly Dictionary<Entity, SegmentUpgradePreset> m_PlacementPreviews = new Dictionary<Entity, SegmentUpgradePreset>();
        private readonly HashSet<Entity> m_CityMemberPreviews = new HashSet<Entity>();

        private void ResetCityRules()
        {
            m_CityRulesInitialized = false; m_CityRulesRecord = Entity.Null;
            m_PlacementPreviews.Clear(); m_CityMemberPreviews.Clear();
            m_CitySurfaceSources.Clear();
            ResetRoadStyleTransfers();
            RuntimeState.ApplyCityRulesRequested = false; RuntimeState.NewRoadPresetId = "";
            RuntimeState.RestoreAllRoadsRequested = RuntimeState.RestoringAllRoads = false;
            RuntimeState.CityRulesMessage = ""; RuntimeState.CityRulesPending = false; RuntimeState.CityRuleMemberCount = 0;
            Mod.ActiveSetting?.SetAppliedRules(new List<SurfaceRule>());
        }

        private void UpdateCityRules(Setting setting)
        {
            if (!m_CityRulesInitialized)
            {
                using (NativeArray<Entity> records = GetEntityQuery(ComponentType.ReadOnly<SavedCitySurfaceRules>()).ToEntityArray(Allocator.Temp))
                    if (records.Length > 0) m_CityRulesRecord = records[0];
                List<SurfaceRule> applied = new List<SurfaceRule>();
                if (m_CityRulesRecord != Entity.Null && EntityManager.HasBuffer<CitySurfaceRuleJson>(m_CityRulesRecord))
                {
                    StringBuilder json = new StringBuilder();
                    foreach (CitySurfaceRuleJson chunk in EntityManager.GetBuffer<CitySurfaceRuleJson>(m_CityRulesRecord, true)) json.Append(chunk.Text.ToString());
                    try { applied = JsonConvert.DeserializeObject<List<SurfaceRule>>(json.ToString()) ?? applied; }
                    catch (JsonException exception) { Mod.Log.Error("Cannot read the city's applied rules: " + exception.Message); }
                }
                else if (m_ManagedEdges.Count > 0)
                {
                    // Preserve existing saves once when upgrading from the
                    // automatic workflow. Only their already existing roads
                    // inherit the last library; no future roads are enrolled.
                    applied = CloneCityRules(setting);
                    MarkCurrentCityRoads(setting);
                    SaveCityRuleSnapshot(applied);
                    Mod.Log.Info("Preserved existing city surfaces as an explicit city rule snapshot.");
                }
                else SaveCityRuleSnapshot(applied);
                applied.RemoveAll(rule => rule == null);
                setting.SetAppliedRules(applied);
                m_CityRulesInitialized = true;
            }
            if (RuntimeState.ApplyCityRulesRequested)
            {
                RuntimeState.ApplyCityRulesRequested = false;
                List<SurfaceRule> rules = CloneCityRules(setting);
                setting.SetAppliedRules(rules); SaveCityRuleSnapshot(rules);
                MarkCurrentCityRoads(setting);
                setting.Enabled = true; setting.ApplyAndSave();
                // Explicit city apply supersedes in-flight polygons from the
                // previous snapshot, including wider bus-shoulder requests.
                foreach (Entity owner in new List<Entity>(m_PendingAreaCounts.Keys)) CancelPendingFor(owner);
                QueueAllRoads();
                RuntimeState.CityRulesMessage = "City rules applied to existing roads. Segment overrides stay in place. New roads use the road-tool picker.";
                RuntimeState.RebuildRequested = true;
            }
            if (m_Frame % 15 == 0)
            {
                RuntimeState.CityRulesPending = JsonConvert.SerializeObject(setting.GetRules()) != JsonConvert.SerializeObject(setting.PlacementRules);
                RuntimeState.CityRuleMemberCount = GetEntityQuery(ComponentType.ReadOnly<CitySurfaceRuleMember>(),
                    ComponentType.ReadOnly<Edge>(), ComponentType.Exclude<Temp>(), ComponentType.Exclude<Deleted>()).CalculateEntityCount();
            }
            MaintainCitySurfaceSources();
            CompleteRoadStyleTransfers();
            TrackRoadPlacement(setting);
        }

        private static List<SurfaceRule> CloneCityRules(Setting setting) =>
            JsonConvert.DeserializeObject<List<SurfaceRule>>(JsonConvert.SerializeObject(setting.GetRules())) ?? new List<SurfaceRule>();

        private void SaveCityRuleSnapshot(List<SurfaceRule> rules)
        {
            if (m_CityRulesRecord == Entity.Null || !EntityManager.Exists(m_CityRulesRecord))
            {
                m_CityRulesRecord = EntityManager.CreateEntity(typeof(SavedCitySurfaceRules));
                EntityManager.AddBuffer<CitySurfaceRuleJson>(m_CityRulesRecord);
            }
            string json = JsonConvert.SerializeObject(rules);
            DynamicBuffer<CitySurfaceRuleJson> chunks = EntityManager.GetBuffer<CitySurfaceRuleJson>(m_CityRulesRecord);
            chunks.Clear();
            // At most 3 UTF-8 bytes per UTF-16 unit, leaving ample fixed-string
            // room. Do not split a surrogate pair across buffer elements.
            for (int offset = 0; offset < json.Length;)
            {
                int length = Math.Min(800, json.Length - offset);
                if (char.IsHighSurrogate(json[offset + length - 1])) length--;
                chunks.Add(new CitySurfaceRuleJson { Text = new FixedString4096Bytes(json.Substring(offset, length)) });
                offset += length;
            }
        }

        private void MarkCurrentCityRoads(Setting setting)
        {
            using (NativeArray<Entity> edges = m_AllEdges.ToEntityArray(Allocator.Temp))
                foreach (Entity edge in edges)
                    if (IsRoadEdge(edge) && PassesSegmentFilter(edge, EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab, setting) &&
                        !EntityManager.HasComponent<CitySurfaceRuleMember>(edge)) EntityManager.AddComponent<CitySurfaceRuleMember>(edge);
        }

        private bool UsesCityRules(Entity edge) => edge != Entity.Null &&
            (EntityManager.HasComponent<CitySurfaceRuleMember>(edge) || m_CitySurfaceSources.ContainsKey(edge));
        private bool HasPlacementRules(Entity edge) => UsesCityRules(edge) || m_SegmentRuleRecords.ContainsKey(edge);

        private Entity FindJunctionRuleRoad(Entity node, Entity prefab, Entity preferred = default)
        {
            if (IsRoadEdge(preferred) && HasPlacementRules(preferred) &&
                EntityManager.GetComponentData<PrefabRef>(preferred).m_Prefab == prefab) return preferred;
            Entity result = Entity.Null;
            if (EntityManager.HasBuffer<ConnectedEdge>(node))
                foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                    if (IsRoadEdge(connected.m_Edge) && HasPlacementRules(connected.m_Edge) &&
                        EntityManager.GetComponentData<PrefabRef>(connected.m_Edge).m_Prefab == prefab &&
                        (result == Entity.Null || connected.m_Edge.Index < result.Index)) result = connected.m_Edge;
            return result;
        }

        private void QueueAssignedRoads()
        {
            using (NativeArray<Entity> edges = m_AllEdges.ToEntityArray(Allocator.Temp))
                foreach (Entity edge in edges) if (IsRoadEdge(edge) && HasPlacementRules(edge)) QueueEdge(edge);
        }

        private void TrackRoadPlacement(Setting setting)
        {
            ReadPreviewStyleSources();
            ToolSystem tools = World.GetOrCreateSystemManaged<ToolSystem>();
            NetToolSystem net = World.GetOrCreateSystemManaged<NetToolSystem>();
            bool drawing = tools.activeTool == net && tools.activePrefab is RoadPrefab &&
                net.actualMode != NetToolSystem.Mode.Replace && !net.upgradeOnly;
            SegmentUpgradePreset selected = setting.GetSegmentPresets().Find(item => item.Rule.Id == RuntimeState.NewRoadPresetId);
            using (NativeArray<Entity> previews = GetEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Temp>(),
                ComponentType.Exclude<Deleted>()).ToEntityArray(Allocator.Temp))
                foreach (Entity preview in previews)
                {
                    Temp temp = EntityManager.GetComponentData<Temp>(preview);
                    if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel | TempFlags.Duplicate)) != 0)
                    { m_CityMemberPreviews.Remove(preview); m_PlacementPreviews.Remove(preview); continue; }
                    if (UsesCityRules(PreviewStyleSource(preview, temp))) m_CityMemberPreviews.Add(preview);
                    else m_CityMemberPreviews.Remove(preview);
                    // A replacement/split has an original. Its existing style
                    // transfers separately, even when the picker says None.
                    if (drawing && temp.m_Original == Entity.Null && (temp.m_Flags & TempFlags.Create) != 0)
                        m_PlacementPreviews[preview] = selected;
                    else m_PlacementPreviews.Remove(preview);
                }
            foreach (Entity preview in new List<Entity>(m_CityMemberPreviews))
            {
                if (IsRoadEdge(preview))
                {
                    if (!UsesCityRules(preview)) EntityManager.AddComponent<CitySurfaceRuleMember>(preview);
                    QueueManualEdge(preview); m_CityMemberPreviews.Remove(preview);
                }
                else if (!EntityManager.Exists(preview) || EntityManager.HasComponent<Deleted>(preview) ||
                    !EntityManager.HasComponent<Temp>(preview)) m_CityMemberPreviews.Remove(preview);
            }
            foreach (Entity preview in new List<Entity>(m_PlacementPreviews.Keys))
            {
                if (IsRoadEdge(preview))
                {
                    SegmentUpgradePreset preset = m_PlacementPreviews[preview];
                    m_PlacementPreviews.Remove(preview);
                    if (preset == null || TryGetSegmentRule(preview, out _, out _)) continue;
                    try
                    {
                        SaveSegmentRule(preview, new SegmentRuleDraft { Rule = preset.Rule, FillBusShoulders = preset.FillBusShoulders });
                        if (!setting.Enabled) { setting.Enabled = true; setting.ApplyAndSave(); }
                        QueueManualEdge(preview);
                    }
                    catch (ArgumentException exception) { RuntimeState.CityRulesMessage = "New-road preset could not be applied: " + exception.Message; }
                }
                else if (!EntityManager.Exists(preview) || EntityManager.HasComponent<Deleted>(preview) ||
                    !EntityManager.HasComponent<Temp>(preview)) m_PlacementPreviews.Remove(preview);
            }
        }
    }
}
