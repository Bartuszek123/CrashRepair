using System.Collections.Generic;
using Colossal;

namespace CrashRepair
{
    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Crash Repair" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kRepairGroup), "Savegame repair" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ScanStatus)), "Last scan result" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ScanStatus)), "Every time a savegame is loaded, the mod scans it for objects that belong to mods or assets that are no longer installed. Such orphaned objects are a common cause of crashes. A detailed list is written to ModsData/CrashRepair/missing_prefabs_report.csv." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RepairNow)), "Repair now" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RepairNow)), "Deletes every object in the current city that references a missing mod or asset (removed cars, props, surfaces, road markings, …). Afterwards, save your city under a NEW name and keep the original file as a backup. Note: the deleted objects will not come back if you re-subscribe the missing mod later." },
                { m_Setting.GetOptionWarningLocaleID(nameof(Setting.RepairNow)), "This permanently deletes all objects whose source mod or asset is missing — they will not come back even if you re-subscribe that mod later. Keep your original savegame as a backup and save the repaired city under a new name. Continue?" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AutoRepairOnLoad)), "Repair automatically on load" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AutoRepairOnLoad)), "Recommended. Runs the repair automatically every time a savegame finishes loading — whenever you remove a mod in the future, its leftovers are cleaned before they can cause a crash, and your city stays permanently healthy. Off by default so the first repair is always your own decision." },
            };
        }

        public void Unload()
        {
        }
    }
}
