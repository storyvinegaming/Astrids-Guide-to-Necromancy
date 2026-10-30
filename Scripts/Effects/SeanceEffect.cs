// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// A free spell (no Magicka/gold cost - see SetProperties) that only functions inside a dungeon, and
// only once the caster has slain VoxsNecromancy.SeanceKillRequirement creatures since entering the
// CURRENT dungeon (VoxsNecromancy.SeanceKillCount, tracked via EnemyDeath.OnEnemyDeath and reset on
// PlayerEnterExit.OnTransitionDungeonInterior - see VoxsNecromancy.cs). On a successful cast,
// despawns any previously-summoned Seance ghost (SeanceGhostGuide.DespawnActive), then summons a new
// one and points it at the nearest live quest resource (Person/Item/Foe) placed in this dungeon - or,
// when no quest resource is placed here at all, at the dungeon's own start marker instead, so the
// shade guides the player back to the entrance rather than the cast fizzling.
//
// SupportChance is left on (always succeeding via ChanceSuccess below) purely to dodge DFU's own
// zero-active-component cost fudge, same trick as Dark Deal/Corpse Explosion/Deathknell.

using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Formulas;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Utility;
using VoxsNecromancyMod.AI;
using VoxsNecromancyMod.UI;

namespace VoxsNecromancyMod
{
    public class SeanceEffect : BaseEntityEffect
    {
        public const string EffectKey = "Seance";

        // Single switch between the voxel-grid A* guide (current) and the legacy NavMesh-based guide
        // (SeanceGhostGuideLegacyNavMesh.cs, kept fully intact and untouched) - flip to false to roll
        // back if the new pathfinder ever needs to be backed out. Nothing else in this file needs to
        // change either way; both guides expose the same Initialize(Transform)/DespawnActive() shape.
        const bool UseVoxelPathfinding = true;

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
                "Only functions within a dungeon, and only once you have slain",
                string.Format("{0} creatures since entering it.", VoxsNecromancy.SeanceKillRequirement),
                "Summons a restless shade that seeks out whatever this dungeon's quest still",
                "demands of you - or leads you back to the entrance if nothing does - forcing",
                "open any door in its way, locked or not. Casting again dismisses the previous",
                "shade. It fades after 30 rounds + 1 per caster level.");
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

