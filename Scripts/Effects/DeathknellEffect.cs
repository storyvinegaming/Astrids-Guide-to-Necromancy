// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Consumes the nearest valid corpse within radius to restore BOTH health and stamina to the caster
// in one cast, each equal to half the consumed corpse's own typical (average) max health (see
// CorpseFinder.TryGetTypicalMaxHealth) - a bigger corpse restores more. Originally two separate
// spells (Death's Sustenance for health, Death's Endurance for stamina), merged into a single
// effect and later renamed Deathknell.
//
// Same corpse-eligibility override as Corpse Explosion: can consume ANY corpse in the game
// (including undead corpses) regardless of the "Limit to Human Corpses" setting's state, though the
// quest-corpse exclusion (governed by the "Protect Quest Corpses" setting) still applies. Kept
// under Mysticism (not Restoration) for consistency with the rest of the necromancy line despite
// being a healing effect.
//
// Free (no Magicka/gold cost) and no longer available in the Spellmaker/Potion/Item crafting
// stations, and no longer sold at Mages Guild spell sellers - consuming the corpse is the
// cost. Granted only via Astrid's Guide to Necromancy (the book) or embrace_darkness.
//
// SupportChance is left on (always succeeding via the ChanceSuccess override below) purely to keep
// the cost at zero - DFU's own FormulaHelper.CalculateEffectCosts imputes a large "fudge" gold cost
// for any effect with NO active Chance/Magnitude/Duration component at all (see its own comment:
// "Classic does not assign a gold cost when a zero-component effect is the only effect present,
// which seems like a bug" - the modern engine deliberately works around that by fabricating a cost
// instead). Having one active component with all-zero ChanceCosts sidesteps that fudge entirely,
// leaving only the engine's unavoidable minimum floor (5 spellpoints) - as close to genuinely free
// as this spell can get.
//
// Formerly "Necrotic Leech", then "Death's Sustenance" (after the Death's Endurance merge), before
// this rename.
//
// Not a VoxsNecromancyEffect subclass - that base class's chance/duration machinery is
// Reanimate-specific and not needed here.

using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Utility;

namespace VoxsNecromancyMod
{
    public class DeathknellEffect : BaseEntityEffect
    {
        public const string EffectKey = "Deathknell";

        // Same technique Corpse Explosion uses for its Fire impact visual (DaggerfallMissile.cs's
        // own UseSpellBillboardAnims(1, true)/PlayImpactSound), swapped to the Poison missile
        // archive/sound for a green "life drained away" look at the corpse's position instead.
        const int poisonMissileArchive = 377;
        const int poisonImpactRecord = 1;
        const int impactBillboardFramesPerSecond = 15;

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
                "Consumes the nearest corpse in range to restore both your health and stamina in",
                "one cast, each equal to half that creature type's own typical (average) health -",
                "a bigger corpse restores more.");
        }

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

        void DoEffect()
        {
            DaggerfallEntityBehaviour casterBehaviour = GetPeeredEntityBehaviour(manager);
            if (casterBehaviour == null)
                return;

            VoxsNecromancy.ReportNecromancyCrimeIfApplicable();

            DaggerfallLoot corpse = CorpseFinder.FindNearestCorpse(
                casterBehaviour.transform.position, VoxsNecromancy.CorpseDetectionRadius, anyCorpseType: true);

            if (corpse == null)
            {
                DaggerfallUI.AddHUDText("There is no corpse nearby to leech.");
                return;
            }

            int typicalMaxHealth;
            if (!CorpseFinder.TryGetTypicalMaxHealth(corpse, out typicalMaxHealth))
            {
                DaggerfallUI.AddHUDText("That corpse offers nothing to leech.");
                return;
            }

            Vector3 position = corpse.transform.position;

            ReanimationSpawner.ConvertCorpseToLootPile(corpse);

            SpawnLeechVisual(position);

            int amount = typicalMaxHealth / 2;
            caster.Entity.IncreaseHealth(amount);
            caster.Entity.IncreaseFatigue(amount, true);
        }

        static void SpawnLeechVisual(Vector3 position)
        {
            GameObject go = GameObjectHelper.CreateDaggerfallBillboardGameObject(poisonMissileArchive, poisonImpactRecord, null);
            go.transform.position = position;

            Billboard billboard = go.GetComponent<Billboard>();
            billboard.FramesPerSecond = impactBillboardFramesPerSecond;
            billboard.FaceY = true;
            billboard.OneShot = true;

            MeshRenderer meshRenderer = go.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
                meshRenderer.receiveShadows = false;

            if (DaggerfallUI.Instance.DaggerfallAudioSource)
                DaggerfallUI.Instance.DaggerfallAudioSource.PlayClipAtPoint(SoundClips.SpellImpactPoison, position, 1f);
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                VoxsNecromancy.LogError("DeathknellEffect.Create: Failed to get template from effect broker");
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
