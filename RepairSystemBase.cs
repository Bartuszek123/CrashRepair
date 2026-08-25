using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Colossal.PSI.Environment;
using Game;
using Game.City;
using Game.Common;
using Game.Modding;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CrashRepair
{
    /// <summary>
    /// Shared engine for the auto (on-load) and manual (settings button) repair
    /// systems: finds every instance entity whose PrefabRef points at a prefab
    /// that no longer exists (mod/asset removed), reports the findings (log +
    /// CSV in ModsData/CrashRepair/) and optionally deletes them by tagging
    /// Deleted. The vanilla cleanup pipeline (the *ReferencesSystem family,
    /// IconDeletedSystem, CleanUpSystem) removes deleted entities from all
    /// owner-side buffers — no manual cascade is needed. Owners are NOT marked
    /// Updated: Building/Edge + Updated makes RoadConnectionSystem re-evaluate
    /// road connections, which detaches buildings when it runs while the net
    /// search tree is still empty during loading.
    /// Also prunes the save's "mods used" list (CityConfigurationSystem.usedMods):
    /// vanilla only ever adds to it, so a removed mod stays listed forever and
    /// the load menu keeps flagging the save as missing content.
    /// </summary>
    public abstract partial class RepairSystemBase : GameSystemBase
    {
        private struct MissingGroup
        {
            public int m_Count;
            public Entity m_Sample;
        }

        private PrefabSystem m_PrefabSystem;
        private CityConfigurationSystem m_CityConfigurationSystem;
        private EntityQuery m_PrefabRefQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_CityConfigurationSystem = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            // Excludes mirror vanilla PrimaryPrefabReferencesSystem: composition,
            // effect-instance and live-path entities are runtime-derived state
            // whose prefab references the game repairs through other channels —
            // deleting them would damage healthy roads and effects.
            m_PrefabRefQuery = GetEntityQuery(
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<NetCompositionData>(),
                ComponentType.Exclude<Game.Effects.EffectInstance>(),
                ComponentType.Exclude<Game.Routes.LivePath>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());
        }

        /// <summary>
        /// Scans all prefab-referencing instances; deletes the broken ones when
        /// <paramref name="deleteBroken"/> is set, otherwise only reports them.
        /// </summary>
        protected void RunRepair(bool deleteBroken)
        {
            // A save references few unique prefabs across many instances, so the
            // verdict is cached per prefab entity instead of re-queried per instance.
            var missing = new Dictionary<Entity, MissingGroup>();
            var healthy = new HashSet<Entity>();
            var toDelete = new NativeList<Entity>(Allocator.Temp);

            var entities = m_PrefabRefQuery.ToEntityArray(Allocator.Temp);
            var prefabRefs = m_PrefabRefQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity prefab = prefabRefs[i].m_Prefab;
                    if (healthy.Contains(prefab))
                        continue;

                    if (missing.TryGetValue(prefab, out MissingGroup group))
                    {
                        group.m_Count++;
                        missing[prefab] = group;
                    }
                    else if (MissingPrefabDetector.IsMissing(EntityManager, m_PrefabSystem, prefab))
                    {
                        missing[prefab] = new MissingGroup { m_Count = 1, m_Sample = entities[i] };
                    }
                    else
                    {
                        healthy.Add(prefab);
                        continue;
                    }
                    if (deleteBroken)
                        toDelete.Add(entities[i]);
                }

                Mod.lastScanResult = Report(entities.Length, missing, deleteBroken && missing.Count > 0)
                    + CleanModList(deleteBroken);
                if (toDelete.Length > 0)
                    EntityManager.AddComponent<Deleted>(toDelete.AsArray());
            }
            finally
            {
                entities.Dispose();
                prefabRefs.Dispose();
                toDelete.Dispose();
            }
        }

        /// <summary>
        /// Finds entries of the save's mod list that no loaded mod matches and
        /// removes them when repairing. Returns a status suffix for the UI.
        /// </summary>
        private string CleanModList(bool remove)
        {
            // An empty list can only mean the mod manager is not fully up (this
            // mod itself is always in it) — never prune against that.
            string[] enabled = ModManager.GetModsEnabled();
            if (enabled == null || enabled.Length == 0)
                return string.Empty;

            var stale = m_CityConfigurationSystem.usedMods.Except(enabled).ToArray();
            if (stale.Length == 0)
                return string.Empty;

            // Entries are assembly full names (with version), so every mod update
            // leaves an outdated entry behind — most of these are old versions of
            // mods that are still installed, not missing mods.
            Mod.log.Warn($"Save's mod list has {stale.Length} stale entries ({(remove ? "removed" : "kept")}): {string.Join(" | ", stale)}");
            if (remove)
                m_CityConfigurationSystem.usedMods.ExceptWith(stale);
            return remove
                ? $" Removed {stale.Length} stale entries (missing or outdated mods) from the save's mod list."
                : $" The save's mod list has {stale.Length} stale entries (missing or outdated mods).";
        }

        /// <summary>Logs the scan outcome, writes the CSV; returns the status line for the UI.</summary>
        private string Report(int scanned, Dictionary<Entity, MissingGroup> missing, bool deleting)
        {
            if (missing.Count == 0)
            {
                Mod.log.Info($"Scan done: {scanned} instances checked, no missing prefabs. Save is clean.");
                return $"{scanned:N0} objects checked — no broken objects found.";
            }

            int total = missing.Values.Sum(g => g.m_Count);
            string action = deleting ? "deleted" : "found (not deleted)";
            Mod.log.Warn($"Scan done: {scanned} instances checked, {total} instances reference {missing.Count} missing prefabs ({action}):");

            var csv = new StringBuilder();
            csv.AppendLine("prefab_id;instance_count;sample_entity;sample_components");
            foreach (var pair in missing.OrderByDescending(p => p.Value.m_Count))
            {
                string id = DescribePrefab(pair.Key);
                string components = DescribeComponents(pair.Value.m_Sample);
                Mod.log.Warn($"  {id}: {pair.Value.m_Count} instances (sample {pair.Value.m_Sample.Index}, components: {components})");
                csv.AppendLine($"{id};{pair.Value.m_Count};{pair.Value.m_Sample.Index}:{pair.Value.m_Sample.Version};{components}");
            }

            try
            {
                string dir = Path.Combine(EnvPath.kUserDataPath, "ModsData", nameof(CrashRepair));
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "missing_prefabs_report.csv");
                File.WriteAllText(path, csv.ToString());
                Mod.log.Info($"CSV report written to {path}");
            }
            catch (System.Exception e)
            {
                Mod.log.Warn($"Failed to write CSV report: {e.Message}");
            }
            return $"{scanned:N0} objects checked — {total:N0} broken objects ({missing.Count} missing assets) {action}.";
        }

        private string DescribePrefab(Entity prefab)
        {
            // GetObsoleteID keeps the "Type:Name" form that identifies which
            // missing asset the instance came from; GetPrefabName covers
            // anything it rejects.
            if (prefab == Entity.Null || !EntityManager.HasComponent<PrefabData>(prefab))
                return prefab.ToString();
            try
            {
                return m_PrefabSystem.GetObsoleteID(prefab).ToString();
            }
            catch (System.Exception)
            {
                return m_PrefabSystem.GetPrefabName(prefab);
            }
        }

        private string DescribeComponents(Entity entity)
        {
            using var types = EntityManager.GetComponentTypes(entity, Allocator.Temp);
            return string.Join(",", types.ToArray()
                .Select(t => t.GetManagedType()?.Name ?? "?")
                .OrderBy(n => n));
        }
    }
}
