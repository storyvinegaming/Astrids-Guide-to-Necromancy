// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Converts a hostile undead target to an allied undead, reusing the same allied-AI/faction-flip
// machinery Reanimate's allied outcome uses (AllyCombatController, Team flip) rather than
// reimplementing it - the target here is just an existing undead entity instead of a freshly-raised
// one. Standard DFU chance mechanic (not Reanimate's inverted use of it): SupportChance=true with no
// ChanceSuccess override, matching CharmEffect/PacifyEffect - the framework's own pre-check in
// EntityEffectManager gates the entire effect on a failed roll before MagicRound() ever runs, so a
// failed cast simply has no effect at all (touch/target spell, like Charm/Pacify, not the
// radius-based corpse detection Reanimate uses).
//
// Duration determines how long the command lasts on a genuinely vanilla hostile undead: on expiry
// it reverts to hostile (SubjugationTimer.cs) rather than dying, a distinct expiry behavior from
// Reanimate. Exception: if the target was already a player-raised undead (has ReanimatedLifetime,
// regardless of its current Allied state - e.g. a Reanimate cast that rolled hostile), this spell's
// own Duration does not apply at all - it just flips Team/Allied and leaves the creature's own
// pre-existing ReanimatedLifetime.RoundsRemaining ticking down unaffected, since that undead was
// always going to expire (die) on its own original timer regardless of being re-subjugated.
//
// Also consumes upkeep budget exactly like Reanimate does (VoxsNecromancy.CalculateMagickaUpkeep/
// CanAffordUpkeep), computed fresh at the moment of subjugation using this creature's own type and
// the caster's current Mysticism - refused with the same message and a cast-cost refund if the
// budget can't cover it, same failure path as a Reanimate cast hitting the same ceiling.

using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Formulas;
using DaggerfallWorkshop.Game.MagicAndEffects;
using FullSerializer;
using VoxsNecromancyMod.AI;

namespace VoxsNecromancyMod
{
    public class SubjugateUndeadEffect : BaseEntityEffect
    {
        public const string EffectKey = "Subjugate Undead";

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = false;
            properties.SupportChance = true;
            properties.SupportDuration = true;
            properties.AllowedTargets = EntityEffectBroker.TargetFlags_Other;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            properties.MagicSkill = DFCareer.MagicSkills.Mysticism;
            properties.DisableReflectiveEnumeration = true;

            properties.ChanceCosts = new EffectCosts { CostA = 10, CostB = 40, OffsetGold = 30 };
            properties.DurationCosts = new EffectCosts { CostA = 4, CostB = 16, OffsetGold = 13 };
        }

        public override string GroupName => EffectKey;
        public override TextFile.Token[] SpellMakerDescription => GetDescription();
        public override TextFile.Token[] SpellBookDescription => GetDescription();

        TextFile.Token[] GetDescription()
        {
            return DaggerfallUnity.Instance.TextProvider.CreateTokens(
                TextFile.Formatting.JustifyCenter,
                GroupName,
                "Commands a hostile undead to fight for you, using the same allied-AI as a",
                "reanimated ally. On a failed roll, nothing happens at all.",
                "Duration: rounds the command lasts (unless the target was already yours to begin",
                "with, in which case it just reverts to its own original timer instead).",
                "Chance: likelihood the command succeeds - 30% + 3% per caster level.");
        }

        bool hasFired;

        public override void MagicRound()
        {
            base.MagicRound();

            if (hasFired)
                return;
            hasFired = true;

            DaggerfallEntityBehaviour targetBehaviour = GetPeeredEntityBehaviour(manager);
            if (!targetBehaviour)
                return;

            VoxsNecromancy.ReportNecromancyCrimeIfApplicable();

            DoEffect(targetBehaviour);
        }

