// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Mod entry point: loads settings, registers all nine Reanimate effects and this mod's other
// spells (none of which are Spellmaker-customizable or sold at Mages Guild anymore - every spell
// this mod contributes is granted only via Astrid's Guide to Necromancy, the book handed to every
// new character, or the embrace_darkness console command), and carries reanimated creatures'
// remaining duration across scene transitions and fast travel, since DFU tears down and respawns
// non-quest summoned enemies on every one of those (same record-before-teardown/restore-after-
// respawn pattern ChebsNecromancyPacified/Scripts/ChebsNecromancy.cs uses for its own minions,
// extended here to carry ReanimatedLifetime.RoundsRemaining forward instead of resetting it).
//
using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.FallExe;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Game.Utility.ModSupport.ModSettings;
using VoxsNecromancyMod.AI;

namespace VoxsNecromancyMod
{
    public enum Logging
    {
        Errors = 0,
        All,
        None
    }

    public class VoxsNecromancy : MonoBehaviour
    {
        static Mod mod;

        // No longer user-configurable (the "Logging" mod setting was removed) - fixed at the
        // original default. Debug logging in this mod always assumes the least noisy level.
        static Logging LogLevel = Logging.Errors;

        public static float CorpseDetectionRadius = 10f;
        public static bool UseCustomIdleSounds = true;
        public static bool LimitToHumanCorpses = false;
        public static bool NecromancyLegalInTowns = false;
        public static bool ProtectQuestCorpses = true;

        // Backed by a "Reanimate Allegiance" MultipleChoiceKey slider (Necromancy section) rather than
        // a plain bool - three modes, index-matched to that key's own Options list.
        public const int ReanimateAllegianceDefault = 0;
        public const int ReanimateAllegianceAlwaysAllied = 1;
        public const int ReanimateAllegianceAlwaysHostile = 2;
        public static int ReanimateAllegianceMode = ReanimateAllegianceDefault;

        // Applies the "Reanimate Allegiance" mode on top of a fresh RollChance() result - both
        // VoxsNecromancyEffect.DoEffect and ReanimateEffect's own cascading DoEffect call this instead
        // of duplicating the mode check. Always Allied/Always Hostile short-circuit the chance roll's
        // OUTCOME, not the roll itself (rolledAllied is still computed by the caller) - RollChance()
        // has no meaningful side effects worth skipping, and this keeps the mode logic in one place.
        public static bool ResolveReanimateAllegiance(bool rolledAllied)
        {
            if (ReanimateAllegianceMode == ReanimateAllegianceAlwaysAllied)
                return true;
            if (ReanimateAllegianceMode == ReanimateAllegianceAlwaysHostile)
                return false;

            return rolledAllied;
        }

        // Off by default - allied undead physically collide with each other like any other pack of
        // creatures. See AllyCombatController.SetIgnoreCollisionWithOtherAllies, which only ever
        // ignores the player's own collider unconditionally; the ally-vs-ally ignore is gated on this.
        public static bool AlliedUndeadIgnoreEachOtherCollision = false;

        // Off by default - when on, a reanimated creature's duration timer still counts down (see
        // ReanimatedLifetime.Tick) but never actually kills it on expiry, so every summon lasts until
        // it dies in combat or is dismissed instead of wearing off on its own.
        public static bool PermanentUndeadSummons = false;

        // Off by default - when on, Reanimate's own corpse search is skipped entirely (see
        // VoxsNecromancyEffect.DoEffect) and the creature rises a few paces in front of the caster
        // instead (ReanimationSpawner.SpawnInFrontOfCaster) - no corpse required, none consumed.
        public static bool RaiseUndeadWithoutCorpse = false;

        // Master kill switch for the whole custom idle-sound/attack-sound-override system (see
        // IdleSoundPool.Poll()'s own guard) - when on, none of the other Sound section settings have
        // any effect, since Poll() returns before ever consulting them.
        public static bool MuteAllFollowerNoises = false;

        // Shared across all idle sound pools (skeleton/zombie, ghost/wraith, vampire alike) - a
        // single setting is simpler than a per-pool one and there's no indication the user wants
        // different pacing per undead type.
        public static float IdleSoundIntervalMin = 20f;
        public static float IdleSoundIntervalMax = 50f;
        public static bool FunnyMode = false;

        static bool justFastTravelled = false;
        static List<ReanimatedRecord> reanimationsToRestore = new List<ReanimatedRecord>();
        static Dictionary<ulong, ReanimatedRecord> reanimationsBeingLoaded = new Dictionary<ulong, ReanimatedRecord>();

        // Seance's own "kills since entering the current dungeon" gate. Incremented via
        // EnemyDeath.OnEnemyDeath (subscribed in Init(), guarded to only count kills actually made
        // while inside a dungeon), reset to 0 only when PlayerEnterExit.OnTransitionDungeonInterior
        // reports a genuinely different DaggerfallDungeon.Summary.ID than last time - re-entering the
        // SAME dungeon does not reset progress. seanceLastDungeonID's sentinel (int.MinValue, never a
        // real Summary.ID) guarantees the very first dungeon entry of a session always resets to 0.
        //
        // Persisted across a save+load via ReanimationSaveDataHandler (same per-mod save data file
        // reanimated allies use - see its own header comment for why a plain in-memory value can't
        // survive a cold relaunch on its own). RestoreSeanceProgress is called from there;
        // OnTransitionDungeonInterior above may fire first during a load (transition events happen
        // early, before the mod's own save data restore runs - see VoxsNecromancy.Init()'s own
        // comment on isLoadingSave) and reset these to 0/sentinel using a stale value, but the later
        // RestoreSeanceProgress call simply overwrites that with the true persisted values, so the
        // net result after a load is always correct regardless of which runs first.
        static int seanceKillCount;
        static int seanceLastDungeonID = int.MinValue;
        public static int SeanceKillRequirement = 10;
        public static int SeanceKillCount => seanceKillCount;
        public static int SeanceLastDungeonID => seanceLastDungeonID;

        public static void RestoreSeanceProgress(int killCount, int lastDungeonID)
        {
            seanceKillCount = killCount;
            seanceLastDungeonID = lastDungeonID;
        }

        // Skips SeanceEffect's own kill-count gate entirely when on (see SeanceEffect.DoEffect) -
        // Seance becomes castable in any dungeon regardless of SeanceKillCount/SeanceKillRequirement.
        public static bool IgnoreSeanceKillRequirement = false;

        // Seance ghost movement/wait-for-player tuning (see SeanceGhostGuide.cs) - multiplier applies
        // on top of the caster's own LiveSpeed-derived base, matching how the ghost's speed has always
        // been calculated.
        public static float SeanceGhostSpeedMultiplier = 1f;
        public static float SeanceWaitForPlayerDistance = 8f;
        public static float SeanceResumeDistance = 5f;

        static List<EffectBundleSettings> allSpellBundles = new List<EffectBundleSettings>();

        // Maps each Reanimate tier's spell bundle Name to that tier's own MinCasterLevel (see
        // VoxsNecromancyEffect.MinCasterLevel) - populated alongside effect registration in
        // LoadSettings() below. Consulted only by GrantQualifyingSpells, so reading the book doesn't
        // hand out a tier the player can't even cast yet; bundles with no entry here (every non-
        // Reanimate spell) are always eligible.
        static Dictionary<string, int> reanimateMinLevels = new Dictionary<string, int>();

