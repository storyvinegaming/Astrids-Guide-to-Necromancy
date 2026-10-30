// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// A free spell (no Magicka/gold cost) that trades a fixed slice of the caster's own health for
// magicka - 10% of MaxHealth taken, 20% of MaxMagicka restored, no corpse or target involved. Not
// available in the Spellmaker or Mages Guild - granted only via Astrid's Guide to Necromancy (the
// book) or the embrace_darkness console command, matching the other effects in this mod (none of
// which are sold or Spellmaker-customizable anymore).
//
// The health cost is real and uncapped - it can kill the caster if their current health is below
// the cost, same as any other source of damage. Uses SetHealth directly rather than DecreaseHealth
// so an active Shield effect can't mitigate your own self-imposed cost (Shield mitigating incoming
// damage from hostiles makes sense; mitigating a price you're deliberately paying yourself
// doesn't).
//
// SupportChance is left on (always succeeding via the ChanceSuccess override below) purely to keep
// the cost at zero - DFU's own FormulaHelper.CalculateEffectCosts imputes a large "fudge" gold cost
// for any effect with NO active Chance/Magnitude/Duration component at all (see its own comment:
// "Classic does not assign a gold cost when a zero-component effect is the only effect present,
// which seems like a bug" - the modern engine deliberately works around that by fabricating a cost
// instead). Having one active component with all-zero ChanceCosts sidesteps that fudge entirely,
// leaving only the engine's unavoidable minimum floor (5 spellpoints) - as close to genuinely free
// as this spell can get.

using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;

namespace VoxsNecromancyMod
{
    public class DarkDealEffect : BaseEntityEffect
    {
        public const string EffectKey = "Dark Deal";

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = false;
            properties.AllowedTargets = TargetTypes.CasterOnly;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_MagicOnly;
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            properties.MagicSkill = DFCareer.MagicSkills.Mysticism;
            properties.SupportChance = true;
            properties.DisableReflectiveEnumeration = true;

            properties.ChanceCosts = new EffectCosts { CostA = 0, CostB = 0, OffsetGold = 0 };
        }

        // Always succeeds - Chance is only enabled to avoid the zero-active-component cost fudge
        // (see the file header comment), not to gate anything. There's no Chance UI exposed anyway
        // (no Spellmaker access), so this override is purely functional.
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
                "Strikes a dark deal: costs 10% of your maximum health, restores 20% of your",
                "maximum magicka.");
        }

        bool hasFired;

        public override void MagicRound()
        {
            base.MagicRound();

            if (hasFired)
                return;
            hasFired = true;

            DaggerfallEntityBehaviour casterBehaviour = GetPeeredEntityBehaviour(manager);
            if (!casterBehaviour)
                return;

            VoxsNecromancy.ReportNecromancyCrimeIfApplicable();

            DoEffect();
        }

        void DoEffect()
        {
            int hpCost = Mathf.Max(1, Mathf.CeilToInt(caster.Entity.MaxHealth * 0.1f));
            int manaGain = Mathf.Max(1, Mathf.CeilToInt(caster.Entity.MaxMagicka * 0.2f));

            caster.Entity.SetHealth(caster.Entity.CurrentHealth - hpCost);
            caster.Entity.IncreaseMagicka(manaGain);

            DaggerfallUI.AddHUDText(string.Format("You strike a dark deal: -{0} health, +{1} magicka.", hpCost, manaGain));
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                VoxsNecromancy.LogError("DarkDealEffect.Create: Failed to get template from effect broker");
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
