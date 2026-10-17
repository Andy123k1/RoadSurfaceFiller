using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Settings;
using Unity.Entities;
using Unity.Mathematics;

namespace SurfaceNetworkFilter
{
    public enum JunctionEnd { Nearest, Other }

    // The native serializer remaps this entity reference when a city is loaded.
    // Material priority is independent of the physical road/curb reference.
    public struct JunctionSurfacePriority : IComponentData, ISerializable
    {
        public Entity Road;
        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter { writer.Write(Road); }
        public void Deserialize<TReader>(TReader reader) where TReader : IReader { reader.Read(out Road); }
    }

    internal enum JunctionAction { UseSelectedRoad, NextRoad, Automatic }

    internal sealed class JunctionEditorState
    {
        internal bool Available, CanCycle, CanUseSelected, HasOverride;
        internal JunctionEnd End;
        internal string Kind = "Junction", Source = "", Surface = "";
    }

    internal static partial class RuntimeState
    {
        internal static Entity SelectedJunction;
        internal static float3 LastRoadSelectionPosition;
        internal static string JunctionStatus = "Select a road near the junction you want to change.";
        internal static string JunctionMessage = "";
        internal static JunctionEditorState JunctionEditor = new JunctionEditorState();
        internal static readonly List<JunctionRequest> JunctionRequests = new List<JunctionRequest>();

        internal readonly struct JunctionRequest
        {
            internal readonly Entity Node, Road;
            internal readonly JunctionAction Action;
            internal JunctionRequest(Entity node, Entity road, JunctionAction action)
                { Node = node; Road = road; Action = action; }
        }

        internal static void RequestJunctionAction(JunctionAction action)
        {
            JunctionRequests.Add(new JunctionRequest(SelectedJunction, SelectedEdge, action));
        }
    }

    public sealed partial class Setting
    {
        [SettingsUIHidden]
        [SettingsUIMultilineText]
        public string JunctionHelp
        {
            get { return "Under Tools, activate Cycle junction surface or Restore automatic. Left-click junctions or roundabouts directly. Each click cycles the finish or clears its saved choice. The tool stays active until you right-click. Choices are saved with the city and connected segments update automatically."; }
            set { }
        }
        [SettingsUIHidden]
        public JunctionEnd JunctionEnd { get; set; }
        [SettingsUIHidden]
        [SettingsUIMultilineText]
        public string SelectedJunction { get { return RuntimeState.JunctionStatus; } set { } }
        [SettingsUIHidden]
        [SettingsUIButton]
        public bool UseSelectedRoadAtJunction { set { RuntimeState.RequestJunctionAction(JunctionAction.UseSelectedRoad); } }
        [SettingsUIHidden]
        [SettingsUIButton]
        public bool NextJunctionRoad { set { RuntimeState.RequestJunctionAction(JunctionAction.NextRoad); } }
        [SettingsUIHidden]
        [SettingsUIButton]
        public bool AutomaticJunctionRoad { set { RuntimeState.RequestJunctionAction(JunctionAction.Automatic); } }
    }

    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private Entity GetPreferredJunctionRoad(Entity node)
        {
            if (!EntityManager.Exists(node)) return Entity.Null;
            if (EntityManager.HasComponent<JunctionSurfacePriority>(node))
            {
                Entity road = EntityManager.GetComponentData<JunctionSurfacePriority>(node).Road;
                if (IsRoadEdge(road))
                {
                    Edge endpoints = EntityManager.GetComponentData<Edge>(road);
                    if (endpoints.m_Start == node || endpoints.m_End == node) return road;
                }
            }
            if (TryGetSavedJunctionSurface(node, out SavedJunctionSurface saved) && EntityManager.HasBuffer<ConnectedEdge>(node))
                foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                    if (IsRoadEdge(connection.m_Edge) &&
                        EntityManager.GetComponentData<PrefabRef>(connection.m_Edge).m_Prefab == saved.RoadPrefab)
                        return connection.m_Edge;
            return Entity.Null;
        }