        // Unique book ID for Astrid's Guide to Necromancy (see Books/Mapping/AstridBookMapping.json
        // - must match the "ID" field there). Reading the book runs GrantQualifyingSpells below.
        const int AstridBookID = 90210;

        // Daggerfall Enemy Expansion (Nexus mod 372) registers its own undead - Skeletal Soldier,
        // Ghoul, Dire Ghoul, Faded Ghost, Gloom Wraith - under custom enemy IDs (266/277/287/274/273)
        // via DaggerfallEntity.RegisterCustomCareerTemplate, not through GameObjectHelper.EnemyDict.
        // Its own MonsterBase.mdb.csv data additionally appends full MobileEnemy entries (complete
        // with real MinHealth/MaxHealth) directly onto EnemyBasics.Enemies from its own Start()
        // method - which runs well before any spell could ever be cast, so CorpseFinder's existing
        // EnemyDict-based health lookup already resolves its corpses correctly with no changes
        // needed. What DOES need explicit support is (1) excluding its undead from Reanimate's own
        // corpse-chaining guard (see CorpseFinder.IsUndeadCorpse) and (2) offering its undead as
        // their own Reanimate tiers (see the five DEE-specific Reanimate*Effect.cs subclasses) -
        // both gated on this flag so a player without DEE installed never sees a spell that would
        // try to spawn an enemy ID nothing has registered. GUID is read from DEE's own manifest and
        // is stable across versions (GUIDs aren't meant to change between releases of the same mod).
        const string DaggerfallEnemyExpansionGUID = "76557441-7025-402e-a145-e3e1a28a093d";
        public static bool DaggerfallEnemyExpansionInstalled { get; private set; }

        static bool IsDaggerfallEnemyExpansionInstalled()
        {
            Mod dee = ModManager.Instance.GetModFromGUID(DaggerfallEnemyExpansionGUID);
            return dee != null && dee.Enabled;
        }

        // ---- Magicka upkeep budget ------------------------------------------------------------------
        //
        // Replaces the old level-scaled per-type/global party caps entirely: every allied undead has
        // an upkeep cost (Mysticism-reduced, computed once when it enters service and never
        // recomputed), and the sum of every currently active ally's upkeep may never exceed
        // GetUpkeepBudget() below (MAX Magicka - the real, unmodified value - plus the caster's own
        // Mysticism skill score). This is a BUDGET CEILING, not a drain: unlike an earlier version of
        // this system, nothing here ever touches MaxMagicka or MaxMagickaModifier. Max Magicka behaves
        // exactly as vanilla; only whether a new ally can be raised/subjugated is gated by whether
        // doing so would push total upkeep past that ceiling. Casting a Reanimate spell costs normal
        // Magicka like any other spell - the upkeep budget is a separate, strategic question from
        // whether you can afford this one cast right now.
        //
        // Listed weakest to strongest (ascending BaseCost). MinCasterLevel here is the SAME per-tier
        // value each Reanimate*Effect.cs's own MinCasterLevel property already carries (kept in sync
        // manually - there are only 14 of these and they change rarely) - duplicated here so
        // SelectBestAvailableUndeadType can level-gate its cascade without a reflection lookup into
        // each effect class.
        struct UndeadMagickaCost
        {
            public MobileTypes FoeType;
            public bool RequiresDEE;
            public int BaseCost;
            public int MinCasterLevel;
        }

        static readonly UndeadMagickaCost[] undeadMagickaCosts = new UndeadMagickaCost[]
        {
            new UndeadMagickaCost { FoeType = (MobileTypes)266, RequiresDEE = true, BaseCost = 50, MinCasterLevel = 1 },  // Skeletal Soldier
            new UndeadMagickaCost { FoeType = MobileTypes.SkeletalWarrior, RequiresDEE = false, BaseCost = 53, MinCasterLevel = 1 },
            new UndeadMagickaCost { FoeType = MobileTypes.Zombie, RequiresDEE = false, BaseCost = 56, MinCasterLevel = 2 },
            new UndeadMagickaCost { FoeType = MobileTypes.Ghost, RequiresDEE = false, BaseCost = 60, MinCasterLevel = 3 },
            new UndeadMagickaCost { FoeType = (MobileTypes)274, RequiresDEE = true, BaseCost = 64, MinCasterLevel = 6 },  // Faded Ghost
            new UndeadMagickaCost { FoeType = (MobileTypes)277, RequiresDEE = true, BaseCost = 68, MinCasterLevel = 6 },  // Ghoul
            new UndeadMagickaCost { FoeType = MobileTypes.Mummy, RequiresDEE = false, BaseCost = 72, MinCasterLevel = 5 },
            new UndeadMagickaCost { FoeType = (MobileTypes)287, RequiresDEE = true, BaseCost = 76, MinCasterLevel = 7 },  // Dire Ghoul
            new UndeadMagickaCost { FoeType = MobileTypes.Wraith, RequiresDEE = false, BaseCost = 80, MinCasterLevel = 7 },
            new UndeadMagickaCost { FoeType = MobileTypes.Vampire, RequiresDEE = false, BaseCost = 85, MinCasterLevel = 9 },
            new UndeadMagickaCost { FoeType = MobileTypes.Lich, RequiresDEE = false, BaseCost = 100, MinCasterLevel = 13 },
            new UndeadMagickaCost { FoeType = MobileTypes.VampireAncient, RequiresDEE = false, BaseCost = 120, MinCasterLevel = 12 },
            new UndeadMagickaCost { FoeType = (MobileTypes)273, RequiresDEE = true, BaseCost = 145, MinCasterLevel = 15 },  // Gloom Wraith
            new UndeadMagickaCost { FoeType = MobileTypes.AncientLich, RequiresDEE = false, BaseCost = 175, MinCasterLevel = 16 },
        };

        // Mysticism reduces upkeep, capped at 50% off (Mysticism 125+) - i.e. never less than 50% of
        // BaseCost, enforced twice over: once by clamping the reduction fraction itself, and once more
        // defensively via the final Max() against the same floor (guards against any future tweak to
        // the formula above that might not otherwise preserve the 50% guarantee).
        const float MysticismReductionDivisor = 250f;
        const float MysticismReductionCap = 0.5f;

        public static int GetBaseMagickaCost(MobileTypes foeType)
        {
            for (int i = 0; i < undeadMagickaCosts.Length; i++)
            {
                if (undeadMagickaCosts[i].FoeType == foeType)
                    return undeadMagickaCosts[i].BaseCost;
            }

            return 0;
        }

        // Used by the cascading "Reanimate" spell (ReanimateEffect.cs) to look up what tier it
        // actually ended up summoning, so its own allied-vs-hostile chance roll can reflect that
        // tier's difficulty instead of a flat default - see ReanimateEffect's own comment on this.
        public static int GetMinCasterLevel(MobileTypes foeType)
        {
            for (int i = 0; i < undeadMagickaCosts.Length; i++)
            {
                if (undeadMagickaCosts[i].FoeType == foeType)
                    return undeadMagickaCosts[i].MinCasterLevel;
            }

            return 1;
        }

