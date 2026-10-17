using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Areas;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.SceneFlow;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

using AreaNode = Game.Areas.Node;
using PrefabRef = Game.Prefabs.PrefabRef;

namespace SurfaceNetworkFilter
{
    internal static partial class RuntimeState
    {
        private static readonly List<string> s_SurfaceNames = new List<string>();
        private static readonly Dictionary<string, Entity> s_SurfaceEntities = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        internal static readonly Dictionary<string, string> SurfacePreviews = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static bool RebuildRequested;
        internal static bool SurfaceRefreshRequested;
        internal static string Status = "Waiting for a loaded city.";

        internal static int GetSurfaceCount()
        {
            return s_SurfaceNames.Count;
        }

        internal static IEnumerable<string> GetSurfaceNames(string search)
        {
            string query = (search ?? string.Empty).Trim();
            for (int i = 0; i < s_SurfaceNames.Count; i++)
            {
                string name = s_SurfaceNames[i];
                if (query.Length == 0 || name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    yield return name;
            }
        }

        internal static bool TryGetSurface(string name, out Entity entity)
        {
            if (string.IsNullOrEmpty(name))
            {
                entity = Entity.Null;
                return false;
            }
            return s_SurfaceEntities.TryGetValue(name, out entity);
        }

        internal static IEnumerable<string> GetInstalledSurfaceNames() => s_SurfaceEntities.Keys;

        internal static bool IsSelectableSurface(string name) => !string.IsNullOrEmpty(name) &&
            s_SurfaceNames.BinarySearch(name, StringComparer.OrdinalIgnoreCase) >= 0;

        internal static bool ReplaceSurfaceCatalog(Dictionary<string, Entity> catalog, HashSet<string> roadSurfaces)
        {
            bool namesChanged = s_SurfaceEntities.Count != catalog.Count || s_SurfaceNames.Count != roadSurfaces.Count;
            if (!namesChanged)
            {
                foreach (string existingName in s_SurfaceEntities.Keys)
                {
                    if (!catalog.ContainsKey(existingName))
                    {
                        namesChanged = true;
                        break;
                    }
                }
                foreach (string existingName in s_SurfaceNames)
                    if (!roadSurfaces.Contains(existingName)) { namesChanged = true; break; }
            }

            s_SurfaceEntities.Clear();
            s_SurfaceNames.Clear();
            foreach (KeyValuePair<string, Entity> entry in catalog)
            {
                if (string.IsNullOrEmpty(entry.Key))
                    continue;
                s_SurfaceEntities[entry.Key] = entry.Value;
                // Keep every installed asset resolvable for old rules and saves.
                // Only surfaces whose renderer can affect roads are offered anew.
                if (roadSurfaces.Contains(entry.Key)) s_SurfaceNames.Add(entry.Key);
            }
            s_SurfaceNames.Sort(StringComparer.OrdinalIgnoreCase);
            return namesChanged;
        }
    }

    internal sealed partial class SurfaceNetworkFilterSystem : GameSystemBase
    {
        private const float SampleSpacing = 6f;
        private const float SurfaceLift = 0.04f;
        private const int QueueBudgetPerUpdate = 2;
        private const double QueueTimeBudgetMilliseconds = 4.0;
        private const int PendingAreaBudget = 128;
        private const int CatalogRefreshInterval = 300;
        private const int AreaScanInterval = 15;
        private const int PendingTimeout = 600;

        private EntityQuery m_AllEdges;
        private EntityQuery m_UpdatedEdges;
        private EntityQuery m_SurfacePrefabs;
        private EntityQuery m_AreaEntities;
        private EntityQuery m_GeneratedAreas;
        private EntityQuery m_SurfaceMeshes;
        private EntityQuery m_DirtySurfaceMeshes;
        private EntityQuery m_UnclaimedSurfaceAreas;
        private PrefabSystem m_PrefabSystem;

        private readonly HashSet<Entity> m_NewRoadEdges = new HashSet<Entity>();
        private readonly Dictionary<Entity, int> m_EdgeSignatures = new Dictionary<Entity, int>();
        private readonly HashSet<Entity> m_QueuedEdges = new HashSet<Entity>();
        private readonly List<Entity> m_EdgeQueue = new List<Entity>();
        private readonly Dictionary<Entity, int> m_EdgeReadyFrames = new Dictionary<Entity, int>();
        private readonly Dictionary<Entity, int> m_GeometryChecks = new Dictionary<Entity, int>();
        private int m_QueueCursor;
        private readonly Dictionary<Entity, List<Entity>> m_GeneratedByOwner = new Dictionary<Entity, List<Entity>>();
        private readonly HashSet<Entity> m_KnownAreaEntities = new HashSet<Entity>();
        private readonly HashSet<Entity> m_ManagedEdges = new HashSet<Entity>();
        private readonly HashSet<Entity> m_DeferredEdges = new HashSet<Entity>();
        private readonly Dictionary<Entity, int> m_PendingAreaCounts = new Dictionary<Entity, int>();
        private readonly List<PendingArea> m_PendingAreas = new List<PendingArea>();
        private readonly HashSet<Entity> m_LoggedSurfaceRenderers = new HashSet<Entity>();
        private readonly HashSet<Entity> m_PreparedSurfaceMeshes = new HashSet<Entity>();
        private bool m_LoggedProjection;
        private float m_LastProjectionDepth = -1f;
        private readonly Dictionary<Entity, float> m_ProjectionPadding = new Dictionary<Entity, float>();
        private bool m_LoggedBridgeHeight;