        private Entity GetPreferredJunctionPrefab(Entity node)
        {
            if (TryGetSavedJunctionSurface(node, out SavedJunctionSurface saved)) return saved.RoadPrefab;
            Entity road = GetPreferredJunctionRoad(node);
            return road == Entity.Null ? Entity.Null : EntityManager.GetComponentData<PrefabRef>(road).m_Prefab;
        }

        private int MixJunctionSurfacePriority(int hash, Entity node)
        {
            if (TryGetSavedJunctionSurface(node, out SavedJunctionSurface saved))
                return Mix(Mix(Mix(Mix(Mix(hash, saved.RoadPrefab.Index), saved.RoadPrefab.Version),
                    saved.SurfacePrefab.Index), saved.SurfacePrefab.Version), JunctionCoversBusLanes(node) ? 1 : 0);
            Entity road = GetPreferredJunctionRoad(node);
            Entity prefab = GetPreferredJunctionPrefab(node);
            hash = Mix(Mix(Mix(Mix(hash, road.Index), road.Version), prefab.Index), prefab.Version);
            hash = MixAutomaticJunctionStyles(hash, node);
            return road == Entity.Null ? hash : MixBezier(hash, EntityManager.GetComponentData<Curve>(road).m_Bezier);
        }

        private bool IsEditableSurfaceJunction(Entity node)
        {
            if (!EntityManager.Exists(node) || EntityManager.HasComponent<Deleted>(node) ||
                EntityManager.HasComponent<Game.Tools.Temp>(node) ||
                !EntityManager.HasComponent<Game.Net.Node>(node) ||
                !EntityManager.HasBuffer<ConnectedEdge>(node)) return false;
            int count = 0;
            foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                if (IsRoadEdge(connection.m_Edge))
                {
                    count++;
                    Edge endpoints = EntityManager.GetComponentData<Edge>(connection.m_Edge);
                    if (endpoints.m_Start == node && EntityManager.HasComponent<StartNodeGeometry>(connection.m_Edge) &&
                        EntityManager.GetComponentData<StartNodeGeometry>(connection.m_Edge).m_Geometry.m_MiddleRadius > 0.01f) return true;
                    if (endpoints.m_End == node && EntityManager.HasComponent<EndNodeGeometry>(connection.m_Edge) &&
                        EntityManager.GetComponentData<EndNodeGeometry>(connection.m_Edge).m_Geometry.m_MiddleRadius > 0.01f) return true;
                }
            return count >= 2;
        }

        internal Entity FindJunctionCycleTarget(Entity hit, float3 position)
        {
            if (IsEditableSurfaceJunction(hit)) return hit;
            if (!IsRoadEdge(hit)) return Entity.Null;
            Edge ends = EntityManager.GetComponentData<Edge>(hit);
            Entity target = Entity.Null;
            float nearest = float.MaxValue;
            // At the mesh seam the native raycast can return an approach edge.
            // Accept only its actual junction geometry, never a remote end just
            // because it happens to be the nearest junction on a long road.
            for (int end = 0; end < 2; end++)
            {
                Entity node = end == 0 ? ends.m_Start : ends.m_End;
                if (!IsEditableSurfaceJunction(node)) continue;
                Colossal.Mathematics.Bounds3 bounds;
                if (end == 0 && EntityManager.HasComponent<StartNodeGeometry>(hit))
                    bounds = EntityManager.GetComponentData<StartNodeGeometry>(hit).m_Geometry.m_Bounds;
                else if (end == 1 && EntityManager.HasComponent<EndNodeGeometry>(hit))
                    bounds = EntityManager.GetComponentData<EndNodeGeometry>(hit).m_Geometry.m_Bounds;
                else if (EntityManager.HasComponent<NodeGeometry>(node))
                    bounds = EntityManager.GetComponentData<NodeGeometry>(node).m_Bounds;
                else continue;
                if (!math.all(position >= bounds.min - new float3(0.5f, 2f, 0.5f)) ||
                    !math.all(position <= bounds.max + new float3(0.5f, 2f, 0.5f))) continue;
                float distance = math.distancesq(position, EntityManager.GetComponentData<Game.Net.Node>(node).m_Position);
                if (distance < nearest) { target = node; nearest = distance; }
            }
            return target;
        }