        // Final Upkeep = Base Cost x (1 - min(Mysticism / 250, 0.5)). Computed ONCE at the moment an
        // undead enters service (Reanimate or Subjugate alike) and stored directly on that instance
        // (ReanimatedLifetime.MagickaUpkeep / AllyCombatController.MagickaUpkeep) - never recomputed
        // afterward, so it stays exactly fixed for that undead's whole service regardless of the
        // caster's Mysticism changing later.
        public static int CalculateMagickaUpkeep(MobileTypes foeType, int mysticismSkill)
        {
            int baseCost = GetBaseMagickaCost(foeType);
            if (baseCost <= 0)
                return 0;

            float reduction = Mathf.Clamp(mysticismSkill / MysticismReductionDivisor, 0f, MysticismReductionCap);
            int upkeep = Mathf.RoundToInt(baseCost * (1f - reduction));
            int floor = Mathf.RoundToInt(baseCost * (1f - MysticismReductionCap));
            return Mathf.Max(upkeep, floor);
        }

        // The upkeep ceiling itself: MAX Magicka (the real, unmodified value - nothing in this mod
        // touches it) plus the caster's own Mysticism skill score - the same skill that already
        // discounts each individual ally's own upkeep cost, so a necromancer who has invested in
        // Mysticism gets a larger legion in two compounding ways at once (cheaper allies AND more
        // headroom to hold them), not just one.
        public static int GetUpkeepBudget()
        {
            PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
            int mysticismSkill = playerEntity.Skills.GetLiveSkillValue(DFCareer.Skills.Mysticism);
            return playerEntity.MaxMagicka + mysticismSkill;
        }

        // The budget check shared by every path that can add a new ally (the fixed-tier Reanimate
        // spells, the cascading "Reanimate", and Subjugate Undead) - total upkeep across every
        // currently active ally, plus the additional cost being considered, must not exceed
        // GetUpkeepBudget() above.
        public static bool CanAffordUpkeep(int additionalCost)
        {
            return AllyCombatController.TotalUpkeep() + additionalCost <= GetUpkeepBudget();
        }

        // Shared "(current/budget)" formatting for both the failure message (budget exceeded) and the
        // success message (new totals after a successful raise/subjugation), so the two always agree.
        public static string DescribeUpkeepBudget()
        {
            return string.Format("{0}/{1}", AllyCombatController.TotalUpkeep(), GetUpkeepBudget());
        }

        // Used by the "Reanimate" spell (ReanimateEffect.cs) to pick the strongest tier the player can
        // currently sustain: walks the table strongest-first, unconditionally skipping every
        // Daggerfall Enemy Expansion tier (this cascading spell only ever picks from the nine
        // base-game tiers, regardless of whether DEE is installed - the DEE tiers remain fully
        // castable via their own dedicated spells), any tier the caster's level hasn't reached yet
        // (same MinCasterLevel gate VoxsNecromancyEffect.DoEffect enforces for the fixed-tier spells -
        // without this, "Reanimate" would let a low-level caster skip the gate entirely just by using
        // the generic spell instead of the specific one), and any tier whose Mysticism-reduced upkeep
        // would push the total over GetUpkeepBudget() - returning the first one that clears all three.
        public static MobileTypes? SelectBestAvailableUndeadType(int level)
        {
            int mysticismSkill = GameManager.Instance.PlayerEntity.Skills.GetLiveSkillValue(DFCareer.Skills.Mysticism);

            for (int i = undeadMagickaCosts.Length - 1; i >= 0; i--)
            {
                UndeadMagickaCost entry = undeadMagickaCosts[i];
                if (entry.RequiresDEE)
                    continue;

                if (level < entry.MinCasterLevel)
                    continue;

                int upkeep = CalculateMagickaUpkeep(entry.FoeType, mysticismSkill);
                if (CanAffordUpkeep(upkeep))
                    return entry.FoeType;
            }

            return null;
        }

        // Loading a save routes through the exact same PlayerEnterExit transition events a live
        // transition does (confirmed - SaveLoadManager.LoadGame() calls RestorePositionHelper(),
        // which raises OnPreTransition/OnTransitionX same as walking through a door), so without
        // this flag RecordActiveReanimations/RestoreActiveReanimations would ALSO fire during a
        // load and spawn their own brand-new ally on top of whatever OnModSaveDataRestored/
        // ReconcileAfterLoad already handle - which is what caused a new working duplicate to pile
        // up on every single save+load cycle. Bracketed by OnStartLoad (set true, fires first) and
        // the end of ReconcileAfterLoad (set false, fires last), covering the whole load sequence.
        static bool isLoadingSave;

        // See ReconcileAfterLoad's own guard comment - tracks the loadID set and real-time
        // timestamp of the last completed reconciliation, so a suspiciously-immediate repeat (a
        // base-game double-fired load, not a deliberate reload) can be skipped instead of
        // reattaching a second time.
        static HashSet<ulong> lastReconciledLoadIDs = new HashSet<ulong>();
        static float lastReconcileRealtime = -1000f;

        struct ReanimatedRecord
        {
            public MobileTypes FoeType;
            public bool Allied;
            public int Magnitude;
            public int MysticismLevel;
            public int Intelligence;
            public int Willpower;
            public int RoundsRemaining;
            public int MagickaUpkeep;
        }

