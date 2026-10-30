// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// A touch/target spell (like Subjugate Undead/Dismiss Undead) that detonates one of the caster's
// own allied undead, dealing AOE damage equal to the sacrificed ally's full current health to
// everything nearby - reuses Corpse Explosion's own fire-impact visual/sound and OverlapSphere
// damage loop, just centered on the sacrificed ally instead of a consumed corpse.
//
// Valid target is any currently allied undead (Team == MobileTeams.PlayerAlly - covers both
// Reanimate and Subjugate Undead allies alike, same check AllyCombatController.IsAlly uses).
// Targeting anything else (hostile undead, the caster, any other creature) has no effect, matching
// how Dismiss Undead handles an invalid target.
//
// Unlike Corpse Explosion, the blast explicitly spares the caster and all OTHER allied undead -
// sacrificing one servant shouldn't accidentally kill the rest of your legion or yourself.
//
// Free (no Magicka/gold cost) and no longer available in the Spellmaker/Potion/Item crafting
// stations, and no longer sold at Mages Guild spell sellers - sacrificing the ally is the
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
    public class VolatileUndeadEffect : BaseEntityEffect
    {
        public const string EffectKey = "Volatile Undead";

        // Same technique Corpse Explosion uses for its Fire impact visual (DaggerfallMissile.cs's
        // own UseSpellBillboardAnims(1, true)/PlayImpactSound).
        const int fireMissileArchive = 375;
        const int fireImpactRecord = 1;
        const int impactBillboardFramesPerSecond = 15;

        const float explosionRadius = 8f;

        public override void SetProperties()
        {
            properties.Key = EffectKey;
            properties.ShowSpellIcon = false;
            properties.AllowedTargets = EntityEffectBroker.TargetFlags_Other;
            properties.AllowedElements = EntityEffectBroker.ElementFlags_All;
            properties.AllowedCraftingStations = MagicCraftingStations.None;
            properties.MagicSkill = DFCareer.MagicSkills.Mysticism;
            properties.SupportChance = true;
            properties.DisableReflectiveEnumeration = true;

            properties.ChanceCosts = new EffectCosts { CostA = 0, CostB = 0, OffsetGold = 0 };
        }

        // Always succeeds - Chance is only enabled to avoid the zero-active-component cost fudge
        // (see the file header comment), not to gate anything - whether this "works" is decided
        // entirely by the Team check in DoEffect(), not by a roll. There's no Chance UI exposed
        // anyway (no Spellmaker access).
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
                "Detonates one of your own allied undead (Reanimated or Subjugated), dealing Fire",
                "damage to everything nearby equal to its full current health. Spares you and",
                "your other allied undead - only unrelated/hostile creatures take damage.");
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

            DoEffect(targetBehaviour);
        }

        void DoEffect(DaggerfallEntityBehaviour targetBehaviour)
        {
            EnemyEntity targetEntity = targetBehaviour.Entity as EnemyEntity;
            if (targetEntity == null || targetEntity.Team != MobileTeams.PlayerAlly || targetEntity.CurrentHealth <= 0)
            {
                DaggerfallUI.AddHUDText("You have no allied undead servant to detonate there.");
                return;
            }

            Vector3 position = targetBehaviour.transform.position;
            int damage = targetEntity.CurrentHealth;

            targetEntity.SetHealth(0);

            SpawnExplosionVisual(position);

            Collider[] hits = Physics.OverlapSphere(position, explosionRadius);
            for (int i = 0; i < hits.Length; i++)
            {
                DaggerfallEntityBehaviour hitBehaviour = hits[i].GetComponent<DaggerfallEntityBehaviour>();
                if (hitBehaviour == null || hitBehaviour.Entity == null || hitBehaviour.Entity.CurrentHealth <= 0)
                    continue;

                // Spare the caster and every other allied undead - only unrelated/hostile creatures
                // caught in the blast take damage.
                if (hitBehaviour == GameManager.Instance.PlayerEntityBehaviour)
                    continue;
                if (hitBehaviour.Entity.Team == MobileTeams.PlayerAlly)
                    continue;

                SpawnExplosionVisual(hitBehaviour.transform.position);
                hitBehaviour.DamageHealthFromSource(this, damage, false, position);
            }
        }

        // Called once at the sacrificed ally's own position for the main blast, then again at each
        // individual damaged target's position so the player can see exactly what got caught in it -
        // same technique Corpse Explosion uses.
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
                VoxsNecromancy.LogError("VolatileUndeadEffect.Create: Failed to get template from effect broker");
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
                ElementType = ElementTypes.Fire,
                Name = EffectKey,
                IconIndex = 12,
                Effects = new EffectEntry[] { effectEntry },
            };

            return bundle;
        }
    }
}
