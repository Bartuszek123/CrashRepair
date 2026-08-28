using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Colossal.Entities;
using Colossal.PSI.Environment;
using Game;
using Game.Buildings;
using Game.City;
using Game.Common;
using Game.Modding;
using Game.Net;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;

namespace CrashRepair
{
    /// <summary>
    /// Shared engine for the scan-on-load and in-game repair systems: finds every
    /// instance entity whose PrefabRef points at a prefab that no longer exists
    /// (mod/asset removed), reports the findings (log + CSV in ModsData/CrashRepair/)
    /// and, when repairing, deletes them by tagging Deleted from the PreTool phase,
    /// so the game's own cleanup handles them exactly like a bulldozed object:
    /// SubElementDeleteSystem cascades sub-nets/lots/routes/vehicles, the
    /// *ReferencesSystem family removes owner-side references, CleanUpSystem
    /// destroys the entities at the end of the frame. Two gaps of that pipeline are
    /// filled here: route children (<see cref="CascadeRouteChildren"/>) and the road
    /// network (<see cref="DeleteNet"/>). Also tidies the save's "mods used" list
    /// (CityConfigurationSystem.usedMods) and, through <see cref="SecondaryReferences"/>,
    /// reports or repairs the references to missing prefabs that live outside PrefabRef.
    /// </summary>
    public abstract partial class RepairSystemBase : GameSystemBase
    {
        private struct MissingGroup
        {
            public int m_Count;
            public Entity m_Sample;
        }

        public enum RepairMode
        {
            /// <summary>Report only (the load-time scan).</summary>
            Scan,
            /// <summary>Delete broken objects and repair the road network.</summary>
            Repair,
            /// <summary>Repair plus the secondary references.</summary>
            RepairAdvanced
        }

        // Node archetype tags ApplyNetSystem syncs when a node changes prefab.
        private static readonly ComponentType[] kNodeTags =
        {
            ComponentType.ReadWrite<Road>(),
            ComponentType.ReadWrite<TramTrack>(),
            ComponentType.ReadWrite<TrainTrack>(),
            ComponentType.ReadWrite<Waterway>(),
            ComponentType.ReadWrite<LandValue>(),
            ComponentType.ReadWrite<Game.Net.Pollution>(),
            ComponentType.ReadWrite<Marker>(),
            ComponentType.ReadWrite<TrafficLights>(),
            ComponentType.ReadWrite<Orphan>(),
            ComponentType.ReadWrite<Native>(),
            ComponentType.ReadWrite<Standalone>(),
            ComponentType.ReadWrite<LocalConnect>(),
            ComponentType.ReadWrite<Game.Tools.EditorContainer>(),
        };

