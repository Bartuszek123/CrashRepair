using Colossal.Serialization.Entities;
using Game.Serialization;
using UnityEngine.Scripting;

namespace CrashRepair
{
    /// <summary>
    /// Runs in the Deserialize phase, once per savegame load, after all vanilla
    /// systems of that phase (mod systems register last, so prefab references
    /// are already resolved). Always scans and reports; deletes the broken
    /// instances only when the "repair automatically" setting is enabled.
    /// </summary>
    public partial class AutoRepairSystem : RepairSystemBase
    {
        private LoadGameSystem m_LoadGameSystem;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_LoadGameSystem = World.GetOrCreateSystemManaged<LoadGameSystem>();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (m_LoadGameSystem.context.purpose != Purpose.LoadGame)
                return;
            RunRepair(Mod.settings != null && Mod.settings.AutoRepairOnLoad);
        }
    }
}
