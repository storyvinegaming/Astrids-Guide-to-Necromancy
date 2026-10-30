// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// One of five Daggerfall Enemy Expansion (DEE) compatibility tiers - only ever registered when
// VoxsNecromancy.DaggerfallEnemyExpansionInstalled is true (see VoxsNecromancy.LoadSettings).
// Faded Ghost is DEE's own custom enemy ID 274.
//
// MinCasterLevel (6) matches the level assigned in Astrid's Guide to Necromancy's own "Further
// Reanimation Spells by Level" table - the same level as DEE's own Ghoul (see
// ReanimateGhoulEffect.cs), so this shares that tier's cost bracket (Tier 4).
//
// Tier 4 (DEE compatibility).

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects;

namespace VoxsNecromancyMod
{
    public class ReanimateFadedGhostEffect : VoxsNecromancyEffect
    {
        public const string EffectKey = "Reanimate Faded Ghost";
        protected override string effectKey => EffectKey;
        protected override string effectDescription => "Reanimates nearby corpses as Faded Ghosts.";

        // DEE custom enemy ID 274.
        protected override MobileTypes FoeType => (MobileTypes)274;
        public override int MinCasterLevel => 6;

        protected override EffectCosts FixedChanceCosts => new EffectCosts { CostA = 26, CostB = 100, OffsetGold = 100 };
        protected override EffectCosts FixedMagnitudeCosts => new EffectCosts { CostA = 36, CostB = 146, OffsetGold = 117 };
        protected override EffectCosts FixedDurationCosts => new EffectCosts { CostA = 8, CostB = 38, OffsetGold = 25 };

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                VoxsNecromancy.LogError("ReanimateFadedGhostEffect.Create: Failed to get template from effect broker");
                return new EffectBundleSettings();
            }

            IEntityEffect template = effectBroker.GetEffectTemplate(EffectKey);
            EffectSettings templateSettings = new EffectSettings()
            {
                ChanceBase = 0,
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
