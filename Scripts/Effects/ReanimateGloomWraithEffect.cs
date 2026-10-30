// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// One of five Daggerfall Enemy Expansion (DEE) compatibility tiers - only ever registered when
// VoxsNecromancy.DaggerfallEnemyExpansionInstalled is true (see VoxsNecromancy.LoadSettings).
// Gloom Wraith is DEE's own custom enemy ID 273.
//
// MinCasterLevel (15) matches the level assigned in Astrid's Guide to Necromancy's own "Further
// Reanimation Spells by Level" table - the level Ancient Lich itself used to occupy before being
// bumped to 16 (see ReanimateAncientLichEffect.cs), so this shares that tier's cost bracket
// (Tier 7) rather than the extrapolated Tier 8 it previously used.
//
// Tier 7 (DEE compatibility).

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects;

namespace VoxsNecromancyMod
{
    public class ReanimateGloomWraithEffect : VoxsNecromancyEffect
    {
        public const string EffectKey = "Reanimate Gloom Wraith";
        protected override string effectKey => EffectKey;
        protected override string effectDescription => "Reanimates nearby corpses as Gloom Wraiths.";

        // DEE custom enemy ID 273.
        protected override MobileTypes FoeType => (MobileTypes)273;
        public override int MinCasterLevel => 15;

        protected override EffectCosts FixedChanceCosts => new EffectCosts { CostA = 60, CostB = 240, OffsetGold = 240 };
        protected override EffectCosts FixedMagnitudeCosts => new EffectCosts { CostA = 84, CostB = 336, OffsetGold = 270 };
        protected override EffectCosts FixedDurationCosts => new EffectCosts { CostA = 18, CostB = 90, OffsetGold = 60 };

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                VoxsNecromancy.LogError("ReanimateGloomWraithEffect.Create: Failed to get template from effect broker");
                return new EffectBundleSettings();
            }

            IEntityEffect template = effectBroker.GetEffectTemplate(EffectKey);
            EffectSettings templateSettings = new EffectSettings()
            {
                ChanceBase = -90,
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
