using System;
using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace SurfaceNetworkFilter
{
    // Register before prefab initialization, including while loading a save.
    // Stable variant names must exist when saved PrefabRefs are resolved.
    internal sealed partial class NetworkOverlayPrefabSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            World.GetOrCreateSystemManaged<SurfaceNetworkFilterSystem>().RegisterNetworkOverlayPrefabs();
        }
    }

    internal sealed partial class SurfaceNetworkFilterSystem
    {
        private const string NetworkOverlayPrefix = "SurfaceNetworkFilter.Overlay.";
        private readonly Dictionary<string, SurfacePrefab> m_NetworkOverlayPrefabs =
            new Dictionary<string, SurfacePrefab>(StringComparer.Ordinal);
        private readonly List<Material> m_NetworkOverlayMaterials = new List<Material>();
        private readonly HashSet<string> m_FailedNetworkOverlayPrefabs = new HashSet<string>();
        private readonly HashSet<Entity> m_LoggedNetworkOverlayPrefabs = new HashSet<Entity>();

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            RegisterNetworkOverlayPrefabs();
        }

        protected override void OnDestroy()
        {
            foreach (Material material in m_NetworkOverlayMaterials)
                if (material != null) UnityEngine.Object.Destroy(material);
            m_NetworkOverlayMaterials.Clear();
            base.OnDestroy();
        }

        private static bool IsNetworkOverlayPrefab(string name)
        {
            return name != null && name.StartsWith(NetworkOverlayPrefix, StringComparison.Ordinal);
        }

        internal void RegisterNetworkOverlayPrefabs()
        {
            // Register all available surfaces, rather than only active rules,
            // so old generated areas still resolve after a rule changes.
            using (NativeArray<Entity> entities = m_SurfacePrefabs.ToEntityArray(Allocator.Temp))
                foreach (Entity entity in entities)
                    if (m_PrefabSystem.TryGetPrefab(entity, out SurfacePrefab source) &&
                        !IsNetworkOverlayPrefab(source.name))
                        RegisterNetworkOverlayPrefab(source);
        }

        private void RegisterNetworkOverlayPrefab(SurfacePrefab source)
        {
            string name = source.name;
            if (m_NetworkOverlayPrefabs.ContainsKey(name) || m_FailedNetworkOverlayPrefabs.Contains(name) ||
                !source.TryGet(out RenderedArea original)) return;
            SurfacePrefab variant = null;
            Material material = null;
            try
            {
                string variantName = NetworkOverlayPrefix + name;
                if (m_PrefabSystem.TryGetPrefab(new PrefabID(nameof(SurfacePrefab), variantName), out PrefabBase existing) &&
                    existing is SurfacePrefab registered)
                {
                    m_NetworkOverlayPrefabs.Add(name, registered);
                    return;
                }
                variant = (SurfacePrefab)source.Clone(variantName);
                // Clones are rendering resources, not additional placeable menu
                // items or replacements for the source asset's obsolete IDs.
                variant.components.RemoveAll(component => component is UIObject || component is ObsoleteIdentifiers);
                RenderedArea renderer = variant.GetComponent<RenderedArea>();
                CopyNetworkOverlayRenderer(original, renderer);
                // The native shader scales its border by Roundness * the area's
                // minimum node distance. The default surface border parameter
                // is 0.375 m at EVERY edge, including internal road seams.
                // Use the native minimum width, suppress its raised/noisy rim,
                // and retain the asset's texture, opacity and fade convention.
                renderer.m_Roundness = 0.01f;
                renderer.m_EdgeNormal = 0f;
                // EdgeNoise.x is the strength; y is its spatial scale. Native
                // RenderedArea metadata permits y only in [0.01, 10]. A zero
                // strength disables noise without supplying an invalid scale.
                renderer.m_EdgeNoise = new float2(0f,
                    math.clamp(math.isfinite(original.m_EdgeNoise.y) ? original.m_EdgeNoise.y : 1f, 0.01f, 10f));
                // Legacy surfaces read these parameters from their Material,
                // while current surfaces read them from RenderedArea. Copy the
                // material too, so both paths leave the provider's asset intact.
                if (original.m_Material != null)
                {
                    material = new Material(original.m_Material) { name = variantName };
                    int normal = Shader.PropertyToID("colossal_EdgeNormal");
                    int noise = Shader.PropertyToID("colossal_EdgeNoise");
                    if (material.HasProperty(normal)) material.SetFloat(normal, 0f);
                    if (material.HasProperty(noise)) material.SetVector(noise,
                        new Vector4(renderer.m_EdgeNoise.x, renderer.m_EdgeNoise.y, 0f, 0f));
                    renderer.m_Material = material;
                }
                if (!m_PrefabSystem.AddPrefab(variant))
                    throw new InvalidOperationException("Native prefab registration did not accept the overlay.");
                m_NetworkOverlayPrefabs.Add(name, variant);
                if (material != null) m_NetworkOverlayMaterials.Add(material);
            }
            catch (Exception exception)
            {
                if (material != null) UnityEngine.Object.Destroy(material);
                if (variant != null) UnityEngine.Object.Destroy(variant);
                m_FailedNetworkOverlayPrefabs.Add(name);
                Mod.Log.Warn("Could not create a network rendering copy of '" + name + "'; using its original renderer. " + exception.Message);
            }
        }

        private static void CopyNetworkOverlayRenderer(RenderedArea source, RenderedArea target)
        {
            // PrefabBase.Clone uses Unity's JSON serializer for components.
            // AssetReference<TextureAsset> contains external database references:
            // preserve those objects directly instead of relying on JSON to
            // reconstruct them. Preserve all other authored rendering fields too.
            target.m_Version = source.m_Version;
            target.m_Material = source.m_Material;
            target.m_Roundness = source.m_Roundness;
            target.m_LodBias = source.m_LodBias;
            target.m_RendererPriority = source.m_RendererPriority;
            target.m_DecalLayerMask = source.m_DecalLayerMask;
            target.m_BaseColor = source.m_BaseColor;
            target.m_BaseColorMap = source.m_BaseColorMap;
            target.m_NormalMap = source.m_NormalMap;
            target.m_MaskMap = source.m_MaskMap;
            target.m_Metallic = source.m_Metallic;
            target.m_Smoothness = source.m_Smoothness;
            target.m_NormalOpacity = source.m_NormalOpacity;
            target.m_MetallicOpacity = source.m_MetallicOpacity;
            target.m_NormalAlphaSource = source.m_NormalAlphaSource;
            target.m_MetallicAlphaSource = source.m_MetallicAlphaSource;
            target.m_UVScale = source.m_UVScale;
            target.m_EdgeNormal = source.m_EdgeNormal;
            target.m_EdgeFadeRange = source.m_EdgeFadeRange;
            target.m_EdgeNormalRange = source.m_EdgeNormalRange;
            target.m_EdgeNoise = source.m_EdgeNoise;
        }

        private Entity GetNetworkOverlayPrefab(Entity source)
        {
            if (!m_PrefabSystem.TryGetPrefab(source, out SurfacePrefab surface)) return source;
            RegisterNetworkOverlayPrefab(surface);
            if (!m_NetworkOverlayPrefabs.TryGetValue(surface.name, out SurfacePrefab variant)) return source;
            if (!m_PrefabSystem.TryGetEntity(variant, out Entity entity) || !EntityManager.Exists(entity) ||
                !EntityManager.HasComponent<AreaData>(entity) ||
                !EntityManager.GetComponentData<AreaData>(entity).m_Archetype.Valid) return Entity.Null;
            m_LoggedSurfaceRenderers.Add(entity); // Track native projection padding for this render batch too.
            if (m_LoggedNetworkOverlayPrefabs.Add(entity) && surface.TryGet(out RenderedArea original))
                Mod.Log.Info("Network overlay renderer: source='" + surface.name + "'; original roundness=" + original.m_Roundness +
                    "; original edge normal=" + original.m_EdgeNormal + "; original edge fade=" + original.m_EdgeFadeRange +
                    "; original edge noise=" + original.m_EdgeNoise +
                    ". Generated overlay uses roundness=0.01, edge normal=0, edge noise=" + variant.GetComponent<RenderedArea>().m_EdgeNoise +
                    "; source texture references preserved=" +
                    (ReferenceEquals(original.m_BaseColorMap, variant.GetComponent<RenderedArea>().m_BaseColorMap) &&
                     ReferenceEquals(original.m_NormalMap, variant.GetComponent<RenderedArea>().m_NormalMap) &&
                     ReferenceEquals(original.m_MaskMap, variant.GetComponent<RenderedArea>().m_MaskMap)) + ".");
            return entity;
        }

        private bool NetworkOverlayPrefabsReady(Setting setting)
        {
            // Wait for the native initialization pass before consuming a road
            // queue entry. A newly registered AreaData has no usable archetype.
            bool ready = true;
            for (int slot = 0; slot < setting.RuleCount; slot++)
            {
                SurfaceRule rule = setting.GetRule(slot);
                if (!rule.Enabled) continue;
                foreach (SurfaceAssignment item in rule.ActiveAssignments)
                    if (item.Surface.Length > 0 && RuntimeState.TryGetSurface(item.Surface, out Entity source) &&
                        EntityManager.Exists(source) && GetNetworkOverlayPrefab(source) == Entity.Null) ready = false;
            }
            if (!ready) RuntimeState.Status = "Preparing surface renderers.";
            return ready;
        }
    }
}
