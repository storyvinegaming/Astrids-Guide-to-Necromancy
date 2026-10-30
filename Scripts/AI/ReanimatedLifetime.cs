// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Ticks down a reanimated creature's remaining duration (in standard DFU magic rounds - the same
// unit every other duration-based spell effect uses) on the engine's own global per-round tick,
// rather than depending on the caster's spell-effect instance surviving (which it may not, across
// serialization or dispel). When duration runs out, kills the creature via the same SetHealth(0)
// primitive the engine itself uses elsewhere (WeaponManager) - this routes through the identical
// OnDeath -> EnemyDeath.CompleteDeath() flow as a normal combat death, so it's just as "real" a
// death, not a special-cased despawn.
//
// Also doubles as the marker + metadata holder used to identify "this is one of ours" and to
// snapshot/restore remaining duration across scene transitions (VoxsNecromancy.cs), mirroring how
// UndeadMinion served the same dual marker+data role in the Cheb's Necromancy fork.

using UnityEngine;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;

namespace VoxsNecromancyMod.AI
{
    public class ReanimatedLifetime : MonoBehaviour
    {
        public int RoundsRemaining;
        public MobileTypes FoeType;
        public bool Allied;
        public int Magnitude;
        public int MysticismLevel;
        public int Intelligence;
        public int Willpower;

        // Fixed at creation (VoxsNecromancy.CalculateMagickaUpkeep) - carried here purely so it
        // survives scene transitions/save-load alongside the rest of this component's snapshot data.
        // AllyCombatController holds its own copy for the actual budget accounting
        // (VoxsNecromancy.CanAffordUpkeep) - this field is just persisted metadata.
        public int MagickaUpkeep;

        void OnEnable()
        {
            EntityEffectBroker.OnNewMagicRound += Tick;
        }

        void OnDisable()
        {
            EntityEffectBroker.OnNewMagicRound -= Tick;
        }

        void Tick()
        {
            RoundsRemaining--;
            if (RoundsRemaining > 0)
                return;

            // Countdown still runs normally either way (so nothing else reading RoundsRemaining
            // breaks), but the "Permanent Undead Summons" setting skips the actual expiry kill -
            // the creature just keeps going until it dies in combat or is dismissed instead.
            if (VoxsNecromancy.PermanentUndeadSummons)
                return;

            DaggerfallEntityBehaviour behaviour = GetComponent<DaggerfallEntityBehaviour>();
            if (behaviour != null && behaviour.Entity != null && behaviour.Entity.CurrentHealth > 0)
                behaviour.Entity.SetHealth(0);
        }
    }
}