        [Invoke(StateManager.StateTypes.Start, 0)]
        public static void Init(InitParams initParams)
        {
            mod = initParams.Mod;

            GameObject go = new GameObject(mod.Title);
            go.AddComponent<VoxsNecromancy>();
            go.AddComponent<PlayerAttackWatcher>();
            go.AddComponent<Sound.IdleSoundPool>().Initialize(mod);

            VoxsNecromancyConsoleCommands.RegisterCommands();

            mod.LoadSettingsCallback = LoadSettings;

            PlayerEnterExit.OnPreTransition += args => { RecordActiveReanimations(); };
            PlayerEnterExit.OnTransitionExterior += args => { RestoreActiveReanimations(); };
            PlayerEnterExit.OnTransitionInterior += args => { RestoreActiveReanimations(); };
            PlayerEnterExit.OnTransitionDungeonExterior += args => { RestoreActiveReanimations(); };
            PlayerEnterExit.OnTransitionDungeonInterior += args => { RestoreActiveReanimations(); };

            EnemyDeath.OnEnemyDeath += (sender, args) =>
            {
                if (GameManager.Instance.IsPlayerInsideDungeon)
                    seanceKillCount++;
            };

            PlayerEnterExit.OnTransitionDungeonInterior += args =>
            {
                int newID = args.DaggerfallDungeon != null ? args.DaggerfallDungeon.Summary.ID : seanceLastDungeonID;
                if (newID != seanceLastDungeonID)
                {
                    seanceKillCount = 0;
                    seanceLastDungeonID = newID;
                }
            };

            // Both hooked unconditionally regardless of SeanceEffect.UseVoxelPathfinding - clearing a
            // cache belonging to whichever system isn't currently active is harmless, and this way the
            // flag can be flipped without also needing to change this subscription.
            PlayerEnterExit.OnTransitionDungeonInterior += args => SeanceDungeonNavMesh.OnDungeonTransition(args.DaggerfallDungeon);
            PlayerEnterExit.OnTransitionDungeonInterior += args => SeanceDungeonGrid.OnDungeonTransition(args.DaggerfallDungeon);

            DaggerfallTravelPopUp.OnPreFastTravel += up => { RecordActiveReanimations(); };
            DaggerfallTravelPopUp.OnPostFastTravel += () =>
            {
                // Same workaround ChebsNecromancy.cs uses: OnPostFastTravel fires before the
                // player's position has actually updated, so restoring here would spawn allies at
                // the old location. Defer to the next OnEnterLocationRect instead.
                justFastTravelled = true;
            };
            PlayerGPS.OnEnterLocationRect += location =>
            {
                if (justFastTravelled)
                {
                    justFastTravelled = false;
                    RestoreActiveReanimations();
                }
            };

            // Loading a save game routes through the same PlayerEnterExit transition events above,
            // but DFU's own save/load system (SerializableEnemy/SerializableStateManager) ALSO
            // independently and unconditionally reconstructs every enemy with a saved LoadID -
            // including reanimated allies, since they get real LoadIDs like any other enemy. That
            // reconstruction is a flat copy (correct Team/stats, no ReanimatedLifetime/
            // AllyCombatController) and happens whether or not the original object still exists,
            // which is what produces the reported duplicate (one with working AI, one inert) - see
            // ReconcileAfterLoad for the fix. isLoadingSave must already be true before any of the
            // PlayerEnterExit transition events fire (they happen early, inside LoadGame()'s own
            // RestorePositionHelper call), so it's set here on OnStartLoad rather than waiting for
            // the mod save data restore below (which runs later, after enemies are restored).
            SaveLoadManager.OnStartLoad += data => { isLoadingSave = true; };
            SaveLoadManager.OnLoad += data => { ReconcileAfterLoad(); };
            SaveLoadManager.OnLoad += data => { MigrateStaleCommandUndead(); };

            // Persists active reanimations through DFU's own per-mod save data mechanism (see
            // ReanimationSaveDataHandler.cs) rather than only an in-memory snapshot, so allies
            // survive a save made, then the game fully closed and relaunched later - not just a
            // save+load within the same running session.
            mod.SaveDataInterface = new ReanimationSaveDataHandler();

            // StartGameBehaviour.OnStartGame fires both for a brand-new character AND for a
            // classic-save import (never for an ordinary DFU save load, which doesn't raise this
            // event at all) - LastStartMethod on the sender disambiguates the two.
            StartGameBehaviour.OnStartGame += (sender, args) =>
            {
                StartGameBehaviour behaviour = sender as StartGameBehaviour;
                Debug.LogFormat("Vox's Necromancy: OnStartGame fired. sender is StartGameBehaviour={0}, LastStartMethod={1}.",
                    behaviour != null, behaviour != null ? behaviour.LastStartMethod.ToString() : "n/a");

                if (behaviour == null || behaviour.LastStartMethod != StartGameBehaviour.StartMethods.NewCharacter)
                    return;

                GrantAstridBook();
            };

            // Books work generically off an item's own "message" field (the book ID) resolved
            // through BookReplacement/LocalizedBook - no custom item template needed. All books
            // created this way share the same underlying TemplateIndex ((int)Books.Book0, see
            // ItemBuilder.CreateBook), so this handler checks item.message first and always returns
            // false for anything that isn't Astrid's Guide specifically, both to avoid interfering
            // with other books and in case another mod ever registers its own handler on the same
            // shared template index (RegisterItemUseHandler only keeps one delegate per index).
            DaggerfallUnity.Instance.ItemHelper.RegisterItemUseHandler((int)Books.Book0, OnAstridBookUsed);

            mod.LoadSettings();

            mod.IsReady = true;
        }

        static void LoadSettings(ModSettings modSettings, ModSettingsChange change)
        {
            CorpseDetectionRadius = modSettings.GetFloat("General", "Corpse Detection Radius");
            LimitToHumanCorpses = modSettings.GetBool("Necromancy", "Limit to Human Corpses");
            NecromancyLegalInTowns = modSettings.GetBool("Necromancy", "Necromancy Legal In Towns");
            ProtectQuestCorpses = modSettings.GetBool("Necromancy", "Protect Quest Corpses");
            ReanimateAllegianceMode = modSettings.GetValue<int>("Necromancy", "Reanimate Allegiance");
            AlliedUndeadIgnoreEachOtherCollision = modSettings.GetBool("Necromancy", "Allied Undead Ignore Each Other's Collision");
            PermanentUndeadSummons = modSettings.GetBool("Necromancy", "Permanent Undead Summons");
            RaiseUndeadWithoutCorpse = modSettings.GetBool("Necromancy", "Raise Undead Without Corpse");
            SeanceKillRequirement = modSettings.GetInt("Necromancy", "Seance Kill Requirement");
            IgnoreSeanceKillRequirement = modSettings.GetBool("Necromancy", "Ignore Seance Kill Requirement");
            SeanceGhostSpeedMultiplier = modSettings.GetFloat("Necromancy", "Seance Ghost Speed Multiplier");
            SeanceWaitForPlayerDistance = modSettings.GetFloat("Necromancy", "Seance Wait For Player Distance");
            SeanceResumeDistance = modSettings.GetFloat("Necromancy", "Seance Resume Distance");

            MuteAllFollowerNoises = modSettings.GetBool("Sound", "Mute All Follower Noises");
            UseCustomIdleSounds = modSettings.GetBool("Sound", "Enable Custom Undead Noises");
            FunnyMode = modSettings.GetBool("Sound", "Funny Mode");
            IdleSoundIntervalMin = modSettings.GetInt("Sound", "Idle Sound Interval Min");
            IdleSoundIntervalMax = modSettings.GetInt("Sound", "Idle Sound Interval Max");

            DaggerfallEnemyExpansionInstalled = IsDaggerfallEnemyExpansionInstalled();

            List<VoxsNecromancyEffect> effects = new List<VoxsNecromancyEffect>()
            {
                new ReanimateEffect(),
                new ReanimateSkeletonEffect(),
                new ReanimateZombieEffect(),
                new ReanimateGhostEffect(),
                new ReanimateMummyEffect(),
                new ReanimateWraithEffect(),
                new ReanimateVampireEffect(),
                new ReanimateAncientVampireEffect(),
                new ReanimateLichEffect(),
                new ReanimateAncientLichEffect(),
            };

            if (DaggerfallEnemyExpansionInstalled)
            {
                effects.Add(new ReanimateSkeletalSoldierEffect());
                effects.Add(new ReanimateGhoulEffect());
                effects.Add(new ReanimateFadedGhostEffect());
                effects.Add(new ReanimateDireGhoulEffect());
                effects.Add(new ReanimateGloomWraithEffect());
            }

            EntityEffectBroker effectBroker = GameManager.Instance.EntityEffectBroker;
            reanimateMinLevels.Clear();
            foreach (VoxsNecromancyEffect effect in effects)
            {
                effect.SetProperties();
                effectBroker.RegisterEffectTemplate(effect, true);
                reanimateMinLevels[effect.Properties.Key] = effect.MinCasterLevel;
            }

            allSpellBundles.Clear();

            allSpellBundles.Add(ReanimateEffect.Create());
            allSpellBundles.Add(ReanimateSkeletonEffect.Create());
            allSpellBundles.Add(ReanimateZombieEffect.Create());
            allSpellBundles.Add(ReanimateGhostEffect.Create());
            allSpellBundles.Add(ReanimateMummyEffect.Create());
            allSpellBundles.Add(ReanimateWraithEffect.Create());
            allSpellBundles.Add(ReanimateVampireEffect.Create());
            allSpellBundles.Add(ReanimateAncientVampireEffect.Create());
            allSpellBundles.Add(ReanimateLichEffect.Create());
            allSpellBundles.Add(ReanimateAncientLichEffect.Create());

            if (DaggerfallEnemyExpansionInstalled)
            {
                allSpellBundles.Add(ReanimateSkeletalSoldierEffect.Create());
                allSpellBundles.Add(ReanimateGhoulEffect.Create());
                allSpellBundles.Add(ReanimateFadedGhostEffect.Create());
                allSpellBundles.Add(ReanimateDireGhoulEffect.Create());
                allSpellBundles.Add(ReanimateGloomWraithEffect.Create());
            }

            CorpseExplosionEffect corpseExplosionEffect = new CorpseExplosionEffect();
            corpseExplosionEffect.SetProperties();
            effectBroker.RegisterEffectTemplate(corpseExplosionEffect, true);
            allSpellBundles.Add(CorpseExplosionEffect.Create());

            DeathknellEffect deathknellEffect = new DeathknellEffect();
            deathknellEffect.SetProperties();
            effectBroker.RegisterEffectTemplate(deathknellEffect, true);
            allSpellBundles.Add(DeathknellEffect.Create());

            SubjugateUndeadEffect subjugateUndeadEffect = new SubjugateUndeadEffect();
            subjugateUndeadEffect.SetProperties();
            effectBroker.RegisterEffectTemplate(subjugateUndeadEffect, true);
            allSpellBundles.Add(SubjugateUndeadEffect.Create());

            DismissUndeadEffect dismissUndeadEffect = new DismissUndeadEffect();
            dismissUndeadEffect.SetProperties();
            effectBroker.RegisterEffectTemplate(dismissUndeadEffect, true);
            allSpellBundles.Add(DismissUndeadEffect.Create());

            VolatileUndeadEffect volatileUndeadEffect = new VolatileUndeadEffect();
            volatileUndeadEffect.SetProperties();
            effectBroker.RegisterEffectTemplate(volatileUndeadEffect, true);
            allSpellBundles.Add(VolatileUndeadEffect.Create());

            CommandUndeadEffect commandUndeadEffect = new CommandUndeadEffect();
            commandUndeadEffect.SetProperties();
            effectBroker.RegisterEffectTemplate(commandUndeadEffect, true);
            allSpellBundles.Add(CommandUndeadEffect.Create());

            DarkDealEffect darkDealEffect = new DarkDealEffect();
            darkDealEffect.SetProperties();
            effectBroker.RegisterEffectTemplate(darkDealEffect, true);
            allSpellBundles.Add(DarkDealEffect.Create());

            SeanceEffect seanceEffect = new SeanceEffect();
            seanceEffect.SetProperties();
            effectBroker.RegisterEffectTemplate(seanceEffect, true);
            allSpellBundles.Add(SeanceEffect.Create());
        }

