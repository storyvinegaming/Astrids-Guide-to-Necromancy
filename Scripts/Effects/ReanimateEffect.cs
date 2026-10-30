// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// A tenth Reanimate spell, distinct from the fourteen fixed-tier ones (nine base game + five
// Daggerfall Enemy Expansion): rather than always raising the same undead type, this picks the
// STRONGEST type the caster can currently sustain - see VoxsNecromancy.SelectBestAvailableUndeadType,
// which walks tiers strongest-first and returns the first that clears BOTH gates: the caster's level
// meets that tier's own MinCasterLevel (same gate VoxsNecromancyEffect.DoEffect enforces for the
// fixed-tier spells - applied here too, so this spell can't be used to bypass it just by not naming a
// specific tier), and raising it wouldn't push total upkeep past the caster's own upkeep budget
// (VoxsNecromancy.GetUpkeepBudget: MAX Magicka + Mysticism skill) - falling back to progressively
// cheaper tiers until one clears both. If not even the cheapest tier does (the budget is maxed out
// entirely) and there's at least one active ally to retire, this spell honors the cast anyway: the
// single oldest active ally (any type) is retired and replaced with an exact copy of whatever it
// was (AllyCombatController.TryRetireOldestForReplacement) - a straight swap, not a net increase in
// legion size, and distinct from CullOldestUntilAffordable's "keep culling until room for the NAMED
// tier" behavior used by the fixed-tier spells/Subjugate Undead. Only if there's no ally left to
// retire either does the cast fail and refund, same as finding no corpse.
//
// Only ever picks from the nine BASE GAME tiers - SelectBestAvailableUndeadType unconditionally
// skips every Daggerfall Enemy Expansion tier for this spell specifically, regardless of whether DEE
// is installed. The five DEE tiers remain fully castable via their own dedicated spells; they're just
// never auto-selected by this one.
//
// Duration/Magnitude use the exact same flat formula every other Reanimate tier does (see
// VoxsNecromancyEffect's own header comment) - only the cost coefficients are unique per tier
// normally, and since this spell can produce any tier, its own FixedChanceCosts/FixedMagnitudeCosts/
// FixedDurationCosts (inherited, unoverridden - the base class's lowest-tier values) only ever drive
// the pre-cast affordability check and the spellbook's displayed estimate. The Magicka actually
// deducted on a successful cast is swapped out for whichever tier SelectBestAvailableUndeadType
// picked's own upkeep (VoxsNecromancy.CalculateMagickaUpkeep) - see ApplyUpkeepAsCastingCost below -
// so this spell's real cost varies cast to cast based on what it actually summons.
//
// Chance is swapped the same way, for the same reason: each fixed-tier spell's own ChanceBase is
// tuned (see ReanimateSkeletonEffect.cs's header comment) so the allied-vs-hostile roll is a 60/40
// coin flip at exactly that tier's own MinCasterLevel, +10% per level above that (ChanceBase = 60 -
// 10*MinCasterLevel, with the shared ChancePlus=10/ChancePerLevel=1 from Create() below). Left alone,
// this spell's own flat ChanceBase=50 (calibrated for the lowest tier) would make raising something
// like an Ancient Lich far more reliable through "Reanimate" than through its own dedicated spell at
// the same level - so DoEffect() overwrites Settings.ChanceBase with the chosen tier's own value
// (via VoxsNecromancy.GetMinCasterLevel) right before rolling, once a corpse is confirmed and the
// cast can no longer fail. Settings is a per-cast-instance copy (see EntityEffect.Settings), so this
// never leaks into the template or any other cast.
//
// FoeType (from the base class) is unused here - DoEffect is fully overridden to select a type at
// cast time instead of using a fixed one, so the inherited default is never read.

using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;
using VoxsNecromancyMod.AI;

namespace VoxsNecromancyMod
{
    public class ReanimateEffect : VoxsNecromancyEffect
    {
        public const string EffectKey = "Reanimate";
        protected override string effectKey => EffectKey;
        protected override string effectDescription =>
            "Reanimates the nearest corpse as the strongest undead servant your current Magicka can " +
            "sustain, stepping down to a cheaper tier if you can't yet afford anything stronger.";

        public override int MinCasterLevel => 1;

