using System.Collections.Generic;
using System.IO;
using System.Linq;
using Colossal.Entities;
using Colossal.PSI.Environment;
using Colossal.Serialization.Entities;
using Game.Buildings;
using Game.City;
using Game.Common;
using Game.Companies;
using Game.Net;
using Game.Objects;
using Game.Policies;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.Triggers;
using Unity.Collections;
using Unity.Entities;

namespace CrashRepair
{
    /// <summary>
    /// References to missing prefabs that live outside PrefabRef. The game follows
    /// these when it saves (PrimaryPrefabReferencesSystem), so any of them keeps the
    /// removed asset pack listed as required content and the load menu keeps asking
    /// to subscribe to it. Scans always; fixes only when asked (manual repair with the
    /// advanced cleanup setting on). Each kind is handled the way the game itself
    /// handles the equivalent player action. This class owns the catalogue of
    /// reference kinds, including the content-prerequisite chain used to tell which
    /// placeholders are held by data nobody here knows about.
    /// </summary>
    internal sealed class SecondaryReferences
    {
        public struct Row
        {
            public string m_Kind;
            public Entity m_Holder;
            public Entity m_Prefab;
            public string m_Action;
        }

        private const string kNotFixable = "not fixable";
        private const string kFound = "found";

        private readonly EntityManager m_EntityManager;
        private readonly PrefabSystem m_PrefabSystem;
        private readonly CityConfigurationSystem m_CityConfiguration;
        private readonly ZoneBuiltRequirementSystem m_ZoneBuiltRequirement;
        private readonly ClimateSystem m_Climate;
        private readonly EntityArchetype m_RentEventArchetype;
        private readonly EntityQuery m_VehicleModelQuery;
        private readonly EntityQuery m_CompanyQuery;
        private readonly EntityQuery m_UnderConstructionQuery;
        private readonly EntityQuery m_PolicyQuery;
        private readonly EntityQuery m_ServiceBudgetQuery;
        private readonly EntityQuery m_ChirpQuery;
        private readonly EntityQuery m_SubReplacementQuery;
        private readonly EntityQuery m_PlaceholderQuery;
        // Reassigned per run: shares the PrefabRef scan's missing-prefab cache.
        private Dictionary<Entity, bool> m_Verdicts = new Dictionary<Entity, bool>();

        /// <summary>Every reference found, in scan order.</summary>
        public readonly List<Row> rows = new List<Row>();
        /// <summary>Kinds whose scan threw; the rest of the pass still ran.</summary>
        public readonly List<string> failedKinds = new List<string>();

        public SecondaryReferences(World world, PrefabSystem prefabSystem, CityConfigurationSystem cityConfiguration)
        {
            m_EntityManager = world.EntityManager;
            m_PrefabSystem = prefabSystem;
            m_CityConfiguration = cityConfiguration;
            m_ZoneBuiltRequirement = world.GetOrCreateSystemManaged<ZoneBuiltRequirementSystem>();
            m_Climate = world.GetOrCreateSystemManaged<ClimateSystem>();
            m_RentEventArchetype = m_EntityManager.CreateArchetype(
                ComponentType.ReadWrite<Event>(), ComponentType.ReadWrite<RentersUpdated>());

            m_VehicleModelQuery = Live(ComponentType.ReadOnly<VehicleModel>());
            m_CompanyQuery = Live(ComponentType.ReadOnly<CompanyData>());
            m_UnderConstructionQuery = Live(ComponentType.ReadOnly<UnderConstruction>(),
                ComponentType.ReadOnly<Building>(), ComponentType.Exclude<Destroyed>());
            m_PolicyQuery = Live(ComponentType.ReadOnly<Policy>());
            m_ServiceBudgetQuery = Live(ComponentType.ReadOnly<ServiceBudgetData>());
            m_ChirpQuery = Live(ComponentType.ReadOnly<Game.Triggers.Chirp>());
            m_SubReplacementQuery = Live(ComponentType.ReadOnly<SubReplacement>());
            // Placeholders are the prefabs whose PrefabData is disabled; WithDisabled
            // lets ECS skip every chunk without one instead of walking the catalogue.
            using var builder = new EntityQueryBuilder(Allocator.Temp);
            m_PlaceholderQuery = builder
                .WithAll<LoadedIndex>()
                .WithDisabled<PrefabData>()
                .Build(m_EntityManager);
        }