        // Adds any of this mod's spells the player doesn't already know to their spellbook - used by
        // both the "Mysticism primary skill" starting-spell grant and the embrace_darkness console
        // command.
        public static void GrantAllSpells(PlayerEntity playerEntity)
        {
            EffectBundleSettings[] known = playerEntity.GetSpells();

            for (int i = 0; i < allSpellBundles.Count; i++)
            {
                EffectBundleSettings bundle = allSpellBundles[i];
                bool alreadyKnown = false;
                for (int j = 0; j < known.Length; j++)
                {
                    if (known[j].Name == bundle.Name)
                    {
                        alreadyKnown = true;
                        break;
                    }
                }

                if (!alreadyKnown)
                    playerEntity.AddSpell(bundle);
            }
        }

        // Adds every spell the player doesn't already know AND already qualifies for, same set as
        // GrantAllSpells minus the level gate - used exclusively by Astrid's Guide to Necromancy's own
        // read handler (see OnAstridBookUsed). A Reanimate tier whose MinCasterLevel exceeds the
        // player's current level is skipped entirely (not just left uncastable) - re-reading the book
        // at a higher level picks up whatever newly qualifies. This is separate from, and doesn't
        // replace, the same MinCasterLevel gate already enforced at cast time (VoxsNecromancyEffect.
        // DoEffect/SelectBestAvailableUndeadType) - that gate stays regardless of how a spell was
        // learned (e.g. via embrace_darkness's unconditional GrantAllSpells).
        public static void GrantQualifyingSpells(PlayerEntity playerEntity)
        {
            EffectBundleSettings[] known = playerEntity.GetSpells();
            int grantedCount = 0;

            for (int i = 0; i < allSpellBundles.Count; i++)
            {
                EffectBundleSettings bundle = allSpellBundles[i];

                int minLevel;
                if (reanimateMinLevels.TryGetValue(bundle.Name, out minLevel) && playerEntity.Level < minLevel)
                    continue;

                bool alreadyKnown = false;
                for (int j = 0; j < known.Length; j++)
                {
                    if (known[j].Name == bundle.Name)
                    {
                        alreadyKnown = true;
                        break;
                    }
                }

                if (!alreadyKnown)
                {
                    playerEntity.AddSpell(bundle);
                    grantedCount++;
                }
            }

            if (grantedCount > 0)
                DaggerfallUI.AddHUDText(string.Format("You glean {0} new spell{1} from the tome.", grantedCount, grantedCount == 1 ? "" : "s"));
            else
                DaggerfallUI.AddHUDText("The tome holds nothing new for you yet.");

            Debug.LogFormat("Vox's Necromancy: GrantQualifyingSpells granted {0} new spell(s).", grantedCount);
        }

        static bool OnAstridBookUsed(DaggerfallUnityItem item, ItemCollection collection)
        {
            if (item.message != AstridBookID)
                return false;

            GrantQualifyingSpells(GameManager.Instance.PlayerEntity);
            PlayAstridBookOpenSound();

            // Let the vanilla book-open flow still run so the player actually sees the text.
            return false;
        }

        // Lazily loaded (rather than at Init() time) since it's only ever needed once the player
        // actually opens the book, and this avoids any dependency on mod asset-bundle load order.
        static AudioClip astridBookOpenSound;

        static void PlayAstridBookOpenSound()
        {
            if (astridBookOpenSound == null)
                astridBookOpenSound = mod.GetAsset<AudioClip>("vampire_idle_vampire.ogg");

            if (astridBookOpenSound != null && DaggerfallUI.Instance.DaggerfallAudioSource)
                DaggerfallUI.Instance.DaggerfallAudioSource.AudioSource.PlayOneShot(astridBookOpenSound, DaggerfallUnity.Settings.SoundVolume);
        }

        // Lazily loaded, same reasoning as astridBookOpenSound above. Played by SeanceEffect only
        // when the summoned ghost actually has somewhere to lead the caster - the "finds nothing to
        // seek" outcome stays silent - and only when MuteAllFollowerNoises is off, matching how that
        // setting silences every other follower sound this mod plays.
        static AudioClip seanceFollowMeSound;

        public static void PlaySeanceFollowMeSound()
        {
            if (MuteAllFollowerNoises)
                return;

            if (seanceFollowMeSound == null)
                seanceFollowMeSound = mod.GetAsset<AudioClip>("followme.ogg");

            if (seanceFollowMeSound != null && DaggerfallUI.Instance.DaggerfallAudioSource)
                DaggerfallUI.Instance.DaggerfallAudioSource.AudioSource.PlayOneShot(seanceFollowMeSound, DaggerfallUnity.Settings.SoundVolume);
        }

