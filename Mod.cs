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

        /// <summary>Human-readable result of the most recent scan, one line per finding, shown in the options UI.</summary>
        public static string[] lastScanLines { get; internal set; } = new string[0];

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            settings = new Setting(this);
            settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(settings));
            AssetDatabase.global.LoadSettings(nameof(CrashRepair), settings, new Setting(this));

            // UpdateAfter puts the system in the phase's back band (index + 1000000),
            // behind every vanilla UpdateAfter registration (the DeserializationBarrier
            // and all PostDeserialize wrappers), so the scan sees the fully loaded
            // world. UpdateAt would land it in the middle band, before those.
            updateSystem.UpdateAfter<AutoRepairSystem>(SystemUpdatePhase.Deserialize);
            // PreTool runs inside ToolSystem before PostTool (SubElementDeleteSystem)
            // and the Modification phases: the same frame path a bulldozed object takes.
            updateSystem.UpdateAt<ManualRepairSystem>(SystemUpdatePhase.PreTool);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            settings?.UnregisterInOptionsUI();
            settings = null;
            lastScanLines = new string[0];
        }
    }
}