        private EntityQuery Live(params ComponentType[] all)
        {
            var types = new List<ComponentType>(all)
            {
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>()
            };
            return m_EntityManager.CreateEntityQuery(types.ToArray());
        }

        /// <summary>
        /// Runs every scan; with <paramref name="fix"/> the safe ones also repair.
        /// <paramref name="verdicts"/> is the missing-prefab cache shared with the
        /// PrefabRef scan (filled here for prefabs it did not see). The caller must
        /// have completed all jobs: buffers are written directly.
        /// </summary>
        /// <summary>
        /// Runs every scan. <paramref name="fixBasic"/> repairs the kinds that belong
        /// to every repair: a transit line's missing vehicle model and a road's missing
        /// street trees keep spawning broken entities (a crash risk), and the map's
        /// requirement list is metadata only. <paramref name="fixAdvanced"/> repairs the
        /// rest (brands, level-ups, policies, budgets, chirps).
        /// </summary>
        public void Run(bool fixBasic, bool fixAdvanced, Dictionary<Entity, bool> verdicts)
        {
            rows.Clear();
            failedKinds.Clear();
            m_Verdicts = verdicts;

            Guarded("map requirement", () => RequiredContent(fixBasic));
            Guarded("transit line vehicle", () => VehicleModels(fixBasic));
            Guarded("street trees", () => SubReplacements(fixBasic));
            Guarded("company brand", () => CompanyBrands(fixAdvanced));
            Guarded("building level-up", () => UnderConstructions(fixAdvanced));
            Guarded("policy", () => Policies(fixAdvanced));
            Guarded("service budget", () => PruneBuffers<ServiceBudgetData>(
                m_ServiceBudgetQuery, ("service budget", "removed"), s => s.m_Service, fixAdvanced));
            Guarded("chirp", () => Chirps(fixAdvanced));
            Guarded("city theme/climate", SystemReferences);
        }

        public enum Outcome
        {
            All,
            /// <summary>Rows this run repaired.</summary>
            Repaired,
            /// <summary>Rows found but left alone (a later repair, or the advanced cleanup, handles them).</summary>
            Pending,
            /// <summary>Rows nothing here can repair.</summary>
            NotFixable
        }

        private static readonly HashSet<string> kAdvancedKinds = new HashSet<string>
        {
            "company brand", "building level-up", "policy", "service budget", "chirp"
        };

        /// <summary>True when a pending row belongs to a kind only the advanced cleanup repairs.</summary>
        public bool pendingNeedsAdvanced => rows.Any(r => r.m_Action == kFound && kAdvancedKinds.Contains(r.m_Kind));

        /// <summary>Status text for one outcome, e.g. "street trees: reset ×13, company brand: found ×2".</summary>
        public string Summary(Outcome outcome)
        {
            var parts = rows
                .Where(r => Matches(r, outcome))
                .GroupBy(r => r.m_Kind + ": " + r.m_Action)
                .Select(g => $"{g.Key} ×{g.Count()}")
                .ToList();
            if (outcome == Outcome.All)
            {
                foreach (string kind in failedKinds)
                    parts.Add($"{kind}: FAILED (see log)");
            }
            return string.Join(", ", parts);
        }

        private static bool Matches(Row row, Outcome outcome)
        {
            switch (outcome)
            {
                case Outcome.Repaired: return row.m_Action != kFound && row.m_Action != kNotFixable;
                case Outcome.Pending: return row.m_Action == kFound;
                case Outcome.NotFixable: return row.m_Action == kNotFixable;
                default: return true;
            }
        }

