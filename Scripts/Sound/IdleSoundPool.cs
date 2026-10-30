// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Periodic idle ambient sounds for skeleton, zombie, ghost/wraith, vampire, lich/ancient lich, and
// vampire ancient enemies generally (not just reanimated ones), modeled on DFU's own periodic idle
// "attract sound" timer (EnemySounds.PlayAttractSound()/StartWaiting(), randomized cooldown) but
// driven independently from its own component rather than patching EnemySounds. Wraith shares
// Ghost's pool and Ancient Lich shares Lich's pool (same precedent as skeleton/zombie each having
// their own dedicated pool); Vampire and Vampire Ancient each get their own separate pool - baseline
// Vampire's pool is never used for Vampire Ancient, and vice versa.
//
// Pools are loaded from this mod's own bundled assets by filename prefix (Mod.AssetNames +
// GetAsset<AudioClip>()), not the 1:1-only SoundReplacement system - so dropping more
// correctly-prefixed files into Sounds/ and rebuilding grows a pool without any code changes.
//
// "Funny Mode" (mod setting, off by default) adds a set of undead_elvis_*.ogg clips to the
// skeleton/zombie idle rotation, and a single bonus clip mixed into the vampire, lich/ancient lich,
// and vampire ancient pools each (vampblood.ogg, lich_funny.ogg, vampire_ancient_funny.ogg
// respectively) - never mixed across pools or into the Ghost/Wraith pool. All funny clips are
// scanned/loaded unconditionally at Initialize() time (cheap, and avoids depending on
// LoadSettings() having already run, since Initialize() is called before mod.LoadSettings() in
// VoxsNecromancy.Init()); whether they're actually mixed into the pick each tick is decided live
// from VoxsNecromancy.FunnyMode, matching the same live-read pattern already used for
// UseCustomIdleSounds/interval settings so toggling the setting takes effect without a restart.
//
// The skeleton/zombie attack sound swap also goes through this same asset-loading path rather
// than SoundReplacement: SoundReplacement's mod-bundle lookup requires an exact name match against
// ModManager.GetAssetName()'s normalized (lowercased, extension-kept) form, and there's no
// straightforward way to guarantee our bundled clip's name normalizes to exactly "EnemySkeletonAttack"
// with no extension. Instead we directly set EnemySounds' own private attackClip field via
// reflection - EnemySounds.IsReady() only assigns attackClip "if (!attackClip)", so setting it
// first (before that lazy default ever runs) makes our override stick with no further code needed
// in EnemySounds itself.
//
// EnemySounds also runs its OWN periodic idle "attract sound" timer (PlayAttractSound(), using
// private fields moveClip/barkClip) completely independently of this component's Poll() loop -
// without suppressing it, vanilla idle groans keep playing alongside our custom pool. IsReady()
// uses the exact same "if (!field) field = ..." guard for moveClip/barkClip as it does for
// attackClip, but setting them to null would NOT stick: IsReady() runs on every FixedUpdate (many
// times a second) and "!null" is true, so it would just reassign the vanilla clip back on the very
// next tick. Setting them to a non-null but silent AudioClip exploits the same guard successfully -
// PlayAttractSound() still fires on its own timer, but plays silence instead of the vanilla clip.
//
// ApplyAttackSoundState/ApplyVanillaAttractState both recompute and re-apply the correct clip every
// poll tick (rather than a one-shot "already touched, never revisit" flag) so behavior stays correct
// across live setting changes, with three exhaustive states per relevant field:
//   - Mute All Follower Noises on: real silence (silentClip) - the vanilla original is suppressed
//     too, not just our own replacement, since muted should mean nothing at all plays.
//   - Mute off, Enable Custom Undead Noises on: our own replacement clip (attackClip) or silentClip
//     (moveClip/barkClip, so only our idle pool plays instead of a double groan).
//   - Mute off, Enable Custom Undead Noises off: field cleared back to null, which lets
//     EnemySounds' own lazy "if (!field) field = <vanilla default>" assignment in IsReady() restore
//     the vanilla clip on its own very next tick - vanilla sounds play exactly as they would with
//     this mod absent.

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Utility.ModSupport;

namespace VoxsNecromancyMod.Sound
{
    public class IdleSoundPool : MonoBehaviour
    {
        const float pollInterval = 2f;

        List<AudioClip> skeletonPool = new List<AudioClip>();
        List<AudioClip> zombiePool = new List<AudioClip>();
        List<AudioClip> ghostPool = new List<AudioClip>();
        List<AudioClip> vampirePool = new List<AudioClip>();
        List<AudioClip> lichPool = new List<AudioClip>();
        List<AudioClip> vampireAncientPool = new List<AudioClip>();

