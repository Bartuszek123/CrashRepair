using Colossal.IO.AssetDatabase;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Unity.Entities;

namespace CrashRepair
{
    [FileLocation("ModsSettings/" + nameof(CrashRepair) + "/" + nameof(CrashRepair))]
    [SettingsUIGroupOrder(kRepairGroup)]
    [SettingsUIShowGroupName(kRepairGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kRepairGroup = "Repair";

        public Setting(IMod mod) : base(mod)
        {
        }

        /// <summary>Result of the most recent scan, refreshed on every savegame load.</summary>
        [SettingsUISection(kSection, kRepairGroup)]
        public string ScanStatus => string.IsNullOrEmpty(Mod.lastScanResult)
            ? "No scan yet — load a savegame first."
            : Mod.lastScanResult;

        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(kSection, kRepairGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsNotInGame))]
        public bool RepairNow
        {
            set
            {
                World.DefaultGameObjectInjectionWorld
                    .GetOrCreateSystemManaged<ManualRepairSystem>().Enabled = true;
            }
        }

        [SettingsUISection(kSection, kRepairGroup)]
        public bool AutoRepairOnLoad { get; set; }

        public bool IsNotInGame() => !GameManager.instance.gameMode.IsGame();

        public override void SetDefaults()
        {
            AutoRepairOnLoad = false;
        }
    }
}