        /// <summary>
        /// Sorts the PDX packs behind the missing prefabs: <paramref name="disabled"/>
        /// are still subscribed (their folder is in the mods cache) but not active in
        /// the playset, and leave exactly the same placeholders as a removed pack;
        /// <paramref name="trimmed"/> are active (their content prefab is live), so
        /// the asset itself was dropped from the pack and the save's requirement on
        /// the pack is legitimate.
        /// </summary>
        public void PackStates(out List<string> disabled, out List<string> trimmed)
        {
            disabled = new List<string>();
            trimmed = new List<string>();
            string root = Path.Combine(EnvPath.kUserDataPath, ".cache", "Mods", "pdx_mods");
            foreach (KeyValuePair<Entity, bool> verdict in m_Verdicts)
            {
                if (!verdict.Value || !m_EntityManager.TryGetComponent(verdict.Key, out ModPrerequisiteData mod))
                    continue;
                Entity content = mod.m_ContentPrerequisite;
                string name = m_PrefabSystem.GetPrefabName(content);
                if (!name.StartsWith("Mod:"))
                    continue;
                string id = name.Substring(4);
                if (disabled.Contains(id) || trimmed.Contains(id))
                    continue;
                if (m_EntityManager.HasComponent<PrefabData>(content) && m_EntityManager.IsComponentEnabled<PrefabData>(content))
                    trimmed.Add(id);
                else if (Directory.Exists(root) && Directory.GetDirectories(root, id + "_*").Length > 0)
                    disabled.Add(id);
            }
        }

        // Modifier refresh already skips policies whose prefab has no data; Updated on
        // the holder makes the game recompute anyway, except on the city entity, which
        // has no Updated-driven refresh.
        private void Policies(bool fix)
        {
            List<Entity> holders = PruneBuffers<Policy>(m_PolicyQuery, ("policy", "removed"), p => p.m_Policy, fix);
            holders.RemoveAll(h => m_EntityManager.HasComponent<Game.City.City>(h) || m_EntityManager.HasComponent<Updated>(h));
            Tag<Updated>(holders);
        }

        /// <summary>
        /// Placeholders that neither the PrefabRef scan nor this one reached. A
        /// placeholder survives loading only if something referenced it, so these are
        /// held by data this mod does not know (another mod's components, or an
        /// unhandled vanilla field). Content prefabs ("Mod:&lt;id&gt;") are reached
        /// through the placeholders that require them, never directly.
        /// </summary>
        public List<Entity> Unhandled()
        {
            // Placeholders keep their serialized prefab-to-prefab links, and the game
            // follows them on save (SecondaryPrefabReferencesSystem): a building's zone,
            // a service object's service, a lane's pathfind prefab, a line's notification,
            // and the content prefab of the mod. Everything reachable that way from a
            // placeholder we know about is accounted for.
            var known = new HashSet<Entity>();
            var pending = new List<Entity>();
            foreach (KeyValuePair<Entity, bool> verdict in m_Verdicts)
            {
                if (verdict.Value && known.Add(verdict.Key))
                    pending.Add(verdict.Key);
            }
            while (pending.Count > 0)
            {
                Entity prefab = pending[pending.Count - 1];
                pending.RemoveAt(pending.Count - 1);
                foreach (Entity linked in LinkedPrefabs(prefab))
                {
                    if (linked != Entity.Null && known.Add(linked))
                        pending.Add(linked);
                }
            }

            var unhandled = new List<Entity>();
            using var candidates = m_PlaceholderQuery.ToEntityArray(Allocator.Temp);
            foreach (Entity candidate in candidates)
            {
                if (!known.Contains(candidate) && IsMissing(candidate))
                    unhandled.Add(candidate);
            }
            return unhandled;
        }

