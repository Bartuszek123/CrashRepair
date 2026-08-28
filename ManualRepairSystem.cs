using Game;
using Game.SceneFlow;
using Game.Tools;
using UnityEngine.Scripting;

namespace CrashRepair
{
    /// <summary>
    /// Runs every repair that changes the world, in a running game. Registered in
    /// the PreTool phase: the ToolSystem runs it before PostTool, where the game's
    /// SubElementDeleteSystem cascades a deleted owner's sub-nets, lots, routes and
    /// vehicles, and before the Modification phases that clean every other
    /// owner-side reference, so a Deleted tag set here travels the same frame as
    /// a bulldozed object's. Triggered by the "Repair now" button, or scheduled by
    /// <see cref="AutoRepairSystem"/> a few frames after loading, once the net
    /// search tree has been rebuilt. Disabled until scheduled; disables itself
    /// again afterwards.
    /// </summary>
    public partial class ManualRepairSystem : RepairSystemBase
    {
        private RepairMode m_Mode;
        private int m_Countdown;
        private bool m_FinishPending;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            Enabled = false;
        }

        /// <summary>Runs <paramref name="mode"/> after <paramref name="delayFrames"/> frames.</summary>
        public void Schedule(RepairMode mode, int delayFrames)
        {
            // A follow-up still owed by the previous run must not be lost.
            if (m_FinishPending)
                FinishRepair();
            m_Mode = mode;
            m_Countdown = delayFrames;
            m_FinishPending = false;
            Enabled = true;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            // A run scheduled at load waits for the loading screen to end; the frame
            // countdown starts only then.
            if (GameManager.instance.isGameLoading)
                return;
            if (!GameManager.instance.gameMode.IsGame())
            {
                Enabled = false;
                return;
            }
            if (m_FinishPending)
            {
                // The entities deleted last frame are gone now, so the statistics
                // rebuilt here count only what remains.
                FinishRepair();
                m_FinishPending = false;
                Enabled = false;
                return;
            }
            if (m_Countdown-- > 0)
                return;
            // A road or object tool still holds Temp copies of the originals from the
            // previous frame; switching to the default tool makes ToolSystem discard
            // them this frame, before anything is deleted.
            var toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            toolSystem.activeTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            m_FinishPending = RunRepair(m_Mode);
            if (!m_FinishPending)
                Enabled = false;
        }
    }
}
