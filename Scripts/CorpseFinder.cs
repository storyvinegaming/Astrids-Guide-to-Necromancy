// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Finds valid corpse-consuming-spell targets: dead corpse markers within a radius of a given
// position. Quest-target corpses are excluded by default, regardless of any other setting or the
// anyCorpseType override below, via the "Protect Quest Corpses" mod setting (on by default) -
// DaggerfallLoot.corpseQuestUID (set from EnemyDeath.cs's own QuestResourceBehaviour check when a
// dying enemy was a quest resource, 0 otherwise) is the engine's own canonical "is this a quest
// corpse" test (it's the same field the engine itself checks for its own tombstone-cleanup logic),
// so mirroring it needs no new tracking. Turning the setting off lets corpse-consuming spells affect
// quest corpses too, at the player's own risk of breaking quest state.
//
// By default any corpse qualifies (subject only to the undead-exclusion below) - the "Limit to
// Human Corpses" mod setting (VoxsNecromancy.LimitToHumanCorpses, off by default) restricts Reanimate
// back to only human/classed enemy corpses, via DaggerfallLoot.isEnemyClass, set at corpse-creation
// time from EntityType == EntityTypes.EnemyClass (see GameObjectHelper.CreateLootContainer). Undead
// corpses are always excluded (see IsUndeadCorpse) regardless of that setting, to avoid the obvious
// exploit of chaining Reanimate spells off already-undead kills.
//
// The anyCorpseType parameter (used by Corpse Explosion/Deathknell, never by Reanimate)
// bypasses BOTH the isEnemyClass/LimitToHumanCorpses gate AND the undead-exclusion entirely - those
// spells can affect any corpse in the game unconditionally, independent of LimitToHumanCorpses's
// state. The quest-corpse exclusion above is independent of anyCorpseType too, governed only by its
// own "Protect Quest Corpses" setting.

using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Formulas;
using DaggerfallWorkshop.Utility;

namespace VoxsNecromancyMod
{
    public static class CorpseFinder
    {
        static readonly MobileTypes[] undeadTypes =
        {
            MobileTypes.SkeletalWarrior,
            MobileTypes.Zombie,
            MobileTypes.Ghost,
            MobileTypes.Mummy,
            MobileTypes.Wraith,
            MobileTypes.Vampire,
            MobileTypes.VampireAncient,
            MobileTypes.Lich,
            MobileTypes.AncientLich,
        };

        // Daggerfall Enemy Expansion (see VoxsNecromancy.DaggerfallEnemyExpansionInstalled) registers
        // its own undead under custom enemy IDs outside the vanilla MobileTypes range: Skeletal
        // Soldier, Gloom Wraith, Faded Ghost, Ghoul, Dire Ghoul. Only checked when that mod is
        // actually detected as installed - TextManager.GetLocalizedEnemyName has nothing registered
        // for these IDs otherwise, so the match would just harmlessly never hit, but there's no
        // reason to bother probing at all when the mod isn't present.
        static readonly int[] deeUndeadIDs = { 266, 273, 274, 277, 287 };

        public static DaggerfallLoot FindNearestCorpse(Vector3 fromPosition, float radius, bool anyCorpseType = false)
        {
            DaggerfallLoot nearest = null;
            float nearestDistance = float.MaxValue;

            foreach (DaggerfallLoot loot in ActiveGameObjectDatabase.GetActiveLoot())
            {
                if (!IsEligible(loot, anyCorpseType))
                    continue;

                float distance = Vector3.Distance(fromPosition, loot.transform.position);
                if (distance > radius)
                    continue;

                if (distance < nearestDistance)
                {
                    nearest = loot;
                    nearestDistance = distance;
                }
            }

            return nearest;
        }

        static bool IsEligible(DaggerfallLoot loot, bool anyCorpseType)
        {
            if (loot == null)
                return false;

            if (loot.ContainerType != LootContainerTypes.CorpseMarker)
                return false;

            // Applies regardless of anyCorpseType or LimitToHumanCorpses - only the "Protect Quest
            // Corpses" setting itself governs this exclusion.
            if (VoxsNecromancy.ProtectQuestCorpses && loot.corpseQuestUID != 0)
                return false;

            if (anyCorpseType)
                return true;

            if (VoxsNecromancy.LimitToHumanCorpses && !loot.isEnemyClass)
                return false;

            if (IsUndeadCorpse(loot))
                return false;

            return true;
        }

        // Flat stand-in used only when the exact-match lookup below can't identify the corpse's
        // creature type at all (see the fallback branch in TryGetTypicalMaxHealth) - a modest,
        // roughly mid-tier value so an unrecognized corpse still gives some benefit instead of
        // silently doing nothing.
        const int fallbackTypicalMaxHealth = 40;

