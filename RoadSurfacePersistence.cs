using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    // Run after the native tool has accepted the click, before ApplyNetSystem
    // removes Temp/original references. Accepted targets retain their style
    // markers before the native handoff; canceled previews are never stamped.
    internal sealed partial class RoadSurfaceApplyCaptureSystem : GameSystemBase
    {
        protected override void OnUpdate() => World.GetOrCreateSystemManaged<SurfaceNetworkFilterSystem>().CaptureRoadSurfaceCommit();
    }

    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private readonly Dictionary<Entity, Entity> m_PreviewStyleSources = new Dictionary<Entity, Entity>();
        private readonly Dictionary<Entity, RoadStyleCommit> m_RoadStyleCommits = new Dictionary<Entity, RoadStyleCommit>();
        private int m_LoggedRoadStyleTransfers, m_LoggedUnresolvedSplits;

        private sealed class RoadStyleCommit
        {
            internal Entity Preview, Target, Source;
            internal int Frame;
            internal bool City, Selected, Excluded, Manual, HasLocal;
            internal SavedSegmentSurfaceRule Local;
            internal List<SavedSegmentSurfaceTarget> Targets;
            internal string PresetId;
        }

        private void ResetRoadStyleTransfers()
        {
            m_PreviewStyleSources.Clear(); m_RoadStyleCommits.Clear(); m_SurfaceReplaceRequests.Clear();
            m_LoggedRoadStyleTransfers = m_LoggedUnresolvedSplits = 0;
            m_NativeRoadNodeSources.Clear(); m_LoggedNativeRoadNodes = 0;
            m_NativeSplitSeedSources.Clear(); m_LoggedSeedSplitTransfers = 0;
            m_InvalidatedSurfaceOwners.Clear(); m_SurfaceInvalidationFrame = -1; m_LoggedSurfaceRepairs = 0;
            m_RetiredSurfaceAreas.Clear();
        }

        private void ReadPreviewStyleSources()
        {
            m_PreviewStyleSources.Clear();
            using (NativeArray<Entity> previews = GetEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(),
                ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Temp>(), ComponentType.Exclude<Deleted>()).ToEntityArray(Allocator.Temp))
            {
                HashSet<Entity> originals = new HashSet<Entity>();
                foreach (Entity preview in previews)
                {
                    Temp temp = EntityManager.GetComponentData<Temp>(preview);
                    if (IsRoadStyleSource(temp.m_Original)) originals.Add(temp.m_Original);
                }
                foreach (Entity source in m_NativeSplitSeedSources.Keys) originals.Add(source);
                // GenerateEdgesJob.SplitEdge takes its source edge from the
                // new junction NODE's Temp.m_Original. The child edges do not
                // carry that reference, and there need not be a temporary
                // edge pointing back to it. Capture the native node link too.
                using (NativeArray<Entity> nodes = GetEntityQuery(ComponentType.ReadOnly<Game.Net.Node>(),
                    ComponentType.ReadOnly<Temp>(), ComponentType.Exclude<Deleted>()).ToEntityArray(Allocator.Temp))
                    foreach (Entity node in nodes)
                    {
                        Temp temp = EntityManager.GetComponentData<Temp>(node);
                        if ((temp.m_Flags & TempFlags.Cancel) != 0) continue;
                        if (TryNativeRoadNodeSource(node, out Entity capturedSource)) originals.Add(capturedSource);
                        if (IsRoadStyleSource(temp.m_Original)) originals.Add(temp.m_Original);
                        else if (EntityManager.HasBuffer<ConnectedEdge>(temp.m_Original))
                            foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(temp.m_Original, true))
                                if (IsRoadStyleSource(connected.m_Edge)) originals.Add(connected.m_Edge);
                    }
                Dictionary<Entity, Entity> splitSources = ReadNativeSplitSources(previews, originals);
                foreach (Entity preview in previews)
                {
                    Temp temp = EntityManager.GetComponentData<Temp>(preview);
                    if ((temp.m_Flags & (TempFlags.Delete | TempFlags.Cancel | TempFlags.Duplicate)) != 0) continue;
                    if (temp.m_Original != Entity.Null)
                    { m_PreviewStyleSources[preview] = temp.m_Original; continue; }
                    // Native GenerateEdgesJob.CreateTempEdge creates split
                    // fragments as Essential, with no original and no Create
                    // flag. Only those fragments can inherit by subdivision.
                    if ((temp.m_Flags & TempFlags.Essential) == 0 || (temp.m_Flags & TempFlags.Create) != 0) continue;
                    if (splitSources.TryGetValue(preview, out Entity splitSource))
                    { m_PreviewStyleSources[preview] = splitSource; continue; }
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(preview).m_Prefab;
                    Edge endpoints = EntityManager.GetComponentData<Edge>(preview);
                    Bezier4x3 fragment = EntityManager.GetComponentData<Curve>(preview).m_Bezier;
                    Entity source = Entity.Null;
                    // Native splitting can adjust endpoint elevation and can
                    // rebuild hanging/straight handles. Preserve the explicit
                    // node/edge provenance before considering curve equality.
                    foreach (Entity original in originals)
                    {
                        if (!TryReadRoadStyleSource(original, out Entity sourcePrefab, out _, out _) || sourcePrefab != prefab ||
                            !HasNativeFragmentEndpoints(endpoints, original)) continue;
                        if (source != Entity.Null) { source = Entity.Null; break; }
                        source = original;
                    }
                    if (source != Entity.Null) { m_PreviewStyleSources[preview] = source; continue; }
                    foreach (Entity original in originals)
                    {
                        if (!TryReadRoadStyleSource(original, out Entity sourcePrefab, out _, out Bezier4x3 sourceCurve) || sourcePrefab != prefab ||
                            !IsNativeCurveSubdivision(sourceCurve, fragment)) continue;
                        // Ambiguous coincident roads must never borrow each
                        // other's styles. This is not a nearest-road search.
                        if (source != Entity.Null) { source = Entity.Null; break; }
                        source = original;
                    }
                    if (source != Entity.Null) { m_PreviewStyleSources[preview] = source; continue; }
                    if (TryNativeSplitSeedSource(preview, originals, out source)) m_PreviewStyleSources[preview] = source;
                }
            }
        }

        private bool HasNativeFragmentEndpoints(Edge fragment, Entity source)
        {
            if (!TryReadRoadStyleSource(source, out _, out Edge original, out _)) return false;
            return BelongsToNativeSource(fragment.m_Start, source, original) &&
                BelongsToNativeSource(fragment.m_End, source, original);
        }

        private bool BelongsToNativeSource(Entity node, Entity source, Edge original)
        {
            if (TryNativeRoadNodeSource(node, out Entity captured)) return captured == source;
            node = NativeSourceNode(node);
            if (node == original.m_Start || node == original.m_End) return true;
            if (!EntityManager.HasComponent<Temp>(node)) return false;
            Entity from = EntityManager.GetComponentData<Temp>(node).m_Original;
            return from == source || from == original.m_Start || from == original.m_End;
        }

        private static bool IsNativeCurveSubdivision(Bezier4x3 original, Bezier4x3 fragment)
        {
            const float tolerance = 0.025f;
            if (MathUtils.Distance(original, fragment.a, out float from) > tolerance ||
                MathUtils.Distance(original, fragment.d, out float to) > tolerance || math.abs(to - from) < 0.00001f) return false;
            Bezier4x3 cut = MathUtils.Cut(original, new float2(math.min(from, to), math.max(from, to)));
            if (from > to) cut = new Bezier4x3(cut.d, cut.c, cut.b, cut.a);
            // Validate all four control points, including elevation, rather
            // than assigning styles to roads merely sharing an endpoint.
            return math.distance(cut.a, fragment.a) <= tolerance && math.distance(cut.b, fragment.b) <= tolerance &&
                math.distance(cut.c, fragment.c) <= tolerance && math.distance(cut.d, fragment.d) <= tolerance;
        }

        private Entity PreviewStyleSource(Entity preview, Temp temp) =>
            m_PreviewStyleSources.TryGetValue(preview, out Entity source) ? source : temp.m_Original;

        internal void CaptureRoadSurfaceCommit()
        {
            if (!IsInLoadedGame() || !m_CityRulesInitialized || !m_SegmentRulesInitialized ||
                World.GetOrCreateSystemManaged<ToolSystem>().applyMode != ApplyMode.Apply) return;
            ReadPreviewStyleSources();
            ToolSystem tools = World.GetOrCreateSystemManaged<ToolSystem>();
            NetToolSystem net = World.GetOrCreateSystemManaged<NetToolSystem>();
            // Record unresolved accepted fragments rather than silently hiding
            // a new native topology case behind an apparently successful apply.
            using (NativeArray<Entity> previews = GetEntityQuery(ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Temp>(),
                ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Deleted>()).ToEntityArray(Allocator.Temp))
                foreach (Entity preview in previews)
                {
                    Temp temp = EntityManager.GetComponentData<Temp>(preview);
                    if (m_LoggedUnresolvedSplits >= 12 || m_PreviewStyleSources.ContainsKey(preview) ||
                        temp.m_Original != Entity.Null || (temp.m_Flags & TempFlags.Essential) == 0 ||
                        (temp.m_Flags & (TempFlags.Create | TempFlags.Delete | TempFlags.Cancel | TempFlags.Duplicate)) != 0) continue;
                    Edge ends = EntityManager.GetComponentData<Edge>(preview); m_LoggedUnresolvedSplits++;
                    Mod.Log.Info("Unresolved accepted road split: edge=" + preview.Index + "; flags=" + temp.m_Flags +
                        "; endpoints=" + NativeSourceNode(ends.m_Start).Index + "," + NativeSourceNode(ends.m_End).Index +
                        "; child seed=" + (EntityManager.HasComponent<PseudoRandomSeed>(preview) ? EntityManager.GetComponentData<PseudoRandomSeed>(preview).m_Seed.ToString() : "missing") +
                        "; start lineage=" + (TryNativeRoadNodeSource(ends.m_Start, out Entity startSource) ? startSource.Index.ToString() : "none") +
                        "; end lineage=" + (TryNativeRoadNodeSource(ends.m_End, out Entity endSource) ? endSource.Index.ToString() : "none") +
                        "; prefab='" + m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(preview).m_Prefab) + "'.");
                }
            foreach (KeyValuePair<Entity, Entity> entry in m_PreviewStyleSources)
            {
                Entity preview = entry.Key, source = entry.Value;
                // ApplyMode.Apply is the native acceptance gate. A committed
                // edge may still carry a nonblocking Warning; its style must
                // transfer once that actual edge exists after the apply pass.
                if (!IsRoadStyleSource(source)) continue;
                if (UsesCityRules(source) && IsRoadEdge(source)) RememberCitySurfaceSource(source);
                Temp temp = EntityManager.GetComponentData<Temp>(preview);
                Entity target = (temp.m_Flags & (TempFlags.Replace | TempFlags.Combine)) != 0 || temp.m_Original == Entity.Null
                    ? preview : temp.m_Original;
                if (tools.activeTool == net && net.actualMode == NetToolSystem.Mode.Replace &&
                    m_SurfaceReplaceRequests.TryGetValue(source, out SurfaceReplaceRequest replace) &&
                    m_Frame - replace.Frame <= 2) replace.NativeTargets.Add(target);
                bool hasLocal = TryGetSegmentRule(source, out Entity record, out SavedSegmentSurfaceRule local);
                RoadStyleCommit commit = new RoadStyleCommit { Preview = preview, Source = source, Target = target, Frame = m_Frame,
                    City = UsesCityRules(source), Selected = EntityManager.HasComponent<SurfaceSegmentSelected>(source),
                    Excluded = EntityManager.HasComponent<SurfaceSegmentExcluded>(source),
                    Manual = EntityManager.HasComponent<ManualSurfaceRoad>(source), HasLocal = hasLocal, Local = local };
                if (!commit.City && !commit.Selected && !commit.Excluded && !commit.Manual && !hasLocal) continue;
                if (hasLocal)
                {
                    commit.PresetId = CaptureSegmentPresetId(record);
                    commit.Targets = new List<SavedSegmentSurfaceTarget>();
                    foreach (SavedSegmentSurfaceTarget item in EntityManager.GetBuffer<SavedSegmentSurfaceTarget>(record, true)) commit.Targets.Add(item);
                }
                m_RoadStyleCommits[preview] = commit;
                // Native Create keeps arbitrary components on this accepted
                // entity when removing Temp. Stamp membership before that
                // handoff, instead of relying solely on a later preview lookup.
                if (EntityManager.Exists(target))
                {
                    if (commit.City && !EntityManager.HasComponent<CitySurfaceRuleMember>(target)) EntityManager.AddComponent<CitySurfaceRuleMember>(target);
                    if (commit.Selected && !EntityManager.HasComponent<SurfaceSegmentSelected>(target)) EntityManager.AddComponent<SurfaceSegmentSelected>(target);
                    if (commit.Excluded && !EntityManager.HasComponent<SurfaceSegmentExcluded>(target)) EntityManager.AddComponent<SurfaceSegmentExcluded>(target);
                    if (commit.Manual && !EntityManager.HasComponent<ManualSurfaceRoad>(target)) EntityManager.AddComponent<ManualSurfaceRoad>(target);
                    if (commit.HasLocal && !TryGetSegmentRule(target, out _, out _)) RestoreSegmentStyle(target, commit);
                }
            }
        }

        private void CompleteRoadStyleTransfers()
        {
            foreach (Entity preview in new List<Entity>(m_RoadStyleCommits.Keys))
            {
                RoadStyleCommit commit = m_RoadStyleCommits[preview];
                if (m_Frame - commit.Frame > 240) { m_RoadStyleCommits.Remove(preview); continue; }
                if (EntityManager.Exists(preview) && EntityManager.HasComponent<Temp>(preview) && !EntityManager.HasComponent<Deleted>(preview)) continue;
                if (!IsRoadEdge(commit.Target))
                {
                    continue;
                }
                Entity target = commit.Target;
                if (commit.City && !UsesCityRules(target)) EntityManager.AddComponent<CitySurfaceRuleMember>(target);
                if (commit.Selected && !EntityManager.HasComponent<SurfaceSegmentSelected>(target)) EntityManager.AddComponent<SurfaceSegmentSelected>(target);
                if (commit.Excluded && !EntityManager.HasComponent<SurfaceSegmentExcluded>(target)) EntityManager.AddComponent<SurfaceSegmentExcluded>(target);
                if (commit.Manual && !EntityManager.HasComponent<ManualSurfaceRoad>(target)) EntityManager.AddComponent<ManualSurfaceRoad>(target);
                if (commit.HasLocal && !TryGetSegmentRule(target, out _, out _)) RestoreSegmentStyle(target, commit);
                QueueRoadStyleRebuild(target);
                m_RoadStyleCommits.Remove(preview);
                if (target != commit.Source && m_LoggedRoadStyleTransfers++ < 16)
                    Mod.Log.Info("Preserved road surfaces through native split/replacement: original=" + commit.Source.Index +
                        "; result=" + target.Index + "; city rules=" + commit.City + "; segment override=" + commit.HasLocal + ".");
            }
        }

        private void RestoreSegmentStyle(Entity edge, RoadStyleCommit commit)
        {
            Entity record = EntityManager.CreateEntity(typeof(SavedSegmentSurfaceRule), typeof(PrefabRef));
            SavedSegmentSurfaceRule saved = commit.Local; saved.Edge = edge;
            EntityManager.SetComponentData(record, saved);
            EntityManager.SetComponentData(record, EntityManager.GetComponentData<PrefabRef>(edge));
            DynamicBuffer<SavedSegmentSurfaceTarget> buffer = EntityManager.AddBuffer<SavedSegmentSurfaceTarget>(record);
            foreach (SavedSegmentSurfaceTarget item in commit.Targets) buffer.Add(item);
            SetSegmentPresetLink(record, commit.PresetId);
            QueueSegmentPresetRefresh(record);
            m_SegmentRuleRecords[edge] = record;
        }

        private void RestoreRoadToVanilla(Entity edge)
        {
            RemoveSegmentRule(edge);
            m_CitySurfaceSources.Remove(edge);
            if (UsesCityRules(edge)) EntityManager.RemoveComponent<CitySurfaceRuleMember>(edge);
            m_SegmentRuleReplacements.Remove(edge);
            m_CityMemberPreviews.Remove(edge);
            m_PlacementPreviews.Remove(edge);
            // A previously accepted native edit must not restore a stale
            // surface snapshot after the user's explicit vanilla choice.
            // Keep saved selection/exclusion metadata independent of surfaces.
            foreach (RoadStyleCommit commit in m_RoadStyleCommits.Values)
                if (commit.Target == edge)
                {
                    commit.City = false; commit.HasLocal = false; commit.Targets = null; commit.PresetId = null;
                }
        }

        private void QueueRoadStyleRebuild(Entity edge)
        {
            InvalidateRoadSurfaceOwner(edge);
            if (!IsRoadEdge(edge)) return;
            Edge endpoints = EntityManager.GetComponentData<Edge>(edge);
            HashSet<Entity> neighbors = new HashSet<Entity>();
            foreach (Entity node in new[] { endpoints.m_Start, endpoints.m_End })
                if (EntityManager.HasBuffer<ConnectedEdge>(node))
                    foreach (ConnectedEdge connected in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                        if (IsRoadEdge(connected.m_Edge)) neighbors.Add(connected.m_Edge);
            foreach (Entity neighbor in neighbors) InvalidateRoadSurfaceOwner(neighbor);
        }
    }
}
