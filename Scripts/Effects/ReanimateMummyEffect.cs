// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Duration and Magnitude formulas are flat and identical across all nine Reanimate effects:
// Duration = 30 + 1 per caster level, Magnitude = 1 + 1 per caster level (a given caster level has
// the same lasting power and damage bonus regardless of tier). Chance is tier-dependent instead -
// each tier's ChanceBase is set so the reanimation is a 60/40 coin flip at exactly that tier's own
// MinCasterLevel (the earliest level it can be cast at all), rising 10% per level above that - so
// every tier is equally reliable right when it unlocks, and gets more reliable the more you outlevel
// its requirement. Cost (see the base class's FixedChanceCosts/FixedMagnitudeCosts/
// FixedDurationCosts) still differs per tier, so stronger undead are simply costlier to raise.
// No longer available in the Spellmaker or Mages Guild - Reanimate is granted only via
// Astrid's Guide to Necromancy (the book) or the embrace_darkness console command.
//
// Tier 3.

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects;

namespace VoxsNecromancyMod
{
    public class ReanimateMummyEffect : VoxsNecromancyEffect
    {
        public const string EffectKey = "Reanimate Mummy";
        protected override string effectKey => EffectKey;
        protected override string effectDescription => "Reanimates nearby corpses as Mummies.";

        protected override MobileTypes FoeType => MobileTypes.Mummy;
        public override int MinCasterLevel => 5;

        protected override EffectCosts FixedChanceCosts => new EffectCosts { CostA = 16, CostB = 64, OffsetGold = 64 };
        protected override EffectCosts FixedMagnitudeCosts => new EffectCosts { CostA = 22, CostB = 90, OffsetGold = 72 };
        protected override EffectCosts FixedDurationCosts => new EffectCosts { CostA = 5, CostB = 24, OffsetGold = 16 };

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                VoxsNecromancy.LogError("ReanimateMummyEffect.Create: Failed to get template from effect broker");
                return new EffectBundleSettings();
            }

            IEntityEffect template = effectBroker.GetEffectTemplate(EffectKey);
            EffectSettings templateSettings = new EffectSettings()
            {
                ChanceBase = 10,
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
