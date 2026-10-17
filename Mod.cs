using Colossal.IO.AssetDatabase;
using Colossal.Localization;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Modding;
using Game.SceneFlow;
using Game.Rendering;
using Game.Prefabs;

namespace SurfaceNetworkFilter
{
    public sealed class Mod : IMod
    {
        public static readonly ILog Log = LogManager.GetLogger("SurfaceNetworkFilter").SetShowsErrorsInUI(false);
        internal static Setting ActiveSetting;

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info("Loading Road Surface Filler " + typeof(Mod).Assembly.GetName().Version);

            ActiveSetting = new Setting(this);
            ActiveSetting.RegisterInOptionsUI();
            AssetDatabase.global.LoadSettings("SurfaceNetworkFilter", ActiveSetting, new Setting(this));
            ActiveSetting.InitializeRules();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(ActiveSetting));
            // Definitions must exist before the game's modification pass consumes
            // Updated tags and generates the permanent areas.
            updateSystem.UpdateBefore<SurfaceNetworkFilterSystem, ModificationSystem>(SystemUpdatePhase.MainLoop);
            updateSystem.UpdateAt<SurfaceRuleUISystem>(SystemUpdatePhase.UIUpdate);
            updateSystem.UpdateAt<SegmentUpgradeToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<JunctionCycleToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateBefore<ManualSurfaceEditorSystem, Game.Tools.AreaToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateBefore<ManualSurfaceEditorSystem, Game.Tools.GenerateAreasSystem>(SystemUpdatePhase.Modification1);
            updateSystem.UpdateBefore<ManualSurfaceEditorSystem, Game.Tools.ApplyAreasSystem>(SystemUpdatePhase.ApplyTool);
            updateSystem.UpdateAt<RoadSurfaceReplaceSystem>(SystemUpdatePhase.PostTool);
            updateSystem.UpdateBefore<RoadSurfaceApplyCaptureSystem, Game.Tools.ApplyNetSystem>(SystemUpdatePhase.ApplyTool);
            updateSystem.UpdateBefore<RoadNodeProvenanceSystem, Game.Tools.GenerateEdgesSystem>(SystemUpdatePhase.Modification2);
            updateSystem.UpdateBefore<NetworkOverlayPrefabSystem, PrefabInitializeSystem>(SystemUpdatePhase.PrefabUpdate);
            updateSystem.UpdateAt<JunctionPrefabReferencesSystem>(SystemUpdatePhase.PrefabReferences);
            updateSystem.UpdateBefore<SurfaceProjectionSystem, Game.Areas.SearchSystem>(SystemUpdatePhase.Modification5);
            updateSystem.UpdateBefore<SurfaceProjectionSystem, AreaBatchSystem>(SystemUpdatePhase.PreCulling);
        }

        public void OnDispose()
        {
            if (ActiveSetting != null)
            {
                ActiveSetting.UnregisterInOptionsUI();
                ActiveSetting = null;
            }
            Log.Info("Unloaded Road Surface Filler");
        }

        internal static void RequestRebuild()
        {
            RuntimeState.RebuildRequested = true;
        }

        internal static void RequestSurfaceRefresh()
        {
            RuntimeState.SurfaceRefreshRequested = true;
        }

        internal static void RefreshOptionsUI()
        {
            Setting setting = ActiveSetting;
            if (setting == null)
                return;

            // Dropdown choices are materialized when the settings are registered.
            // Re-register after the game has loaded its surface prefabs so the
            // options UI sees the runtime catalog instead of the initial empty list.
            setting.UnregisterInOptionsUI();
            setting.RegisterInOptionsUI();
        }
    }
}