        private bool IsRoundaboutJunction(Entity node)
        {
            foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
            {
                Entity road = connection.m_Edge;
                if (!IsRoadEdge(road)) continue;
                Edge endpoints = EntityManager.GetComponentData<Edge>(road);
                if (endpoints.m_Start == node && EntityManager.HasComponent<StartNodeGeometry>(road) &&
                    EntityManager.GetComponentData<StartNodeGeometry>(road).m_Geometry.m_MiddleRadius > 0.01f) return true;
                if (endpoints.m_End == node && EntityManager.HasComponent<EndNodeGeometry>(road) &&
                    EntityManager.GetComponentData<EndNodeGeometry>(road).m_Geometry.m_MiddleRadius > 0.01f) return true;
            }
            return false;
        }

        private List<Entity> GetJunctionRoadChoices(Entity node)
        {
            List<Entity> roads = new List<Entity>();
            HashSet<Entity> types = new HashSet<Entity>();
            foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                if (IsRoadEdge(connection.m_Edge) && types.Add(EntityManager.GetComponentData<PrefabRef>(connection.m_Edge).m_Prefab))
                    roads.Add(connection.m_Edge);
            roads.Sort((a, b) => string.Compare(JunctionRoadLabel(a), JunctionRoadLabel(b), StringComparison.OrdinalIgnoreCase));
            return roads;
        }