            DoEffect(casterBehaviour);
        }

        void DoEffect(DaggerfallEntityBehaviour casterBehaviour)
        {
            if (!GameManager.Instance.IsPlayerInsideDungeon)
            {
                DaggerfallUI.AddHUDText("The dead do not stir beneath open sky - Seance requires a dungeon.");
                RefundCastingCost();
                return;
            }

            if (!VoxsNecromancy.IgnoreSeanceKillRequirement && VoxsNecromancy.SeanceKillCount < VoxsNecromancy.SeanceKillRequirement)
            {
                DaggerfallUI.AddHUDText(string.Format(
                    "The dead are not restless enough here yet ({0}/{1} slain).",
                    VoxsNecromancy.SeanceKillCount, VoxsNecromancy.SeanceKillRequirement));
                RefundCastingCost();
                return;
            }

            VoxsNecromancy.ReportNecromancyCrimeIfApplicable();

            // Prepares whichever navigation backend is active for this dungeon now that the cast is
            // actually going through - not gated on its result, since a failed/empty setup still
            // leaves the ghost spawning and simply idling, matching the no-quest-target outcome below.
            if (UseVoxelPathfinding)
                SeanceDungeonGrid.EnsureForCurrentDungeon();
            else
                SeanceDungeonNavMesh.EnsureBakedForCurrentDungeon();

            if (UseVoxelPathfinding)
                SeanceGhostGuide.DespawnActive();
            else
                SeanceGhostGuideLegacyNavMesh.DespawnActive();
            SeanceQuestMarker.DespawnActive();

            Transform target = FindNearestQuestTarget(casterBehaviour.transform.position);
            int roundsRemaining = 30 + caster.Entity.Level;

            // No quest resource placed in this dungeon - guide the player back OUT instead. The
            // dungeon's own start marker (DaggerfallDungeon.StartMarker, the spot the player appears
            // at on entry) is the entrance, so the shade still has real work to do: lead the way home.
            bool guidingToEntrance = false;
            if (target == null)
            {
                DaggerfallDungeon dungeon = GameManager.Instance.PlayerEnterExit != null
                    ? GameManager.Instance.PlayerEnterExit.Dungeon
                    : null;
                if (dungeon != null && dungeon.StartMarker != null)
                {
                    target = dungeon.StartMarker.transform;
                    guidingToEntrance = true;
                }
            }

            if (target == null)
            {
                // No quest target AND no start marker (a should-never-happen layout defect) - refund
                // and report failure the same way the mod's other spells report a failed cast (Seance
                // itself costs 0 Magicka/gold - see SetProperties - so this is a no-op today, but
                // stays correct if that ever changes).
                RefundCastingCost();
                DaggerfallUI.AddHUDText("The spirits find nothing to seek here.");
                return;
            }

            // Created independent of whether the ghost itself successfully spawns below - a backup
            // for the player to see where the quest target is even if the ghost's own pathing (or its
            // spawn entirely) fails, not just a companion to a working ghost.
            SeanceQuestMarker.Create(target, roundsRemaining);

            // Offset from the caster's own position - spawning exactly on top of the player leaves
            // the ghost invisible (camera-clipped inside the player's own collision) until it moves
            // away, which never happens at all if no quest target was found to walk toward.
            Vector3 spawnPosition = casterBehaviour.transform.position + casterBehaviour.transform.forward * 2f;

            GameObject[] spawned = GameObjectHelper.CreateFoeGameObjects(
                spawnPosition, MobileTypes.Ghost, 1, MobileReactions.Passive, null, alliedToPlayer: true);

            if (spawned == null || spawned.Length == 0)
            {
                VoxsNecromancy.LogError("SeanceEffect: CreateFoeGameObjects returned no result.");
                RefundCastingCost();
                return;
            }

            GameObject ghost = spawned[0];

            SeanceGhostLifetime lifetime = ghost.AddComponent<SeanceGhostLifetime>();
            lifetime.RoundsRemaining = roundsRemaining;

            // Must activate before Initialize() - CreateFoeGameObjects returns the ghost inactive, and
            // Unity defers Awake() on newly-added components until the GameObject is actually active,
            // so a guide's own Awake()-populated fields would still be null if Initialize() ran any
            // earlier than this.
            ghost.SetActive(true);

            // Initialize() itself starts the (possibly async/coroutine-driven) path search - the
            // follow-me sound fires right after, so it plays at spawn, alongside the search starting,
            // matching the "gathering" framing this sound has always paired with.
            if (UseVoxelPathfinding)
                ghost.AddComponent<SeanceGhostGuide>().Initialize(target);
            else
                ghost.AddComponent<SeanceGhostGuideLegacyNavMesh>().Initialize(target);

            VoxsNecromancy.PlaySeanceFollowMeSound();

            DaggerfallUI.AddHUDText(guidingToEntrance
                ? "No quest calls from these depths - the shade turns back toward daylight."
                : "A pale shade rises, drawn toward something unseen.");
        }

        // Nearest live quest resource to the caster - null is a valid "nothing placed here right now"
        // outcome, not a failure (the caller then guides to the entrance instead). Public so the
        // seance_pathcheck console command resolves its target through the exact same logic.
        //
        // Two filters, both needed: IsFoeDead alone is NOT sufficient - a KILLED quest target's
        // QuestResourceBehaviour was observed surviving, not flagged dead, parked at (354.7, -2863.9,
        // 393.4) - nearly 3km below the dungeon - which sent the ghost chasing an unreachable point in
        // the void (goal snapping can't reject it either: empty space trivially reads "open" to a
        // physics probe). So any resource parked outside the dungeon's own footprint is rejected too:
        // GetPlayerBlockIndex returns -1 for positions outside every RDB block (it's an XZ rect check
        // only - its own comment notes blocks have no vertical extents - hence the additional Y bound).
        public static Transform FindNearestQuestTarget(Vector3 fromPosition)
        {
            DaggerfallDungeon dungeon = GameManager.Instance.PlayerEnterExit != null
                ? GameManager.Instance.PlayerEnterExit.Dungeon
                : null;

            QuestResourceBehaviour[] resources = GameObject.FindObjectsOfType<QuestResourceBehaviour>();

            Transform nearest = null;
            float nearestDistance = float.MaxValue;

            for (int i = 0; i < resources.Length; i++)
            {
                QuestResourceBehaviour resource = resources[i];
                if (resource == null || resource.IsFoeDead)
                    continue;

                Vector3 position = resource.transform.position;

                if (dungeon != null)
                {
                    if (dungeon.GetPlayerBlockIndex(position) < 0)
                        continue;
                    if (Mathf.Abs(position.y - dungeon.transform.position.y) > 150f)
                        continue;
                }

                float distance = Vector3.Distance(fromPosition, position);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = resource.transform;
                }
            }

            return nearest;
        }

        void RefundCastingCost()
        {
            if (caster == null)
                return;

            EffectEntry entry = new EffectEntry { Key = Properties.Key, Settings = Settings };
            FormulaHelper.SpellCost cost = FormulaHelper.CalculateTotalEffectCosts(new EffectEntry[] { entry }, TargetTypes.CasterOnly, caster.Entity);
            caster.Entity.IncreaseMagicka(cost.spellPointCost);
        }

        public static EffectBundleSettings Create()
        {
            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            if (!effectBroker.HasEffectTemplate(EffectKey))
            {
                VoxsNecromancy.LogError("SeanceEffect.Create: Failed to get template from effect broker");
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