        void DoEffect(DaggerfallEntityBehaviour targetBehaviour)
        {
            EnemyEntity enemyEntity = targetBehaviour.Entity as EnemyEntity;
            if (enemyEntity == null)
                return;

            // Only works on undead, and only while still hostile - subjugating something already
            // allied (including one you already reanimated) has nothing to do.
            if (enemyEntity.GetEnemyGroup() != DFCareer.EnemyGroups.Undead || enemyEntity.Team == MobileTeams.PlayerAlly)
                return;

            // Budget check BEFORE any state mutation (Team flip, components). Below this, no amount
            // of culling the rest of the legion would ever make room - this creature's own upkeep
            // alone exceeds the whole budget - so nothing dies and the cast simply fails.
            int mysticismLevel = caster.Entity.Skills.GetLiveSkillValue(DFCareer.Skills.Mysticism);
            int magickaUpkeep = VoxsNecromancy.CalculateMagickaUpkeep((MobileTypes)enemyEntity.MobileEnemy.ID, mysticismLevel);

            if (magickaUpkeep > VoxsNecromancy.GetUpkeepBudget())
            {
                string foeName = TextManager.Instance.GetLocalizedEnemyName(enemyEntity.MobileEnemy.ID);
                DaggerfallUI.AddHUDText(string.Format("You do not have the magical talent to bind a {0}.", foeName));
                RefundCastingCost();
                return;
            }

            // Otherwise, honor the player's explicit choice of target - cull the oldest currently
            // active allies (any type) until there's room, same as a fixed-tier Reanimate cast would.
            AllyCombatController.CullOldestUntilAffordable(magickaUpkeep);

            ReanimatedLifetime existingLifetime = targetBehaviour.GetComponent<ReanimatedLifetime>();
            MobileTeams originalTeam = enemyEntity.Team;
            enemyEntity.Team = MobileTeams.PlayerAlly;

            // Flipping Team alone doesn't stop a creature that was already mid-attack on the player -
            // EnemyMotor's own attack/pursuit state machine only reacts to Target/IsHostile, not Team,
            // and neither is touched by a Team change. A freshly reanimated ally never hits this
            // (ReanimationSpawner creates it with MobileReactions.Passive from the start, so it's
            // never had the player as a Target to begin with), but an existing subjugated creature
            // needs both cleared explicitly to actually stop attacking right away.
            EnemyMotor motor = targetBehaviour.GetComponent<EnemyMotor>();
            EnemySenses senses = targetBehaviour.GetComponent<EnemySenses>();
            if (motor != null)
                motor.IsHostile = false;
            if (senses != null)
            {
                senses.Target = null;
                senses.SecondaryTarget = null;
            }

            if (existingLifetime != null)
            {
                // Already player-raised - its own pre-existing duration keeps ticking down
                // unaffected; this spell's own Duration doesn't apply here at all.
                existingLifetime.Allied = true;
            }
            else
            {
                SubjugationTimer timer = targetBehaviour.gameObject.AddComponent<SubjugationTimer>();
                timer.RoundsRemaining = RoundsRemaining;
                timer.OriginalTeam = originalTeam;
            }

            // Upkeep here uses the SAME value already computed and budget-checked above (this
            // caster's current Mysticism against whatever type is actually being subjugated) - a
            // previously-Reanimated-then-hostile target's OLD upkeep (from whenever it was first
            // raised, possibly by a different Mysticism skill level) is irrelevant here, since it was
            // never actually "in service" while hostile; this subjugation is what puts it back into
            // service, so it gets its own fresh upkeep like any other subjugation would.
            if (existingLifetime != null)
                existingLifetime.MagickaUpkeep = magickaUpkeep;

            AllyCombatController combatController = targetBehaviour.GetComponent<AllyCombatController>();
            if (combatController == null)
                combatController = targetBehaviour.gameObject.AddComponent<AllyCombatController>();
            combatController.MagickaUpkeep = magickaUpkeep;

            string name = TextManager.Instance.GetLocalizedEnemyName(enemyEntity.MobileEnemy.ID);
            DaggerfallUI.AddHUDText(string.Format("The {0} now serves you.", name));
            DaggerfallUI.AddHUDText(string.Format("Bound. ({0})", VoxsNecromancy.DescribeUpkeepBudget()));
            DaggerfallUI.AddHUDText(string.Format("Your legion numbers {0}.", AllyCombatController.ActiveAllyCount));
        }

        void RefundCastingCost()
        {
            if (caster == null)
                return;

            EffectEntry entry = new EffectEntry { Key = Properties.Key, Settings = Settings };
            FormulaHelper.SpellCost cost = FormulaHelper.CalculateTotalEffectCosts(new EffectEntry[] { entry }, TargetTypes.SingleTargetAtRange, caster.Entity);
            caster.Entity.IncreaseMagicka(cost.spellPointCost);
        }

        // Same reasoning as VoxsNecromancyEffect's own SaveData_v1: SupportDuration=true keeps this
        // effect instance alive on the caster for its whole RoundsRemaining, which DFU's own save
        // data serializes/restores like any ongoing buff - without persisting hasFired, a reload
        // mid-duration would let a freshly deserialized instance re-run the conversion logic once
        // more (harmless/idempotent here since it doesn't spawn anything new, but avoided anyway for
        // correctness and consistency).
        #region Serialization

        [fsObject("v1")]
        public struct SaveData_v1
        {
            public bool hasFired;
        }

        public override object GetSaveData()
        {
            SaveData_v1 data = new SaveData_v1();
            data.hasFired = hasFired;
            return data;
        }

        public override void RestoreSaveData(object dataIn)
        {
            if (dataIn == null)
                return;

            SaveData_v1 data = (SaveData_v1)dataIn;
            hasFired = data.hasFired;
        }

        #endregion

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                VoxsNecromancy.LogError("SubjugateUndeadEffect.Create: Failed to get template from effect broker");
                return new EffectBundleSettings();
            }

            IEntityEffect template = effectBroker.GetEffectTemplate(EffectKey);
            EffectSettings templateSettings = new EffectSettings()
            {
                ChanceBase = 30,
                ChancePlus = 3,
                ChancePerLevel = 1,
                DurationBase = 20,
                DurationPlus = 5,
                DurationPerLevel = 1,
            };
            EffectEntry effectEntry = new EffectEntry()
            {
                Key = template.Properties.Key,
                Settings = templateSettings,
            };
            EffectBundleSettings bundle = new EffectBundleSettings()
            {
                Version = 1,
                BundleType = BundleTypes.Spell,
                TargetType = TargetTypes.SingleTargetAtRange,
                ElementType = ElementTypes.Magic,
                Name = EffectKey,
                IconIndex = 12,
                Effects = new EffectEntry[] { effectEntry },
            };

            return bundle;
        }
    }
}
