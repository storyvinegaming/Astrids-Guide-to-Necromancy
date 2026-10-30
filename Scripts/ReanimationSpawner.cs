// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Shared spawn logic used by all nine Reanimate effects: consume the corpse (transfer its items
// to a fresh dropped-loot pile so nothing is lost, then destroy it - see ConvertCorpseToLootPile
// for why this must be a real Destroy(), not just SetActive(false)), spawn the chosen undead type
// via the same GameObjectHelper.CreateFoeGameObjects() entry point DFU's own Sanguine Rose artifact
// uses for its ally, suppress its loot table (see ReanimatedLifetime.cs for why clearing Items once
// at spawn is sufficient for both combat death and duration expiry), and attach the AI/lifetime
// components appropriate to whether the reanimation came out allied or hostile.
//
// Magnitude applies a permanent damage bonus to the reanimated creature's attacks, baked in once at
// creation via a MobileEnemy reflection-mutation technique - reverted back to this after testing
// showed the multi-corpse-per-cast rework (Magnitude -> how many corpses one casting could raise)
// wasn't fun in practice. No cleanup/removal step is needed here since this bonus is baked in for
// the reanimated creature's whole lifetime, not tied to staying in some radius.

using System.Reflection;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Utility;
using VoxsNecromancyMod.AI;

namespace VoxsNecromancyMod
{
    public static class ReanimationSpawner
    {
        static readonly FieldInfo mobileEnemyField = typeof(EnemyEntity).GetField("mobileEnemy", BindingFlags.NonPublic | BindingFlags.Instance);

        // Converts a corpse's loot into a normal dropped-loot pile at the same spot (so the player
        // never loses access to it), then removes the corpse itself. Shared by every
        // corpse-consuming spell in this mod (Reanimate, Corpse Explosion, Deathknell), not
        // just the undead-raising ones.
        //
        // Must be a real Destroy(), not SetActive(false): every corpse is customDrop=true (not part
        // of the location's baked scene data), and entering/exiting a building runs
        // SerializableStateManager.CacheScene/RestoreCachedScene on the exterior even though it's
        // never actually torn down (PlayerEnterExit.TransitionInterior deregisters all stateful
        // objects "since exterior isn't destroyed"). A merely-deactivated corpse's
        // SerializableLootContainer never fires OnDestroy, so it stays registered, gets cached with
        // its now-empty Items, and RestoreLootContainerData - finding no live object left registered
        // under that LoadID after the deregister pass - spawns a BRAND NEW corpse marker to match the
        // stale cache entry (indistinguishable from an unlooted one, since GameObjectHelper.
        // RemoveLootContainer's empty-container cleanup explicitly excludes CorpseMarker). A real
        // Destroy() fires OnDestroy -> deregisters the LoadID immediately, so nothing gets cached for
        // it in the first place and it can never come back.
        public static void ConvertCorpseToLootPile(DaggerfallLoot corpse)
        {
            DaggerfallLoot newLoot = GameObjectHelper.CreateDroppedLootContainer(GameManager.Instance.PlayerObject, DaggerfallUnity.NextUID);
            newLoot.transform.position = corpse.transform.position;
            newLoot.Items.TransferAll(corpse.Items);
            GameObject.Destroy(corpse.gameObject);
        }

        // Full flow for a fresh cast: consume the corpse, then create the undead. magickaUpkeep
        // is computed ONCE by the caller (VoxsNecromancy.CalculateMagickaUpkeep, using the
        // caster's Mysticism at this exact moment) and simply passed through here, the same way
        // magnitude already is - this function never computes it itself, so a restored/reattached
        // ally (see SpawnRestored below) always carries forward the EXACT original value rather than
        // risking a different one if Mysticism has since changed.
        public static void SpawnFromCorpse(DaggerfallLoot corpse, MobileTypes foeType, bool allied, int magnitude,
            int mysticismLevel, int intelligence, int willpower, int durationRounds, int magickaUpkeep,
            bool showHUDMessage)
        {
            Vector3 position = corpse.transform.position;
            ConvertCorpseToLootPile(corpse);
            CreateUndead(position, foeType, allied, magnitude, mysticismLevel, intelligence, willpower,
                durationRounds, magickaUpkeep, showHUDMessage, alignToGround: false);
        }

        // Used by the "Raise Undead Without Corpse" mod setting - no corpse involved at all, so
        // nothing is consumed and there's no loot pile to create. Spawns a few units in front of the
        // caster along their own forward direction; unlike the corpse case (already resting on valid
        // ground), this candidate position is only approximately right, so alignToGround is set to
        // snap it onto the actual floor afterward (same primitive, GameObjectHelper.
        // AlignControllerToGround, DFU's own FoeSpawner uses to place freshly summoned allies).
        public static void SpawnInFrontOfCaster(Vector3 casterPosition, Vector3 casterForward, MobileTypes foeType,
            bool allied, int magnitude, int mysticismLevel, int intelligence, int willpower, int durationRounds,
            int magickaUpkeep, bool showHUDMessage)
        {
            Vector3 position = casterPosition + casterForward.normalized * 3f;
            CreateUndead(position, foeType, allied, magnitude, mysticismLevel, intelligence, willpower,
                durationRounds, magickaUpkeep, showHUDMessage, alignToGround: true);
        }

