// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// A free (no Chance/Duration/Magnitude, no Magicka/gold cost), instant, self-targeted spell that
// immediately teleports every currently active allied undead to the player's side (see
// AllyCombatController.TeleportAllToPlayer). Replaces this spell's earlier behaviour (firing a
// projectile to mark a hostile as the legion's hard-locked focus-fire target) - that mechanic, and
// the "Hunt the Living" long-range dungeon-hunting tier it was meant to complement, have both been
// removed in favour of this single, simpler tool: gather everyone to you on demand, then let Tier 1
// (same-room engagement, always on) handle the fight from there.
//
// Always succeeds (ChanceSuccess overridden true) and has no configurable parameters - nothing for
// SupportDuration/SupportMagnitude to expose, so neither is set.
//
// SupportChance IS enabled (with all-zero ChanceCosts) purely to keep the cost at zero - DFU's own
// FormulaHelper.CalculateEffectCosts imputes a large "fudge" gold cost for any effect with NO
// active Chance/Magnitude/Duration component at all (see its own comment: "Classic does not assign
// a gold cost when a zero-component effect is the only effect present, which seems like a bug" -
// the modern engine deliberately works around that by fabricating a cost instead). Having one
// active component with all-zero ChanceCosts sidesteps that fudge entirely, leaving only the
// engine's unavoidable minimum floor (5 spellpoints) - as close to genuinely free as this spell can
// get.

using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects;
using VoxsNecromancyMod.AI;

namespace VoxsNecromancyMod
{
    public class CommandUndeadEffect : BaseEntityEffect
    {
        public const string EffectKey = "Command Undead";

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
        // (see the file header comment), not to gate anything.
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
                "Instantly summons every allied undead servant you command to your side, wherever",
                "they currently are. Always succeeds.");
        }

        bool hasFired;

        public override void MagicRound()
        {
            base.MagicRound();

            if (hasFired)
                return;
            hasFired = true;

            VoxsNecromancy.ReportNecromancyCrimeIfApplicable();

            DoEffect();
        }

        void DoEffect()
        {
            AllyCombatController.TeleportAllToPlayer();

            DaggerfallUI.AddHUDText("Your legion answers your call.");
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                VoxsNecromancy.LogError("CommandUndeadEffect.Create: Failed to get template from effect broker");
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