        // Lazily loaded, same reasoning as the sounds above. Played by SeanceGhostGuide when the ghost
        // reaches its quest target and dissipates - reuses an existing shipped idle clip from the
        // ghost/wraith sound pool rather than adding a new asset, same as PlaySeanceFollowMeSound
        // reusing followme.ogg rather than needing anything ghost-specific commissioned for it.
        static AudioClip seanceDissipateSound;

        public static void PlaySeanceDissipateSound()
        {
            if (MuteAllFollowerNoises)
                return;

            if (seanceDissipateSound == null)
                seanceDissipateSound = mod.GetAsset<AudioClip>("ghost_idle_ghosts_24.ogg");

            if (seanceDissipateSound != null && DaggerfallUI.Instance.DaggerfallAudioSource)
                DaggerfallUI.Instance.DaggerfallAudioSource.AudioSource.PlayOneShot(seanceDissipateSound, DaggerfallUnity.Settings.SoundVolume);
        }

        // Lazily loaded, same reasoning as the sounds above. Played by SeanceGhostGuide when it
        // vanishes to cross an active teleporter - a different clip from the same shipped ghost/wraith
        // idle pool than the dissipate sound, so the two moments read as audibly distinct.
        static AudioClip seanceTeleportSound;

        public static void PlaySeanceTeleportSound()
        {
            if (MuteAllFollowerNoises)
                return;

            if (seanceTeleportSound == null)
                seanceTeleportSound = mod.GetAsset<AudioClip>("ghost_idle_ghosts_15.ogg");

            if (seanceTeleportSound != null && DaggerfallUI.Instance.DaggerfallAudioSource)
                DaggerfallUI.Instance.DaggerfallAudioSource.AudioSource.PlayOneShot(seanceTeleportSound, DaggerfallUnity.Settings.SoundVolume);
        }

        // Lazily loaded, same reasoning as the sounds above. Played once by SeanceGhostGuide when it
        // arrives at a lever/switch/interactable it wants the player to use (see
        // SeanceGhostGuide.BeginWaitForActivation) - a real shipped asset the user added specifically
        // for this, not reused from the ghost/wraith idle pool like the other Seance sounds.
        static AudioClip seanceUseThisSound;

        public static void PlaySeanceUseThisSound()
        {
            if (MuteAllFollowerNoises)
                return;

            if (seanceUseThisSound == null)
                seanceUseThisSound = mod.GetAsset<AudioClip>("use_this.ogg");

            if (seanceUseThisSound != null && DaggerfallUI.Instance.DaggerfallAudioSource)
                DaggerfallUI.Instance.DaggerfallAudioSource.AudioSource.PlayOneShot(seanceUseThisSound, DaggerfallUnity.Settings.SoundVolume);
        }

        // Lazily loaded, same reasoning as astridBookOpenSound/seanceFollowMeSound above. Used by
        // SeanceQuestMarker in place of its own procedurally-generated dot texture - that fallback
        // stays in place for the (should-never-happen) case this asset fails to load.
        static Texture2D seanceMarkerTexture;

        public static Texture2D GetSeanceMarkerTexture()
        {
            if (seanceMarkerTexture == null)
                seanceMarkerTexture = mod.GetAsset<Texture2D>("skull.png");

            return seanceMarkerTexture;
        }

        // Called once per successful cast by every qualifying spell in this mod (all 9 Reanimate
        // tiers via VoxsNecromancyEffect.DoEffect(), plus Corpse Explosion/Deathknell/Subjugate
        // Undead/Dismiss Undead/Volatile Undead/Command Undead/Dark Deal each individually).
        //
        // PlayerEntity.Crimes is a fixed, compiled enum a mod can't extend with a genuinely new
        // crime type, and DFU has no witness-detection system to gate on at all (every existing
        // crime fires unconditionally the instant the act happens, e.g. WeaponManager.cs's own
        // Murder/Assault triggers) - so this reuses Crimes.Murder directly to get real reputation
        // penalty/guard response using the exact mechanism the base game itself uses, matching the
        // "comparable severity to Murder" goal. The crime log/guard dialogue will describe it as
        // murder, since that's the only real severity-20 mechanism available.
        public static void ReportNecromancyCrimeIfApplicable()
        {
            if (NecromancyLegalInTowns)
                return;

            if (!GameManager.Instance.PlayerGPS.IsPlayerInTown())
                return;

            PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
            playerEntity.CrimeCommitted = PlayerEntity.Crimes.Murder;
            playerEntity.SpawnCityGuards(true);
        }

        // Every new character gets this book, regardless of chosen skills - Astrid's Guide to
        // Necromancy (read to grant qualifying spells) has fully replaced the old Mysticism-primary
        // starting-spell grant this mod used to do directly. Constructed directly rather than via
        // ItemBuilder.CreateBook(id) - that helper requires a valid classic-format
        // binary book blob to exist for the given ID (even for modded books), which this mod doesn't
        // ship at all, since Astrid's Guide only ever needs the plain-text localized book file
        // (Books/ASTRID00-LOC.txt) that DaggerfallBookReaderWindow/LocalizedBook read directly.
        static void GrantAstridBook()
        {
            PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
            if (playerEntity == null)
            {
                Debug.LogWarning("Vox's Necromancy: GrantAstridBook - PlayerEntity is null.");
                return;
            }

            DaggerfallUnityItem book = new DaggerfallUnityItem(ItemGroups.Books, 0);
            book.message = AstridBookID;
            book.value = 100;

            // Gives the book a (functionally inert) legacy enchantment purely so IsEnchanted
            // returns true, which is what DaggerfallInventoryWindow's own Magic Items tab filter
            // actually checks (see its TabPages.MagicItems case: "item.IsEnchanted ||
            // item.IsOfTemplate(MiscItems.Spellbook)") - there's no dedicated "magic book" item
            // group to assign instead. RegensHealth is equip-slot-only (it only ever does anything
            // through the normal equipped-item enchantment scan), so on a book - which has no equip
            // slot at all - it can never actually apply; it's here solely for tab classification.
            // Deliberately NOT EnchantmentTypes.SpecialArtifactEffect: ItemHelper.GetItemInfo()
            // specifically special-cases that exact type on ItemGroups.Books to mean "this is the
            // Oghma Infinium" and would show the wrong flavor text.
            book.legacyMagic = new DaggerfallEnchantment[] { new DaggerfallEnchantment { type = EnchantmentTypes.RegensHealth, param = 0 } };

            playerEntity.Items.AddItem(book);

            DaggerfallUI.AddHUDText("A dark tome appears in your bag...");
            Debug.LogFormat("Vox's Necromancy: Granted Astrid's Guide to Necromancy book (id={0}) to new character.", AstridBookID);
        }