        private string JunctionRoadLabel(Entity road)
        {
            if (!IsRoadEdge(road)) return "Automatic";
            return m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(road).m_Prefab) +
                (IsHighway(road) ? " (highway)" : " (street)");
        }

        private void QueueJunctionRebuild(Entity node)
        {
            List<Entity> roads = new List<Entity>();
            foreach (ConnectedEdge connection in EntityManager.GetBuffer<ConnectedEdge>(node, true))
                if (IsRoadEdge(connection.m_Edge)) roads.Add(connection.m_Edge);
            foreach (Entity road in roads) InvalidateRoadSurfaceOwner(road);
        }

        private void UpdateJunctionSelection(Setting setting)
        {
            Entity selected = RuntimeState.SelectedEdge;
            Entity target = Entity.Null;
            if (RuntimeState.JunctionCycleActive)
            {
                if (IsEditableSurfaceJunction(RuntimeState.JunctionCycleTarget)) target = RuntimeState.JunctionCycleTarget;
            }
            else if (IsRoadEdge(selected))
            {
                Edge ends = EntityManager.GetComponentData<Edge>(selected);
                bool first = IsEditableSurfaceJunction(ends.m_Start), second = IsEditableSurfaceJunction(ends.m_End);
                bool nearestStart = first && (!second ||
                    math.distancesq(EntityManager.GetComponentData<Game.Net.Node>(ends.m_Start).m_Position, RuntimeState.LastRoadSelectionPosition) <=
                    math.distancesq(EntityManager.GetComponentData<Game.Net.Node>(ends.m_End).m_Position, RuntimeState.LastRoadSelectionPosition));
                target = setting.JunctionEnd == JunctionEnd.Other
                    ? (nearestStart ? (second ? ends.m_End : Entity.Null) : (first ? ends.m_Start : Entity.Null))
                    : (nearestStart ? ends.m_Start : (second ? ends.m_End : Entity.Null));
            }
            RuntimeState.SelectedJunction = target;
            while (RuntimeState.JunctionRequests.Count > 0)
            {
                RuntimeState.JunctionRequest request = RuntimeState.JunctionRequests[0];
                RuntimeState.JunctionRequests.RemoveAt(0);
                if (!IsEditableSurfaceJunction(request.Node))
                {
                    RuntimeState.JunctionMessage = "That junction is no longer available. Point at another junction.";
                    continue;
                }
                if (request.Action == JunctionAction.Automatic)
                {
                    if (!TryGetSavedJunctionSurface(request.Node, out _) && !EntityManager.HasComponent<JunctionSurfacePriority>(request.Node))
                    {
                        RuntimeState.JunctionMessage = "This junction already uses automatic pavement selection.";
                        continue;
                    }
                    ClearSavedJunctionSurface(request.Node);
                    EntityManager.RemoveComponent<JunctionSurfacePriority>(request.Node);
                    RuntimeState.JunctionMessage = "Automatic pavement priority restored.";
                }
                else
                {
                    Entity road = request.Road;
                    JunctionSurfaceOption option = null;
                    if (request.Action == JunctionAction.NextRoad)
                    {
                        List<JunctionSurfaceOption> choices = GetJunctionSurfaceOptions(request.Node, setting);
                        if (choices.Count == 0)
                            { RuntimeState.JunctionMessage = "None of the connected roads has an active pavement or bus-lane finish."; continue; }
                        Entity current = GetPreferredJunctionPrefab(request.Node);
                        if (current == Entity.Null) current = IsRoundaboutJunction(request.Node)
                            ? GetJunctionReferencePrefab(request.Node, true) : GetOrdinaryJunctionReferencePrefab(request.Node);
                        if (current == Entity.Null) current = GetJunctionReferencePrefab(request.Node, false);
                        SurfaceChoice savedChoice = GetSavedJunctionChoice(request.Node);
                        bool coversBus = JunctionCoversBusLanes(request.Node);
                        int index = savedChoice != null ? choices.FindIndex(candidate => candidate.Surface.Prefab == savedChoice.Prefab && candidate.CoverBusLanes == coversBus) :
                            choices.FindIndex(candidate => EntityManager.GetComponentData<PrefabRef>(candidate.Road).m_Prefab == current && candidate.Target == SurfaceTarget.Roads);
                        option = choices[(index + 1) % choices.Count]; road = option.Road;
                    }
                    if (!IsRoadEdge(road)) { RuntimeState.JunctionMessage = "Select a connected road first."; continue; }
                    Edge endpoints = EntityManager.GetComponentData<Edge>(road);
                    if (endpoints.m_Start != request.Node && endpoints.m_End != request.Node)
                        { RuntimeState.JunctionMessage = "That road is no longer connected to the selected junction."; continue; }
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(road).m_Prefab;
                    option = option ?? EffectiveRoadJunctionOption(road, setting);
                    if (option == null)
                        { RuntimeState.JunctionMessage = "Create an active pavement rule for " + JunctionRoadLabel(road) + " in the surface rule panel first."; continue; }
                    if (!SaveJunctionSurface(request.Node, road, setting, option.Surface, option.CoverBusLanes))
                        { RuntimeState.JunctionMessage = "The selected pavement surface is not ready. Try again after the surface catalog loads."; continue; }
                    JunctionSurfacePriority priority = new JunctionSurfacePriority { Road = road };
                    if (EntityManager.HasComponent<JunctionSurfacePriority>(request.Node)) EntityManager.SetComponentData(request.Node, priority);
                    else EntityManager.AddComponentData(request.Node, priority);
                    RuntimeState.JunctionMessage = "Junction finish saved: " + JunctionRoadLabel(road) +
                        " — " + JunctionOptionLabel(option) + ".";
                }
                QueueJunctionRebuild(request.Node);
            }
            if (target == Entity.Null)
            {
                RuntimeState.JunctionEditor = new JunctionEditorState { End = setting.JunctionEnd };
                RuntimeState.JunctionStatus = "No junction at this end of the current selection.\n" + RuntimeState.JunctionMessage;
                return;
            }
            List<string> connected = new List<string>();
            foreach (Entity road in GetJunctionRoadChoices(target)) connected.Add(JunctionRoadLabel(road));
            Entity preferred = GetPreferredJunctionRoad(target);
            bool savedPavement = TryGetSavedJunctionSurface(target, out SavedJunctionSurface savedSurface);
            bool roundabout = IsRoundaboutJunction(target);
            Entity automatic = roundabout ? GetJunctionReferencePrefab(target, true) : GetOrdinaryJunctionReferencePrefab(target);
            if (automatic == Entity.Null) automatic = GetJunctionReferencePrefab(target, false);
            string source = savedPavement ? "Saved — " + m_PrefabSystem.GetPrefabName(savedSurface.RoadPrefab) :
                preferred == Entity.Null ? "Automatic — " + m_PrefabSystem.GetPrefabName(automatic) : JunctionRoadLabel(preferred);
            if (savedPavement && HasJunctionBusPaths(target, setting))
            {
                SurfaceChoice chosen = GetSavedJunctionChoice(target);
                JunctionSurfaceOption selectedOption = chosen == null ? null : GetJunctionSurfaceOptions(target, setting).Find(option =>
                    option.Surface.Prefab == chosen.Prefab && option.CoverBusLanes == JunctionCoversBusLanes(target));
                if (selectedOption != null) source += " — " + JunctionOptionLabel(selectedOption);
            }
            if (!savedPavement && preferred != Entity.Null && GetChoices(setting, SurfaceTarget.Roads, EntityManager.GetComponentData<PrefabRef>(preferred).m_Prefab, segment: preferred).Count == 0)
                source += " (no active pavement rule; using automatic selection)";
            RuntimeState.JunctionStatus = (IsRoundaboutJunction(target) ? "Roundabout" : "Junction") +
                ": " + (setting.JunctionEnd == JunctionEnd.Other ? "other end" : "nearest end") +
                "\nPavement source: " + source + "\nConnected road types: " + string.Join(", ", connected) +
                "\n" + RuntimeState.JunctionMessage;

            bool canCycle = GetJunctionSurfaceOptions(target, setting).Count > 0;
            Entity preferredPrefab = GetPreferredJunctionPrefab(target);
            bool manual = savedPavement || (preferredPrefab != Entity.Null && GetChoices(setting, SurfaceTarget.Roads, preferredPrefab, segment: preferred).Count > 0);
            bool separate = !manual && !roundabout && GetJunctionPavementDomain(target) == null && !setting.MainRoadJunctionSurfaces && !IsHighwayPrefab(automatic);
            Entity materialPrefab = manual ? preferredPrefab : automatic;
            Curve seed = manual && preferred != Entity.Null ? EntityManager.GetComponentData<Curve>(preferred) : JunctionMaterialSeed(default, target);
            Entity materialEdge = preferred != Entity.Null ? preferred : FindJunctionRuleRoad(target, materialPrefab);
            SurfaceChoice surface = savedPavement ? GetSavedJunctionChoice(target) : separate || materialEdge == Entity.Null ? null : Choose(GetChoices(setting, SurfaceTarget.Roads, materialPrefab,
                streetContext: !manual && (roundabout ? TryGetConnectedStreetPrefab(target, out _) : !IsHighwayPrefab(materialPrefab)),
                segment: materialEdge),
                m_PrefabSystem.GetPrefabName(materialPrefab), seed, 0);
            if (surface == null && !manual && !separate)
            {
                JunctionSurfaceOption fallback = FindAutomaticJunctionSurface(target, setting, TryGetConnectedStreetPrefab(target, out _));
                if (fallback != null)
                {
                    surface = fallback.Surface;
                    source = "Automatic — " + JunctionRoadLabel(fallback.Road);
                }
            }
            string surfaceName = surface == null ? "" : m_PrefabSystem.GetPrefabName(surface.Prefab);
            if (IsNetworkOverlayPrefab(surfaceName)) surfaceName = surfaceName.Substring(NetworkOverlayPrefix.Length);
            RuntimeState.JunctionEditor = new JunctionEditorState {
                Available = true, End = setting.JunctionEnd,
                Kind = roundabout ? "Roundabout" : "Junction", Source = separate ? "Each connected road" : source,
                Surface = savedPavement && surface == null ? UnavailableJunctionSurfaceMessage(target) : surfaceName,
                CanCycle = canCycle, HasOverride = savedPavement || EntityManager.HasComponent<JunctionSurfacePriority>(target),
                CanUseSelected = !RuntimeState.JunctionCycleActive && IsRoadEdge(selected) && EffectiveRoadJunctionOption(selected, setting) != null
            };
        }
    }
}