        // Best-effort lookup of "how much health did this corpse's original creature typically
        // have" - used by Corpse Explosion to add bonus blast damage based on the consumed corpse.
        // DaggerfallLoot itself never stores the exact rolled health the original entity had (that
        // entity, and its actual rolled value, is long gone by the time it's just a corpse marker) -
        // this instead reverse-looks-up the creature TYPE from its stored entityName (same technique
        // IsUndeadCorpse uses) and returns that type's average of MobileEnemy.MinHealth/MaxHealth as
        // a stand-in for "how tough was this kind of creature."
        //
        // GameObjectHelper.EnemyDict only covers vanilla MobileTypes IDs (built once from the fixed
        // EnemyBasics.Enemies table) - custom/modded enemy types (common for unique quest bosses)
        // are registered elsewhere (DaggerfallEntity.CustomCareerTemplates, a DFCareer with no
        // Min/MaxHealth field at all, and with no public way to enumerate its IDs or recover the
        // original numeric ID from just a DaggerfallLoot corpse - entityName is the only surviving
        // signal, and it's a perfectly valid string even for custom enemies, just one nothing in
        // EnemyDict can match). Rather than silently returning false for a real, valid corpse
        // whenever this happens, this falls back to fallbackTypicalMaxHealth instead - the corpse is
        // real and was eligible to consume, so consuming it should still do something.
        //
        // Class-type enemies (vanilla Knights/Mages/etc, IDs 128-146, and any modded equivalent -
        // DaggerfallEntity.IsClassEnemyId) are a separate case entirely: EnemyBasics.Enemies never
        // sets MinHealth/MaxHealth for ANY class-type entry at all (confirmed - every one of the 19
        // vanilla class entries leaves both at their struct default of 0), because EnemyEntity.
        // SetEnemyCareer's EnemyClass branch computes health from FormulaHelper.
        // RollEnemyClassMaxHealth(level, career.HitPointsPerLevel) instead - a level-dependent roll,
        // not a fixed range. Averaging MinHealth/MaxHealth for these would silently give 0 every
        // time (matching the exact "no health returned" symptom reported for both vanilla classes
        // and Daggerfall Enemy Expansion's own new human classes). The player's current level is
        // the best available stand-in for "what level was this one actually encountered at," since
        // that information doesn't survive death either.
        public static bool TryGetTypicalMaxHealth(DaggerfallLoot loot, out int typicalMaxHealth)
        {
            typicalMaxHealth = 0;

            if (loot == null || string.IsNullOrEmpty(loot.entityName))
                return false;

            foreach (KeyValuePair<int, MobileEnemy> pair in GameObjectHelper.EnemyDict)
            {
                if (loot.entityName != TextManager.Instance.GetLocalizedEnemyName(pair.Key))
                    continue;

                DFCareer career = GetClassCareerIfApplicable(pair.Key);
                if (career != null)
                    typicalMaxHealth = FormulaHelper.RollEnemyClassMaxHealth(GameManager.Instance.PlayerEntity.Level, career.HitPointsPerLevel);
                else
                    typicalMaxHealth = (pair.Value.MinHealth + pair.Value.MaxHealth) / 2;

                return true;
            }

            typicalMaxHealth = fallbackTypicalMaxHealth;
            return true;
        }

        // Returns the DFCareer for a class-type enemy ID (vanilla 128-146 via GetClassCareerTemplate,
        // or a mod-registered one via GetCustomCareerTemplate - same two-branch shape
        // SetupDemoEnemy.ApplyEnemySettings itself uses to decide EntityType), or null if enemyId
        // isn't a class-type enemy at all (DaggerfallEntity.IsClassEnemyId - enemyId & 128 != 0).
        static DFCareer GetClassCareerIfApplicable(int enemyId)
        {
            if (enemyId >= 128 && enemyId <= 146)
                return DaggerfallEntity.GetClassCareerTemplate((ClassCareers)(enemyId - 128));

            if (DaggerfallEntity.IsClassEnemyId(enemyId))
                return DaggerfallEntity.GetCustomCareerTemplate(enemyId);

            return null;
        }

        static bool IsUndeadCorpse(DaggerfallLoot loot)
        {
            if (string.IsNullOrEmpty(loot.entityName))
                return false;

            for (int i = 0; i < undeadTypes.Length; i++)
            {
                if (loot.entityName == TextManager.Instance.GetLocalizedEnemyName((int)undeadTypes[i]))
                    return true;
            }

            if (VoxsNecromancy.DaggerfallEnemyExpansionInstalled)
            {
                for (int i = 0; i < deeUndeadIDs.Length; i++)
                {
                    if (loot.entityName == TextManager.Instance.GetLocalizedEnemyName(deeUndeadIDs[i]))
                        return true;
                }
            }

            return false;
        }
    }
}