        protected override void DoEffect()
        {
            DaggerfallEntityBehaviour casterBehaviour = GetPeeredEntityBehaviour(manager);
            if (casterBehaviour == null)
                return;

            MobileTypes? chosen = VoxsNecromancy.SelectBestAvailableUndeadType(caster.Entity.Level);

            // Budget maxed out entirely (no tier has any headroom) - only a PEEK here at whether
            // there's anyone at all to retire. The actual retire is deferred past the corpse check
            // below, so a corpse-search miss never costs an ally for nothing.
            bool willReplaceOldest = chosen == null && AllyCombatController.ActiveAllyCount > 0;

            if (chosen == null && !willReplaceOldest)
            {
                DaggerfallUI.AddHUDText(string.Format("Your will cannot bind another. ({0})", VoxsNecromancy.DescribeUpkeepBudget()));
                RefundCastingCost();
                return;
            }

            VoxsNecromancy.ReportNecromancyCrimeIfApplicable();

            DaggerfallLoot corpse = CorpseFinder.FindNearestCorpse(
                casterBehaviour.transform.position, VoxsNecromancy.CorpseDetectionRadius, anyCorpseType: false);

            if (corpse == null)
            {
                DaggerfallUI.AddHUDText("There is no corpse nearby to reanimate.");
                RefundCastingCost();
                return;
            }

            if (willReplaceOldest)
            {
                MobileTypes replacementType;
                if (!AllyCombatController.TryRetireOldestForReplacement(out replacementType))
                {
                    // The roster changed out from under us between the peek above and here (extremely
                    // unlikely) - fail cleanly rather than dereference chosen.Value below with nothing
                    // chosen.
                    DaggerfallUI.AddHUDText(string.Format("Your will cannot bind another. ({0})", VoxsNecromancy.DescribeUpkeepBudget()));
                    RefundCastingCost();
                    return;
                }
                chosen = replacementType;
            }

            int mysticismLevel = caster.Entity.Skills.GetLiveSkillValue(DFCareer.Skills.Mysticism);
            int intelligence = caster.Entity.Stats.LiveIntelligence;
            int willpower = caster.Entity.Stats.LiveWillpower;
            int magnitude = GetMagnitude();
            int rounds = RoundsRemaining;
            int magickaUpkeep = VoxsNecromancy.CalculateMagickaUpkeep(chosen.Value, mysticismLevel);

            // Nothing below this point can fail the cast anymore - safe to swap the vanilla charge
            // for the chosen tier's own upkeep now.
            ApplyUpkeepAsCastingCost(magickaUpkeep);

            // Same swap for Chance - see the file header comment. Matches exactly what casting the
            // chosen tier's own dedicated Reanimate spell would roll at this caster level.
            int chosenMinCasterLevel = VoxsNecromancy.GetMinCasterLevel(chosen.Value);
            EffectSettings adjustedSettings = Settings;
            adjustedSettings.ChanceBase = 60 - 10 * chosenMinCasterLevel;
            Settings = adjustedSettings;

            bool allied = VoxsNecromancy.ResolveReanimateAllegiance(RollChance());
            ReanimationSpawner.SpawnFromCorpse(corpse, chosen.Value, allied, magnitude,
                mysticismLevel, intelligence, willpower, rounds, magickaUpkeep, true);
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                VoxsNecromancy.LogError("ReanimateEffect.Create: Failed to get template from effect broker");
                return new EffectBundleSettings();
            }

            IEntityEffect template = effectBroker.GetEffectTemplate(EffectKey);
            EffectSettings templateSettings = new EffectSettings()
            {
                ChanceBase = 50,
                ChancePlus = 10,
                ChancePerLevel = 1,
                DurationBase = 30,
                DurationPlus = 1,
                DurationPerLevel = 1,
                MagnitudeBaseMin = 1,
                MagnitudeBaseMax = 1,
                MagnitudePlusMin = 1,
                MagnitudePlusMax = 1,
                MagnitudePerLevel = 1,
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
                TargetType = TargetTypes.CasterOnly,
                ElementType = ElementTypes.Magic,
                Name = EffectKey,
                IconIndex = 12,
                Effects = new EffectEntry[] { effectEntry },
            };

            return bundle;
        }
    }
}