        // Used to recreate an already-reanimated ally after a scene transition (no corpse involved
        // - it was already consumed when this ally was first cast). See VoxsNecromancy.cs's
        // RecordActiveReanimations/RestoreActiveReanimations, mirroring the same
        // record-before-teardown/restore-after-respawn pattern ChebsNecromancyPacified's
        // ChebsNecromancy.cs uses for its own minions, extended to carry remaining duration forward.
        public static void SpawnRestored(Vector3 position, MobileTypes foeType, bool allied, int magnitude,
            int mysticismLevel, int intelligence, int willpower, int durationRounds, int magickaUpkeep)
        {
            CreateUndead(position, foeType, allied, magnitude, mysticismLevel, intelligence, willpower,
                durationRounds, magickaUpkeep, false, alignToGround: false);
        }

        static void CreateUndead(Vector3 position, MobileTypes foeType, bool allied, int magnitude,
            int mysticismLevel, int intelligence, int willpower, int durationRounds, int magickaUpkeep,
            bool showHUDMessage, bool alignToGround)
        {
            // Passive ("not hostile unless attacked") when allied, matching the same
            // reaction-vs-IsHostile-race consideration already worked out for pacified summons
            // elsewhere in this project - setting the underlying MobileReactions here avoids relying
            // on overwriting EnemyMotor.IsHostile after its own Start() has already run.
            GameObject[] spawned = GameObjectHelper.CreateFoeGameObjects(
                position, foeType, 1,
                allied ? MobileReactions.Passive : MobileReactions.Hostile,
                null, allied);

            if (spawned == null || spawned.Length == 0)
            {
                VoxsNecromancy.LogError("ReanimationSpawner: CreateFoeGameObjects returned no result for " + foeType);
                return;
            }

            GameObject undead = spawned[0];
            DaggerfallEntityBehaviour behaviour = undead.GetComponent<DaggerfallEntityBehaviour>();
            EnemyEntity enemyEntity = behaviour.Entity as EnemyEntity;

            // Only needed for the corpse-less "in front of the caster" spawn point (see
            // SpawnInFrontOfCaster) - a position derived from a corpse is already resting on valid
            // ground, but an offset-from-caster guess might be floating slightly above/below the
            // floor on uneven terrain.
            if (alignToGround)
                GameObjectHelper.AlignControllerToGround(undead.GetComponent<CharacterController>());

            // No loot table drop for a reanimated creature, ever - only the original corpse's items
            // (moved to a dropped-loot pile at cast time) should be recoverable. Clearing once here
            // covers both combat death and duration expiry, since EnemyDeath.CompleteDeath() only
            // ever transfers whatever is left in Items into the eventual corpse marker.
            enemyEntity.Items.Clear();

            ReanimatedLifetime lifetime = undead.AddComponent<ReanimatedLifetime>();
            lifetime.RoundsRemaining = durationRounds;
            lifetime.FoeType = foeType;
            lifetime.Allied = allied;
            lifetime.Magnitude = magnitude;
            lifetime.MysticismLevel = mysticismLevel;
            lifetime.Intelligence = intelligence;
            lifetime.Willpower = willpower;
            lifetime.MagickaUpkeep = magickaUpkeep;

            ApplyMagnitudeDamageBonus(enemyEntity, magnitude);

            if (allied)
            {
                AllyCombatController combatController = undead.AddComponent<AllyCombatController>();
                combatController.MagickaUpkeep = magickaUpkeep;
            }

            undead.SetActive(true);

            if (showHUDMessage)
            {
                string name = TextManager.Instance.GetLocalizedEnemyName(enemyEntity.MobileEnemy.ID);
                if (allied)
                {
                    DaggerfallUI.AddHUDText(string.Format("The {0} rises, and answers to you.", name));
                    DaggerfallUI.AddHUDText(string.Format("Bound. ({0})", VoxsNecromancy.DescribeUpkeepBudget()));
                    DaggerfallUI.AddHUDText(string.Format("Your legion numbers {0}.", AllyCombatController.ActiveAllyCount));
                }
                else
                {
                    DaggerfallUI.AddHUDText(string.Format("The {0} rises, but does not recognize you as its master!", name));
                }
            }
        }

        static void ApplyMagnitudeDamageBonus(EnemyEntity enemyEntity, int magnitude)
        {
            if (mobileEnemyField == null)
                return;

            int bonus = Mathf.Max(1, magnitude / 2);
            MobileEnemy mobileEnemy = enemyEntity.MobileEnemy;
            mobileEnemy.MinDamage += bonus;
            mobileEnemy.MaxDamage += bonus;
            mobileEnemy.MinDamage2 += bonus;
            mobileEnemy.MaxDamage2 += bonus;
            mobileEnemy.MinDamage3 += bonus;
            mobileEnemy.MaxDamage3 += bonus;
            mobileEnemyField.SetValue(enemyEntity, mobileEnemy);
        }
    }
}
