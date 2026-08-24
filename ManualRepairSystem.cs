using UnityEngine.Scripting;

namespace CrashRepair
{
    /// <summary>
    /// Runs the repair on demand, triggered by the "Repair now" button in the
    /// options UI. Registered at the start of the modification phase, so the
    /// deleted entities are cleaned up by the regular in-game pipeline within
    /// the same frame — the same way bulldozing works. Disabled until the
    /// button requests a run; disables itself again afterwards.
    /// </summary>
    public partial class ManualRepairSystem : RepairSystemBase
    {
        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            Enabled = false;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            Enabled = false;
            RunRepair(deleteBroken: true);
        }
    }
}
