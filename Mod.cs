using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace CrashRepair
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger(nameof(CrashRepair)).SetShowsErrorsInUI(false);

        public static Setting settings { get; private set; }

        /// <summary>Human-readable result of the most recent scan, shown in the options UI.</summary>
        public static string lastScanResult { get; internal set; } = string.Empty;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            settings = new Setting(this);
            settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(settings));
            AssetDatabase.global.LoadSettings(nameof(CrashRepair), settings, new Setting(this));

            // Deserialize-phase mod systems run after all vanilla systems of
            // that phase, so prefab references are already resolved when the
            // scan sees them.
            updateSystem.UpdateAt<AutoRepairSystem>(SystemUpdatePhase.Deserialize);
            updateSystem.UpdateAt<ManualRepairSystem>(SystemUpdatePhase.Modification1);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            settings?.UnregisterInOptionsUI();
            settings = null;
        }
    }
}