        List<AudioClip> funnySkeletonZombiePool = new List<AudioClip>();
        AudioClip funnyVampireClip;
        AudioClip funnyLichClip;
        AudioClip funnyVampireAncientClip;

        AudioClip skeletonAttackClip;
        AudioClip zombieAttackClip;

        Dictionary<DaggerfallEntityBehaviour, float> nextPlayTime = new Dictionary<DaggerfallEntityBehaviour, float>();

        bool hasLoggedFirstIdlePlay;
        bool hasLoggedMissingAudioSource;
        bool hasLoggedFirstAttackOverride;
        bool hasLoggedFirstAttractSuppress;

        static readonly FieldInfo attackClipField = typeof(EnemySounds).GetField("attackClip", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo moveClipField = typeof(EnemySounds).GetField("moveClip", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo barkClipField = typeof(EnemySounds).GetField("barkClip", BindingFlags.NonPublic | BindingFlags.Instance);

        // Silent placeholder used to permanently occupy EnemySounds' moveClip/barkClip fields (see
        // the file-header comment above for why null wouldn't stick here the way it does for
        // attackClip).
        static readonly AudioClip silentClip = AudioClip.Create("VoxsNecromancySilence", 1, 1, 44100, false);

        public void Initialize(Mod mod)
        {
            LoadPool(mod, "skeleton_idle_", skeletonPool);
            LoadPool(mod, "zombie_idle_", zombiePool);
            LoadPool(mod, "ghost_idle_", ghostPool);
            LoadPool(mod, "vampire_idle_", vampirePool);
            LoadPool(mod, "lich_idle_", lichPool);
            LoadPool(mod, "vampire_ancient_idle_", vampireAncientPool);

            LoadPool(mod, "undead_elvis_", funnySkeletonZombiePool);
            funnyVampireClip = LoadSingle(mod, "vampblood");
            funnyLichClip = LoadSingle(mod, "lich_funny");
            funnyVampireAncientClip = LoadSingle(mod, "vampire_ancient_funny");

            skeletonAttackClip = LoadSingle(mod, "enemyskeletonattack");
            zombieAttackClip = LoadSingle(mod, "enemyzombieattack");

            // Unconditional (not gated by the Logging setting) - this is a one-time startup
            // diagnostic, not per-frame noise, so it should always be visible regardless of the
            // user's chosen log level.
            Debug.LogFormat(
                "Vox's Necromancy: IdleSoundPool loaded {0} skeleton idle, {1} zombie idle, {2} ghost idle, {3} vampire idle, {4} lich idle, {5} vampire ancient idle clip(s); {6} funny skeleton/zombie clip(s), vampire funny clip {7}, lich funny clip {8}, vampire ancient funny clip {9}; skeleton attack override {10}; zombie attack override {11}.",
                skeletonPool.Count, zombiePool.Count, ghostPool.Count, vampirePool.Count, lichPool.Count, vampireAncientPool.Count,
                funnySkeletonZombiePool.Count, funnyVampireClip != null ? "found" : "MISSING",
                funnyLichClip != null ? "found" : "MISSING", funnyVampireAncientClip != null ? "found" : "MISSING",
                skeletonAttackClip != null ? "found" : "MISSING",
                zombieAttackClip != null ? "found" : "MISSING");

            if (skeletonPool.Count == 0 || zombiePool.Count == 0 || ghostPool.Count == 0 || skeletonAttackClip == null || zombieAttackClip == null)
            {
                string[] assetNames = mod.AssetNames;
                Debug.LogWarningFormat("Vox's Necromancy: IdleSoundPool - at least one pool/clip failed to load, dumping all {0} bundled asset names for diagnosis:", assetNames.Length);
                for (int i = 0; i < assetNames.Length; i++)
                    Debug.LogWarning("Vox's Necromancy: IdleSoundPool asset -> " + assetNames[i]);
            }

            if (attackClipField == null)
                VoxsNecromancy.LogWarning("IdleSoundPool: could not find EnemySounds.attackClip via reflection - attack sound override disabled.");
            if (moveClipField == null || barkClipField == null)
                VoxsNecromancy.LogWarning("IdleSoundPool: could not find EnemySounds.moveClip/barkClip via reflection - vanilla idle sound suppression disabled.");
        }

        static void LoadPool(Mod mod, string prefix, List<AudioClip> pool)
        {
            string[] assetNames = mod.AssetNames;
            for (int i = 0; i < assetNames.Length; i++)
            {
                if (assetNames[i].IndexOf(prefix, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                AudioClip clip = mod.GetAsset<AudioClip>(assetNames[i]);
                if (clip != null)
                    pool.Add(clip);
            }
        }

        static AudioClip LoadSingle(Mod mod, string nameContains)
        {
            string[] assetNames = mod.AssetNames;
            for (int i = 0; i < assetNames.Length; i++)
            {
                if (assetNames[i].IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                AudioClip clip = mod.GetAsset<AudioClip>(assetNames[i]);
                if (clip != null)
                    return clip;
            }
            return null;
        }

        void Start()
        {
            InvokeRepeating("Poll", pollInterval, pollInterval);
        }

        void Poll()
        {
            bool muted = VoxsNecromancy.MuteAllFollowerNoises;
            bool useCustom = VoxsNecromancy.UseCustomIdleSounds;

            foreach (DaggerfallEntityBehaviour behaviour in ActiveGameObjectDatabase.GetActiveEnemyBehaviours())
            {
                if (behaviour == null || behaviour.Entity == null || behaviour.Entity.CurrentHealth <= 0)
                    continue;

                EnemyEntity enemyEntity = behaviour.Entity as EnemyEntity;
                if (enemyEntity == null)
                    continue;

                int mobileEnemyId = enemyEntity.MobileEnemy.ID;

                ApplyAttackSoundState(behaviour, mobileEnemyId, muted, useCustom);
                ApplyVanillaAttractState(behaviour, mobileEnemyId, muted, useCustom);

                if (muted || !useCustom)
                    continue;

                List<AudioClip> pool = GetIdlePoolFor(mobileEnemyId);
                if (pool == null || pool.Count == 0)
                    continue;

                EnemySenses senses = behaviour.GetComponent<EnemySenses>();
                if (senses != null && senses.TargetInSight)
                    continue; // don't groan mid-combat

                float minCooldown = VoxsNecromancy.IdleSoundIntervalMin;
                float maxCooldown = VoxsNecromancy.IdleSoundIntervalMax;

                float now = Time.time;
                float scheduledTime;
                if (!nextPlayTime.TryGetValue(behaviour, out scheduledTime))
                {
                    // First time we've seen this enemy - stagger its first play rather than
                    // firing immediately, so a room full of skeletons doesn't groan in unison.
                    nextPlayTime[behaviour] = now + UnityEngine.Random.Range(0f, maxCooldown);
                    continue;
                }

                if (now < scheduledTime)
                    continue;

                DaggerfallAudioSource audioSource = behaviour.GetComponent<DaggerfallAudioSource>();
                if (audioSource != null)
                {
                    AudioClip clip = PickClip(mobileEnemyId, pool);
                    audioSource.AudioSource.PlayOneShot(clip, DaggerfallUnity.Settings.SoundVolume);
                    if (!hasLoggedFirstIdlePlay)
                    {
                        hasLoggedFirstIdlePlay = true;
                        Debug.LogFormat("Vox's Necromancy: IdleSoundPool played idle clip '{0}' for enemy ID {1}.", clip.name, mobileEnemyId);
                    }
                }
                else if (!hasLoggedMissingAudioSource)
                {
                    hasLoggedMissingAudioSource = true;
                    Debug.LogWarning("Vox's Necromancy: IdleSoundPool found no DaggerfallAudioSource component on an enemy that should have one.");
                }

                nextPlayTime[behaviour] = now + UnityEngine.Random.Range(minCooldown, maxCooldown);
            }
        }

        // Mixes in the Funny Mode clips (if currently enabled) without permanently mutating the
        // base pool lists, so toggling Funny Mode off at runtime immediately stops them being
        // picked without needing to reload/rebuild the pools.
        AudioClip PickClip(int mobileEnemyId, List<AudioClip> basePool)
        {
            if (!VoxsNecromancy.FunnyMode)
                return basePool[UnityEngine.Random.Range(0, basePool.Count)];

            if ((mobileEnemyId == (int)MobileTypes.SkeletalWarrior || mobileEnemyId == (int)MobileTypes.Zombie) && funnySkeletonZombiePool.Count > 0)
            {
                int total = basePool.Count + funnySkeletonZombiePool.Count;
                int pick = UnityEngine.Random.Range(0, total);
                return pick < basePool.Count ? basePool[pick] : funnySkeletonZombiePool[pick - basePool.Count];
            }

            if (mobileEnemyId == (int)MobileTypes.Vampire && funnyVampireClip != null)
            {
                int pick = UnityEngine.Random.Range(0, basePool.Count + 1);
                return pick < basePool.Count ? basePool[pick] : funnyVampireClip;
            }

            if ((mobileEnemyId == (int)MobileTypes.Lich || mobileEnemyId == (int)MobileTypes.AncientLich) && funnyLichClip != null)
            {
                int pick = UnityEngine.Random.Range(0, basePool.Count + 1);
                return pick < basePool.Count ? basePool[pick] : funnyLichClip;
            }

            if (mobileEnemyId == (int)MobileTypes.VampireAncient && funnyVampireAncientClip != null)
            {
                int pick = UnityEngine.Random.Range(0, basePool.Count + 1);
                return pick < basePool.Count ? basePool[pick] : funnyVampireAncientClip;
            }

            return basePool[UnityEngine.Random.Range(0, basePool.Count)];
        }

        // Recomputes and re-applies attackClip every poll tick - see the file-header comment for the
        // three exhaustive states (muted -> real silence, custom-on -> our replacement, custom-off ->
        // cleared back to null so EnemySounds' own lazy default restores the vanilla clip).
        void ApplyAttackSoundState(DaggerfallEntityBehaviour behaviour, int mobileEnemyId, bool muted, bool useCustom)
        {
            if (attackClipField == null)
                return;

            AudioClip customReplacement = null;
            if (mobileEnemyId == (int)MobileTypes.SkeletalWarrior)
                customReplacement = skeletonAttackClip;
            else if (mobileEnemyId == (int)MobileTypes.Zombie)
                customReplacement = zombieAttackClip;

            // No attack sound override exists for this enemy type at all - leave it alone entirely,
            // regardless of settings (matches the pre-existing scope: only skeleton/zombie have a
            // custom attack clip to begin with).
            if (customReplacement == null)
                return;

            EnemySounds enemySounds = behaviour.GetComponent<EnemySounds>();
            if (enemySounds == null)
                return;

            AudioClip desired = muted ? silentClip : (useCustom ? customReplacement : null);
            attackClipField.SetValue(enemySounds, desired);

            if (!hasLoggedFirstAttackOverride && desired == customReplacement)
            {
                hasLoggedFirstAttackOverride = true;
                Debug.LogFormat("Vox's Necromancy: IdleSoundPool set attackClip to '{0}' for enemy ID {1}.", desired.name, mobileEnemyId);
            }
        }

        // Recomputes and re-applies moveClip/barkClip every poll tick - same three-state shape as
        // ApplyAttackSoundState above, but silentClip covers both the muted AND custom-on cases here
        // (custom-on still needs vanilla suppressed so it doesn't groan alongside our own pool).
        void ApplyVanillaAttractState(DaggerfallEntityBehaviour behaviour, int mobileEnemyId, bool muted, bool useCustom)
        {
            if (moveClipField == null || barkClipField == null)
                return;

            // Only enemy types this mod has an idle pool for are touched at all - everything else is
            // left entirely alone regardless of settings.
            if (GetIdlePoolFor(mobileEnemyId) == null)
                return;

            EnemySounds enemySounds = behaviour.GetComponent<EnemySounds>();
            if (enemySounds == null)
                return;

            AudioClip desired = (muted || useCustom) ? silentClip : null;
            moveClipField.SetValue(enemySounds, desired);
            barkClipField.SetValue(enemySounds, desired);

            if (!hasLoggedFirstAttractSuppress && desired == silentClip)
            {
                hasLoggedFirstAttractSuppress = true;
                Debug.LogFormat("Vox's Necromancy: IdleSoundPool silenced vanilla moveClip/barkClip for enemy ID {0}.", mobileEnemyId);
            }
        }

        List<AudioClip> GetIdlePoolFor(int mobileEnemyId)
        {
            if (mobileEnemyId == (int)MobileTypes.SkeletalWarrior)
                return skeletonPool;
            if (mobileEnemyId == (int)MobileTypes.Zombie)
                return zombiePool;
            if (mobileEnemyId == (int)MobileTypes.Ghost || mobileEnemyId == (int)MobileTypes.Wraith)
                return ghostPool;
            if (mobileEnemyId == (int)MobileTypes.Vampire)
                return vampirePool;
            if (mobileEnemyId == (int)MobileTypes.Lich || mobileEnemyId == (int)MobileTypes.AncientLich)
                return lichPool;
            if (mobileEnemyId == (int)MobileTypes.VampireAncient)
                return vampireAncientPool;
            return null;
        }
    }
}
