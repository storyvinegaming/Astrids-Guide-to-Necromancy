// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Ticks down the Seance ghost's remaining duration on the engine's own global per-round tick, same
// shape as ReanimatedLifetime - but deliberately NOT ReanimatedLifetime itself, and deliberately
// despawns via a plain Destroy() rather than Entity.SetHealth(0):
//
// SetHealth(0) routes through the real OnDeath -> EnemyDeath.CompleteDeath() flow, which fires
// EnemyDeath.OnEnemyDeath - the exact event VoxsNecromancy's own Seance kill-counter subscribes to
// (see VoxsNecromancy.cs). If the ghost's own expiry (or a recast despawning it early) counted as a
// "real" death, it would silently increment the necromancer's own kill count toward their NEXT
// Seance cast - a self-referential bug. A plain Destroy() sidesteps this entirely: no corpse, no
// death animation, but that's fitting for a construct that isn't meant to be "killed" so much as
// dismissed - the shade simply dissipates.

using UnityEngine;
using DaggerfallWorkshop.Game.MagicAndEffects;

namespace VoxsNecromancyMod.AI
{
    public class SeanceGhostLifetime : MonoBehaviour
    {
        public int RoundsRemaining;

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
            if (RoundsRemaining <= 0)
                GameObject.Destroy(gameObject);
        }
    }
}