        private IEnumerable<Entity> LinkedPrefabs(Entity prefab)
        {
            if (m_EntityManager.TryGetComponent(prefab, out ModPrerequisiteData mod))
                yield return mod.m_ContentPrerequisite;
            if (m_EntityManager.TryGetComponent(prefab, out ContentPrerequisiteData content))
                yield return content.m_ContentPrerequisite;
            if (m_EntityManager.TryGetComponent(prefab, out SpawnableBuildingData spawnable))
                yield return spawnable.m_ZonePrefab;
            if (m_EntityManager.TryGetComponent(prefab, out PlaceholderBuildingData placeholder))
                yield return placeholder.m_ZonePrefab;
            if (m_EntityManager.TryGetComponent(prefab, out ServiceObjectData service))
                yield return service.m_Service;
            if (m_EntityManager.TryGetComponent(prefab, out NetLaneData lane))
                yield return lane.m_PathfindPrefab;
            if (m_EntityManager.TryGetComponent(prefab, out TransportLineData line))
                yield return line.m_PathfindPrefab;
        }

        private void Guarded(string kind, System.Action scan)
        {
            try
            {
                scan();
            }
            catch (System.Exception ex)
            {
                Mod.log.Error($"Secondary reference scan '{kind}' failed: {ex}");
                failedKinds.Add(kind);
            }
        }

        private bool IsMissing(Entity prefab)
        {
            if (prefab == Entity.Null)
                return false;
            if (!m_Verdicts.TryGetValue(prefab, out bool missing))
            {
                missing = MissingPrefabDetector.IsMissing(m_EntityManager, m_PrefabSystem, prefab);
                m_Verdicts[prefab] = missing;
            }
            return missing;
        }

        private void Add(string kind, Entity holder, Entity prefab, string action)
        {
            rows.Add(new Row { m_Kind = kind, m_Holder = holder, m_Prefab = prefab, m_Action = action });
        }

        /// <summary>
        /// Drops every element whose prefab is missing from each holder's buffer.
        /// <paramref name="label"/> is the row kind and the action word used when
        /// fixing. Returns the holders that lost an element.
        /// </summary>
        private List<Entity> PruneBuffers<T>(EntityQuery holders, (string kind, string fixWord) label,
            System.Func<T, Entity> prefabOf, bool fix) where T : unmanaged, IBufferElementData
        {
            var changed = new List<Entity>();
            using var entities = holders.ToEntityArray(Allocator.Temp);
            foreach (Entity holder in entities)
            {
                DynamicBuffer<T> buffer = m_EntityManager.GetBuffer<T>(holder);
                bool any = false;
                for (int i = buffer.Length - 1; i >= 0; i--)
                {
                    Entity prefab = prefabOf(buffer[i]);
                    if (!IsMissing(prefab))
                        continue;
                    Add(label.kind, holder, prefab, fix ? label.fixWord : "found");
                    if (!fix)
                        continue;
                    buffer.RemoveAt(i);
                    any = true;
                }
                if (any)
                    changed.Add(holder);
            }
            return changed;
        }

        // The map's "Requirements" list from the editor is saved with the city and
        // re-marked as referenced on every save; nothing in the game ever prunes it.
        private void RequiredContent(bool fix)
        {
            ref NativeList<Entity> list = ref m_CityConfiguration.requiredContent;
            for (int i = list.Length - 1; i >= 0; i--)
            {
                if (!IsMissing(list[i]))
                    continue;
                Add("map requirement", Entity.Null, list[i], fix ? "removed" : "found");
                if (fix)
                    list.RemoveAt(i);
            }
        }

