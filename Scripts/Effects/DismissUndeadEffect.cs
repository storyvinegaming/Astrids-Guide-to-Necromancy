// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Instantly kills an undead target, but only if it's one the player themselves created - reuses the
// same ReanimatedLifetime marker Subjugate Undead's expiry exception relies on to identify "player-
// raised" undead (present regardless of the creature's current Allied/hostile state, since a
// Reanimate cast that rolled hostile still carries this component). Targeting anything else -
// vanilla undead, quest undead, any other living creature - simply fails to find a valid target
// with no effect, same as how the mod already handles "no corpse found" gracefully elsewhere; it
// never accidentally kills something it shouldn't.
//
// No Duration or Magnitude - this is an instant, binary effect (kill or fail to find a valid
// target), not a probabilistic one. ChanceSuccess is overridden to always succeed (same pattern as
// VoxsNecromancyEffect) purely so the Chance cost coefficients still produce a real Magicka cost;
// the actual "does this work" decision is made entirely by the ReanimatedLifetime check in
// DoEffect(), not by any roll.

using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;
using VoxsNecromancyMod.AI;

namespace VoxsNecromancyMod
{
    public class DismissUndeadEffect : BaseEntityEffect
    {
        public const string EffectKey = "Dismiss Undead";

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = false;
            properties.SupportChance = true;
            properties.AllowedTargets = EntityEffectBroker.TargetFlags_Other;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            properties.MagicSkill = DFCareer.MagicSkills.Mysticism;
            properties.DisableReflectiveEnumeration = true;

            properties.ChanceCosts = new EffectCosts { CostA = 2, CostB = 8, OffsetGold = 5 };
        }

        public override bool ChanceSuccess
        {
            get { return true; }
        }

        public override string GroupName => EffectKey;
        public override TextFile.Token[] SpellMakerDescription => GetDescription();
        public override TextFile.Token[] SpellBookDescription => GetDescription();

        TextFile.Token[] GetDescription()
        {
            return DaggerfallUnity.Instance.TextProvider.CreateTokens(
                TextFile.Formatting.JustifyCenter,
                GroupName,
                "Instantly destroys undead you yourself have reanimated or subjugated, allied or",
                "hostile. Has no effect on vanilla undead, quest undead, or anything else you",
                "didn't personally raise or command. Always succeeds.");
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

            if (targetBehaviour.GetComponent<ReanimatedLifetime>() == null)
                return;

            if (targetBehaviour.Entity != null && targetBehaviour.Entity.CurrentHealth > 0)
                targetBehaviour.Entity.SetHealth(0);
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                VoxsNecromancy.LogError("DismissUndeadEffect.Create: Failed to get template from effect broker");
                return new EffectBundleSettings();
            }

            IEntityEffect template = effectBroker.GetEffectTemplate(EffectKey);
            EffectSettings templateSettings = new EffectSettings()
            {
                ChanceBase = 100,
                ChancePlus = 0,
                ChancePerLevel = 1,
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
