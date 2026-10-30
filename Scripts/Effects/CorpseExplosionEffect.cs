// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Detonates the nearest valid corpse within radius, dealing damage to everything caught in a fixed
// blast radius around it, equal to the consumed corpse's own typical (average) max health (see
// CorpseFinder.TryGetTypicalMaxHealth) - a bigger, tougher corpse makes for a bigger explosion.
// Unlike Reanimate, this can consume ANY corpse in the game (including undead corpses) regardless
// of the "Limit to Human Corpses" setting's state - CorpseFinder's anyCorpseType override bypasses
// Reanimate's stricter human/undead-exclusion rules, though the quest-corpse exclusion still
// applies. The corpse's loot still converts to a normal dropped-loot pile, same as every other
// corpse-consuming spell in this mod.
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
    public class CorpseExplosionEffect : BaseEntityEffect
    {
        public const string EffectKey = "Corpse Explosion";

        // Fixed blast radius around the consumed corpse - not a spell parameter.
        const float explosionRadius = 8f;

        // Same texture archive/record and impact sound DaggerfallMissile.cs itself uses for a Fire
        // missile's impact (UseSpellBillboardAnims(1, true) on archive 375, SoundClips.SpellImpactFire)
        // - reused directly here rather than spawning/detonating a real missile, since we already do
        // our own damage via OverlapSphere and don't want a second, duplicate damage application.
        const int fireMissileArchive = 375;
        const int fireImpactRecord = 1;
        const int impactBillboardFramesPerSecond = 15;

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = false;
            properties.AllowedTargets = TargetTypes.CasterOnly;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_All;
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
                "Consumes the nearest corpse in range and detonates it, dealing Fire damage to",
                "everything caught in the blast (sparing you and your allies) equal to that",
                "creature type's own typical (average) health - a bigger brute means a bigger",
                "explosion.");
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
                DaggerfallUI.AddHUDText("There is no corpse nearby to detonate.");
                return;
            }

            int damage;
            if (!CorpseFinder.TryGetTypicalMaxHealth(corpse, out damage))
            {
                DaggerfallUI.AddHUDText("That corpse offers nothing to explode.");
                return;
            }

            Vector3 position = corpse.transform.position;

            ReanimationSpawner.ConvertCorpseToLootPile(corpse);

            SpawnExplosionVisual(position);

            Collider[] hits = Physics.OverlapSphere(position, explosionRadius);
            for (int i = 0; i < hits.Length; i++)
            {
                DaggerfallEntityBehaviour targetBehaviour = hits[i].GetComponent<DaggerfallEntityBehaviour>();
                if (targetBehaviour == null || targetBehaviour.Entity == null || targetBehaviour.Entity.CurrentHealth <= 0)
                    continue;

                // Spare the caster and your own reanimated/subjugated undead - real DFU AI treats
                // any damage source as an attacker regardless of team, so hitting an ally here would
                // otherwise immediately flip it hostile, and standing next to your own corpse
                // shouldn't hurt you either (matches Volatile Undead's own protections).
                if (targetBehaviour == GameManager.Instance.PlayerEntityBehaviour)
                    continue;
                if (targetBehaviour.Entity.Team == MobileTeams.PlayerAlly)
                    continue;

                SpawnExplosionVisual(targetBehaviour.transform.position);
                targetBehaviour.DamageHealthFromSource(this, damage, false, position);
            }
        }

        // Reuses the exact same billboard texture/sound a real Fire missile plays on impact
        // (DaggerfallMissile.cs's UseSpellBillboardAnims/PlayImpactSound), so Corpse Explosion looks
        // and sounds like a fireball detonated at the corpse's position without spawning a real
        // missile (which would need its own damage payload, duplicating our own OverlapSphere loop).
        // Called once at the corpse's own position for the main blast, then again at each individual
        // damaged target's position so the player can see exactly what got caught in it.
        static void SpawnExplosionVisual(Vector3 position)
        {
            GameObject go = GameObjectHelper.CreateDaggerfallBillboardGameObject(fireMissileArchive, fireImpactRecord, null);
            go.transform.position = position;

            Billboard billboard = go.GetComponent<Billboard>();
            billboard.FramesPerSecond = impactBillboardFramesPerSecond;
            billboard.FaceY = true;
            billboard.OneShot = true;

            MeshRenderer meshRenderer = go.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
                meshRenderer.receiveShadows = false;

            if (DaggerfallUI.Instance.DaggerfallAudioSource)
                DaggerfallUI.Instance.DaggerfallAudioSource.PlayClipAtPoint(SoundClips.SpellImpactFire, position, 1f);
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                VoxsNecromancy.LogError("CorpseExplosionEffect.Create: Failed to get template from effect broker");
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
                ElementType = ElementTypes.Fire,
                Name = EffectKey,
                IconIndex = 12,
                Effects = new EffectEntry[] { effectEntry },
            };

            return bundle;
        }
    }
}
