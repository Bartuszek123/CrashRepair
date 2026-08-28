using Colossal.Serialization.Entities;
using Game.Serialization;
using UnityEngine.Scripting;

namespace CrashRepair
{
    /// <summary>
    /// Scans once per savegame load. Registered with UpdateAfter so it is the last
    /// system of the Deserialize phase: after the DeserializationBarrier has played
    /// and every vanilla PostDeserialize has run, the world is in its final loaded
    /// state. It never deletes anything itself: with the "repair automatically"
    /// setting on, it schedules the in-game repair for a couple of frames later,
    /// when the net search tree has been rebuilt (Game.Net.SearchSystem does its
    /// full pass in Modification5 of the first frame) and a Deleted tag travels
    /// the same frame path as a bulldozed object's.
    /// </summary>
    public partial class AutoRepairSystem : RepairSystemBase
    {
        private const int kRepairDelayFrames = 2;

        private LoadGameSystem m_LoadGameSystem;
        private ManualRepairSystem m_ManualRepairSystem;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_LoadGameSystem = World.GetOrCreateSystemManaged<LoadGameSystem>();
            m_ManualRepairSystem = World.GetOrCreateSystemManaged<ManualRepairSystem>();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (m_LoadGameSystem.context.purpose != Purpose.LoadGame)
                return;
            RunRepair(RepairMode.Scan);
            if (Mod.settings != null && Mod.settings.AutoRepairOnLoad)
                m_ManualRepairSystem.Schedule(Mod.settings.AdvancedCleanup ? RepairMode.RepairAdvanced : RepairMode.Repair, kRepairDelayFrames);
        }
    }
}
