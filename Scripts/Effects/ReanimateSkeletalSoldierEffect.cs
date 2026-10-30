// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// One of five Daggerfall Enemy Expansion (DEE) compatibility tiers - only ever registered when
// VoxsNecromancy.DaggerfallEnemyExpansionInstalled is true (see VoxsNecromancy.LoadSettings).
// Skeletal Soldier is DEE's own custom enemy ID 266, which DEE itself registers via
// DaggerfallEntity.RegisterCustomCareerTemplate and a MobileEnemy entry appended onto
// EnemyBasics.Enemies - both of which GameObjectHelper.CreateFoeGameObjects and
// TextManager.GetLocalizedEnemyName already resolve transparently, so no special spawning/health
// logic is needed here beyond picking the right FoeType.
//
// MinCasterLevel (1) matches the level assigned in Astrid's Guide to Necromancy's own "Further
// Reanimation Spells by Level" table - the same level as the base game's own Skeletal Warrior, so
// this shares that tier's cost bracket.
//
// Tier 1 (DEE compatibility).

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects;

namespace VoxsNecromancyMod
{
    public class ReanimateSkeletalSoldierEffect : VoxsNecromancyEffect
    {
        public const string EffectKey = "Reanimate Skeletal Soldier";
        protected override string effectKey => EffectKey;
        protected override string effectDescription => "Reanimates nearby corpses as Skeletal Soldiers.";

        // DEE custom enemy ID 266.
        protected override MobileTypes FoeType => (MobileTypes)266;
        public override int MinCasterLevel => 1;

        protected override EffectCosts FixedChanceCosts => new EffectCosts { CostA = 10, CostB = 40, OffsetGold = 40 };
        protected override EffectCosts FixedMagnitudeCosts => new EffectCosts { CostA = 14, CostB = 56, OffsetGold = 45 };
        protected override EffectCosts FixedDurationCosts => new EffectCosts { CostA = 3, CostB = 15, OffsetGold = 10 };

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                VoxsNecromancy.LogError("ReanimateSkeletalSoldierEffect.Create: Failed to get template from effect broker");
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