        // Mirrors SelectVehiclesSection.DeselectVehicleModel: Entity.Null means
        // "no specific model", an element with both fields null is dropped.
        private void VehicleModels(bool fix)
        {
            using var holders = m_VehicleModelQuery.ToEntityArray(Allocator.Temp);
            foreach (Entity holder in holders)
            {
                DynamicBuffer<VehicleModel> buffer = m_EntityManager.GetBuffer<VehicleModel>(holder);
                for (int i = buffer.Length - 1; i >= 0; i--)
                {
                    VehicleModel model = buffer[i];
                    bool changed = ClearIfMissing(holder, ref model.m_PrimaryPrefab, fix)
                        | ClearIfMissing(holder, ref model.m_SecondaryPrefab, fix);
                    if (!changed || !fix)
                        continue;
                    if (model.m_PrimaryPrefab == Entity.Null && model.m_SecondaryPrefab == Entity.Null)
                        buffer.RemoveAtSwapBack(i);
                    else
                        buffer[i] = model;
                }
            }
        }

        private bool ClearIfMissing(Entity holder, ref Entity field, bool fix)
        {
            if (!IsMissing(field))
                return false;
            Add("transit line vehicle", holder, field, fix ? "cleared" : "found");
            field = Entity.Null;
            return true;
        }

        // A null brand makes NameSystem print "brand is null!", so re-pick one the way
        // CompanyInitializeSystem does: at random from the company prefab's brand list.
        // Signage colours refresh through the same RentersUpdated event the game uses.
        private void CompanyBrands(bool fix)
        {
            using var companies = m_CompanyQuery.ToEntityArray(Allocator.Temp);
            foreach (Entity company in companies)
            {
                CompanyData data = m_EntityManager.GetComponentData<CompanyData>(company);
                if (!IsMissing(data.m_Brand))
                    continue;
                if (!fix)
                {
                    Add("company brand", company, data.m_Brand, "found");
                    continue;
                }
                Entity oldBrand = data.m_Brand;
                data.m_Brand = PickBrand(company, ref data);
                Add("company brand", company, oldBrand, data.m_Brand != Entity.Null ? "reassigned" : "cleared");
                m_EntityManager.SetComponentData(company, data);
                if (m_EntityManager.TryGetComponent(company, out PropertyRenter renter) && renter.m_Property != Entity.Null)
                {
                    Entity e = m_EntityManager.CreateEntity(m_RentEventArchetype);
                    m_EntityManager.SetComponentData(e, new RentersUpdated(renter.m_Property));
                }
            }
        }

        private Entity PickBrand(Entity company, ref CompanyData data)
        {
            if (!m_EntityManager.TryGetComponent(company, out PrefabRef prefabRef)
                || !m_EntityManager.HasBuffer<CompanyBrandElement>(prefabRef.m_Prefab))
                return Entity.Null;
            var candidates = new List<Entity>();
            foreach (CompanyBrandElement element in m_EntityManager.GetBuffer<CompanyBrandElement>(prefabRef.m_Prefab, true))
            {
                if (element.m_Brand != Entity.Null && !IsMissing(element.m_Brand)
                    && !m_EntityManager.HasComponent<Deleted>(element.m_Brand))
                    candidates.Add(element.m_Brand);
            }
            return candidates.Count == 0 ? Entity.Null : candidates[data.m_RandomSeed.NextInt(candidates.Count)];
        }

        // A level-up into a missing building prefab. m_NewPrefab must never be set to
        // Entity.Null (that means "initial construction"); the component is removed
        // instead and the zone level statistics are rebuilt later (see
        // RebuildZoneStatistics). Enqueuing the inverse ZoneBuiltLevelUpdate would be
        // wrong for a level-up loaded from the save: its increment was never applied
        // in this session, so the rollback would subtract twice.
        private void UnderConstructions(bool fix)
        {
            using var buildings = m_UnderConstructionQuery.ToEntityArray(Allocator.Temp);
            foreach (Entity building in buildings)
            {
                UnderConstruction construction = m_EntityManager.GetComponentData<UnderConstruction>(building);
                if (construction.m_NewPrefab == Entity.Null || !IsMissing(construction.m_NewPrefab))
                    continue;
                Add("building level-up", building, construction.m_NewPrefab, fix ? "cancelled" : "found");
                if (!fix)
                    continue;
                m_EntityManager.RemoveComponent<UnderConstruction>(building);
                zoneStatisticsDirty = true;
            }
        }