        private bool m_BaselineCaptured;
        private bool m_AreaIndexInitialized;
        private bool m_CatalogInitialized;
        private readonly HashSet<string> m_FailedSurfacePreviews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int m_Frame;
        private string m_LastFingerprint;
        private int m_GeneratedCount;
        private int m_FailedAreaCount;
        private bool m_LoggedAreaDefinitionQueued;
        private bool m_LoggedNativeAreaCompletion;
        private bool m_LoggedBoundaryGeometry;
        private bool m_LoggedRoundaboutGeometry;
        private int m_RefreshCompletedFrame = -1;
        private bool m_RefreshInProgress;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            m_AllEdges = GetEntityQuery(
                ComponentType.ReadOnly<Edge>(),
                ComponentType.ReadOnly<Curve>(),
                ComponentType.ReadOnly<Composition>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
            m_UpdatedEdges = GetEntityQuery(
                ComponentType.ReadOnly<Edge>(),
                ComponentType.ReadOnly<Curve>(),
                ComponentType.ReadOnly<Composition>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Updated>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
            m_SurfacePrefabs = GetEntityQuery(
                ComponentType.ReadOnly<SurfaceData>(),
                ComponentType.ReadOnly<AreaData>());
            // Query Area entities broadly, then filter by the selected SurfaceData prefab.
            // This lets completion tracking see area output even if a provider's archetype
            // does not carry the Surface marker expected by the narrower query.
            m_AreaEntities = GetEntityQuery(
                ComponentType.ReadOnly<Game.Areas.Area>(),
                ComponentType.ReadOnly<Owner>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());
            m_GeneratedAreas = GetEntityQuery(
                ComponentType.ReadOnly<Game.Areas.Area>(),
                ComponentType.ReadOnly<GeneratedSurface>(),
                ComponentType.ReadOnly<Owner>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());
            m_SurfaceMeshes = GetEntityQuery(ComponentType.ReadOnly<SurfaceMeshTriangle>(),
                ComponentType.ReadOnly<Game.Areas.Area>(), ComponentType.ReadOnly<Owner>(),
                ComponentType.Exclude<Temp>(), ComponentType.Exclude<Deleted>());
            m_DirtySurfaceMeshes = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<SurfaceMeshTriangle>(), ComponentType.ReadOnly<Game.Areas.Area>(), ComponentType.ReadOnly<Owner>() },
                Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Created>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() }
            });
            m_UnclaimedSurfaceAreas = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Areas.Area>(), ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<PrefabRef>() },
                Any = new[] { ComponentType.ReadOnly<Updated>(), ComponentType.ReadOnly<Created>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<SurfaceMeshTriangle>() }
            });
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context context)
        {
            base.OnGameLoaded(context);
            // A city load can reuse this World. Keep serialized segment/area data,
            // but discard entity caches and queued work from the previous city.
            m_NewRoadEdges.Clear(); m_EdgeSignatures.Clear(); m_QueuedEdges.Clear(); m_EdgeQueue.Clear();
            m_EdgeReadyFrames.Clear(); m_GeometryChecks.Clear(); m_QueueCursor = 0;
            m_GeneratedByOwner.Clear(); m_KnownAreaEntities.Clear(); m_ManagedEdges.Clear();
            m_DeferredEdges.Clear(); m_PendingAreaCounts.Clear(); m_PendingAreas.Clear(); m_ManualEdges.Clear();
            m_LoggedSurfaceRenderers.Clear(); m_PreparedSurfaceMeshes.Clear(); m_ProjectionPadding.Clear();
            ResetSurfaceGenerationPerformance();
            m_LoggedNetworkOverlayPrefabs.Clear();
            m_FailedNetworkOverlayPrefabs.Clear();
            m_PieceTopVertices.Clear(); m_LoggedLayouts.Clear();
            m_LoggedPlantedLayouts.Clear(); m_LoggedPlantingCapEdges.Clear(); m_PieceSurfaceProfiles.Clear(); m_LoggedPieceSurfaceProfiles.Clear();
            m_LoggedSidewalkMaterialLayouts.Clear();
            m_JunctionPlantMasks.Clear(); m_JunctionPlantFrame = -1; m_ActiveJunctionPlantMasks = null;
            m_LoggedDedicatedLanes.Clear();
            m_JunctionLaneProfiles.Clear(); m_LoggedJunctionLanes.Clear(); m_ActiveJunctionLaneProfile = null;
            m_LoggedJunctionLaneSignatures.Clear(); m_JunctionLaneReportCount = 0;
            m_LoggedContourMasks = 0;
            m_LoggedLaneMaskFallbacks = 0;
            m_JunctionLaneCacheFrame = -1;
            m_BaselineCaptured = m_AreaIndexInitialized = m_CatalogInitialized = false;
            m_GeneratedCount = m_FailedAreaCount = 0; m_Frame = 0; m_LastFingerprint = null;
            m_LastProjectionDepth = -1f; m_RefreshCompletedFrame = -1; m_RefreshInProgress = false;
            m_LoggedAreaDefinitionQueued = m_LoggedNativeAreaCompletion = m_LoggedBoundaryGeometry = false;
            m_LoggedRoundaboutGeometry = m_LoggedBridgeHeight = m_LoggedProjection = false;
            m_LoggedMeshReadFailure = m_LoggedMissingNodeLayout = m_LoggedJunctionCounts = m_LoggedRoundaboutCounts = false;
            m_LoggedStreetJunctionRules = m_LoggedRoundaboutStreetRules = false;
            m_LoggedRoundaboutRuleContexts.Clear();
            m_LoggedJunctionMapping = false;
            m_LoggedRoundaboutSidewalks.Clear();
            m_LoggedSmallRoundabouts.Clear();
            m_LoggedRoundaboutShoulders.Clear();
            m_LoggedRoundaboutCurbProjection.Clear();
            m_RoundaboutWalkMasks.Clear(); m_RoundaboutWalkMaskFrame = -1;
            m_LoggedRoundaboutWalkClips = 0;
            m_LoggedSmallRoundaboutGuards = 0;
            m_LoggedMainRoadJunctions.Clear();
            m_AlleyApproachLayouts.Clear();
            ResetJunctionSurfaceRecords();
            ResetSegmentRules();
            ResetCityRules();
            m_LoggedSweepMasks = m_LoggedJunctionShoulders = 0;
            m_LoggedBusShoulderFallbacks = m_LoggedBusShoulderConnections = 0;
            m_JunctionPavementDomains.Clear(); m_JunctionDomainFrame = -1; m_LoggedCrosswalkDomains = 0;
            m_LoggedCurbCuts.Clear(); m_LoggedSharedJunctions.Clear(); m_BoundaryOffsets.Clear();
            m_JunctionDiagnosticOwners.Clear(); m_JunctionDiagnosticEdges.Clear();
            m_LoggedJunctionPatchCoordinates = m_LoggedJunctionMaterialization = 0;
            m_LoggedJunctionClipFallbacks = 0;
            m_LoggedBoundaryCompensation = 0;
            m_LoggedSmallBoundaryCompensation = 0;
            m_LoggedRoundaboutBevels = false;
            RuntimeState.SelectedEdge = Entity.Null; RuntimeState.SegmentRequests.Clear(); RuntimeState.SelectionMessage = "";
            RuntimeState.SelectedJunction = Entity.Null; RuntimeState.JunctionRequests.Clear(); RuntimeState.JunctionMessage = "";
            RuntimeState.JunctionStatus = "Select a road near the junction you want to change.";
            RuntimeState.JunctionEditor = new JunctionEditorState();
            RuntimeState.JunctionCycleActive = false;
            RuntimeState.JunctionRestoreAutomatic = false;
            RuntimeState.ManualSurfaceEditorActive = false;
            RuntimeState.ManualSurfaceEditorMessage = "";
            RuntimeState.JunctionCycleTarget = Entity.Null;
            RuntimeState.LastRoadSelectionPosition = default;
            RuntimeState.SelectionStatus = "Select a road segment in the city first.";
            RuntimeState.Status = "Reading the loaded city.";
        }

        protected override void OnUpdate()
        {
            m_Frame++;
            Setting setting = Mod.ActiveSetting;
            if (setting == null || !IsInLoadedGame())
                return;

            if (RuntimeState.SurfaceRefreshRequested || !m_CatalogInitialized || m_Frame % CatalogRefreshInterval == 0)
            {
                RuntimeState.SurfaceRefreshRequested = false;
                RefreshSurfaceCatalog();
            }

            if (!m_AreaIndexInitialized) InitializeAreaIndex();
            UpdateCityRules(setting);
            UpdateSegmentRules(setting);
            RestoreAllRoadSurfaces(setting);
            ApplySurfaceReplaceRequests(setting);
            if (!NetworkOverlayPrefabsReady(setting) || !SegmentRenderersReady()) return;

            UpdateSavedJunctionSurfaces(setting);
            if (!SavedJunctionRenderersReady()) return;

            if (!m_AreaIndexInitialized)
                InitializeAreaIndex();

            CompletePendingAreas();
            PruneDeletedOwners();
            MaintainGeneratedSurfaceOwnership(setting);

            if (!m_BaselineCaptured)
                CaptureInitialRoadBaseline(setting);

            UpdateRoadSelection(setting);

            string fingerprint = setting.ApplyFingerprint();
            if (m_LastFingerprint != null && !string.Equals(m_LastFingerprint, fingerprint, StringComparison.Ordinal))
                HandleSettingChange(setting);
            m_LastFingerprint = fingerprint;

            if (RuntimeState.RebuildRequested)
            {
                RuntimeState.RebuildRequested = false;
                if (setting.Enabled)
                    QueueRefresh(setting);
                else
                    RuntimeState.Status = "Enable the mod before rebuilding surfaces.";
            }

            if (m_BaselineCaptured)
            {
                ProcessSettledGeometry(setting);
                ProcessUpdatedRoads(setting);
            }

            PumpQueue(setting);
            UpdateStatus(setting);
        }

        private bool IsInLoadedGame()
        {
            GameManager manager = GameManager.instance;
            return manager != null && manager.gameMode == GameMode.Game && !manager.isGameLoading;
        }

        private void RefreshSurfaceCatalog()
        {
            Dictionary<string, Entity> catalog = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> roadSurfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Game.UI.ImageSystem images = World.GetOrCreateSystemManaged<Game.UI.ImageSystem>();
            RuntimeState.SurfacePreviews.Clear();
            using (NativeArray<Entity> entities = m_SurfacePrefabs.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    string name = m_PrefabSystem.GetPrefabName(entity);
                    if (string.IsNullOrEmpty(name) || IsNetworkOverlayPrefab(name) || catalog.ContainsKey(name)) continue;
                    catalog.Add(name, entity);
                    if (CanRenderSurfaceOnRoads(entity))
                    {
                        roadSurfaces.Add(name);
                        RuntimeState.SurfacePreviews[name] = GetSurfacePreview(images, entity, name);
                    }
                }
            }
            int oldCount = RuntimeState.GetSurfaceCount();
            bool roadNamesChanged = RefreshRoadCatalog();
            bool surfaceNamesChanged = RuntimeState.ReplaceSurfaceCatalog(catalog, roadSurfaces);
            m_JunctionReferenceChecks.Clear();
            if (surfaceNamesChanged) RuntimeState.NotifySegmentPresetsChanged();
            if (surfaceNamesChanged || roadNamesChanged)
            {
                Mod.RefreshOptionsUI();
                Mod.Log.Info("Updated surface dropdown choices: " + roadSurfaces.Count + " road-compatible surface(s), " +
                    (catalog.Count - roadSurfaces.Count) + " surface(s) without road rendering support filtered out.");
            }
            if (catalog.Count == 0 && (!m_CatalogInitialized || oldCount != 0))
                Mod.Log.Warn("No SurfacePrefab entries were found in the loaded game world.");
            else if (roadSurfaces.Count != oldCount)
                Mod.Log.Info("Loaded surface catalog contains " + roadSurfaces.Count + " road-compatible surface(s).");
            m_CatalogInitialized = true;
        }

        private bool CanRenderSurfaceOnRoads(Entity prefab)
        {
            // The native area batch renderer passes this mask to its material,
            // including custom materials. Terrain-only surfaces cannot affect
            // road receivers. Names, UI visibility and provider are irrelevant.
            return m_PrefabSystem.TryGetPrefab(prefab, out SurfacePrefab surface) &&
                surface.TryGet(out RenderedArea renderer) && (renderer.m_DecalLayerMask & DecalLayers.Roads) != 0;
        }

        private string GetSurfacePreview(Game.UI.ImageSystem images, Entity prefab, string name)
        {
            try { return images.GetThumbnail(prefab) ?? ""; }
            catch (Exception exception)
            {
                // A provider's missing/malformed thumbnail must never remove a
                // usable material or stop road generation. Refresh retries it.
                if (m_FailedSurfacePreviews.Add(name))
                    Mod.Log.Warn("Surface preview unavailable for '" + name + "': " + exception.Message);
                return "";
            }
        }

        private void InitializeAreaIndex()
        {
            using (NativeArray<Entity> generated = m_GeneratedAreas.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < generated.Length; i++)
                {
                    Entity area = generated[i];
                    m_KnownAreaEntities.Add(area);
                    Owner owner = EntityManager.GetComponentData<Owner>(area);
                    AddGeneratedArea(owner.m_Owner, area);
                    m_ManagedEdges.Add(owner.m_Owner);
                }
            }
            using (NativeArray<Entity> existing = m_AreaEntities.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < existing.Length; i++)
                    m_KnownAreaEntities.Add(existing[i]);
            }
            m_AreaIndexInitialized = true;
        }

        private void CaptureInitialRoadBaseline(Setting setting)
        {
            using (NativeArray<Entity> edges = m_AllEdges.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < edges.Length; i++)
                {
                    Entity edge = edges[i];
                    if (!IsRoadEdge(edge))
                    {
                        if (m_GeneratedByOwner.ContainsKey(edge))
                            DestroyGeneratedFor(edge);
                        continue;
                    }
                    m_EdgeSignatures[edge] = GetEdgeSignature(edge);
                    if (setting.Enabled && (HasPlacementRules(edge) || m_ManagedEdges.Contains(edge)))
                        QueueEdge(edge);
                }
            }
            m_BaselineCaptured = true;
        }

        private void HandleSettingChange(Setting setting)
        {
            if (!setting.Enabled || !m_BaselineCaptured)
                return;

            QueueManagedRoads();
            QueueAssignedRoads();
        }

        private void QueueRefresh(Setting setting)
        {
            m_RefreshInProgress = true;
            m_RefreshCompletedFrame = -1;
            QueueManagedRoads();
            QueueAssignedRoads();
        }

        private void QueueAllRoads()
        {
            using (NativeArray<Entity> edges = m_AllEdges.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < edges.Length; i++)
                {
                    Entity edge = edges[i];
                    if (!IsRoadEdge(edge))
                        continue;
                    QueueEdge(edge);
                }
            }
        }

        private void QueueManagedRoads()
        {
            List<Entity> owners = new List<Entity>(m_ManagedEdges);
            for (int i = 0; i < owners.Count; i++)
            {
                Entity edge = owners[i];
                if (EntityManager.Exists(edge) && IsRoadEdge(edge))
                    QueueEdge(edge);
            }
        }

        private void QueueNewRoads()
        {
            List<Entity> newRoads = new List<Entity>(m_NewRoadEdges);
            for (int i = 0; i < newRoads.Count; i++)
            {
                Entity edge = newRoads[i];
                if (EntityManager.Exists(edge) && IsRoadEdge(edge))
                    QueueEdge(edge);
            }
        }

        private void ProcessUpdatedRoads(Setting setting)
        {
            using (NativeArray<Entity> edges = m_UpdatedEdges.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < edges.Length; i++)
                {
                    Entity edge = edges[i];
                    if (!IsRoadEdge(edge))
                    {
                        if (m_GeneratedByOwner.ContainsKey(edge))
                            DestroyGeneratedFor(edge);
                        continue;
                    }

                    // Adjacent edges can receive new junction geometry later in this
                    // frame without changing their curve or composition beforehand.
                    // Inspect them next frame even if today's signature is unchanged.
                    if (!m_GeometryChecks.ContainsKey(edge))
                        m_GeometryChecks.Add(edge, m_Frame + 1);
                    Edge endpoints = EntityManager.GetComponentData<Edge>(edge);
                    ScheduleJunctionGeometryChecks(endpoints.m_Start);
                    ScheduleJunctionGeometryChecks(endpoints.m_End);

                    int signature = GetEdgeSignature(edge);
                    int oldSignature;
                    bool known = m_EdgeSignatures.TryGetValue(edge, out oldSignature);
                    if (!known)
                        m_NewRoadEdges.Add(edge);
                    if (known && oldSignature == signature)
                        continue;

                    m_EdgeSignatures[edge] = signature;
                    if (setting.Enabled && (HasPlacementRules(edge) || m_ManagedEdges.Contains(edge)))
                        QueueEdge(edge);
                }
            }
        }

        private void ProcessSettledGeometry(Setting setting)
        {
            List<Entity> ready = new List<Entity>();
            foreach (KeyValuePair<Entity, int> check in m_GeometryChecks)
            {
                if (check.Value <= m_Frame)
                    ready.Add(check.Key);
            }
            if (ready.Count > 0) InvalidateGenerationCaches();
            foreach (Entity edge in ready)
            {
                m_GeometryChecks.Remove(edge);
                if (!IsRoadEdge(edge) || !EntityManager.HasComponent<Curve>(edge) ||
                    !EntityManager.HasComponent<Composition>(edge))
                    continue;
                int signature = GetEdgeSignature(edge);
                if (m_EdgeSignatures.TryGetValue(edge, out int previous) && previous == signature)
                    continue;
                m_EdgeSignatures[edge] = signature;
                if (setting.Enabled && (HasPlacementRules(edge) || m_ManagedEdges.Contains(edge)))
                    QueueEdge(edge);
            }
        }

        private bool IsRoadEdge(Entity edge)
        {
            if (!EntityManager.Exists(edge) ||
                EntityManager.HasComponent<Deleted>(edge) ||
                EntityManager.HasComponent<Temp>(edge) ||
                !EntityManager.HasComponent<PrefabRef>(edge) || !EntityManager.HasComponent<Edge>(edge) ||
                !EntityManager.HasComponent<Curve>(edge) || !EntityManager.HasComponent<Composition>(edge))
                return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(edge).m_Prefab;
            return EntityManager.Exists(prefab) && EntityManager.HasComponent<RoadData>(prefab);
        }

        private int GetEdgeSignature(Entity edge)
        {
            unchecked
            {
                int hash = 17;
                Curve curve = EntityManager.GetComponentData<Curve>(edge);
                Composition composition = EntityManager.GetComponentData<Composition>(edge);
                PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(edge);
                hash = Mix(hash, prefabRef.m_Prefab.Index);
                hash = Mix(hash, prefabRef.m_Prefab.Version);
                hash = Mix(hash, composition.m_Edge.Index);
                hash = Mix(hash, composition.m_StartNode.Index);
                hash = Mix(hash, composition.m_EndNode.Index);
                hash = MixSegmentRule(hash, edge);
                hash = MixPlantComposition(hash, composition.m_Edge);
                hash = MixPlantComposition(hash, composition.m_StartNode);
                hash = MixPlantComposition(hash, composition.m_EndNode);
                Setting laneSetting = Mod.ActiveSetting;
                if (laneSetting != null && HasAnyLaneSeparation(laneSetting))
                {
                    hash = MixLaneComposition(hash, composition.m_Edge);
                    Edge laneEnds = EntityManager.GetComponentData<Edge>(edge);
                    hash = MixJunctionLaneSignature(hash, laneEnds.m_Start);
                    hash = MixJunctionLaneSignature(hash, laneEnds.m_End);
                }
                hash = MixVector(hash, curve.m_Bezier.a);
                hash = MixVector(hash, curve.m_Bezier.b);
                hash = MixVector(hash, curve.m_Bezier.c);
                hash = MixVector(hash, curve.m_Bezier.d);
                if (EntityManager.HasComponent<EdgeGeometry>(edge))
                {
                    EdgeGeometry geometry = EntityManager.GetComponentData<EdgeGeometry>(edge);
                    hash = MixSegment(hash, geometry.m_Start);
                    hash = MixSegment(hash, geometry.m_End);
                }
                if (EntityManager.HasComponent<StartNodeGeometry>(edge))
                {
                    EdgeNodeGeometry nodeGeometry = EntityManager.GetComponentData<StartNodeGeometry>(edge).m_Geometry;
                    hash = MixNodeGeometry(hash, nodeGeometry);
                    hash = MixRoundaboutReference(hash, EntityManager.GetComponentData<Edge>(edge).m_Start, nodeGeometry);
                    hash = MixJunctionSurfacePriority(hash, EntityManager.GetComponentData<Edge>(edge).m_Start);
                    if (nodeGeometry.m_MiddleRadius <= 0.01f)
                        hash = MixOrdinaryJunctionReference(hash, EntityManager.GetComponentData<Edge>(edge).m_Start);
                }
                if (EntityManager.HasComponent<EndNodeGeometry>(edge))
                {
                    EdgeNodeGeometry nodeGeometry = EntityManager.GetComponentData<EndNodeGeometry>(edge).m_Geometry;
                    hash = MixNodeGeometry(hash, nodeGeometry);
                    hash = MixRoundaboutReference(hash, EntityManager.GetComponentData<Edge>(edge).m_End, nodeGeometry);
                    hash = MixJunctionSurfacePriority(hash, EntityManager.GetComponentData<Edge>(edge).m_End);
                    if (nodeGeometry.m_MiddleRadius <= 0.01f)
                        hash = MixOrdinaryJunctionReference(hash, EntityManager.GetComponentData<Edge>(edge).m_End);
                }
                return hash;
            }
        }

        private static int MixVector(int hash, float3 value)
        {
            hash = Mix(hash, (int)(value.x * 20f));
            hash = Mix(hash, (int)(value.y * 20f));
            hash = Mix(hash, (int)(value.z * 20f));
            return hash;
        }

        private static int Mix(int hash, int value)
        {
            unchecked { return (hash * 31) + value; }
        }

        private static int MixBezier(int hash, Bezier4x3 curve)
        {
            hash = MixVector(hash, curve.a);
            hash = MixVector(hash, curve.b);
            hash = MixVector(hash, curve.c);
            return MixVector(hash, curve.d);
        }

        private static int MixSegment(int hash, Segment segment)
        {
            return MixBezier(MixBezier(hash, segment.m_Left), segment.m_Right);
        }

        private static int MixNodeGeometry(int hash, EdgeNodeGeometry geometry)
        {
            hash = MixSegment(MixSegment(hash, geometry.m_Left), geometry.m_Right);
            hash = MixBezier(hash, geometry.m_Middle);
            for (int i = 0; i < 4; i++)
            {
                hash = Mix(hash, (int)(geometry.m_SyncVertexTargetsLeft[i] * 10000f));
                hash = Mix(hash, (int)(geometry.m_SyncVertexTargetsRight[i] * 10000f));
            }
            return Mix(hash, (int)(geometry.m_MiddleRadius * 100f));
        }

        private void QueueEdge(Entity edge)
        {
            QueueEdgeCore(edge);
            if (IsRoadEdge(edge) && HasPlacementRules(edge)) QueueAutomaticJunctionApproaches(edge);
        }

        private void QueueEdgeCore(Entity edge)
        {
            InvalidateGenerationCaches();
            // Do not rebuild the same road while the game is still materializing its
            // previous area definitions. Keep an explicit retry only when another event
            // (a changed edge or a settings refresh) requests one during that interval.
            if (HasPendingFor(edge))
            {
                m_DeferredEdges.Add(edge);
                return;
            }

            if (m_QueuedEdges.Add(edge))
            {
                m_EdgeQueue.Add(edge);
                // ToolSystem has just marked edited roads Updated. Their final edge
                // and junction geometry is produced later in Modification4 this frame.
                m_EdgeReadyFrames[edge] = m_Frame + 2;
                m_RefreshInProgress = true;
                m_RefreshCompletedFrame = -1;
            }
            else m_EdgeReadyFrames[edge] = m_Frame + 2;
        }

        private void PumpQueue(Setting setting)
        {
            if (!setting.Enabled)
                return;

            if (m_GenerationCachesDirty)
            {
                ClearGenerationCaches();
                m_GenerationCachesDirty = false;
            }
            int processed = 0;
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            while (processed < QueueBudgetPerUpdate && m_QueueCursor < m_EdgeQueue.Count && m_PendingAreas.Count < PendingAreaBudget)
            {
                // A single junction is indivisible; yield before starting another
                // road once this frame's generation allowance is used.
                if (processed > 0 && GenerationMillisecondsSince(startedAt) >= QueueTimeBudgetMilliseconds) break;
                Entity edge = m_EdgeQueue[m_QueueCursor];
                if (m_EdgeReadyFrames.TryGetValue(edge, out int readyFrame) && m_Frame < readyFrame)
                    break;
                m_QueueCursor++;
                m_EdgeReadyFrames.Remove(edge);
                m_QueuedEdges.Remove(edge);
                m_ManualEdges.Remove(edge);
                if (EntityManager.Exists(edge))
                {
                    long roadStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    RebuildRoadEdge(edge, setting);
                    RecordSurfaceGenerationCost(GenerationMillisecondsSince(roadStartedAt));
                }
                else if (m_GeneratedByOwner.ContainsKey(edge)) DestroyGeneratedFor(edge);
                processed++;
            }
            if (m_QueueCursor >= m_EdgeQueue.Count)
            {
                m_EdgeQueue.Clear();
                m_QueueCursor = 0;
            }
            if (m_RefreshInProgress && m_EdgeQueue.Count == 0 && m_PendingAreas.Count == 0)
            {
                m_RefreshInProgress = false;
                m_RefreshCompletedFrame = m_Frame;
                LogSurfaceGenerationCosts();
                ClearGenerationCaches();
                if (RuntimeState.RestoringAllRoads)
                {
                    RuntimeState.RestoringAllRoads = false;
                    RuntimeState.UpgradeMessage = RuntimeState.CityRulesMessage = "All existing roads restored to vanilla. Saved rules and presets are available to use again.";
                }
            }
        }

        private void RebuildRoadEdge(Entity edge, Setting setting)
        {
            if (HasPendingFor(edge))
                return;

            // A full vanilla restore needs deletion only, even when a road's
            // native geometry is still initializing. Use the existing frame
            // budget without rebuilding empty pavement/junction meshes.
            if (RuntimeState.RestoringAllRoads && setting.RuleCount == 0 &&
                m_SegmentRuleRecords.Count == 0 && m_SavedJunctionRecords.Count == 0)
            {
                DestroyGeneratedFor(edge);
                return;
            }

            if (!IsRoadEdge(edge))
            {
                DestroyGeneratedFor(edge);
                return;
            }

            PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(edge);
            Entity roadPrefab = prefabRef.m_Prefab;
            Curve curve = EntityManager.GetComponentData<Curve>(edge);
            Composition composition = EntityManager.GetComponentData<Composition>(edge);
            if (!PassesSegmentFilter(edge, roadPrefab, setting))
            {
                DestroyGeneratedFor(edge);
                return;
            }
            bool edgeAllowed = PassesRoadFilter(edge, roadPrefab, setting);
            if (!edgeAllowed && (!setting.IncludeStreets || !HasStreetSurfaceJunction(edge)))
            {
                DestroyGeneratedFor(edge);
                return;
            }
            if (edgeAllowed && UsesCityRules(edge) && HasMissingSelectedSurfaces(setting, roadPrefab))
                return;
            if (!TryReadSurfaceLayout(composition.m_Edge, out SurfaceLayout layout, highway: IsHighway(edge), roadPrefab: roadPrefab) ||
                !EntityManager.HasComponent<EdgeGeometry>(edge))
                return;
            ApplySegmentLaneSettings(layout, edge);
            if (!PassesElevationFilter(edge, roadPrefab, setting))
            {
                DestroyGeneratedFor(edge);
                return;
            }

            BeginSurfacePlan(edge);
            try
            {
                string roadName = m_PrefabSystem.GetPrefabName(roadPrefab);
                List<SurfaceChoice> roadChoices = edgeAllowed ? GetChoices(setting, SurfaceTarget.Roads, roadPrefab, segment: edge) : new List<SurfaceChoice>();
                List<SurfaceChoice> sidewalkChoices = edgeAllowed ? GetChoices(setting, SurfaceTarget.Sidewalks, roadPrefab, segment: edge) : new List<SurfaceChoice>();
                EdgeGeometry geometry = EntityManager.GetComponentData<EdgeGeometry>(edge);
                if (!m_LoggedBridgeHeight && EntityManager.HasComponent<Elevation>(edge) &&
                    math.cmax(EntityManager.GetComponentData<Elevation>(edge).m_Elevation) > 0.75f)
                {
                    Mod.Log.Info("Elevated surface placement: native boundary y=" + geometry.m_Start.m_Left.a.y +
                        "; curve datum y=" + curve.m_Bezier.a.y + "; side-mesh height offsets=" + layout.Composition.m_EdgeHeights +
                        "; pavement lane height=" + (layout.Roads.Count > 0 ? layout.Roads[0].LowHeight : 0f) + ".");
                    m_LoggedBridgeHeight = true;
                }
                if (!m_LoggedBoundaryGeometry)
                {
                    Mod.Log.Info("Using native edge and junction boundaries. Composition width=" + layout.Composition.m_Width +
                        "; pavement bands=" + layout.Roads.Count + "; sidewalk bands=" + layout.Sidewalks.Count + ".");
                    m_LoggedBoundaryGeometry = true;
                }
                if (edgeAllowed)
                    AddEdgeSurfaces(edge, geometry, layout, curve, roadName, roadChoices, sidewalkChoices, setting);
                Edge endpoints = EntityManager.GetComponentData<Edge>(edge);
                if (EntityManager.HasComponent<StartNodeGeometry>(edge))
                    AddJunctionSurfaces(edge, endpoints.m_Start, composition.m_StartNode,
                        EntityManager.GetComponentData<StartNodeGeometry>(edge).m_Geometry,
                        curve, roadName, roadChoices, sidewalkChoices, setting, true, layout);
                if (EntityManager.HasComponent<EndNodeGeometry>(edge))
                    AddJunctionSurfaces(edge, endpoints.m_End, composition.m_EndNode,
                        EntityManager.GetComponentData<EndNodeGeometry>(edge).m_Geometry,
                        curve, roadName, roadChoices, sidewalkChoices, setting, false, layout);

                m_ManagedEdges.Add(edge);
                m_EdgeSignatures[edge] = GetEdgeSignature(edge);
                CommitSurfacePlan(edge);
            }
            finally { EndSurfacePlan(); }
        }

        private bool HasPendingFor(Entity owner)
        {
            int count;
            return m_PendingAreaCounts.TryGetValue(owner, out count) && count > 0;
        }

        private bool PassesElevationFilter(Entity edge, Entity roadPrefab, Setting setting)
        {
            if (!EntityManager.HasComponent<Elevation>(edge) || !EntityManager.HasComponent<NetGeometryData>(roadPrefab))
                return true;

            Elevation elevation = EntityManager.GetComponentData<Elevation>(edge);
            NetGeometryData geometry = EntityManager.GetComponentData<NetGeometryData>(roadPrefab);
            float averageElevation = (elevation.m_Elevation.x + elevation.m_Elevation.y) * 0.5f;
            bool tunnel = averageElevation < -0.75f && (geometry.m_Flags & Game.Net.GeometryFlags.LoweredIsTunnel) != 0;
            bool elevated = (geometry.m_Flags & Game.Net.GeometryFlags.RequireElevated) != 0 ||
                (math.cmax(elevation.m_Elevation) > 0.75f &&
                (geometry.m_Flags & (Game.Net.GeometryFlags.RaisedIsElevated | Game.Net.GeometryFlags.ElevatedIsRaised)) != 0);
            if (tunnel && !setting.IncludeTunnels)
                return false;
            if (elevated && !setting.IncludeElevatedRoads)
                return false;
            return true;
        }

        private List<SurfaceChoice> GetChoices(Setting setting, SurfaceTarget target, Entity roadPrefab, bool streetContext = false, Entity segment = default)
        {
            List<SurfaceChoice> choices = new List<SurfaceChoice>();
            if (segment == Entity.Null) return choices;
            int highestPriority = -1;
            bool originalAtHighestPriority = false;
            bool lane = target == SurfaceTarget.BikeLanes || target == SurfaceTarget.BusLanes;
            int lanePriority = -1;
            bool legacyFallback = false;
            if (lane)
            {
                lanePriority = GetRuleLaneSettings(setting, roadPrefab, streetContext, out bool bicycle, out bool bus, out legacyFallback);
                if (segment != Entity.Null && !UsesCityRules(segment)) bicycle = bus = false;
                if (segment != Entity.Null && TryGetSegmentRule(segment, out _, out SavedSegmentSurfaceRule local) && local.Enabled)
                {
                    if (local.BikeMode != 0) bicycle = local.BikeMode == 2;
                    if (local.BusMode != 0) bus = local.BusMode == 2;
                }
                if (!(target == SurfaceTarget.BikeLanes ? bicycle : bus)) return choices;
            }
            if (segment != Entity.Null && TryGetSegmentChoices(segment, target, out List<SurfaceChoice> localChoices)) return localChoices;
            if (segment != Entity.Null && !UsesCityRules(segment)) return choices;
            for (int slot = 0; slot < setting.RuleCount; slot++)
            {
                if (!setting.IsRuleEnabled(slot))
                    continue;
                SurfaceRule rule = setting.GetRule(slot);
                if (!rule.TargetActive(target)) continue;
                SurfaceAssignment assignment = rule.Assignments?.Find(item => item.Target == target);
                if (assignment == null) continue;
                string name = assignment.Surface;
                if (!RuleMatchesRoadContext(setting, slot, roadPrefab, streetContext)) continue;
                int priority = RulePriority(rule);
                if (lane && !legacyFallback && priority != lanePriority) continue;
                if (priority < highestPriority) continue;
                if (priority > highestPriority)
                {
                    choices.Clear(); highestPriority = priority; originalAtHighestPriority = false;
                }
                if (assignment.UseRoadPavement && CanUseRoadPavement(target))
                {
                    if (originalAtHighestPriority) continue;
                    SurfaceChoice road = GetRoadPavementChoice(setting, segment);
                    if (road != null) choices.Add(road);
                    else { choices.Clear(); originalAtHighestPriority = true; }
                    continue;
                }
                // Explicit Original finish is an assignment, not an absent
                // target. It blocks lower-priority finishes and wins ties so
                // overlapping rules cannot paint over a protected original.
                if (string.IsNullOrEmpty(name))
                {
                    choices.Clear(); originalAtHighestPriority = true; continue;
                }
                if (originalAtHighestPriority) continue;
                Entity prefab;
                if (RuntimeState.TryGetSurface(name, out prefab) && EntityManager.Exists(prefab))
                {
                    LogSurfaceRenderer(prefab, name);
                    Entity overlay = GetNetworkOverlayPrefab(prefab);
                    if (overlay != Entity.Null) choices.Add(new SurfaceChoice(overlay,
                        target == SurfaceTarget.BusLanes && rule.FillBusShoulders));
                }
            }
            return choices;
        }

        private void LogSurfaceRenderer(Entity prefab, string name)
        {
            if (!m_LoggedSurfaceRenderers.Add(prefab))
                return;

            SurfacePrefab surface;
            RenderedArea renderer;
            if (!m_PrefabSystem.TryGetPrefab(prefab, out surface) || !surface.TryGet(out renderer))
            {
                Mod.Log.Warn("Selected surface '" + name + "' has no RenderedArea component; its material may not draw.");
                return;
            }

            bool receivesRoads = (renderer.m_DecalLayerMask & DecalLayers.Roads) != 0;
            bool hidden = EntityManager.HasComponent<AreaGeometryData>(prefab) &&
                (EntityManager.GetComponentData<AreaGeometryData>(prefab).m_Flags & Game.Areas.GeometryFlags.HiddenIngame) != 0;
            Mod.Log.Info("Selected surface '" + name + "': decal layers=" + renderer.m_DecalLayerMask +
                "; receives roads=" + receivesRoads + "; hidden in game=" + hidden + ".");
            if (!receivesRoads)
                Mod.Log.Warn("Selected surface '" + name + "' excludes the Roads decal layer; native surfaces using it cannot texture road meshes.");
            if (hidden)
                Mod.Log.Warn("Selected surface '" + name + "' is marked HiddenIngame by its prefab.");
        }

        private static SurfaceChoice Choose(List<SurfaceChoice> choices, string roadName, Curve curve, int band)
        {
            if (choices.Count == 0)
                return null;
            unchecked
            {
                int hash = unchecked((int)2166136261u);
                string seed = (roadName ?? string.Empty) + ":" + band + ":" +
                    ((int)(curve.m_Bezier.a.x * 0.25f)) + ":" + ((int)(curve.m_Bezier.a.z * 0.25f)) + ":" +
                    ((int)(curve.m_Bezier.d.x * 0.25f)) + ":" + ((int)(curve.m_Bezier.d.z * 0.25f));
                for (int i = 0; i < seed.Length; i++)
                {
                    hash ^= char.ToLowerInvariant(seed[i]);
                    hash *= 16777619;
                }
                int index = (int)((uint)hash % (uint)choices.Count);
                return choices[index];
            }
        }


        private void QueueArea(Entity owner, Entity surfacePrefab, SurfacePatch patch)
        {
            if (!EntityManager.Exists(surfacePrefab) || !EntityManager.HasComponent<AreaData>(surfacePrefab)) return;
            if (m_PlanningSurfaceOwner == owner)
            {
                m_PlannedSurfaceAreas.Add(new PlannedSurfaceArea(surfacePrefab, patch));
                return;
            }
            List<AreaNode> polygon = patch.Nodes;
            if (!EntityManager.Exists(surfacePrefab) || !EntityManager.HasComponent<AreaData>(surfacePrefab))
                return;

            // GenerateAreasSystem materializes the closed area, then the subsequent
            // GeometrySystem pass triangulates it. Permanent avoids tool-preview Temp
            // entities, which the tool system can hide or clear.
            Entity definitionEntity = EntityManager.CreateEntity();
            CreationDefinition definition = new CreationDefinition();
            definition.m_Prefab = surfacePrefab;
            definition.m_Owner = owner;
            definition.m_Flags = CreationFlags.Permanent;
            EntityManager.AddComponentData(definitionEntity, definition);
            EntityManager.AddComponentData(definitionEntity, new Updated());

            DynamicBuffer<AreaNode> nodes = EntityManager.AddBuffer<AreaNode>(definitionEntity);
            for (int i = 0; i < polygon.Count; i++)
                nodes.Add(polygon[i]);

            m_PendingAreas.Add(new PendingArea(owner, surfacePrefab, definitionEntity, Average(polygon), m_Frame, patch));
            m_NewSurfaceAreas++;
            int pendingCount;
            m_PendingAreaCounts.TryGetValue(owner, out pendingCount);
            m_PendingAreaCounts[owner] = pendingCount + 1;
            m_ManagedEdges.Add(owner);

            if (!m_LoggedAreaDefinitionQueued)
            {
                m_LoggedAreaDefinitionQueued = true;
                Mod.Log.Info("Queued a surface definition for native area generation. Nodes: " + polygon.Count + ".");
            }
        }

        private static float3 Average(List<AreaNode> nodes)
        {
            int count = nodes.Count;
            if (count > 1 && math.all(nodes[0].m_Position == nodes[count - 1].m_Position))
                count--;
            float3 total = new float3(0f, 0f, 0f);
            for (int i = 0; i < count; i++)
                total += nodes[i].m_Position;
            return total / math.max(1, count);
        }

        internal void PrepareSurfaceRendering()
        {
            if (!IsInLoadedGame() || Mod.ActiveSetting == null)
                return;
            bool projectionChanged = m_LastProjectionDepth != Mod.ActiveSetting.ProjectionDepth;
            m_LastProjectionDepth = Mod.ActiveSetting.ProjectionDepth;
            foreach (Entity prefab in m_LoggedSurfaceRenderers)
            {
                if (!EntityManager.Exists(prefab) || !EntityManager.HasComponent<RenderedAreaData>(prefab)) continue;
                float padding = EntityManager.GetComponentData<RenderedAreaData>(prefab).m_HeightOffset;
                if (!m_ProjectionPadding.TryGetValue(prefab, out float previous) || padding != previous)
                    projectionChanged = true;
                m_ProjectionPadding[prefab] = padding;
                float expansion = EntityManager.GetComponentData<RenderedAreaData>(prefab).m_ExpandAmount;
                if (!m_ProjectionExpansion.TryGetValue(prefab, out float previousExpansion) || expansion != previousExpansion)
                    projectionChanged = true;
                m_ProjectionExpansion[prefab] = expansion;
            }
            // Claim new native areas before the renderer sees them. In particular,
            // an area rejected by native ear clipping still needs our ribbon mesh.
            if (m_PendingAreas.Count > 0)
            {
                using (NativeArray<Entity> areas = m_UnclaimedSurfaceAreas.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity area in areas)
                    {
                        if (EntityManager.HasBuffer<SurfaceMeshTriangle>(area) || m_KnownAreaEntities.Contains(area))
                            continue;
                        Entity owner = EntityManager.GetComponentData<Owner>(area).m_Owner;
                        if (!m_PendingAreaCounts.ContainsKey(owner)) continue;
                        Entity prefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
                        if (!TryGetAreaCenter(area, out float3 center)) continue;
                        int index = FindPending(owner, prefab, center, area);
                        if (index < 0) continue;
                        PendingArea pending = m_PendingAreas[index];
                        LogJunctionMaterialization(area, pending);
                        pending.MaterializedArea = area;
                        m_PendingAreas[index] = pending;
                        AttachPendingSurfaceMesh(area, pending);
                    }
                }
            }
            EntityQuery meshes = projectionChanged || m_InitialMeshPreparation ? m_SurfaceMeshes : m_DirtySurfaceMeshes;
            m_InitialMeshPreparation = false;
            using (NativeArray<Entity> areas = meshes.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity area in areas)
                {
                    if (projectionChanged || !m_PreparedSurfaceMeshes.Contains(area) || EntityManager.HasComponent<Updated>(area) || EntityManager.HasComponent<Created>(area))
                    {
                        RestoreSurfaceMesh(area, Mod.ActiveSetting.ProjectionDepth);
                        m_PreparedSurfaceMeshes.Add(area);
                    }
                }
            }
        }

        private void RestoreSurfaceMesh(Entity area, float depth)
        {
            if (!EntityManager.HasBuffer<AreaNode>(area) || !EntityManager.HasBuffer<Triangle>(area)) return;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(area).m_Prefab;
            if (EntityManager.HasComponent<SmallRoundaboutSurface>(area))
                GuardSmallRoundaboutMesh(area, prefab);
            if (EntityManager.HasComponent<Deleted>(area)) return;
            if (EntityManager.HasComponent<ExactSurfaceBoundary>(area))
                RestoreExactSurfaceBoundary(area, prefab);
            DynamicBuffer<AreaNode> nodes = EntityManager.GetBuffer<AreaNode>(area, true);
            DynamicBuffer<SurfaceMeshTriangle> source = EntityManager.GetBuffer<SurfaceMeshTriangle>(area, true);
            DynamicBuffer<Triangle> triangles = EntityManager.GetBuffer<Triangle>(area);
            if (nodes.Length < 3 || source.Length == 0) return;
            for (int i = 0; i < source.Length; i++)
                if (math.any(source[i].Indices < 0) || math.any(source[i].Indices >= nodes.Length)) return;
            float offset = EntityManager.HasComponent<RenderedAreaData>(prefab)
                ? EntityManager.GetComponentData<RenderedAreaData>(prefab).m_HeightOffset : 0f;
            depth = math.clamp(depth, 0.05f, 2f);
            float minimum = -depth, maximum = depth;
            if (EntityManager.HasComponent<SurfaceProjectionLimits>(area))
            {
                SurfaceProjectionLimits limits = EntityManager.GetComponentData<SurfaceProjectionLimits>(area);
                if (math.isfinite(limits.Minimum)) minimum = math.max(minimum, limits.Minimum);
                if (math.isfinite(limits.Maximum)) maximum = math.min(maximum, limits.Maximum);
            }
            // AddTriangles subtracts/adds the prefab's projection padding after
            // reading this range. Cancel that padding per area, leaving a thin
            // volume around the actual deck, with no projection down to terrain.
            Bounds1 range = new Bounds1(offset + minimum, maximum - offset);
            bool resized = triangles.Length != source.Length;
            bool changed = resized;
            if (resized) triangles.ResizeUninitialized(source.Length);
            for (int i = 0; i < source.Length; i++)
            {
                int3 indices = source[i].Indices;
                Triangle value = new Triangle { m_Indices = indices, m_HeightRange = range, m_MinLod = 0 };
                Triangle old = resized ? default : triangles[i];
                changed |= math.any(old.m_Indices != indices) || old.m_HeightRange.min != range.min || old.m_HeightRange.max != range.max;
                triangles[i] = value;
            }
            Game.Areas.Area data = EntityManager.GetComponentData<Game.Areas.Area>(area);
            AreaFlags flags = (data.m_Flags | AreaFlags.Complete | AreaFlags.CounterClockwise) & ~AreaFlags.NoTriangles;
            changed |= data.m_Flags != flags;
            data.m_Flags = flags;
            EntityManager.SetComponentData(area, data);
            if (EntityManager.HasComponent<Game.Areas.Geometry>(area))
            {
                Game.Areas.Geometry geometry = EntityManager.GetComponentData<Game.Areas.Geometry>(area);
                float3 min = nodes[0].m_Position, max = min;
                for (int i = 1; i < nodes.Length; i++) { min = math.min(min, nodes[i].m_Position); max = math.max(max, nodes[i].m_Position); }
                min.y += minimum; max.y += maximum;
                geometry.m_Bounds = new Bounds3(min, max);
                geometry.m_CenterPosition = (min + max) * 0.5f;
                geometry.m_SurfaceArea = 0f;
                for (int i = 0; i < triangles.Length; i++)
                {
                    int3 index = triangles[i].m_Indices;
                    float2 a = nodes[index.y].m_Position.xz - nodes[index.x].m_Position.xz;
                    float2 b = nodes[index.z].m_Position.xz - nodes[index.x].m_Position.xz;
                    geometry.m_SurfaceArea += math.abs(a.x * b.y - a.y * b.x) * 0.5f;
                }
                EntityManager.SetComponentData(area, geometry);
            }
            if (changed && !EntityManager.HasComponent<Updated>(area)) EntityManager.AddComponent<Updated>(area);
            if (!m_LoggedProjection)
            {
                Mod.Log.Info("Using explicit ribbon triangles and deck-limited decal projection. Half-depth=" + depth +
                    " m; canceled native padding=" + offset + " m; triangles=" + source.Length + ".");
                m_LoggedProjection = true;
            }
        }

        private void CompletePendingAreas()
        {
            CompleteMaterializedSurfaceAreas();
            if (m_Frame % AreaScanInterval != 0 || !m_AreaIndexInitialized || m_PendingAreas.Count == 0)
            {
                FlushDeferredEdges();
                return;
            }

            using (NativeArray<Entity> areas = m_AreaEntities.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < areas.Length; i++)
                {
                    Entity area = areas[i];
                    if (m_KnownAreaEntities.Contains(area) || EntityManager.HasComponent<GeneratedSurface>(area))
                        continue;

                    Owner owner = EntityManager.GetComponentData<Owner>(area);
                    PrefabRef prefab = EntityManager.GetComponentData<PrefabRef>(area);
                    if (!EntityManager.Exists(prefab.m_Prefab) ||
                        !EntityManager.HasComponent<SurfaceData>(prefab.m_Prefab))
                        continue;
                    float3 center;
                    if (!TryGetAreaCenter(area, out center))
                        continue;

                    int pendingIndex = FindPending(owner.m_Owner, prefab.m_Prefab, center, area);
                    if (pendingIndex < 0)
                        continue;

                    PendingArea pending = m_PendingAreas[pendingIndex];
                    pending.MaterializedArea = area;
                    m_PendingAreas[pendingIndex] = pending;

                    // A generated entity can appear before its later geometry pass.
                    // Leave it eligible for another scan until it has real triangles.
                    int triangleCount = EntityManager.HasBuffer<Triangle>(area)
                        ? EntityManager.GetBuffer<Triangle>(area, true).Length
                        : 0;
                    if (triangleCount == 0 ||
                        (EntityManager.GetComponentData<Game.Areas.Area>(area).m_Flags & AreaFlags.Complete) == 0)
                        continue;

                    FinishPendingSurfaceArea(pendingIndex, area);
                }
            }

            for (int i = m_PendingAreas.Count - 1; i >= 0; i--)
            {
                PendingArea pending = m_PendingAreas[i];
                bool sourceMissing = !EntityManager.Exists(pending.Owner) || !EntityManager.Exists(pending.Prefab) ||
                    EntityManager.HasComponent<Deleted>(pending.Owner);
                if (!sourceMissing && m_Frame - pending.CreatedFrame <= PendingTimeout)
                    continue;
                bool definitionStillExists = EntityManager.Exists(pending.Definition);
                bool ownerDeleted = EntityManager.Exists(pending.Owner) && EntityManager.HasComponent<Deleted>(pending.Owner);
                bool areaMaterialized = EntityManager.Exists(pending.MaterializedArea);
                int triangleCount = areaMaterialized && EntityManager.HasBuffer<Triangle>(pending.MaterializedArea)
                    ? EntityManager.GetBuffer<Triangle>(pending.MaterializedArea, true).Length
                    : 0;
                int areaCount = m_AreaEntities.CalculateEntityCount();
                if (EntityManager.Exists(pending.Definition))
                    EntityManager.DestroyEntity(pending.Definition);
                RetireOwnedSurfaceArea(pending.Owner, pending.MaterializedArea);
                RemovePendingArea(i);
                if (!sourceMissing)
                {
                    m_FailedAreaCount++;
                    if (m_FailedAreaCount == 1 || m_FailedAreaCount % 100 == 0)
                        Mod.Log.Warn("A generated surface area timed out before appearing. Total timed out: " +
                            m_FailedAreaCount + "; definition still exists=" + definitionStillExists +
                            "; owner is deleted=" + ownerDeleted + "; area materialized=" + areaMaterialized +
                            "; triangles=" + triangleCount + "; current area entities=" + areaCount + ".");
                }
            }

            FlushDeferredEdges();
        }

        private void RemovePendingArea(int index)
        {
            Entity owner = m_PendingAreas[index].Owner;
            m_PendingAreas.RemoveAt(index);
            int pendingCount;
            if (!m_PendingAreaCounts.TryGetValue(owner, out pendingCount) || pendingCount <= 1)
                m_PendingAreaCounts.Remove(owner);
            else
                m_PendingAreaCounts[owner] = pendingCount - 1;
        }

        private void FlushDeferredEdges()
        {
            if (m_DeferredEdges.Count == 0)
                return;

            List<Entity> ready = new List<Entity>();
            foreach (Entity edge in m_DeferredEdges)
            {
                if (!EntityManager.Exists(edge) || !IsRoadEdge(edge))
                    ready.Add(edge);
                else if (!HasPendingFor(edge))
                    ready.Add(edge);
            }

            for (int i = 0; i < ready.Count; i++)
            {
                Entity edge = ready[i];
                m_DeferredEdges.Remove(edge);
                if (EntityManager.Exists(edge) && IsRoadEdge(edge))
                    // Neighbors were queued with the original request. A
                    // pending owner's retry must not propagate more retries.
                    QueueEdgeCore(edge);
            }
        }

        private int FindPending(Entity owner, Entity prefab, float3 center, Entity materializedArea)
        {
            if (!m_PendingAreaCounts.ContainsKey(owner)) return -1;
            int bestIndex = -1;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < m_PendingAreas.Count; i++)
            {
                PendingArea pending = m_PendingAreas[i];
                if (pending.Owner != owner || pending.Prefab != prefab)
                    continue;
                if (pending.MaterializedArea == materializedArea)
                    return i;
                if (pending.MaterializedArea != Entity.Null && EntityManager.Exists(pending.MaterializedArea))
                    continue;
                // A wide shoulder patch and its narrower replacement often
                // have the same center. Match the actual native polygon, not
                // a nearby area that may belong to a canceled generation.
                if (!PendingBoundaryMatches(pending.Patch, materializedArea)) continue;
                float3 delta = pending.Center - center;
                delta.y = 0f;
                float distance = math.lengthsq(delta);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }
            return bestIndex;
        }

        private bool TryGetAreaCenter(Entity area, out float3 center)
        {
            center = new float3(0f, 0f, 0f);
            if (!EntityManager.HasBuffer<AreaNode>(area))
                return false;
            DynamicBuffer<AreaNode> nodes = EntityManager.GetBuffer<AreaNode>(area, true);
            if (nodes.Length == 0)
                return false;
            for (int i = 0; i < nodes.Length; i++)
                center += nodes[i].m_Position;
            center /= nodes.Length;
            return true;
        }

        private void DestroyGeneratedFor(Entity owner)
        {
            DeleteUnindexedRoadSurfaces(owner);
            List<Entity> areas;
            if (!m_GeneratedByOwner.TryGetValue(owner, out areas))
                return;

            for (int i = areas.Count - 1; i >= 0; i--)
            {
                Entity area = areas[i];
                RetireOwnedSurfaceArea(owner, area);
            }
            m_GeneratedCount = math.max(0, m_GeneratedCount - areas.Count);
            m_GeneratedByOwner.Remove(owner);
            m_ManagedEdges.Remove(owner);
        }

        private void MarkAreaDeleted(Entity area)
        {
            m_BoundaryOffsets.Remove(area);
            m_CommittedSurfacePatches.Remove(area);
            m_PreparedSurfaceMeshes.Remove(area);
            if (!EntityManager.Exists(area))
                return;
            m_RetiredSurfaceAreas.Add(area);
            // GenerateAreasSystem may recycle a soft-deleted native area.
            // It must be claimed with the new request's geometry, rather than
            // retaining our old wide-shoulder mesh and completion marker.
            if (EntityManager.HasComponent<GeneratedSurface>(area)) EntityManager.RemoveComponent<GeneratedSurface>(area);
            if (EntityManager.HasBuffer<SurfaceMeshTriangle>(area)) EntityManager.RemoveComponent<SurfaceMeshTriangle>(area);
            if (EntityManager.HasComponent<ExactSurfaceBoundary>(area)) EntityManager.RemoveComponent<ExactSurfaceBoundary>(area);
            if (EntityManager.HasComponent<SmallRoundaboutSurface>(area)) EntityManager.RemoveComponent<SmallRoundaboutSurface>(area);
            if (EntityManager.HasComponent<SurfaceProjectionLimits>(area)) EntityManager.RemoveComponent<SurfaceProjectionLimits>(area);
            // Native reference, search, and render systems must see deletion
            // before cleanup destroys the entity and frees its render batch.
            if (!EntityManager.HasComponent<Deleted>(area))
                EntityManager.AddComponent<Deleted>(area);
            if (!EntityManager.HasComponent<Updated>(area))
                EntityManager.AddComponent<Updated>(area);
        }

        private void AddGeneratedArea(Entity owner, Entity area)
        {
            m_RetiredSurfaceAreas.Remove(area);
            List<Entity> areas;
            if (!m_GeneratedByOwner.TryGetValue(owner, out areas))
            {
                areas = new List<Entity>();
                m_GeneratedByOwner.Add(owner, areas);
            }
            if (!areas.Contains(area))
            {
                areas.Add(area);
                m_GeneratedCount++;
            }
        }

        private void PruneDeletedOwners()
        {
            if (m_Frame % 60 != 0 || m_GeneratedByOwner.Count == 0)
                return;

            List<Entity> deadOwners = new List<Entity>();
            foreach (KeyValuePair<Entity, List<Entity>> entry in m_GeneratedByOwner)
            {
                if (!EntityManager.Exists(entry.Key) || EntityManager.HasComponent<Deleted>(entry.Key))
                    deadOwners.Add(entry.Key);
            }
            for (int i = 0; i < deadOwners.Count; i++)
                DestroyGeneratedFor(deadOwners[i]);
        }

        private void UpdateStatus(Setting setting)
        {
            if (RuntimeState.GetSurfaceCount() == 0)
            {
                RuntimeState.Status = "No road-compatible surfaces found. Enable a surface pack that supports roads, then refresh the surface list.";
                return;
            }

            int missing = CountMissingSelectedSurfaces(setting);
            if (missing > 0)
            {
                RuntimeState.Status = missing + " enabled surface selection(s) are unavailable. Check that their provider is enabled, then refresh the list.";
                return;
            }

            if (m_RefreshInProgress)
            {
                RuntimeState.Status = "Refreshing; " + math.max(0, m_EdgeQueue.Count - m_QueueCursor) +
                    " road segments queued; " + m_PendingAreas.Count + " areas pending.";
                return;
            }

            if (m_RefreshCompletedFrame >= 0 && m_Frame - m_RefreshCompletedFrame < 180)
            {
                RuntimeState.Status = "Refresh complete; " + m_GeneratedCount + " generated areas are managed.";
                return;
            }

            string state = setting.Enabled ? "Enabled" : "Disabled";
            string mode = "explicit city apply; " + RuntimeState.CityRuleMemberCount + " city roads";
            RuntimeState.Status = state + "; " + mode + "; " + RuntimeState.GetSurfaceCount() +
                " road-compatible surfaces available; " + math.max(0, m_EdgeQueue.Count - m_QueueCursor) + " road segments queued; " +
                m_GeneratedCount + " generated areas; " + m_PendingAreas.Count + " areas pending; " +
                m_FailedAreaCount + " timed out.";
        }

        private int CountMissingSelectedSurfaces(Setting setting)
        {
            int count = 0;
            for (int slot = 0; slot < setting.RuleCount; slot++)
            {
                SurfaceRule rule = setting.GetRule(slot);
                if (!rule.Enabled) continue;
                foreach (SurfaceAssignment item in rule.ActiveAssignments)
                    if (item.Surface.Length > 0 && (!RuntimeState.TryGetSurface(item.Surface, out Entity prefab) || !EntityManager.Exists(prefab)))
                        count++;
            }
            return count;
        }

        private bool HasMissingSelectedSurfaces(Setting setting, Entity roadPrefab)
        {
            for (int slot = 0; slot < setting.RuleCount; slot++)
            {
                SurfaceRule rule = setting.GetRule(slot);
                if (!rule.Enabled || !RuleMatchesRoadContext(setting, slot, roadPrefab, false)) continue;
                // Missing lane materials leave the native finish visible while
                // the road is rebuilt with its lane reservation intact.
                foreach (SurfaceAssignment item in rule.ActiveAssignments)
                {
                    if (item.Target == SurfaceTarget.BikeLanes || item.Target == SurfaceTarget.BusLanes || item.Surface.Length == 0) continue;
                    if (!RuntimeState.TryGetSurface(item.Surface, out Entity prefab) || !EntityManager.Exists(prefab)) return true;
                }
            }
            return false;
        }

        private struct LaneSpan
        {
            internal readonly float Low;
            internal readonly float High;
            internal readonly float Height;
            internal readonly bool Parking;

            internal LaneSpan(float low, float high, float height, bool parking = false)
            {
                Low = low;
                High = high;
                Height = height;
                Parking = parking;
            }
        }

        private struct RoadBand
        {
            internal readonly float Low;
            internal readonly float High;
            internal readonly float LowHeight;
            internal readonly float HighHeight;

            internal RoadBand(float low, float high, float lowHeight, float highHeight)
            {
                Low = low;
                High = high;
                LowHeight = lowHeight;
                HighHeight = highHeight;
            }
        }

        private sealed class SurfaceChoice
        {
            internal readonly Entity Prefab;
            internal readonly bool FillBusShoulders;

            internal SurfaceChoice(Entity prefab, bool fillBusShoulders = false)
            {
                Prefab = prefab; FillBusShoulders = fillBusShoulders;
            }
        }

        private struct PendingArea
        {
            internal readonly Entity Owner;
            internal readonly Entity Prefab;
            internal readonly Entity Definition;
            internal readonly float3 Center;
            internal readonly int CreatedFrame;
            internal Entity MaterializedArea;
            internal readonly SurfacePatch Patch;

            internal PendingArea(Entity owner, Entity prefab, Entity definition, float3 center, int createdFrame, SurfacePatch patch)
            {
                Owner = owner;
                Prefab = prefab;
                Definition = definition;
                Center = center;
                CreatedFrame = createdFrame;
                MaterializedArea = Entity.Null;
                Patch = patch;
            }
        }
    }
}
