using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace SurfaceNetworkFilter
{
    public struct SavedSegmentSurfaceRule : IComponentData, ISerializable
    {
        public Entity Edge;
        public FixedString512Bytes Name;
        public bool Enabled, FillBusShoulders;
        public int BikeMode, BusMode; // 0: inherit, 1: road pavement, 2: separate.
        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(Edge); writer.Write(Name.ToString()); writer.Write(Enabled);
            writer.Write(FillBusShoulders); writer.Write(BikeMode); writer.Write(BusMode);
        }
        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out Edge); reader.Read(out string name); Name = new FixedString512Bytes(name);
            reader.Read(out Enabled); reader.Read(out FillBusShoulders); reader.Read(out BikeMode); reader.Read(out BusMode);
        }
    }

    public struct SavedSegmentSurfaceTarget : IBufferElementData, ISerializable
    {
        public SurfaceTarget Target;
        public Entity Surface;
        public FixedString4096Bytes SurfaceName;
        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        { writer.Write((int)Target); writer.Write(Surface); writer.Write(SurfaceName.ToString()); }
        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        { reader.Read(out int target); Target = (SurfaceTarget)target; reader.Read(out Surface); reader.Read(out string name); SurfaceName = new FixedString4096Bytes(name); }
    }

    // Additive metadata keeps the existing saved rule and target-buffer
    // layouts intact. The bitset marks targets linked to road pavement.
    public struct SavedSegmentRoadPavementTargets : IComponentData, ISerializable
    {
        public int Mask;
        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter => writer.Write(Mask);
        public void Deserialize<TReader>(TReader reader) where TReader : IReader => reader.Read(out Mask);
    }

    internal sealed class SegmentRuleDraft
    {
        public string Key { get; set; } = "";
        public SurfaceRule Rule { get; set; }
        public bool FillBusShoulders { get; set; }
        public string PresetId { get; set; } = "";
    }

    internal static partial class RuntimeState
    {
        internal static string SegmentRuleKey = "", SegmentRuleRoad = "", SegmentRuleMessage = "", SavedSegmentRuleKey = "";
        internal static SurfaceRule SegmentRule;
        internal static bool SegmentFillBusShoulders, SegmentInheritedBike, SegmentInheritedBus;
        internal static int SegmentRuleResponse;
        internal static int SegmentRuleCount, ActiveSegmentRuleCount;
        internal static readonly List<SegmentRuleRequest> SegmentRuleRequests = new List<SegmentRuleRequest>();
        internal readonly struct SegmentRuleRequest
        {
            internal readonly Entity Edge;
            internal readonly SegmentRuleDraft Draft;
            internal readonly bool Clear;
            internal SegmentRuleRequest(Entity edge, SegmentRuleDraft draft, bool clear) { Edge = edge; Draft = draft; Clear = clear; }
        }
        internal static string SegmentKey(Entity edge) => edge.Index + ":" + edge.Version;
    }

    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private readonly Dictionary<Entity, Entity> m_SegmentRuleRecords = new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, Entity> m_SegmentRuleReplacements = new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, int> m_MissingSegmentRules = new Dictionary<Entity, int>();
        private bool m_SegmentRulesInitialized;

        private void ResetSegmentRules()
        {
            ResetSegmentPresetUpdates();
            m_SegmentRuleRecords.Clear(); m_SegmentRuleReplacements.Clear(); m_MissingSegmentRules.Clear(); m_SegmentRulesInitialized = false;
            RuntimeState.SegmentRuleRequests.Clear(); RuntimeState.SegmentRule = null;
            RuntimeState.SegmentRuleKey = RuntimeState.SegmentRuleRoad = RuntimeState.SegmentRuleMessage = RuntimeState.SavedSegmentRuleKey = "";
            RuntimeState.SegmentRuleResponse = 0;
            RuntimeState.SegmentRuleCount = RuntimeState.ActiveSegmentRuleCount = 0;
            RuntimeState.SegmentFillBusShoulders = RuntimeState.SegmentInheritedBike = RuntimeState.SegmentInheritedBus = false;
            RuntimeState.UpgradeRequests.Clear(); RuntimeState.UpgradeActive = false;
            RuntimeState.UpgradeSelectionCount = 0; RuntimeState.UpgradeMessage = "";
        }

        private bool TryGetSegmentRule(Entity edge, out Entity record, out SavedSegmentSurfaceRule saved)
        {
            saved = default;
            if (!m_SegmentRuleRecords.TryGetValue(edge, out record) || !EntityManager.Exists(record) ||
                !EntityManager.HasComponent<SavedSegmentSurfaceRule>(record)) return false;
            saved = EntityManager.GetComponentData<SavedSegmentSurfaceRule>(record);
            return saved.Edge == edge;
        }

        private void ApplySegmentLaneSettings(SurfaceLayout layout, Entity edge)
        {
            if (!UsesCityRules(edge)) layout.SeparateBikeLanes = layout.SeparateBusLanes = layout.FillBusShoulders = false;
            if (!TryGetSegmentRule(edge, out _, out SavedSegmentSurfaceRule saved) || !saved.Enabled)
            {
                // Several equally specific city rules can supply different bus
                // finishes. Shoulder filling belongs to the chosen finish's
                // rule, not the OR of all matching rules' shoulder switches.
                layout.FillBusShoulders = false;
                if (layout.SeparateBusLanes && Mod.ActiveSetting != null)
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
                    SurfaceChoice bus = Choose(GetChoices(Mod.ActiveSetting, SurfaceTarget.BusLanes, prefab, segment: edge),
                        m_PrefabSystem.GetPrefabName(prefab), EntityManager.GetComponentData<Game.Net.Curve>(edge), 4);
                    layout.FillBusShoulders = bus != null && bus.FillBusShoulders;
                }
                return;
            }
            if (saved.BikeMode != 0) layout.SeparateBikeLanes = saved.BikeMode == 2;
            if (saved.BusMode != 0) layout.SeparateBusLanes = saved.BusMode == 2;
            layout.FillBusShoulders = saved.FillBusShoulders && layout.SeparateBusLanes;
        }

        private bool TryGetSegmentChoices(Entity edge, SurfaceTarget target, out List<SurfaceChoice> choices)
        {
            choices = new List<SurfaceChoice>();
            if (!TryGetSegmentRule(edge, out Entity record, out SavedSegmentSurfaceRule saved) || !saved.Enabled) return false;
            if (SegmentUsesRoadPavement(record, target))
            {
                SurfaceChoice road = GetRoadPavementChoice(Mod.ActiveSetting, edge);
                if (road != null) choices.Add(road);
                return true;
            }
            foreach (SavedSegmentSurfaceTarget item in EntityManager.GetBuffer<SavedSegmentSurfaceTarget>(record, true))
            {
                if (item.Target != target) continue;
                string name = item.SurfaceName.ToString();
                if (name.Length > 0 && RuntimeState.TryGetSurface(name, out Entity surface) && EntityManager.Exists(surface))
                {
                    LogSurfaceRenderer(surface, name);
                    Entity overlay = GetNetworkOverlayPrefab(surface);
                    if (overlay != Entity.Null) choices.Add(new SurfaceChoice(overlay,
                        target == SurfaceTarget.BusLanes && saved.FillBusShoulders));
                }
                return true; // An explicit empty choice keeps the native finish.
            }
            return false;
        }

        private static bool CanUseRoadPavement(SurfaceTarget target) => target == SurfaceTarget.Sidewalks ||
            target == SurfaceTarget.BikeLanes || target == SurfaceTarget.BusLanes;

        private int SegmentRoadPavementMask(Entity record) => EntityManager.HasComponent<SavedSegmentRoadPavementTargets>(record) ?
            EntityManager.GetComponentData<SavedSegmentRoadPavementTargets>(record).Mask : 0;

        private bool SegmentUsesRoadPavement(Entity record, SurfaceTarget target) => CanUseRoadPavement(target) &&
            (SegmentRoadPavementMask(record) & (1 << (int)target)) != 0;

        private static int RuleRoadPavementMask(SurfaceRule rule)
        {
            int mask = 0;
            foreach (SurfaceAssignment item in rule.Assignments)
                if (item.UseRoadPavement && CanUseRoadPavement(item.Target)) mask |= 1 << (int)item.Target;
            return mask;
        }

        private void SetSegmentRoadPavementMask(Entity record, int mask)
        {
            if (mask != 0)
            {
                SavedSegmentRoadPavementTargets data = new SavedSegmentRoadPavementTargets { Mask = mask };
                if (EntityManager.HasComponent<SavedSegmentRoadPavementTargets>(record)) EntityManager.SetComponentData(record, data);
                else EntityManager.AddComponentData(record, data);
            }
            else if (EntityManager.HasComponent<SavedSegmentRoadPavementTargets>(record))
                EntityManager.RemoveComponent<SavedSegmentRoadPavementTargets>(record);
        }

        private SurfaceChoice GetRoadPavementChoice(Setting setting, Entity edge)
        {
            if (setting == null || !IsRoadEdge(edge)) return null;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
            // Use the road's actual choice and band seed, including its local
            // override. Lane/sidewalk seeds must not pick a different variant.
            return Choose(GetChoices(setting, SurfaceTarget.Roads, prefab, segment: edge),
                m_PrefabSystem.GetPrefabName(prefab), EntityManager.GetComponentData<Curve>(edge), 0);
        }

        private int MixSegmentRule(int hash, Entity edge)
        {
            if (!TryGetSegmentRule(edge, out Entity record, out SavedSegmentSurfaceRule saved)) return hash;
            hash = Mix(hash, saved.Enabled ? 1 : 0); hash = Mix(hash, saved.BikeMode); hash = Mix(hash, saved.BusMode);
            hash = Mix(hash, saved.FillBusShoulders ? 1 : 0);
            hash = Mix(hash, SegmentRoadPavementMask(record));
            foreach (SavedSegmentSurfaceTarget item in EntityManager.GetBuffer<SavedSegmentSurfaceTarget>(record, true))
            { hash = Mix(hash, (int)item.Target); hash = Mix(hash, item.SurfaceName.GetHashCode()); }
            return hash;
        }

        private bool SegmentRenderersReady()
        {
            foreach (Entity record in m_SegmentRuleRecords.Values)
            {
                if (!EntityManager.Exists(record) || !EntityManager.GetComponentData<SavedSegmentSurfaceRule>(record).Enabled) continue;
                foreach (SavedSegmentSurfaceTarget item in EntityManager.GetBuffer<SavedSegmentSurfaceTarget>(record, true))
                    if (RuntimeState.TryGetSurface(item.SurfaceName.ToString(), out Entity surface) && EntityManager.Exists(surface) &&
                        GetNetworkOverlayPrefab(surface) == Entity.Null) { RuntimeState.Status = "Preparing segment surface renderers."; return false; }
            }
            return true;
        }

        private void UpdateSegmentRules(Setting setting)
        {
            if (!m_SegmentRulesInitialized)
            {
                using (NativeArray<Entity> records = GetEntityQuery(ComponentType.ReadOnly<SavedSegmentSurfaceRule>()).ToEntityArray(Allocator.Temp))
                    foreach (Entity record in records) m_SegmentRuleRecords[EntityManager.GetComponentData<SavedSegmentSurfaceRule>(record).Edge] = record;
                m_SegmentRulesInitialized = true;
            }
            if (m_SegmentRuleRecords.Count > 0)
            {
                using (NativeArray<Entity> previews = GetEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Temp>(),
                    ComponentType.Exclude<Deleted>()).ToEntityArray(Allocator.Temp))
                    foreach (Entity preview in previews)
                    {
                        Temp temp = EntityManager.GetComponentData<Temp>(preview);
                        if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel | TempFlags.Duplicate)) == 0 &&
                            m_SegmentRuleRecords.TryGetValue(PreviewStyleSource(preview, temp), out Entity record)) m_SegmentRuleReplacements[preview] = record;
                        else m_SegmentRuleReplacements.Remove(preview);
                    }
                foreach (Entity original in new List<Entity>(m_SegmentRuleRecords.Keys))
                {
                    Entity record = m_SegmentRuleRecords[original];
                    if (IsRoadEdge(original))
                    {
                        if (m_MissingSegmentRules.Remove(record)) QueueSegmentPresetRefresh(record);
                        continue;
                    }
                    Entity replacement = Entity.Null;
                    foreach (KeyValuePair<Entity, Entity> candidate in m_SegmentRuleReplacements)
                        if (candidate.Value == record && IsRoadEdge(candidate.Key)) { replacement = candidate.Key; break; }
                    if (replacement != Entity.Null && !m_SegmentRuleRecords.ContainsKey(replacement))
                    {
                        SavedSegmentSurfaceRule saved = EntityManager.GetComponentData<SavedSegmentSurfaceRule>(record);
                        saved.Edge = replacement; EntityManager.SetComponentData(record, saved);
                        m_SegmentRuleRecords.Remove(original); m_SegmentRuleRecords[replacement] = record;
                        QueueSegmentPresetRefresh(record);
                        m_MissingSegmentRules.Remove(record); QueueManualEdge(replacement);
                        // A new junction can split the original segment. Only
                        // exact native original-edge links inherit its rule.
                        foreach (KeyValuePair<Entity, Entity> candidate in m_SegmentRuleReplacements)
                            if (candidate.Value == record && candidate.Key != replacement && IsRoadEdge(candidate.Key) &&
                                !m_SegmentRuleRecords.ContainsKey(candidate.Key)) CloneSegmentRule(record, candidate.Key);
                    }
                    else if (!m_MissingSegmentRules.TryGetValue(record, out int since)) m_MissingSegmentRules[record] = m_Frame;
                    else if (m_Frame - since >= 120) RemoveSegmentRule(original);
                }
                foreach (Entity candidate in new List<Entity>(m_SegmentRuleReplacements.Keys))
                    if (!EntityManager.Exists(candidate) || EntityManager.HasComponent<Deleted>(candidate) ||
                        !EntityManager.Exists(m_SegmentRuleReplacements[candidate]) ||
                        (!EntityManager.HasComponent<Temp>(candidate) && IsRoadEdge(EntityManager.GetComponentData<SavedSegmentSurfaceRule>(m_SegmentRuleReplacements[candidate]).Edge)))
                        m_SegmentRuleReplacements.Remove(candidate);
            }
            UpdateSegmentUpgrades(setting);
            UpdateSegmentPresetLinks(setting);
            while (RuntimeState.SegmentRuleRequests.Count > 0)
            {
                RuntimeState.SegmentRuleRequest request = RuntimeState.SegmentRuleRequests[0];
                RuntimeState.SegmentRuleRequests.RemoveAt(0); RuntimeState.SegmentRuleResponse++; RuntimeState.SavedSegmentRuleKey = "";
                if (!IsRoadEdge(request.Edge)) { RuntimeState.SegmentRuleMessage = "That segment is no longer available. Select it again."; continue; }
                try
                {
                    bool firstRule = setting.RuleCount == 0 && m_SegmentRuleRecords.Count == 0;
                    if (request.Clear) RemoveSegmentRule(request.Edge);
                    else SaveSegmentRule(request.Edge, request.Draft);
                    if (!request.Clear && firstRule && !setting.Enabled) { setting.Enabled = true; setting.ApplyAndSave(); }
                    QueueManualEdge(request.Edge);
                    RuntimeState.SavedSegmentRuleKey = RuntimeState.SegmentKey(request.Edge);
                    RuntimeState.SegmentRuleMessage = request.Clear ? "Segment restored to general rules." :
                        "Segment rule saved with the city. Its surfaces are queued for updating.";
                    if (EntityManager.HasComponent<ManualSurfaceRoad>(request.Edge))
                        RuntimeState.SegmentRuleMessage = "Rule saved. This road's areas are independent; use Tools > Resume automatic surfaces to enable automatic painting again.";
                }
                catch (ArgumentException exception) { RuntimeState.SegmentRuleMessage = exception.Message; }
            }
            RuntimeState.SegmentRuleCount = m_SegmentRuleRecords.Count;
            RuntimeState.ActiveSegmentRuleCount = 0;
            foreach (Entity record in m_SegmentRuleRecords.Values)
                if (EntityManager.Exists(record) && EntityManager.GetComponentData<SavedSegmentSurfaceRule>(record).Enabled)
                    RuntimeState.ActiveSegmentRuleCount++;
            UpdateSegmentRuleSnapshot(setting);
        }

        private void CloneSegmentRule(Entity source, Entity edge)
        {
            SavedSegmentSurfaceRule saved = EntityManager.GetComponentData<SavedSegmentSurfaceRule>(source);
            saved.Edge = edge;
            Entity record = EntityManager.CreateEntity(typeof(SavedSegmentSurfaceRule), typeof(PrefabRef));
            EntityManager.SetComponentData(record, saved);
            EntityManager.SetComponentData(record, EntityManager.GetComponentData<PrefabRef>(edge));
            DynamicBuffer<SavedSegmentSurfaceTarget> targets = EntityManager.AddBuffer<SavedSegmentSurfaceTarget>(record);
            foreach (SavedSegmentSurfaceTarget item in EntityManager.GetBuffer<SavedSegmentSurfaceTarget>(source, true)) targets.Add(item);
            SetSegmentRoadPavementMask(record, SegmentRoadPavementMask(source));
            SetSegmentPresetLink(record, CaptureSegmentPresetId(source));
            QueueSegmentPresetRefresh(record);
            m_SegmentRuleRecords[edge] = record; QueueManualEdge(edge);
        }

        private void SaveSegmentRule(Entity edge, SegmentRuleDraft draft)
        {
            SurfaceRule rule = draft.Rule;
            List<SavedSegmentSurfaceTarget> targets = new List<SavedSegmentSurfaceTarget>();
            foreach (SurfaceAssignment item in rule.Assignments)
            {
                Entity surface = Entity.Null;
                if (item.Surface.Length > 0 && !RuntimeState.TryGetSurface(item.Surface, out surface) && rule.Enabled)
                    throw new ArgumentException("A segment surface is unavailable. Refresh the surface list.");
                targets.Add(new SavedSegmentSurfaceTarget { Target = item.Target, Surface = surface, SurfaceName = new FixedString4096Bytes(item.Surface) });
            }
            FixedString512Bytes name = new FixedString512Bytes(rule.Name);
            if (!TryGetSegmentRule(edge, out Entity record, out _))
            {
                record = EntityManager.CreateEntity(typeof(SavedSegmentSurfaceRule), typeof(PrefabRef));
                EntityManager.AddBuffer<SavedSegmentSurfaceTarget>(record); m_SegmentRuleRecords[edge] = record;
            }
            EntityManager.SetComponentData(record, new SavedSegmentSurfaceRule {
                Edge = edge, Name = name, Enabled = rule.Enabled, FillBusShoulders = draft.FillBusShoulders,
                BikeMode = rule.SeparateBikeLanes.HasValue ? rule.SeparateBikeLanes.Value ? 2 : 1 : 0,
                BusMode = rule.SeparateBusLanes.HasValue ? rule.SeparateBusLanes.Value ? 2 : 1 : 0
            });
            DynamicBuffer<SavedSegmentSurfaceTarget> buffer = EntityManager.GetBuffer<SavedSegmentSurfaceTarget>(record);
            buffer.Clear(); foreach (SavedSegmentSurfaceTarget target in targets) buffer.Add(target);
            SetSegmentRoadPavementMask(record, RuleRoadPavementMask(rule));
            EntityManager.SetComponentData(record, new PrefabRef { m_Prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab });
            SetSegmentPresetLink(record, draft.PresetId ?? "");
            QueueSegmentPresetRefresh(record);
        }

        private void RemoveSegmentRule(Entity edge)
        {
            if (!m_SegmentRuleRecords.TryGetValue(edge, out Entity record)) return;
            m_SegmentRuleRecords.Remove(edge); m_MissingSegmentRules.Remove(record);
            foreach (Entity candidate in new List<Entity>(m_SegmentRuleReplacements.Keys))
                if (m_SegmentRuleReplacements[candidate] == record) m_SegmentRuleReplacements.Remove(candidate);
            if (EntityManager.Exists(record)) EntityManager.DestroyEntity(record);
        }

        private void UpdateSegmentRuleSnapshot(Setting setting)
        {
            Entity edge = RuntimeState.SelectedEdge;
            RuntimeState.SegmentRule = null; RuntimeState.SegmentFillBusShoulders = false;
            RuntimeState.SegmentRuleKey = RuntimeState.SegmentRuleRoad = "";
            if (!IsRoadEdge(edge)) return;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
            RuntimeState.SegmentRuleKey = RuntimeState.SegmentKey(edge); RuntimeState.SegmentRuleRoad = m_PrefabSystem.GetPrefabName(prefab);
            GetRuleLaneSettings(setting, prefab, false, out RuntimeState.SegmentInheritedBike, out RuntimeState.SegmentInheritedBus);
            if (!UsesCityRules(edge)) RuntimeState.SegmentInheritedBike = RuntimeState.SegmentInheritedBus = false;
            if (!TryGetSegmentRule(edge, out Entity record, out SavedSegmentSurfaceRule saved)) return;
            SurfaceRule rule = new SurfaceRule { Id = RuntimeState.SegmentRuleKey, Name = saved.Name.ToString(), Enabled = saved.Enabled,
                Assignments = new List<SurfaceAssignment>(), SeparateBikeLanes = saved.BikeMode == 0 ? (bool?)null : saved.BikeMode == 2,
                SeparateBusLanes = saved.BusMode == 0 ? (bool?)null : saved.BusMode == 2 };
            foreach (SavedSegmentSurfaceTarget target in EntityManager.GetBuffer<SavedSegmentSurfaceTarget>(record, true))
                rule.Assignments.Add(new SurfaceAssignment { Target = target.Target, Surface = target.SurfaceName.ToString(),
                    UseRoadPavement = SegmentUsesRoadPavement(record, target.Target) });
            RuntimeState.SegmentRule = rule; RuntimeState.SegmentFillBusShoulders = saved.FillBusShoulders;
        }

        private static bool BusLanesCoverDriving(SurfaceLayout layout)
        {
            if (layout.Driving.Count == 0 || layout.BusLanes.Count == 0) return false;
            foreach (LaneSpan traffic in layout.Driving)
            {
                if (traffic.Parking) continue;
                bool bus = false;
                foreach (LaneSpan lane in layout.BusLanes)
                    bus |= lane.Low <= traffic.Low + 0.01f && lane.High >= traffic.High - 0.01f;
                if (!bus) return false;
            }
            return true;
        }
    }
}
