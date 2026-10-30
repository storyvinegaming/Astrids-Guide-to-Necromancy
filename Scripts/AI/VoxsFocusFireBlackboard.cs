// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// A single shared "what is the player fighting right now" value, rather than each ally
// independently re-deriving it. Self-cleaning: once the target dies, is gone, or has itself become
// an ally (Team == PlayerAlly - e.g. Subjugate Undead landed on it, or it was the current focus
// target when it got subjugated), it's forgotten automatically so allies fall back to their own
// senses instead of chasing a stale target. The Team check matters just as much as the health
// check here - without it, AllyCombatController.ClearAllyTargeting() clearing an individual ally's
// Target the moment its target switches sides would get immediately undone by this same class's
// own focus-fire enforcement re-asserting the identical (now-allied) creature on the very next
// LateUpdate, since nothing else here was tracking which side the target was on.
//
// Carries a hardLock flag alongside the target, since the two callers want very different
// strength: PlayerAttackWatcher sets a soft (hardLock: false) suggestion every time the player's
// own attack lands on something - meant as a nudge only, so allies still naturally respond to
// whatever is actually attacking them or the player right now rather than staying welded to
// whatever the player hit a few seconds ago. CommandUndeadEffect sets a hard (hardLock: true)
// target - a deliberate, explicit player command that should override natural target
// re-acquisition entirely (see AllyCombatController.LateUpdate()) until that target dies.

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Entity;

namespace VoxsNecromancyMod.AI
{
    public static class VoxsFocusFireBlackboard
    {
        static DaggerfallEntityBehaviour focusTarget;
        static bool hardLock;

        public static void SetFocusTarget(DaggerfallEntityBehaviour victim, bool hardLock)
        {
            focusTarget = victim;
            VoxsFocusFireBlackboard.hardLock = hardLock;
        }

        // Only meaningful immediately after a GetValidTarget() call that returned non-null -
        // invalidated together with focusTarget below.
        public static bool IsHardLock => hardLock;

        public static DaggerfallEntityBehaviour GetValidTarget()
        {
            if (focusTarget == null || focusTarget.gameObject == null || !focusTarget.gameObject.activeInHierarchy
                || focusTarget.Entity == null || focusTarget.Entity.CurrentHealth <= 0
                || focusTarget.Entity.Team == MobileTeams.PlayerAlly)
            {
                focusTarget = null;
                hardLock = false;
                return null;
            }

            return focusTarget;
        }
    }
}
