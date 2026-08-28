using Colossal.IO.AssetDatabase;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Game.UI.Localization;
using Unity.Entities;

namespace CrashRepair
{
    [FileLocation("ModsSettings/" + nameof(CrashRepair) + "/" + nameof(CrashRepair))]
    [SettingsUIGroupOrder(kStatusGroup, kRepairGroup)]
    [SettingsUIShowGroupName(kStatusGroup, kRepairGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kStatusGroup = "Status";
        public const string kRepairGroup = "Repair";

        public Setting(IMod mod) : base(mod)
        {
        }

        // The scan result is shown as one text widget per line, the way the game
        // shows its modding disclaimer: a read-only string with
        // SettingsUIMultilineText renders its (dynamic) display name as a paragraph.
        // Widgets are fixed at registration, so a fixed number of slots is kept
        // and the unused ones are hidden.
        private static LocalizedString Line(int index)
        {
            string[] lines = Mod.lastScanLines;
            if (lines.Length == 0)
                return LocalizedString.Value(index == 0 ? "No scan yet — load a savegame first." : string.Empty);
            return LocalizedString.Value(index < lines.Length ? lines[index] : string.Empty);
        }

        private static bool HideLine(int index) => index > 0 && index >= Mod.lastScanLines.Length;

        public static LocalizedString Line0() => Line(0);
        public static LocalizedString Line1() => Line(1);
        public static LocalizedString Line2() => Line(2);
        public static LocalizedString Line3() => Line(3);
        public static LocalizedString Line4() => Line(4);
        public static LocalizedString Line5() => Line(5);
        public static LocalizedString Line6() => Line(6);
        public static LocalizedString Line7() => Line(7);
        public static LocalizedString Line8() => Line(8);
        public static LocalizedString Line9() => Line(9);
        public static LocalizedString Line10() => Line(10);
        public static LocalizedString Line11() => Line(11);

        public static bool HideLine1() => HideLine(1);
        public static bool HideLine2() => HideLine(2);
        public static bool HideLine3() => HideLine(3);
        public static bool HideLine4() => HideLine(4);
        public static bool HideLine5() => HideLine(5);
        public static bool HideLine6() => HideLine(6);
        public static bool HideLine7() => HideLine(7);
        public static bool HideLine8() => HideLine(8);
        public static bool HideLine9() => HideLine(9);
        public static bool HideLine10() => HideLine(10);
        public static bool HideLine11() => HideLine(11);

        [SettingsUISection(kSection, kStatusGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(Line0))]
        public string StatusLine0 => string.Empty;

        [SettingsUISection(kSection, kStatusGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(Line1))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideLine1))]
        public string StatusLine1 => string.Empty;

        [SettingsUISection(kSection, kStatusGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(Line2))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideLine2))]
        public string StatusLine2 => string.Empty;

        [SettingsUISection(kSection, kStatusGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(Line3))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideLine3))]
        public string StatusLine3 => string.Empty;

        [SettingsUISection(kSection, kStatusGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(Line4))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideLine4))]
        public string StatusLine4 => string.Empty;

        [SettingsUISection(kSection, kStatusGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(Line5))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideLine5))]
        public string StatusLine5 => string.Empty;

        [SettingsUISection(kSection, kStatusGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(Line6))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideLine6))]
        public string StatusLine6 => string.Empty;

        [SettingsUISection(kSection, kStatusGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(Line7))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideLine7))]
        public string StatusLine7 => string.Empty;

        [SettingsUISection(kSection, kStatusGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(Line8))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideLine8))]
        public string StatusLine8 => string.Empty;

        [SettingsUISection(kSection, kStatusGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(Line9))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideLine9))]
        public string StatusLine9 => string.Empty;

        [SettingsUISection(kSection, kStatusGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(Line10))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideLine10))]
        public string StatusLine10 => string.Empty;

        [SettingsUISection(kSection, kStatusGroup)]
        [SettingsUIMultilineText]
        [SettingsUIDisplayName(typeof(Setting), nameof(Line11))]
        [SettingsUIHideByCondition(typeof(Setting), nameof(HideLine11))]
        public string StatusLine11 => string.Empty;

        [SettingsUIButton]
        [SettingsUIConfirmation]
        [SettingsUISection(kSection, kRepairGroup)]
        [SettingsUIDisableByCondition(typeof(Setting), nameof(IsNotInGame))]
        public bool RepairNow
        {
            set
            {
                World.DefaultGameObjectInjectionWorld
                    .GetOrCreateSystemManaged<ManualRepairSystem>()
                    .Schedule(AdvancedCleanup ? RepairSystemBase.RepairMode.RepairAdvanced
                        : RepairSystemBase.RepairMode.Repair, 0);
            }
        }

        [SettingsUISection(kSection, kRepairGroup)]
        public bool AutoRepairOnLoad { get; set; }

        /// <summary>
        /// Also repairs the references outside placed objects that need more than a delete
        /// (company brands, pending upgrades, policies, service budgets, chirps).
        /// Applies to "Repair now" and to the automatic repair alike.
        /// </summary>
        [SettingsUISection(kSection, kRepairGroup)]
        public bool AdvancedCleanup { get; set; }

        public bool IsNotInGame() => !GameManager.instance.gameMode.IsGame();

        public override void SetDefaults()
        {
            AutoRepairOnLoad = false;
            AdvancedCleanup = false;
        }
    }
}