        private PrefabSystem m_PrefabSystem;
        private CityConfigurationSystem m_CityConfigurationSystem;
        private SecondaryReferences m_Secondary;
        private EntityQuery m_PrefabRefQuery;
        private EntityQuery m_BuildingQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_CityConfigurationSystem = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_Secondary = new SecondaryReferences(World, m_PrefabSystem, m_CityConfigurationSystem);
            // Excludes mirror vanilla PrimaryPrefabReferencesSystem: composition,
            // effect-instance and live-path entities are runtime-derived state
            // whose prefab references the game repairs through other channels;
            // deleting them would damage healthy roads and effects.
            m_PrefabRefQuery = GetEntityQuery(
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<NetCompositionData>(),
                ComponentType.Exclude<Game.Effects.EffectInstance>(),
                ComponentType.Exclude<Game.Routes.LivePath>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());
            m_BuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());
        }

        /// <summary>
        /// Scans all prefab-referencing instances and repairs as far as
        /// <paramref name="mode"/> allows. Returns true when <see cref="FinishRepair"/>
        /// must be called on a later frame (after the deleted entities are gone).
        /// </summary>
        protected bool RunRepair(RepairMode mode)
        {
            try
            {
                return RunRepairUnguarded(mode);
            }
            catch (System.Exception ex)
            {
                Mod.log.Error($"Repair failed: {ex}");
                Mod.lastScanLines = new[] { "Repair failed, see Logs/CrashRepair.log." };
                return false;
            }
        }

        /// <summary>Second step of a repair: work that must see the deleted entities gone.</summary>
        protected void FinishRepair()
        {
            try
            {
                m_Secondary.RebuildZoneStatistics();
                Mod.log.Info("Zone statistics rebuilt (cancelled level-ups no longer counted)");
            }
            catch (System.Exception ex)
            {
                Mod.log.Error($"Repair follow-up failed: {ex}");
            }
        }

        // Per-run detail log (one line per repaired road, junction, building, …),
        // capped so a huge save cannot flood the log; the CSVs stay complete.
        private const int kMaxDetailLines = 500;
        private int m_DetailLines;
        private int m_RouteChildren, m_LayoutMembers, m_LaneOwners;

        private void Detail(string message)
        {
            if (m_DetailLines < kMaxDetailLines)
                Mod.log.Info("  " + message);
            else if (m_DetailLines == kMaxDetailLines)
                Mod.log.Info($"  … further details omitted after {kMaxDetailLines} lines (see the CSV reports)");
            m_DetailLines++;
        }

        private bool RunRepairUnguarded(RepairMode mode)
        {
            bool deleteBroken = mode != RepairMode.Scan;
            bool fixSecondary = mode == RepairMode.RepairAdvanced;
            m_DetailLines = 0;
            m_RouteChildren = m_LayoutMembers = m_LaneOwners = 0;
            Mod.log.Info($"Run started: mode={mode}, frame={UnityEngine.Time.frameCount}");

            // Buffers and components are written directly from the main thread;
            // the in-game run happens mid-frame among simulation jobs.
            EntityManager.CompleteAllTrackedJobs();

            // A save references few unique prefabs across many instances, so the
            // verdict is cached per prefab entity instead of re-queried per instance.
            // The same cache feeds the secondary scan.
            var verdicts = new Dictionary<Entity, bool>();
            var missing = new Dictionary<Entity, MissingGroup>();
            var deleteSet = new HashSet<Entity>();
            var brokenEdges = new List<Entity>();
            var brokenNodes = new HashSet<Entity>();
            int occupiedBuildings = 0;
            var updated = new HashSet<Entity>();
            var toDelete = new NativeList<Entity>(Allocator.Temp);
            try
            {
                using var entities = m_PrefabRefQuery.ToEntityArray(Allocator.Temp);
                using var prefabRefs = m_PrefabRefQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity prefab = prefabRefs[i].m_Prefab;
                    // A null PrefabRef is legitimate (RequiredComponentSystem gives old
                    // citizens one); vanilla FixPrefabRefJob skips it too.
                    if (prefab == Entity.Null)
                        continue;
                    if (!IsMissing(prefab, verdicts))
                    {
                        // An editor container (Extra Assets Importer's net lane decals and
                        // the like) is a healthy container edge holding one contained prefab;
                        // with that prefab gone it is an empty shell and goes the way of a
                        // broken edge.
                        if (!EntityManager.TryGetComponent(entities[i], out Game.Tools.EditorContainer container)
                            || container.m_Prefab == Entity.Null || !IsMissing(container.m_Prefab, verdicts))
                            continue;
                        prefab = container.m_Prefab;
                    }

                    missing.TryGetValue(prefab, out MissingGroup group);
                    if (group.m_Count == 0)
                        group.m_Sample = entities[i];
                    group.m_Count++;
                    missing[prefab] = group;

                    Entity entity = entities[i];
                    if (EntityManager.HasComponent<Edge>(entity))
                        brokenEdges.Add(entity);
                    else if (EntityManager.HasComponent<Node>(entity))
                        brokenNodes.Add(entity);
                    else
                    {
                        if (EntityManager.TryGetBuffer(entity, true, out DynamicBuffer<Renter> renters) && renters.Length > 0)
                            occupiedBuildings++;
                        if (deleteBroken)
                            QueueDelete(entity, deleteSet, toDelete, updated);
                    }
                }

                string netStatus = deleteBroken
                    ? DeleteNet(brokenEdges, brokenNodes, deleteSet, toDelete, updated)
                    : ReportNet(brokenEdges.Count + brokenNodes.Count, missing);
                m_Secondary.Run(deleteBroken, fixSecondary, verdicts);

                var status = new List<string>
                {
                    Report(entities.Length, missing, deleteBroken),
                    occupiedBuildings == 0 ? string.Empty
                        : $"{occupiedBuildings} of them are buildings with residents or companies inside ({(deleteBroken ? "demolished, occupants move out" : "they would be demolished")}).",
                    netStatus,
                    CleanModList(deleteBroken)
                };
                status.AddRange(ReportSecondary(fixSecondary));
                if (mode == RepairMode.Scan)
                {
                    // Only valid right after loading: placeholders whose instances a
                    // previous in-game repair deleted linger until the next load.
                    status.Add(ReportUnhandled());
                    status.AddRange(ReportPackStates());
                }
                status.Add("Details: ModsData/CrashRepair/*.csv and Logs/CrashRepair.log.");
                Mod.lastScanLines = status.Where(s => !string.IsNullOrEmpty(s)).ToArray();
                foreach (string line in Mod.lastScanLines)
                    Mod.log.Info($"Status: {line}");

                updated.RemoveWhere(e => deleteSet.Contains(e) || EntityManager.HasComponent<Updated>(e));
                if (deleteBroken)
                    Mod.log.Info($"Cascade: {m_RouteChildren} route waypoints/segments, {m_LayoutMembers} vehicle layout members deleted with their owner; {m_LaneOwners} lane owners updated. Tagging {toDelete.Length} entities Deleted and {updated.Count} Updated.");
                if (updated.Count > 0)
                {
                    using var array = new NativeArray<Entity>(updated.ToArray(), Allocator.Temp);
                    EntityManager.AddComponent<Updated>(array);
                }
                if (toDelete.Length > 0)
                    EntityManager.AddComponent<Deleted>(toDelete.AsArray());
                return m_Secondary.zoneStatisticsDirty;
            }
            finally
            {
                toDelete.Dispose();
            }
        }

        private bool IsMissing(Entity prefab, Dictionary<Entity, bool> verdicts)
        {
            if (!verdicts.TryGetValue(prefab, out bool missing))
            {
                missing = MissingPrefabDetector.IsMissing(EntityManager, m_PrefabSystem, prefab);
                verdicts[prefab] = missing;
            }
            return missing;
        }

        private void QueueDelete(Entity entity, HashSet<Entity> seen, NativeList<Entity> toDelete, HashSet<Entity> updated)
        {
            if (!seen.Add(entity))
                return;
            toDelete.Add(entity);
            CascadeRouteChildren(entity, seen, toDelete);
            CascadeVehicleLayout(entity, seen, toDelete);
            // LaneReferencesSystem drops a deleted lane from its owner's SubLane buffer
            // but lanes are only regenerated for Updated owners.
            if (EntityManager.HasComponent<Lane>(entity)
                && EntityManager.TryGetComponent(entity, out Owner owner) && owner.m_Owner != Entity.Null
                && updated.Add(owner.m_Owner))
                m_LaneOwners++;
        }

        /// <summary>
        /// VehicleUtils.DeleteVehicle deletes every member of a vehicle layout (tractor
        /// plus trailers) together; Game.Vehicles.ReferencesSystem alone would leave the
        /// trailers of a deleted tractor standing without a controller.
        /// </summary>
        private void CascadeVehicleLayout(Entity vehicle, HashSet<Entity> seen, NativeList<Entity> toDelete)
        {
            Entity controller = vehicle;
            if (EntityManager.TryGetComponent(vehicle, out Controller c) && c.m_Controller != Entity.Null)
                controller = c.m_Controller;
            if (!EntityManager.TryGetBuffer(controller, true, out DynamicBuffer<LayoutElement> layout))
                return;
            foreach (LayoutElement element in layout)
            {
                if (QueueChild(element.m_Vehicle, seen, toDelete))
                    m_LayoutMembers++;
            }
            if (QueueChild(controller, seen, toDelete))
                m_LayoutMembers++;
            Detail($"Vehicle {vehicle.Index} belongs to a {layout.Length}-part layout (controller {controller.Index}), deleted together");
        }

        /// <summary>
        /// Known gap in vanilla's reactive cleanup: SubElementDeleteSystem cascades
        /// SubArea/SubNet/SubRoute/OwnedVehicle, but a route's waypoints and segments
        /// are only deleted explicitly by ApplyRoutesSystem. Add one such method per
        /// gap found.
        /// </summary>
        private void CascadeRouteChildren(Entity route, HashSet<Entity> seen, NativeList<Entity> toDelete)
        {
            int children = 0;
            if (EntityManager.TryGetBuffer(route, true, out DynamicBuffer<RouteWaypoint> waypoints))
            {
                foreach (RouteWaypoint waypoint in waypoints)
                {
                    if (QueueChild(waypoint.m_Waypoint, seen, toDelete))
                        children++;
                }
            }
            if (EntityManager.TryGetBuffer(route, true, out DynamicBuffer<RouteSegment> segments))
            {
                foreach (RouteSegment segment in segments)
                {
                    if (QueueChild(segment.m_Segment, seen, toDelete))
                        children++;
                }
            }
            if (children > 0)
            {
                m_RouteChildren += children;
                Detail($"Route {route.Index}: {children} waypoints/segments deleted with it");
            }
        }

        private bool QueueChild(Entity child, HashSet<Entity> seen, NativeList<Entity> toDelete)
        {
            if (child == Entity.Null || EntityManager.HasComponent<Deleted>(child) || !seen.Add(child))
                return false;
            toDelete.Add(child);
            return true;
        }

        private string ReportNet(int count, Dictionary<Entity, MissingGroup> missing)
        {
            if (count == 0)
                return string.Empty;
            List<Entity> roadTypes = missing.Keys.Where(p => EntityManager.HasComponent<NetData>(p)).ToList();
            Mod.log.Warn($"Road network: {count} segments or junctions of {roadTypes.Count} missing road types, left alone during loading (Repair now handles them): {string.Join(" | ", roadTypes.Select(DescribePrefab))}");
            return roadTypes.Count == 0
                ? $"{count} of them are road decal containers (or their junctions) whose decal is missing."
                : $"{count} of them are road segments or junctions of {roadTypes.Count} missing road types.";
        }

        /// <summary>
        /// Road network the way the bulldozer does it (ApplyNetSystem): a broken
        /// edge is deleted and its end nodes plus their remaining edges are updated;
        /// a node is deleted only once no live edge connects to it. Net.ReferencesSystem
        /// cleans a deleted edge out of its nodes, but nothing cleans a deleted node
        /// out of its edges, so destroying a junction that healthy roads still use
        /// leaves dangling Edge.m_Start/m_End and crashes the game. A broken node
        /// that keeps live edges therefore takes the prefab of the connected edge
        /// with the highest node priority (the edge that would define the junction
        /// in vanilla) and is updated instead of deleted. Returns the status
        /// sentence, or empty.
        /// </summary>
        private string DeleteNet(List<Entity> brokenEdges, HashSet<Entity> brokenNodes,
            HashSet<Entity> deleteSet, NativeList<Entity> toDelete, HashSet<Entity> updated)
        {
            if (brokenEdges.Count == 0 && brokenNodes.Count == 0)
                return string.Empty;

            var touchedNodes = new HashSet<Entity>(brokenNodes);
            int containers = 0;
            foreach (Entity edge in brokenEdges)
            {
                QueueDelete(edge, deleteSet, toDelete, updated);
                bool container = EntityManager.HasComponent<Game.Tools.EditorContainer>(edge);
                if (container)
                    containers++;
                Edge ends = EntityManager.GetComponentData<Edge>(edge);
                touchedNodes.Add(ends.m_Start);
                touchedNodes.Add(ends.m_End);
                Detail($"{(container ? "Container" : "Segment")} {edge.Index} ({PrefabOf(edge)}) deleted, nodes {ends.m_Start.Index} and {ends.m_End.Index}");
            }
            int reconnected = ReconnectBuildings(deleteSet);

            int reassigned = 0, orphans = 0;
            foreach (Entity node in touchedNodes)
            {
                if (node == Entity.Null || deleteSet.Contains(node) || !EntityManager.Exists(node)
                    || EntityManager.HasComponent<Deleted>(node))
                    continue;
                Entity donor = BestLiveEdge(node, deleteSet, updated);
                if (donor == Entity.Null)
                {
                    QueueDelete(node, deleteSet, toDelete, updated);
                    orphans++;
                    Detail($"Junction {node.Index} has no remaining road, deleted");
                    continue;
                }
                if (brokenNodes.Contains(node))
                {
                    Entity donorPrefab = EntityManager.GetComponentData<PrefabRef>(donor).m_Prefab;
                    RetypeNode(node, donorPrefab);
                    reassigned++;
                    Detail($"Junction {node.Index} switched to {DescribePrefab(donorPrefab)} (from remaining segment {donor.Index})");
                }
                else
                    Detail($"Junction {node.Index} kept, updated (remaining segment {donor.Index})");
                updated.Add(node);
            }

            var parts = new List<string> { $"{brokenEdges.Count - containers} road segments deleted" };
            if (containers > 0)
                parts.Add($"{containers} empty decal containers deleted");
            if (orphans > 0)
                parts.Add($"{orphans} orphaned junctions deleted");
            if (reassigned > 0)
                parts.Add($"{reassigned} junctions switched to a remaining road type");
            if (reconnected > 0)
                parts.Add($"{reconnected} buildings on those roads will look for a new road");
            string message = "Road network: " + string.Join(", ", parts) + ".";
            Mod.log.Warn(message);
            return message;
        }

        /// <summary>
        /// Gives the node the donor's prefab and syncs the node archetype tags exactly
        /// as ApplyNetSystem does when a node changes prefab (Road, TramTrack, LandValue,
        /// …): the tags are serialized, so a stale set would survive every reload.
        /// </summary>
        private void RetypeNode(Entity node, Entity prefab)
        {
            EntityManager.SetComponentData(node, new PrefabRef(prefab));
            if (!EntityManager.TryGetComponent(prefab, out NetData netData))
                return;
            using var archetypeTypes = netData.m_NodeArchetype.GetComponentTypes(Allocator.Temp);
            foreach (ComponentType tag in kNodeTags)
            {
                bool wanted = archetypeTypes.Contains(tag);
                bool present = EntityManager.HasComponent(node, tag);
                if (wanted && !present)
                    EntityManager.AddComponent(node, tag);
                else if (!wanted && present)
                    EntityManager.RemoveComponent(node, tag);
            }
        }

        /// <summary>
        /// RoadConnectionSystem finds a new road for the buildings of a deleted edge
        /// through the edge's ConnectedBuilding buffer only. A building that points at
        /// the edge (Building.m_RoadEdge) but is missing from that buffer (the kind of
        /// inconsistency these saves have) would keep a dangling reference, so it is
        /// registered before the edge is tagged. Returns how many buildings depend on
        /// the deleted edges.
        /// </summary>
        private int ReconnectBuildings(HashSet<Entity> deleteSet)
        {
            int count = 0;
            using var buildings = m_BuildingQuery.ToEntityArray(Allocator.Temp);
            using var data = m_BuildingQuery.ToComponentDataArray<Building>(Allocator.Temp);
            for (int i = 0; i < buildings.Length; i++)
            {
                Entity edge = data[i].m_RoadEdge;
                if (edge == Entity.Null || !deleteSet.Contains(edge))
                    continue;
                count++;
                // RoadConnectionSystem's deleted-edge query requires the buffer itself.
                if (!EntityManager.TryGetBuffer(edge, false, out DynamicBuffer<ConnectedBuilding> connected))
                    connected = EntityManager.AddBuffer<ConnectedBuilding>(edge);
                bool listed = false;
                foreach (ConnectedBuilding entry in connected)
                    listed |= entry.m_Building == buildings[i];
                if (!listed)
                    connected.Add(new ConnectedBuilding(buildings[i]));
                Detail($"Building {buildings[i].Index} ({PrefabOf(buildings[i])}) fronts deleted segment {edge.Index}{(listed ? string.Empty : ", registered with it")}; the game will look for a new road");
            }
            return count;
        }

        /// <summary>
        /// The live edge ending at <paramref name="node"/> whose prefab has the highest
        /// NetData.m_NodePriority (GenerateNodesSystem.FindNodePrefab: edges that merely
        /// pass through the node as a middle connection do not count), or Entity.Null
        /// when none remains. Every live end edge is added to <paramref name="updated"/>
        /// so its geometry at the node rebuilds.
        /// </summary>
        private Entity BestLiveEdge(Entity node, HashSet<Entity> deleteSet, HashSet<Entity> updated)
        {
            Entity best = Entity.Null;
            float bestPriority = float.NegativeInfinity;
            if (!EntityManager.TryGetBuffer(node, true, out DynamicBuffer<ConnectedEdge> edges))
                return best;
            foreach (ConnectedEdge connected in edges)
            {
                Entity edge = connected.m_Edge;
                if (edge == Entity.Null || deleteSet.Contains(edge) || !EntityManager.Exists(edge)
                    || EntityManager.HasComponent<Deleted>(edge)
                    || !EntityManager.TryGetComponent(edge, out Edge ends)
                    || (ends.m_Start != node && ends.m_End != node))
                    continue;
                updated.Add(edge);
                float priority = float.NegativeInfinity;
                if (EntityManager.TryGetComponent(edge, out PrefabRef prefabRef)
                    && EntityManager.TryGetComponent(prefabRef.m_Prefab, out NetData netData))
                    priority = netData.m_NodePriority;
                if (best == Entity.Null || priority > bestPriority)
                {
                    best = edge;
                    bestPriority = priority;
                }
            }
            return best;
        }

        /// <summary>
        /// Finds entries of the save's mod list that no loaded mod matches and
        /// removes them when repairing. Returns a status sentence, or empty.
        /// </summary>
        private string CleanModList(bool remove)
        {
            // An empty list can only mean the mod manager is not fully up (this
            // mod itself is always in it): never prune against that.
            string[] enabled = ModManager.GetModsEnabled();
            if (enabled == null || enabled.Length == 0)
                return string.Empty;

            var stale = m_CityConfigurationSystem.usedMods.Except(enabled).ToArray();
            if (stale.Length == 0)
                return string.Empty;

            // Entries are assembly full names (with version), so every mod update
            // leaves an outdated entry behind; most of these are old versions of
            // mods that are still installed, not missing mods.
            Mod.log.Warn($"Save's mod list has {stale.Length} stale entries ({(remove ? "removed" : "kept")}): {string.Join(" | ", stale)}");
            if (remove)
                m_CityConfigurationSystem.usedMods.ExceptWith(stale);
            return remove
                ? $"Tidied {stale.Length} stale entries from the save's internal mod list."
                : $"The save's internal mod list has {stale.Length} stale entries.";
        }

        /// <summary>Logs the scan outcome, writes the CSV; returns the status sentence.</summary>
        private string Report(int scanned, Dictionary<Entity, MissingGroup> missing, bool deleting)
        {
            if (missing.Count == 0)
            {
                Mod.log.Info($"Scan done: {scanned} instances checked, no instance references a missing prefab.");
                return $"Last scan: {scanned:N0} objects checked — no broken objects found.";
            }

            int total = missing.Values.Sum(g => g.m_Count);
            string action = deleting ? "deleted" : "found (not deleted)";
            Mod.log.Warn($"Scan done: {scanned} instances checked, {total} instances reference {missing.Count} missing prefabs ({action}):");

            const int kLoggedPrefabs = 200;
            int logged = 0;
            var csv = new StringBuilder();
            csv.AppendLine("prefab_id;instance_count;sample_entity;sample_components");
            foreach (var pair in missing.OrderByDescending(p => p.Value.m_Count))
            {
                string id = DescribePrefab(pair.Key);
                string components = DescribeComponents(pair.Value.m_Sample);
                if (logged++ < kLoggedPrefabs)
                    Mod.log.Warn($"  {id}: {pair.Value.m_Count} instances (sample {pair.Value.m_Sample.Index}, components: {components})");
                csv.AppendLine($"{id};{pair.Value.m_Count};{pair.Value.m_Sample.Index}:{pair.Value.m_Sample.Version};{components}");
            }
            if (logged > kLoggedPrefabs)
                Mod.log.Warn($"  … and {logged - kLoggedPrefabs} more (see the CSV)");
            WriteCsv("missing_prefabs_report.csv", csv);
            return $"Last scan: {scanned:N0} objects checked — {total:N0} broken objects ({missing.Count} missing assets) {action}.";
        }

        /// <summary>Logs a summary and writes the secondary reference rows to CSV; returns one status line per outcome.</summary>
        private List<string> ReportSecondary(bool fixedThem)
        {
            var parts = new List<string>();
            if (m_Secondary.rows.Count == 0 && m_Secondary.failedKinds.Count == 0)
                return parts;

            Mod.log.Warn($"Secondary references to missing prefabs: {m_Secondary.Summary(SecondaryReferences.Outcome.All)}");
            var csv = new StringBuilder();
            csv.AppendLine("kind;holder_entity;prefab_id;action");
            foreach (SecondaryReferences.Row row in m_Secondary.rows)
            {
                string prefab = DescribePrefab(row.m_Prefab);
                csv.AppendLine($"{row.m_Kind};{row.m_Holder.Index}:{row.m_Holder.Version};{prefab};{row.m_Action}");
                Detail($"{row.m_Kind} on {row.m_Holder.Index} → {prefab}: {row.m_Action}");
            }
            foreach (string kind in m_Secondary.failedKinds)
                Mod.log.Warn($"Secondary reference kind '{kind}' failed, see the error above");
            WriteCsv("secondary_references_report.csv", csv);

            string repaired = m_Secondary.Summary(SecondaryReferences.Outcome.Repaired);
            if (repaired.Length > 0)
                parts.Add($"Also repaired: {repaired}.");
            string pending = m_Secondary.Summary(SecondaryReferences.Outcome.Pending);
            if (pending.Length > 0)
            {
                parts.Add(fixedThem
                    ? $"Still pointing at missing assets: {pending}."
                    : $"Other references to missing assets: {pending} ({(m_Secondary.pendingNeedsAdvanced ? "Repair now with Advanced cleanup" : "Repair now")} handles these).");
            }
            string unfixable = m_Secondary.Summary(SecondaryReferences.Outcome.NotFixable);
            if (unfixable.Length > 0)
                parts.Add($"Cannot be repaired: {unfixable} — the load menu warning stays.");
            return parts;
        }

        /// <summary>
        /// Tells the player when the "missing" content is merely disabled in the
        /// playset, or when it was dropped from a pack that is still installed.
        /// </summary>
        private List<string> ReportPackStates()
        {
            var lines = new List<string>();
            m_Secondary.PackStates(out List<string> disabled, out List<string> trimmed);
            if (disabled.Count > 0)
            {
                Mod.log.Warn($"Missing content belongs to subscribed but disabled packs: {string.Join(", ", disabled)}");
                lines.Add($"{disabled.Count} of the missing packs are subscribed but disabled in the current playset (ids {string.Join(", ", disabled)}) — enable them instead if you want to keep their content.");
            }
            if (trimmed.Count > 0)
            {
                Mod.log.Warn($"Missing assets were removed from packs that are still installed and enabled: {string.Join(", ", trimmed)}");
                lines.Add($"Some missing assets belong to packs you still have enabled (ids {string.Join(", ", trimmed)}) — the pack author removed or renamed them; nothing to subscribe to.");
            }
            return lines;
        }

        /// <summary>Names the placeholders nothing here accounts for; says so instead of promising a clean save.</summary>
        private string ReportUnhandled()
        {
            List<Entity> unhandled = m_Secondary.Unhandled();
            if (unhandled.Count == 0)
                return string.Empty;

            string names = string.Join(" | ", unhandled.Select(DescribePrefab));
            Mod.log.Warn($"{unhandled.Count} missing prefabs are referenced by data this mod does not handle: {names}");
            return $"{unhandled.Count} missing assets are referenced by data this mod does not handle (see log) — the load menu warning may stay.";
        }

        private void WriteCsv(string fileName, StringBuilder csv)
        {
            try
            {
                string dir = Path.Combine(EnvPath.kUserDataPath, "ModsData", nameof(CrashRepair));
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, fileName);
                File.WriteAllText(path, csv.ToString());
                Mod.log.Info($"CSV report written to {path}");
            }
            catch (System.Exception ex)
            {
                Mod.log.Warn($"Failed to write {fileName}: {ex.Message}");
            }
        }

        private string PrefabOf(Entity entity) =>
            EntityManager.TryGetComponent(entity, out PrefabRef prefabRef) ? DescribePrefab(prefabRef.m_Prefab) : "?";

        private string DescribePrefab(Entity prefab)
        {
            // GetObsoleteID keeps the "Type:Name" form that identifies which missing
            // asset the instance came from; it throws for anything that is not a
            // placeholder, and GetPrefabName covers those.
            if (!MissingPrefabDetector.IsPrefabEntity(EntityManager, prefab))
                return prefab.ToString();
            try
            {
                return m_PrefabSystem.GetObsoleteID(prefab).ToString();
            }
            catch (System.Exception)
            {
                try
                {
                    return m_PrefabSystem.GetPrefabName(prefab);
                }
                catch (System.Exception)
                {
                    return prefab.ToString();
                }
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