        // Console-command entry point (astrid_book) for granting the book outside the normal
        // new-character flow - lets an existing save pick it up after the mod is installed
        // mid-playthrough. Checks for an existing copy first via AstridBookID (every book in the
        // game shares the same TemplateIndex, so IsOfTemplate alone can't distinguish this book
        // from any other) to avoid handing out duplicates on repeated use.
        public static string GiveAstridBookIfMissing()
        {
            PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
            if (playerEntity == null)
                return "No player entity found.";

            List<DaggerfallUnityItem> books = playerEntity.Items.SearchItems(ItemGroups.Books);
            for (int i = 0; i < books.Count; i++)
            {
                if (books[i].message == AstridBookID)
                    return "You already have Astrid's Guide to Necromancy.";
            }

            GrantAstridBook();
            return "Astrid's Guide to Necromancy has been added to your inventory.";
        }

        static void RecordActiveReanimations()
        {
            if (isLoadingSave)
            {
                Debug.Log("Vox's Necromancy: RecordActiveReanimations SKIPPED (isLoadingSave=true).");
                return;
            }

            reanimationsToRestore.Clear();

            // Only allied reanimations are carried across a transition - this whole mechanism
            // exists so the player's own following companions aren't lost to DFU tearing down and
            // respawning non-quest enemies on scene loads/fast travel. A hostile reanimation (one
            // that rolled hostile, or was left behind and never re-subjugated) is just an ordinary
            // wandering monster at that point and should be abandoned normally like any other
            // enemy the player walks away from - previously this recorded/restored ALL
            // ReanimatedLifetime instances regardless of Allied state, which meant a hostile
            // skeleton left behind in a dungeon could get swept along through every subsequent
            // transition on a long multi-leg trip (e.g. Travel Options' walk-with-rest simulation)
            // and finally get dumped back into existence - already expired - wherever the player
            // next entered an interior, dying on arrival.
            ReanimatedLifetime[] active = GameObject.FindObjectsOfType<ReanimatedLifetime>();
            for (int i = 0; i < active.Length; i++)
            {
                ReanimatedLifetime lifetime = active[i];
                if (!lifetime.Allied)
                    continue;

                reanimationsToRestore.Add(new ReanimatedRecord
                {
                    FoeType = lifetime.FoeType,
                    Allied = lifetime.Allied,
                    Magnitude = lifetime.Magnitude,
                    MysticismLevel = lifetime.MysticismLevel,
                    Intelligence = lifetime.Intelligence,
                    Willpower = lifetime.Willpower,
                    RoundsRemaining = lifetime.RoundsRemaining,
                    MagickaUpkeep = lifetime.MagickaUpkeep,
                });
                lifetime.gameObject.SetActive(false);
            }

            Debug.LogFormat("Vox's Necromancy: RecordActiveReanimations ran, recorded {0}.", reanimationsToRestore.Count);
        }

        static void RestoreActiveReanimations()
        {
            if (isLoadingSave)
            {
                Debug.Log("Vox's Necromancy: RestoreActiveReanimations SKIPPED (isLoadingSave=true).");
                return;
            }

            if (reanimationsToRestore.Count == 0)
                return;

            Debug.LogFormat("Vox's Necromancy: RestoreActiveReanimations ran, spawning {0}.", reanimationsToRestore.Count);

            Vector3 center = GameManager.Instance.PlayerObject.transform.position;

            for (int i = 0; i < reanimationsToRestore.Count; i++)
            {
                ReanimatedRecord record = reanimationsToRestore[i];
                Vector3 position = center + UnityEngine.Random.insideUnitSphere * 3f;
                position.y = center.y;

                ReanimationSpawner.SpawnRestored(position, record.FoeType, record.Allied, record.Magnitude,
                    record.MysticismLevel, record.Intelligence, record.Willpower, record.RoundsRemaining,
                    record.MagickaUpkeep);
            }

            reanimationsToRestore.Clear();
        }

        // Called by ReanimationSaveDataHandler.RestoreSaveData, which SaveLoadManager invokes after
        // DFU's own enemy data has already been restored (so the flat copies ReconcileAfterLoad
        // needs to find already exist) but before OnLoad fires (so reanimationsBeingLoaded is ready
        // in time). This is what makes an ally survive a save made in one session and loaded in a
        // completely different one, unlike the in-memory-only approach this replaced.
        public static void OnModSaveDataRestored(ReanimationSaveData_v1 data)
        {
            reanimationsBeingLoaded.Clear();

            if (data == null || data.records == null)
                return;

            for (int i = 0; i < data.records.Length; i++)
            {
                ReanimationRecord_v1 record = data.records[i];
                reanimationsBeingLoaded[record.loadID] = new ReanimatedRecord
                {
                    FoeType = record.foeType,
                    Allied = record.allied,
                    Magnitude = record.magnitude,
                    MysticismLevel = record.mysticismLevel,
                    Intelligence = record.intelligence,
                    Willpower = record.willpower,
                    RoundsRemaining = record.roundsRemaining,
                    // record.magickaReservation is the PERSISTED (wire) field name, deliberately left
                    // unchanged for save compatibility - see ReanimationSaveDataHandler.cs's own
                    // comment on ReanimationRecord_v1. Everything downstream of this read uses the
                    // current "upkeep" terminology.
                    MagickaUpkeep = record.magickaReservation,
                };
            }

            Debug.LogFormat("Vox's Necromancy: OnModSaveDataRestored loaded {0} persisted reanimation record(s): [{1}].",
                reanimationsBeingLoaded.Count, string.Join(",", new List<ulong>(reanimationsBeingLoaded.Keys).ConvertAll(id => id.ToString()).ToArray()));
        }

        // A player's spellbook (DaggerfallEntity.spellbook) stores EffectBundleSettings as plain
        // struct copies taken at AddSpell() time - nothing re-resolves an already-known spell against
        // its effect's current Create()/SetProperties() output. Command Undead was repurposed from a
        // targeted projectile (TargetTypes.SingleTargetAtRange) to a self-cast teleport
        // (TargetTypes.CasterOnly); any save that already knew the old spell before this change would
        // otherwise keep casting the old projectile version forever, since GrantAllSpells/
        // GrantQualifyingSpells only ever skip a spell whose Name is already known - they never
        // overwrite it. Finds that one stale entry (if present) on load and replaces it with the
        // current bundle, once.
        static void MigrateStaleCommandUndead()
        {
            PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
            if (playerEntity == null)
                return;

            EffectBundleSettings[] known = playerEntity.GetSpells();
            for (int i = 0; i < known.Length; i++)
            {
                if (known[i].Name == CommandUndeadEffect.EffectKey && known[i].TargetType != TargetTypes.CasterOnly)
                {
                    playerEntity.DeleteSpell(i);
                    playerEntity.AddSpell(CommandUndeadEffect.Create());
                    Debug.LogFormat("Astrid's Guide to Necormancy: Migrated stale Command Undead spellbook entry to the new self-cast version.");
                    break;
                }
            }
        }

