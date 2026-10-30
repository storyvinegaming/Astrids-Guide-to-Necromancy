// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Ticks down Subjugate Undead's own command duration (in standard DFU magic rounds). On expiry,
// reverts Team back to whatever it was before subjugation (snapshotted at cast time, so this is
// correct regardless of what team a given undead type actually starts on) and removes
// AllyCombatController - the creature survives and simply resumes being a normal hostile undead,
// unlike ReanimatedLifetime's own expiry, which kills the creature. Only ever attached to a vanilla
// hostile undead that did NOT already have a ReanimatedLifetime at the moment Subjugate Undead was
// cast on it - a previously-Reanimated-then-hostile ally instead keeps ticking down on its own
// original ReanimatedLifetime timer, untouched by this component (see SubjugateUndeadEffect.cs).

using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;

namespace VoxsNecromancyMod.AI
{
    public class SubjugationTimer : MonoBehaviour
    {
        public int RoundsRemaining;
        public MobileTeams OriginalTeam;

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

            DaggerfallEntityBehaviour behaviour = GetComponent<DaggerfallEntityBehaviour>();
            EnemyEntity enemyEntity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
            if (enemyEntity != null)
                enemyEntity.Team = OriginalTeam;

            AllyCombatController combatController = GetComponent<AllyCombatController>();
            if (combatController != null)
                Destroy(combatController);

            Destroy(this);
        }
    }
}
