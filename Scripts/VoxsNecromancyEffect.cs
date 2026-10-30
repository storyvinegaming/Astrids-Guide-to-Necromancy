// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Shared base for all Reanimate effects - the base game's own nine tiers, plus five optional
// Daggerfall Enemy Expansion compatibility tiers registered only when that mod is detected as
// installed (see VoxsNecromancy.DaggerfallEnemyExpansionInstalled and each Reanimate*Effect.cs).
// Same shape as ChebsNecromancyPacified's ChebEffect, fixed to the Mysticism school (not
// user-configurable, per the mod's own design - Reanimate is meant to be a Mysticism family
// specifically).
//
// The spell always casts (never fizzles): ChanceSuccess is overridden here to always return true,
// so EntityEffectManager's chance gate (which would otherwise skip DoEffect() entirely on a failed
// roll) never triggers. Chance instead determines whether the single corpse consumed this casting
// comes out allied or hostile - see DoEffect() below. The "Reanimate Allegiance" mod setting
// (VoxsNecromancy.ReanimateAllegianceMode, applied via VoxsNecromancy.ResolveReanimateAllegiance)
// can override that roll's outcome entirely - Always Allied/Always Hostile ignore Chance, while
// Default Behavior keeps the roll.
//
// Magnitude applies a permanent damage bonus to the reanimated creature's attacks (baked in once at
// creation - see ReanimationSpawner.ApplyMagnitudeDamageBonus). Each casting still only consumes
// one corpse (the nearest valid one in range), same as Corpse Explosion/Deathknell -
// Magnitude was briefly reworked to instead control how many corpses a single casting could raise
// at once, but that didn't feel good in practice and has been reverted; Magnitude Cost is tuned in
// line with other damage-scaling spells rather than the old per-tier corpse-count curve.
//
// DoEffect() itself lives entirely in this base class (parameterized by each subclass's FoeType
// override) rather than being reimplemented per tier, since all nine tiers share the exact same
// single-corpse logic and differ only in that one value plus their own Create()'s cost coefficients
// (now fixed constants via FixedChanceCosts/FixedMagnitudeCosts/FixedDurationCosts, no longer
// user-configurable through mod settings).

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
    public class VoxsNecromancyEffect : BaseEntityEffect
    {
        protected virtual string effectKey => "Reanimate";
        protected virtual string effectDescription => "Reanimates nearby corpses.";

        // Overridden per tier - see each Reanimate*Effect.cs.
        protected virtual MobileTypes FoeType => MobileTypes.SkeletalWarrior;

        // Minimum caster level to cast this tier at all - enforced unconditionally in DoEffect()
        // below, alongside (not instead of) the Magicka upkeep budget: the budget limits how much
        // undead strength a legion can SUSTAIN, but says nothing about what's available the instant a
        // low-level caster has enough Magicka on hand, which is what this gate closes. Overridden per
        // tier, matching that undead type's own earliest common appearance in DFU's random encounter
        // tables - the same values used before the upkeep budget replaced the old level-scaled caps.
        public virtual int MinCasterLevel => 1;

        // Fixed per-tier gold/Magicka cost coefficients - formerly configurable via modsettings.json
        // ("advanced" per-spell sections), removed in favor of hardcoded balance since that level of
        // tuning wasn't something players were expected to touch. Overridden per tier below with
        // that tier's own former default values, preserving the exact balance that shipped. Only
        // drives the pre-cast affordability check and the spellbook's displayed cost estimate now -
        // the amount actually deducted on a successful cast is this tier's own Magicka upkeep instead
        // (see DoEffect/ApplyUpkeepAsCastingCost below).
        protected virtual EffectCosts FixedChanceCosts => new EffectCosts { CostA = 10, CostB = 40, OffsetGold = 40 };
        protected virtual EffectCosts FixedMagnitudeCosts => new EffectCosts { CostA = 14, CostB = 56, OffsetGold = 45 };
        protected virtual EffectCosts FixedDurationCosts => new EffectCosts { CostA = 3, CostB = 15, OffsetGold = 10 };

        public override void SetProperties()
        {
            properties.Key = effectKey;
            properties.ShowSpellIcon = false;
            properties.AllowedTargets = TargetTypes.CasterOnly;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            // No longer available in the Spellmaker/Potion/Item crafting stations - Reanimate is now
            // guild-purchase only, with fixed (non-customizable) Chance/Duration/Magnitude values
            // per tier (see each Reanimate*Effect.cs's own Create()).
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            properties.MagicSkill = DFCareer.MagicSkills.Mysticism;
            properties.SupportChance = true;
            properties.SupportMagnitude = true;
            // Must be true for RoundsRemaining to populate normally from the template's own Duration
            // values (leaving this false previously silently zeroed Duration entirely). MagicRound()
            // below guards DoEffect() with a per-cast instance flag instead, so it still only fires
            // once regardless of how many rounds this effect instance ticks through.
            properties.SupportDuration = true;
            properties.DisableReflectiveEnumeration = true;

            properties.ChanceCosts = FixedChanceCosts;
            properties.MagnitudeCosts = FixedMagnitudeCosts;
            properties.DurationCosts = FixedDurationCosts;
        }

        // Spell always succeeds - chance instead decides allegiance, rolled fresh per corpse in
        // DoEffect() via the inherited RollChance() (which does its own independent
        // Dice100.SuccessRoll(ChanceValue()) each call, untouched by this override).
        public override bool ChanceSuccess
        {
            get { return true; }
        }

        #region Text

        public override string GroupName => effectKey;
        public override TextFile.Token[] SpellMakerDescription => GetSpellMakerDescription();
        public override TextFile.Token[] SpellBookDescription => GetSpellBookDescription();

        // Plain-language stat descriptions rather than %-macro tokens: the spellmaker's "customize
        // spell" editor (DaggerfallEffectSettingsEditorWindow) never expands macros in this text for
        // any effect, vanilla included (it just passes SpellMakerDescription straight to the label) -
        // the actual numbers are read from the spinners below, not this text. Vanilla effects like
        // Chameleon describe what each stat means in plain English here instead.
        TextFile.Token[] GetSpellMakerDescription()
        {
            return DaggerfallUnity.Instance.TextProvider.CreateTokens(
                TextFile.Formatting.JustifyCenter,
                GroupName,
                effectDescription,
                "Duration: 30 rounds + 1 per caster level.",
                string.Format("Chance of an ally (vs. hostile): 60% at level {0},", MinCasterLevel),
                "+10% per level above that.",
                "Magnitude: +1 permanent attack damage at level 1, +1 more per level.");
        }

        TextFile.Token[] GetSpellBookDescription()
        {
            return DaggerfallUnity.Instance.TextProvider.CreateTokens(
                TextFile.Formatting.JustifyCenter,
                GroupName,
                "Duration: 30 rounds + 1 per caster level.",
                string.Format("Chance of an ally (vs. hostile): 60% at level {0},", MinCasterLevel),
                "+10% per level above that.",
                "Magnitude: +1 permanent attack damage at level 1, +1 more per level.",
                effectDescription);
        }

        #endregion

        bool hasFired;

        public override void MagicRound()
        {
            base.MagicRound();

            if (hasFired)
                return;
            hasFired = true;

            DaggerfallEntityBehaviour entityBehaviour = GetPeeredEntityBehaviour(manager);
            if (!entityBehaviour)
                return;

            DoEffect();
        }

        protected virtual void DoEffect()
        {
            DaggerfallEntityBehaviour casterBehaviour = GetPeeredEntityBehaviour(manager);
            if (casterBehaviour == null)
                return;

            // Every failure branch below refunds whatever the vanilla EffectCosts/FormulaHelper
            // formula pre-charged at cast-ready time (fizzled casts, matching the pre-upkeep-economy
            // convention). Once a cast is guaranteed to succeed, that vanilla charge is swapped out
            // for this tier's own Magicka upkeep instead - see ApplyUpkeepAsCastingCost below.
            if (caster.Entity.Level < MinCasterLevel)
            {
                string foeName = TextManager.Instance.GetLocalizedEnemyName((int)FoeType);
                DaggerfallUI.AddHUDText(string.Format(
                    "The dead do not yet answer to one so unpracticed - you must be at least level {0} to command a {1}.",
                    MinCasterLevel, foeName));
                RefundCastingCost();
                return;
            }

            int mysticismLevel = caster.Entity.Skills.GetLiveSkillValue(DFCareer.Skills.Mysticism);
            int magickaUpkeep = VoxsNecromancy.CalculateMagickaUpkeep(FoeType, mysticismLevel);

            // Below this, no amount of culling the rest of the legion would ever make room - this
            // tier's own upkeep alone exceeds the whole budget. Nothing dies in that case; a specific
            // tier that DOES fit (once other allies are culled) is handled after the roll below.
            if (magickaUpkeep > VoxsNecromancy.GetUpkeepBudget())
            {
                string foeName = TextManager.Instance.GetLocalizedEnemyName((int)FoeType);
                DaggerfallUI.AddHUDText(string.Format("You do not have the magical talent to raise a {0}.", foeName));
                RefundCastingCost();
                return;
            }

            VoxsNecromancy.ReportNecromancyCrimeIfApplicable();

            // "Raise Undead Without Corpse" skips the corpse search entirely - there's nothing to
            // consume and nothing to fail on, so the cast always goes through and the creature rises
            // a few paces in front of the caster instead (see ReanimationSpawner.SpawnInFrontOfCaster).
            DaggerfallLoot corpse = null;
            if (!VoxsNecromancy.RaiseUndeadWithoutCorpse)
            {
                corpse = CorpseFinder.FindNearestCorpse(
                    casterBehaviour.transform.position, VoxsNecromancy.CorpseDetectionRadius, anyCorpseType: false);

                if (corpse == null)
                {
                    DaggerfallUI.AddHUDText("There is no corpse nearby to reanimate.");
                    RefundCastingCost();
                    return;
                }
            }

            int intelligence = caster.Entity.Stats.LiveIntelligence;
            int willpower = caster.Entity.Stats.LiveWillpower;
            int magnitude = GetMagnitude();
            int rounds = RoundsRemaining;

            // Nothing below this point can fail the cast anymore - safe to swap the vanilla charge
            // for this tier's own upkeep now.
            ApplyUpkeepAsCastingCost(magickaUpkeep);

            bool allied = VoxsNecromancy.ResolveReanimateAllegiance(RollChance());

            // Only cull if this roll is actually joining the roster - a hostile outcome never costs
            // upkeep at all (see ReanimationSpawner.CreateUndead), so there's nothing to make room for.
            if (allied)
                AllyCombatController.CullOldestUntilAffordable(magickaUpkeep);

            if (corpse != null)
            {
                ReanimationSpawner.SpawnFromCorpse(corpse, FoeType, allied, magnitude,
                    mysticismLevel, intelligence, willpower, rounds, magickaUpkeep, true);
            }
            else
            {
                ReanimationSpawner.SpawnInFrontOfCaster(casterBehaviour.transform.position, casterBehaviour.transform.forward,
                    FoeType, allied, magnitude, mysticismLevel, intelligence, willpower, rounds, magickaUpkeep, true);
            }
        }

        protected void RefundCastingCost()
        {
            if (caster == null)
                return;

            EffectEntry entry = new EffectEntry { Key = Properties.Key, Settings = Settings };
            FormulaHelper.SpellCost cost = FormulaHelper.CalculateTotalEffectCosts(new EffectEntry[] { entry }, TargetTypes.CasterOnly, caster.Entity);
            caster.Entity.IncreaseMagicka(cost.spellPointCost);
        }

        // Refunds whatever the vanilla EffectCosts/FormulaHelper formula already deducted at
        // cast-ready time (same computation RefundCastingCost uses above), then charges the given
        // upkeep amount instead - a straight swap, not a top-up, so this must only be called once a
        // cast is already guaranteed to go through. Makes the real Magicka price of raising a given
        // undead type equal to its own upkeep (VoxsNecromancy.CalculateMagickaUpkeep), Mysticism
        // discount included, regardless of what this tier's FixedChanceCosts/FixedMagnitudeCosts/
        // FixedDurationCosts happen to price out to. Those Fixed*Costs still drive the pre-cast
        // affordability check and the estimate shown in the spellbook - only the amount actually
        // deducted on a successful cast is overridden here.
        protected void ApplyUpkeepAsCastingCost(int magickaUpkeep)
        {
            if (caster == null)
                return;

            RefundCastingCost();
            caster.Entity.DecreaseMagicka(magickaUpkeep);
        }

        // Because SupportDuration=true, this effect instance stays "live" on the caster (DFU keeps
        // it in EntityEffectManager's own liveEffects/instancedBundle) for its whole RoundsRemaining
        // duration - which DFU's own player save data serializes and restores just like any other
        // ongoing buff. Without persisting hasFired here too, loading a save WHILE a Reanimate
        // spell's duration hadn't yet expired would deserialize a BRAND NEW effect instance with
        // hasFired reset to false, and the next magic round would fire DoEffect() all over again -
        // reanimating corpses out of nowhere. Same save-data pattern DFU's own FortifyEffect uses
        // for its own extra per-instance state.
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
    }
}