        /// <summary>True after a level-up was cancelled; <see cref="RebuildZoneStatistics"/> clears it.</summary>
        public bool zoneStatisticsDirty { get; private set; }

        /// <summary>
        /// Asks ZoneBuiltRequirementSystem for the same full pass over all buildings
        /// it does on load (PreDeserialize only completes its own writer, clears the
        /// table and sets the reload flag). Must run on a frame after the repair's
        /// Deleted entities are destroyed: the pass subtracts entities still tagged
        /// Deleted, so running it in the repair frame would undercount.
        /// </summary>
        public void RebuildZoneStatistics()
        {
            if (!zoneStatisticsDirty)
                return;
            zoneStatisticsDirty = false;
            m_ZoneBuiltRequirement.PreDeserialize(default(Context));
        }

        // Only references that are prefabs count (a citizen sender is not one), like
        // FixChirpJob. Deleted chirps are handled by ChirpLinkSystem's own query.
        private void Chirps(bool fix)
        {
            using var chirps = m_ChirpQuery.ToEntityArray(Allocator.Temp);
            var toDelete = new List<Entity>();
            foreach (Entity chirp in chirps)
            {
                Entity broken = BrokenChirpReference(chirp);
                if (broken == Entity.Null)
                    continue;
                Add("chirp", chirp, broken, fix ? "deleted" : "found");
                if (fix)
                    toDelete.Add(chirp);
            }
            Tag<Deleted>(toDelete);
        }

        private Entity BrokenChirpReference(Entity chirp)
        {
            Entity sender = m_EntityManager.GetComponentData<Game.Triggers.Chirp>(chirp).m_Sender;
            if (MissingPrefabDetector.IsPrefabEntity(m_EntityManager, sender) && IsMissing(sender))
                return sender;
            if (m_EntityManager.TryGetBuffer(chirp, true, out DynamicBuffer<ChirpEntity> links))
            {
                foreach (ChirpEntity link in links)
                {
                    if (MissingPrefabDetector.IsPrefabEntity(m_EntityManager, link.m_Entity) && IsMissing(link.m_Entity))
                        return link.m_Entity;
                }
            }
            return Entity.Null;
        }

        // Trees painted on a road (the only SubReplacement type). With the prefab gone,
        // SecondaryObjectSystem keeps re-spawning broken trees on every edge update.
        // Removing the entry restores the road's default trees; Updated on the edge
        // makes them regrow at once, exactly what the tree tool does.
        private void SubReplacements(bool fix)
        {
            List<Entity> edges = PruneBuffers<SubReplacement>(
                m_SubReplacementQuery, ("street trees", "reset"), r => r.m_Prefab, fix);
            edges.RemoveAll(edge => m_EntityManager.HasComponent<Updated>(edge));
            Tag<Updated>(edges);
        }

        private void SystemReferences()
        {
            if (IsMissing(m_CityConfiguration.defaultTheme))
                Add("city theme", Entity.Null, m_CityConfiguration.defaultTheme, kNotFixable);
            else if (IsMissing(m_CityConfiguration.loadedDefaultTheme))
                Add("city theme", Entity.Null, m_CityConfiguration.loadedDefaultTheme, kNotFixable);
            if (IsMissing(m_Climate.currentClimate))
                Add("climate", Entity.Null, m_Climate.currentClimate, kNotFixable);
        }

        private void Tag<T>(List<Entity> entities) where T : unmanaged, IComponentData
        {
            if (entities.Count == 0)
                return;
            using var array = new NativeArray<Entity>(entities.ToArray(), Allocator.Temp);
            m_EntityManager.AddComponent<T>(array);
        }
    }
}
