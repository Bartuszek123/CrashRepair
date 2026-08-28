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
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kStatusGroup), "Last scan result" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kRepairGroup), "Savegame repair" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RepairNow)), "Repair now" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RepairNow)), "Bulldozes everything in the current city that belongs to a missing mod or asset: cars, props, surfaces, road markings, buildings (their residents and companies move out), roads (junctions that other roads still use are kept and switched to a remaining road type). Transit lines whose chosen vehicle is missing go back to random vehicles of their type (as when none is chosen), roads to default street trees, and the map's list of required content is trimmed. These leftovers would otherwise keep spawning broken objects. Also tidies the save's internal list of used mods. With Advanced cleanup on, it additionally fixes company brands, pending building upgrades, policies, service budgets and chirps that point at missing content. Afterwards, save your city under a NEW name and keep the original file as a backup. Note: nothing comes back if you re-subscribe the missing mod later." },
                { m_Setting.GetOptionWarningLocaleID(nameof(Setting.RepairNow)), "This permanently bulldozes everything whose source mod or asset is missing (objects, buildings with their occupants, roads) and it will not come back even if you re-subscribe that mod later. If a pack is only disabled in your playset, enable it instead. Keep your original savegame as a backup and save the repaired city under a new name. Continue?" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AutoRepairOnLoad)), "Repair automatically on load" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AutoRepairOnLoad)), "Runs the same repair as 'Repair now' a moment after every savegame finishes loading — whenever you remove a mod in the future, its leftovers (objects, buildings, roads, street trees, transit vehicle models) are cleaned before they can cause a crash. Off by default so the first repair is always your own decision. Careful: a pack that is merely disabled in your playset, or a save made for a different playset, loses its content the same way. With Advanced cleanup on, the automatic repair includes the advanced part as well." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AdvancedCleanup)), "Advanced cleanup" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AdvancedCleanup)), "Only needed when the load menu still shows the 'missing content' warning after a normal repair. Repair now then also fixes the remaining data that points at missing assets: company brands (a new brand is picked), pending building upgrades into a missing building (the upgrade is cancelled, the building stays), policies, service budgets and chirps. Each is handled the way the game itself would, but this touches more than placed objects, so leave it off unless you need it. It applies to Repair now and, if the automatic repair is on, to that as well. Check the load menu entry of the NEW save afterwards; the original file always keeps its warning." },
            };
        }

        public void Unload()
        {
        }

        /// <summary>Identifies this source in the game's localization import log.</summary>
        public override string ToString() => nameof(CrashRepair) + ".LocaleEN";
    }
}