        // Called on SaveLoadManager.OnLoad, after DFU's own RestoreSaveData/RestoreEnemyData has
        // finished reconstructing the world - for each LoadID snapshotted above, there is now
        // either: only DFU's flat reconstruction (the original was torn down as part of respawning,
        // e.g. reloading back into the same dungeon/interior - reattach our components to make it a
        // real reanimation again), or both the flat reconstruction AND the surviving original with
        // working AI (e.g. exterior worlds aren't torn down on load - destroy the stray flat copy).
        static void ReconcileAfterLoad()
        {
            // All PlayerEnterExit transition events this load will raise have already fired by
            // this point (OnLoad fires last, after RestoreSaveData/RestoreEnemyData completes), so
            // it's safe to lift the suppression here even before checking whether there's anything
            // left for this method itself to do.
            isLoadingSave = false;

            if (reanimationsBeingLoaded.Count == 0)
            {
                Debug.Log("Vox's Necromancy: ReconcileAfterLoad - nothing was snapshotted, nothing to do.");
                return;
            }

            // DFU's own enemy-restore step (SerializableStateManager.RestoreEnemyData) has no
            // duplicate-load guard of its own, and the Load button's click handling doesn't debounce
            // a double-click - either can genuinely fire the whole load pipeline twice in a row for
            // what the player experiences as a single "Load" action, with no scene teardown of the
            // first pass's world in between. Each pass individually reconciles perfectly correctly
            // (finds a fresh, un-augmented flat copy, reattaches components) - the bug is that the
            // second pass's flat copies belong to a whole second copy of the interior DFU built on
            // top of the first, not a replacement for it, which is what actually produces the
            // reported "extra passive allies" (and likely duplicates the rest of that interior's
            // ordinary enemies too, just less noticeably). Skip a repeat reconciliation against the
            // exact same loadID set within a few seconds of the last one - a real, deliberate reload
            // of the same save takes far longer than that via the load menu, so this only catches
            // the accidental double-fire, not a legitimate later reload.
            if (IsRepeatOfLastReconciliation())
            {
                Debug.LogFormat("Vox's Necromancy: ReconcileAfterLoad SKIPPED - identical loadID set reconciled {0:0.0}s ago (likely a double-fired load).",
                    Time.realtimeSinceStartup - lastReconcileRealtime);
                reanimationsBeingLoaded.Clear();
                return;
            }

            DaggerfallEnemy[] allEnemies = GameObject.FindObjectsOfType<DaggerfallEnemy>();
            Debug.LogFormat("Vox's Necromancy: ReconcileAfterLoad reconciling {0} loadID(s) against {1} DaggerfallEnemy object(s) currently in scene.",
                reanimationsBeingLoaded.Count, allEnemies.Length);

            HashSet<DaggerfallEnemy> handled = new HashSet<DaggerfallEnemy>();
            List<ReanimatedRecord> unmatchedRecords = new List<ReanimatedRecord>();

            foreach (KeyValuePair<ulong, ReanimatedRecord> pair in reanimationsBeingLoaded)
            {
                ulong loadID = pair.Key;
                ReanimatedRecord record = pair.Value;

                DaggerfallEnemy withLifetime = null;
                DaggerfallEnemy flatCopy = null;
                int matchCount = 0;

                for (int i = 0; i < allEnemies.Length; i++)
                {
                    DaggerfallEnemy candidate = allEnemies[i];
                    if (candidate == null || candidate.LoadID != loadID)
                        continue;

                    matchCount++;
                    if (candidate.GetComponent<ReanimatedLifetime>() != null)
                        withLifetime = candidate;
                    else
                        flatCopy = candidate;
                }

                string action;
                if (withLifetime != null && flatCopy != null)
                {
                    handled.Add(withLifetime);
                    handled.Add(flatCopy);
                    GameObject.Destroy(flatCopy.gameObject);
                    action = "destroyed stray flat copy";
                }
                else if (flatCopy != null)
                {
                    handled.Add(flatCopy);
                    AttachReanimationComponents(flatCopy, record);
                    action = "reattached components to flat copy";
                }
                else if (withLifetime != null)
                {
                    handled.Add(withLifetime);
                    action = "original survived alone, nothing to do";
                }
                else
                {
                    // DFU's own LoadID-collision-avoidance (SerializableEnemy.Start(), dungeon-only)
                    // can renumber a freshly-restored enemy's LoadID away from what was saved, so an
                    // exact-match miss doesn't necessarily mean the ally is truly gone - fall back to
                    // a best-effort sweep below instead of leaving it permanently orphaned/inert.
                    unmatchedRecords.Add(record);
                    action = "no exact LoadID match - deferred to fallback sweep";
                }

                Debug.LogFormat("Vox's Necromancy: ReconcileAfterLoad loadID={0} matches={1} withLifetime={2} flatCopy={3} action='{4}'.",
                    loadID, matchCount, withLifetime != null, flatCopy != null, action);
            }

            if (unmatchedRecords.Count > 0)
            {
                int reattachedByFallback = 0;
                for (int i = 0; i < allEnemies.Length && reattachedByFallback < unmatchedRecords.Count; i++)
                {
                    DaggerfallEnemy candidate = allEnemies[i];
                    if (candidate == null || handled.Contains(candidate) || candidate.GetComponent<ReanimatedLifetime>() != null)
                        continue;

                    DaggerfallEntityBehaviour behaviour = candidate.GetComponent<DaggerfallEntityBehaviour>();
                    EnemyEntity enemyEntity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
                    if (enemyEntity == null || enemyEntity.Team != MobileTeams.PlayerAlly)
                        continue;

                    AttachReanimationComponents(candidate, unmatchedRecords[reattachedByFallback]);
                    handled.Add(candidate);
                    reattachedByFallback++;
                }

                Debug.LogFormat("Vox's Necromancy: ReconcileAfterLoad fallback sweep reattached {0}/{1} unmatched record(s).",
                    reattachedByFallback, unmatchedRecords.Count);
            }

            lastReconciledLoadIDs = new HashSet<ulong>(reanimationsBeingLoaded.Keys);
            lastReconcileRealtime = Time.realtimeSinceStartup;

            reanimationsBeingLoaded.Clear();
        }

        static bool IsRepeatOfLastReconciliation()
        {
            if (Time.realtimeSinceStartup - lastReconcileRealtime >= 5f)
                return false;

            if (reanimationsBeingLoaded.Count != lastReconciledLoadIDs.Count)
                return false;

            foreach (ulong loadID in reanimationsBeingLoaded.Keys)
            {
                if (!lastReconciledLoadIDs.Contains(loadID))
                    return false;
            }

            return true;
        }

        static void AttachReanimationComponents(DaggerfallEnemy target, ReanimatedRecord record)
        {
            ReanimatedLifetime lifetime = target.gameObject.AddComponent<ReanimatedLifetime>();
            lifetime.RoundsRemaining = record.RoundsRemaining;
            lifetime.FoeType = record.FoeType;
            lifetime.Allied = record.Allied;
            lifetime.Magnitude = record.Magnitude;
            lifetime.MysticismLevel = record.MysticismLevel;
            lifetime.Intelligence = record.Intelligence;
            lifetime.Willpower = record.Willpower;
            lifetime.MagickaUpkeep = record.MagickaUpkeep;

            if (record.Allied)
            {
                AllyCombatController combatController = target.gameObject.AddComponent<AllyCombatController>();
                combatController.MagickaUpkeep = record.MagickaUpkeep;
            }
        }

        #region Logging

        public static void Log(string msg)
        {
            if (LogLevel == Logging.All)
                Debug.Log("Vox's Necromancy: " + msg);
        }

        public static void LogWarning(string msg)
        {
            if (LogLevel != Logging.None)
                Debug.LogWarning("Vox's Necromancy: " + msg);
        }

        public static void LogError(string msg)
        {
            if (LogLevel != Logging.None)
                Debug.LogError("Vox's Necromancy: " + msg);
        }

        #endregion
    }
}
