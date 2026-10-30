// Project:         Astrid's Guide to Necormancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Attached to the single ghost Seance summons. Drives movement along a path produced by
// SeanceVoxelPathfinder over a SeanceDungeonGrid - replaces the earlier NavMesh-based version (see
// SeanceGhostGuideLegacyNavMesh.cs, kept fully intact as an inert rollback option; flip
// SeanceEffect.UseVoxelPathfinding to switch back to it). That system kept failing on cave dungeons
// with zero bakeable NavMesh coverage no amount of bake-tuning ever fixed, plus elevators and pits it
// could only paper over with a hybrid Walking/Riding/Flying/Following state machine. The voxel grid
// samples cell openness/support/edges directly from physics on demand instead of relying on Unity's
// own walkable-surface inference, so it never depends on NavMesh's rasterization at all.
//
// State is plain int constants, not an enum - see SeanceCell.cs's own header comment for why (a
// nested enum elsewhere in this mod once broke the mod loader's reflection scan outright).
//
// Movement is pure transform interpolation - the ghost's own CharacterController stays disabled for
// its whole lifetime and it never collides with anything. The PATH is what guarantees the route is
// player-walkable; the ghost itself is ethereal. Real testing with collided movement showed it only
// ever caused problems: the fixed hover offset rode cell centers 2.5-3m high, bumping the solid wall
// above every doorway, and CharacterController.Move() fighting geometry produced constant stutter and
// stuck-recovery churn. Height is handled per waypoint instead - see GetWaypointTarget's
// ground-relative hover, which also dips lower through door passages so the sprite threads the
// opening rather than the lintel.
//
// Doors: reuses ForceOpenNearbyDoor exactly as before - the grid treats every door as open space for
// pathing purposes (see SeanceDungeonGrid's own header comment), so the REAL door object still needs
// to be physically opened as the ghost actually reaches it, same lightweight periodic overlap check
// this mod has always used for that.
//
// Hostile targeting/collision: EnemySenses.Target/SecondaryTarget are cleared every tick (belt and
// suspenders - see ClearHostileTargetingOfSelf for why that alone isn't airtight), and this ghost's own
// CharacterController has its physical collision ignored against both the player and every active
// enemy's CharacterController, refreshed on the same periodic cadence as the hostile-target clear.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;

namespace VoxsNecromancyMod.AI
{
    public class SeanceGhostGuide : MonoBehaviour
    {
        static SeanceGhostGuide activeInstance;

        // Read-only accessor so seance_trace can record where the ghost actually goes. Diagnostic use only -
        // nothing in the guide's own logic reads it.
        public static SeanceGhostGuide Active
        {
            get { return activeInstance; }
        }

        public static void DespawnActive()
        {
            if (activeInstance != null)
                GameObject.Destroy(activeInstance.gameObject);
        }

        const int StateSearching = 0;
        const int StateFollowingPath = 1;
        const int StateWaitingForPlayer = 2;
        const int StateStuckRecovery = 3;
        const int StateNoPathFallback = 4;
        const int StateArriving = 5;
        const int StateWaitingAtTeleporter = 6;
        const int StateTeleporting = 7;
        const int StateWaitingAfterTeleporter = 8;
        const int StateLeadToLever = 9;
        const int StateWaitForActivation = 10;
        const int StateHoveringAtObstacle = 11;

        EnemyMotor enemyMotor;
        EnemySenses enemySenses;
        DaggerfallEntityBehaviour entityBehaviour;
        CharacterController controller;
        Renderer ghostRenderer;

        int state;
        Transform destinationTransform;
        Vector3 rawTargetPosition;

        SeanceVoxelPathfinder pathfinder;
        List<SeanceVoxelPathfinder.SeancePathPoint> path;
        int waypointIndex;

        float hoverHeight = 1.5f;
        const float doorHoverHeight = 1.0f;

        // How far below a swim waypoint to check for real floor (short - only meant to catch
        // shallow-puddle cases, never long enough to reach a genuine lake's own distant floor), and
        // how high above that floor to hover if found - low enough to still read as wading/swimming,
        // not full walking height.
        const float shallowWaterCheckDepth = 2f;
        const float shallowWaterHoverHeight = 0.4f;
        float moveSpeed;
        float waitDistance;
        float resumeDistance;
        const float turnSpeed = 200f;

        const float waypointArrivalThreshold = 0.75f;
        const float finalArrivalThreshold = 2f;

        float lastProgressDistance;
        float lastProgressTime;
        const float stuckThreshold = 2f;

        float stuckRecoveryStartTime;
        const float stuckRecoveryDuration = 2f;

        float loseSightTimer;
        const float loseSightGiveUpTime = 3f;

        float noPathFallbackStartTime;
        const float noPathFallbackDuration = 3f;

        float arrivalStartTime;
        const float arrivalCircleDuration = 2.5f;
        const float arrivalCircleRadius = 1.5f;
        const float arrivalCircleSpeed = 3f;

        const float doorCheckRadius = 2.5f;
        const float hostileTargetClearInterval = 0.5f;
        float nextHostileTargetClearTime;

        float teleportCrossingStartTime;
        const float teleportVanishDuration = 0.4f;

        // Which state UpdateWaitingForPlayer/UpdateStuckRecovery should return to once the player
        // catches up / the failsafe resolves - StateFollowingPath for the main route,
        // StateLeadToLever while escorting to a lever. Set at the point each path-following leg
        // actually begins (see RunSearch/RunLeverSearch's own success branches).
        int pathFollowReturnState = StateFollowingPath;

        // Stage 2 - lever/chain escort. One escort (or trapdoor encounter) is ever active at a time.
        HashSet<SeanceGatedObstacle> attemptedObstacles = new HashSet<SeanceGatedObstacle>();
        HashSet<SeanceGatedObstacle> encounteredTrapdoors = new HashSet<SeanceGatedObstacle>();
        SeanceGatedObstacle currentObstacle;
        List<DaggerfallAction> remainingRoots;
        HashSet<DaggerfallAction> visitedRoots;
        DaggerfallAction currentRoot;

        // True while resolving an obstacle discovered because the OVERALL search failed (Teleporter/
        // Door kind - never actually reached, so "give up on this one, try a different obstacle" is
        // the right move if no root pans out). False while handling a Trapdoor encountered mid-route
        // (already standing right at it, so "fall back to hovering in place" is the right move instead
        // - there is nowhere more useful to retreat to).
        bool obstacleEscortIsSearchDriven;

        float rootWaitStartTime;
        float lastActivationMessageTime;

        // Baseline for Door/Trapdoor kind obstacles - see IsObstacleNowOpen's own comment for why this
        // replaced a CurrentState == ActionState.End check (a barrier that auto-closes/toggles faster
        // than this component's per-frame poll can reliably catch the transient End state in between).
        int obstacleActivationCountAtWaitStart;
        const float multiRootCycleTimeout = 20f;
        const float activationMessageRepeatInterval = 8f;
        const int maxObstacleEscortAttempts = 5;
        int obstacleEscortAttemptCount;

        // Optimistic shortcut routing. A successful search means the ghost CAN get there, but a lever-gated
        // barrier sitting on the natural route (a portcullis or cage in a doorway) may hide a far shorter
        // way, and such a barrier is easy for the player to walk into and get stranded at. So after the
        // honest route is found, re-measure it with each nearby barrier hypothetically open; if one is a
        // genuine shortcut, escort the player to its lever first.
        bool gatedShortcutEvaluated;
        bool obstacleEscortIsShortcut;
        List<SeanceVoxelPathfinder.SeancePathPoint> honestPath;
        float honestPathCost;

        // Only barriers this close to the honest route are considered - a barrier far off-route is not a
        // shortcut, it is a different part of the dungeon.
        const float shortcutRouteNearDistance = 8f;

        // The hypothetical route must beat the honest one by a real margin, or the ghost would send the
        // player to work a lever to save a couple of metres.
        const float shortcutImprovementFactor = 0.6f;

        // Each candidate costs a full extra search, so bound it.
        const int maxShortcutCandidates = 2;

        // The honest route is known to work, so a shortcut escort must never be able to strand the player:
        // if the lever goes unused this long, give up and just take the long way.
        const float shortcutEscortTimeout = 30f;
        float shortcutEscortStartTime;

        // How far from a gated obstacle a reachable cell may be for that obstacle to count as bordering the
        // reachable region. A barrier stops the search right at itself, so its own neighbourhood is where
        // reachable cells end - a few cells of slack covers the barrier's own thickness and footprint.
        const int obstacleBorderCellRadius = 3;

        // The set of cells the last failed main search actually settled - i.e. the reachable region. Captured
        // so obstacle candidacy can be judged by "does this barrier border where we could get to", which is
        // the real question, rather than by distance to the closest-approach point.
        HashSet<SeanceCell> lastMainSearchReachable = new HashSet<SeanceCell>();

        // Teleporter-kind obstacles are excluded from this test: a teleporter's own cell being passable says
        // nothing about whether its destination edge has been enabled, so their state stays action-based.
        bool IsObstacleAlreadyOpen(SeanceDungeonGrid grid, SeanceGatedObstacle obstacle)
        {
            if (obstacle.Kind == SeanceGatedObstacle.KindTeleporter)
                return false;

            grid.InvalidateArea(obstacle.ObstaclePosition, obstacleInvalidateRadius);
            return grid.IsOpen(SeanceCell.FromWorld(obstacle.ObstaclePosition, SeanceDungeonGrid.CellSize));
        }

        bool BordersReachableRegion(Vector3 obstaclePosition, HashSet<SeanceCell> reachable)
        {
            if (reachable == null || reachable.Count == 0)
                return true;

            SeanceCell centre = SeanceCell.FromWorld(obstaclePosition, SeanceDungeonGrid.CellSize);

            for (int dx = -obstacleBorderCellRadius; dx <= obstacleBorderCellRadius; dx++)
            {
                for (int dy = -obstacleBorderCellRadius; dy <= obstacleBorderCellRadius; dy++)
                {
                    for (int dz = -obstacleBorderCellRadius; dz <= obstacleBorderCellRadius; dz++)
                    {
                        if (reachable.Contains(centre.Offset(dx, dy, dz)))
                            return true;
                    }
                }
            }

            return false;
        }

        // Search exhausting this close to the target means the target itself is sealed in (caged target,
        // exit marker inside the exit door), not that the route is blocked. Measured: 2.0m and 2.2m for
        // genuinely sealed targets, versus 12.4m and 27.6m where a lever really was the answer.
        const float sealedTargetDistance = 4f;

        // Independent of any specific obstacle's own activation signal: periodically re-test whether
        // the ORIGINAL overall route is passable yet. Covers an obstacle that was misregistered as
        // blocking in the first place (e.g. a purely decorative mover whose collider reads as solid in
        // the grid but was never actually gating the passage) - waiting on ITS activation could stall
        // forever even though the real route already opened up (e.g. as soon as a DIFFERENT obstacle
        // earlier in the same cascade was resolved). A throwaway pathfinder instance is used so a
        // failed check doesn't disturb the obstacle escort already in progress.
        float lastRouteRecheckTime;
        const float routeRecheckInterval = 6f;
        bool routeRecheckInProgress;

        // Generous enough to cover an obstacle's own footprint PLUS at least one neighboring cell in
        // every direction - see SeanceDungeonGrid.InvalidateArea's own comment for why a tighter
        // radius can leave a stale cached edge on a cell just outside it.
        const float obstacleInvalidateRadius = 4f;

        const float trapdoorProximityRadius = 2f;

        bool teleportActivationDetected;

        void Awake()
        {
            enemyMotor = GetComponent<EnemyMotor>();
            enemySenses = GetComponent<EnemySenses>();
            entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
            controller = GetComponent<CharacterController>();
            ghostRenderer = GetComponentInChildren<Renderer>();

            // Both stay off permanently - see the file header comment on why the ghost never collides.
            enemyMotor.enabled = false;
            controller.enabled = false;

            moveSpeed = ((entityBehaviour.Entity.Stats.LiveSpeed + PlayerSpeedChanger.dfWalkBase) * MeshReader.GlobalScale)
                * 1.2f * VoxsNecromancy.SeanceGhostSpeedMultiplier;
            waitDistance = VoxsNecromancy.SeanceWaitForPlayerDistance;
            resumeDistance = VoxsNecromancy.SeanceResumeDistance;
        }

        // Called once by SeanceEffect right after spawning - target may be null (no quest resource
        // currently placed in this dungeon), in which case the ghost just idles at its spawn point
        // until its SeanceGhostLifetime expires.
        public void Initialize(Transform target)
        {
            destinationTransform = target;
            if (target == null)
                return;

            rawTargetPosition = target.position;

            // Per-casting, so the systemic-failure cap measures THIS summoning rather than accumulating
            // across the ghost's lifetime.

            Debug.LogFormat(
                "Astrid's Guide to Necormancy: Seance leash body radius {0:F2}, search radius {1:F2}, clearance radius {2:F2}.",
                bodyRadius, SeanceDungeonGrid.ProbeRadius, SeanceDungeonGrid.ClearanceRadius);

            BeginSearch();
        }

        void BeginSearch()
        {
            state = StateSearching;
            closestApproachLeadAttempted = false;

            SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
            if (grid == null)
            {
                BeginNoPathFallback();
                return;
            }

            pathfinder = new SeanceVoxelPathfinder(grid, transform.position, rawTargetPosition);
            searchStartRealtime = Time.realtimeSinceStartup;
            StartCoroutine(RunSearch());
        }

        float searchStartRealtime;

        // Clearance ladder, widest first. A WIDER probe samples more of each cell, so thin geometry stops
        // slipping between sample points - measured live, a route that clipped through walls and even
        // through a floor at 0.32 was completely clean at 0.45. But 0.32 is required elsewhere: 0.33+ seals
        // a staircase arch in Privateer's Hold, the starter dungeon. Neither value is right everywhere, so
        // instead of picking one, try the roomiest first and only accept a tighter fit when nothing else
        // works - the ghost then prefers routes the player can comfortably walk, and squeezes only when the
        // dungeon genuinely demands it.
        //
        // Coarse tiers rather than fine steps: changing the radius invalidates every cached cell, so each
        // attempt re-probes the whole reachable area. 0.45 -> 0.29 in 0.01 steps would be 17 full searches
        // for no meaningful extra resolution. In most dungeons the first tier succeeds and this costs
        // exactly what a single search always did.
        static readonly float[] probeRadiusLadder = { 0.45f, 0.38f, SeanceDungeonGrid.DefaultProbeRadius };

        // Real wall-clock time the search actually took (spans many frames) plus the same per-pass
        // node-count/closest-approach breakdown seance_pathcheck reports, for whichever pathfinder instance
        // is current - the "why did this take 14 seconds" question needs THIS cast's own numbers, not an
        // inference from a seance_pathcheck run made from a slightly different spot or moment. Called once
        // after the walk-only ladder and again after the climb ladder (Phase 2.6) - if escort takes over in
        // between, only the walk-only summary is printed, which is correct: climbing was never attempted.
        void LogSearchTiming()
        {
            float elapsedSeconds = Time.realtimeSinceStartup - searchStartRealtime;
            Debug.LogFormat("Astrid's Guide to Necormancy: Seance search took {0:F2}s real time (probe radius {1:F2}).\n{2}",
                elapsedSeconds, SeanceDungeonGrid.ProbeRadius, pathfinder.DebugSummary());
        }

        void FinishSearch()
        {
            honestPath = pathfinder.Result;
            honestPathCost = pathfinder.ResultCost;

            // Evaluated once per cast, on the first successful search only. After a lever is actually
            // thrown, OnObstacleActivated re-searches through here again and must NOT re-evaluate, or the
            // ghost could bounce between shortcut candidates indefinitely.
            if (!gatedShortcutEvaluated)
            {
                gatedShortcutEvaluated = true;
                StartCoroutine(EvaluateGatedShortcuts());
                return;
            }

            FollowHonestPath();
        }

        IEnumerator RunSearch()
        {
            SeanceDungeonGrid ladderGrid = SeanceDungeonGrid.Current;
            bool ladderSucceeded = false;

            // ---- Phase 0: walk only, across every clearance tier, widest first. ----
            for (int tier = 0; tier < probeRadiusLadder.Length && ladderGrid != null; tier++)
            {
                float radius = probeRadiusLadder[tier];
                if (!Mathf.Approximately(SeanceDungeonGrid.ProbeRadius, radius))
                {
                    SeanceDungeonGrid.ProbeRadius = radius;
                    ladderGrid.ClearCellCache();
                }

                pathfinder = new SeanceVoxelPathfinder(ladderGrid, transform.position, rawTargetPosition);
                pathfinder.AllowClimbFallback = false;
                yield return pathfinder.Search();

                if (pathfinder.Status == SeanceVoxelPathfinder.StatusSuccess)
                {
                    ladderSucceeded = true;
                    if (tier > 0)
                        Debug.LogFormat(
                            "Astrid's Guide to Necormancy: Seance settled at probe radius {0:F2} (tier {1} of {2}).",
                            radius, tier + 1, probeRadiusLadder.Length);
                    break;
                }

                Debug.LogFormat(
                    "Astrid's Guide to Necormancy: Seance found no walkable route at probe radius {0:F2}.", radius);
            }

            LogSearchTiming();

            if (ladderSucceeded)
            {
                FinishSearch();
                yield break;
            }

            // Captured once here (not re-read from whatever the CURRENT pathfinder instance happens to be
            // later, which changes meaning after each escort leg) - see TryLeadToNextRoot's own comment on
            // why a self-triggerable obstacle needs this specific, stable reference point rather than its
            // own (unreachable) position. Taken from the WALK-ONLY search specifically (Phase 2.6): escort
            // candidacy must be judged against the search that actually failed, not against a climb-enabled
            // search that may since have expanded a different region.
            lastMainSearchClosestApproach = pathfinder.ClosestApproachPosition;
            lastMainSearchReachable = pathfinder.GetExpandedCells();

            // If the search exhausted while ALREADY standing essentially at the target, the target is
            // sealed in by design - a caged quest target, or a dungeon exit marker embedded in the exit
            // door - and no lever anywhere in the dungeon will ever change that. Escorting here just
            // tours unrelated switches: measured live in the cage dungeon, the search got within 2.0m of
            // the target and the ghost then dragged the player to switches 46-49m away from where it had
            // actually got stuck. Lead to the closest reachable point instead - skipping BOTH escort and
            // climbing, neither of which can help a target that is sealed in by design.
            if (pathfinder.ClosestApproachDistance <= sealedTargetDistance)
            {
                Debug.LogFormat(
                    "Astrid's Guide to Necormancy: Seance search exhausted only {0:F1}m from the target - treating it as " +
                    "sealed in by design and leading to the closest reachable point rather than hunting levers.",
                    pathfinder.ClosestApproachDistance);
                BeginNoPathFallback();
                yield break;
            }

            // ---- Escort before climb (Phase 2.6) ----
            // No direct walking route exists - before ever trying to climb, see if a known gated obstacle
            // (an inactive teleporter, a lever-locked door/portcullis) is worth escorting the player to
            // first. A lever the player can pull beats a wall the ghost might "climb" through geometry with
            // nothing actually climbable there - a climb-pass success used to be indistinguishable from an
            // honest walkable success, which silently disabled the lever escort in ANY dungeon with a bogus
            // climb edge, gated obstacle or not. TryStartObstacleEscort now reports whether it found a
            // candidate rather than deciding the fallback itself.
            if (TryStartObstacleEscort())
                yield break;

            // ---- Phase 1: climb allowed, across every clearance tier. Only reached when no gated obstacle
            // qualified - none bordered the reachable region, all were already open, all previously
            // attempted, or the attempt cap was hit. ----
            for (int tier = 0; tier < probeRadiusLadder.Length && ladderGrid != null; tier++)
            {
                float radius = probeRadiusLadder[tier];
                if (!Mathf.Approximately(SeanceDungeonGrid.ProbeRadius, radius))
                {
                    SeanceDungeonGrid.ProbeRadius = radius;
                    ladderGrid.ClearCellCache();
                }

                pathfinder = new SeanceVoxelPathfinder(ladderGrid, transform.position, rawTargetPosition);
                pathfinder.AllowClimbFallback = true;
                yield return pathfinder.Search();

                if (pathfinder.Status == SeanceVoxelPathfinder.StatusSuccess)
                {
                    ladderSucceeded = true;
                    Debug.LogFormat(
                        "Astrid's Guide to Necormancy: Seance settled at probe radius {0:F2} (tier {1} of {2}) WITH climbing " +
                        "allowed - no walkable route existed at any clearance, and no gated obstacle qualified for an escort either.",
                        radius, tier + 1, probeRadiusLadder.Length);
                    break;
                }

                Debug.LogFormat(
                    "Astrid's Guide to Necormancy: Seance found no climbing route at probe radius {0:F2}.", radius);
            }

            LogSearchTiming();

            if (ladderSucceeded)
            {
                FinishSearch();
                yield break;
            }

            // Neither walking, escorting, nor climbing worked. Closest-approach lead, then drift.
            BeginNoPathFallback();
        }

        // The closest-approach position from the most recent FAILED main search toward the overall
        // target - stable across an entire escort cascade (unlike reading pathfinder.
        // ClosestApproachPosition fresh each time, which after the first lever-leg would instead
        // reflect "how close did we get to THAT leg's own target," not the real destination).
        Vector3 lastMainSearchClosestApproach;

        // Picks the resolvable gated obstacle (Teleporter or Door/Portcullis kind - Trapdoors are only
        // ever discovered by standing at one mid-route, see CheckForTrapdoorObstacle) nearest to where
        // the just-failed search actually got stuck, and begins escorting the player toward whichever
        // of its roots is nearest to the ghost right now. Capped at maxObstacleEscortAttempts to bound
        // a pathological dungeon rather than looping forever.
        // Debug entry point for seance_forceescort. The escort normally only ever runs when the main
        // search FAILS, which makes the lever-then-teleporter sequence impossible to exercise on demand
        // in a dungeon where a walkable alternative exists - this forces it without changing that rule
        // for real casts.
        public string DebugForceObstacleEscort()
        {
            SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
            if (grid == null || grid.GatedObstacles.Count == 0)
                return "No gated obstacles registered in this dungeon.";

            // Reset so an obstacle already tried this cast can be escorted to again.
            attemptedObstacles.Clear();
            encounteredTrapdoors.Clear();
            obstacleEscortAttemptCount = 0;
            currentObstacle = null;

            // The escort measures "nearest obstacle" from where the failed search got stuck; with no
            // failure to draw on, the ghost's own position is the sensible stand-in.
            lastMainSearchClosestApproach = transform.position;

            // Preserves this command's existing behaviour: TryStartObstacleEscort no longer decides the
            // fallback itself, so it is called explicitly here on failure, exactly as it would have run
            // automatically before.
            if (!TryStartObstacleEscort())
                BeginNoPathFallback();

            return currentObstacle != null
                ? string.Format("Forcing escort to obstacle '{0}' (kind={1}, {2} root(s)).",
                    currentObstacle.ObstacleAction.name, currentObstacle.Kind, currentObstacle.Roots.Count)
                : "Could not start an escort - no reachable gated obstacle found (see Player.log).";
        }

        void FollowHonestPath()
        {
            obstacleEscortIsShortcut = false;

            if (honestPath == null)
            {
                BeginNoPathFallback();
                return;
            }

            path = honestPath;
            waypointIndex = 0;
            ResetPathFollowing();
            lastProgressDistance = float.MaxValue;
            lastProgressTime = Time.time;
            pathFollowReturnState = StateFollowingPath;
            state = StateFollowingPath;
        }

        // Abandons a shortcut escort once the ghost has already MOVED (so the stored honest path's start is
        // stale) - re-plans from where it now stands. gatedShortcutEvaluated is already true, so the fresh
        // search will follow its honest result rather than evaluating shortcuts again.
        void AbandonShortcutAndReplan()
        {
            obstacleEscortIsShortcut = false;
            currentObstacle = null;
            BeginSearch();
        }

        IEnumerator EvaluateGatedShortcuts()
        {
            SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
            if (grid == null || grid.GatedObstacles.Count == 0)
            {
                FollowHonestPath();
                yield break;
            }

            // Defensive: drop anything a previously-interrupted evaluation left applied.
            grid.ClearHypotheticalOpen();

            List<SeanceGatedObstacle> candidates = FindGatedObstaclesNearHonestPath(grid);
            SeanceGatedObstacle best = null;
            float bestCost = honestPathCost;

            for (int i = 0; i < candidates.Count && i < maxShortcutCandidates; i++)
            {
                SeanceGatedObstacle candidate = candidates[i];

                bool probeSucceeded;
                float probeCost;

                // try/finally around a yield is legal in a C# iterator, and Unity disposes the iterator when
                // a coroutine is stopped or its GameObject destroyed - so the barrier is released even if
                // this cast is interrupted mid-probe. Without it, an interrupted evaluation would leave the
                // barrier excluded on the GRID, which outlives the ghost, and every later cast in this
                // dungeon would route straight through solid geometry.
                grid.SetHypotheticalOpen(candidate, true);
                try
                {
                    SeanceVoxelPathfinder probe = new SeanceVoxelPathfinder(grid, transform.position, rawTargetPosition);
                    yield return probe.Search();
                    probeSucceeded = probe.Status == SeanceVoxelPathfinder.StatusSuccess;
                    probeCost = probe.ResultCost;
                }
                finally
                {
                    grid.SetHypotheticalOpen(candidate, false);
                }

                bool isRealShortcut = probeSucceeded && probeCost < bestCost * shortcutImprovementFactor;

                Debug.LogFormat(
                    "Astrid's Guide to Necormancy: Seance shortcut check on '{0}' - hypothetically open route {1} (cost {2:F1} " +
                    "vs honest {3:F1}); {4}.",
                    candidate.ObstacleAction.name, probeSucceeded ? "found" : "NOT found", probeCost, honestPathCost,
                    isRealShortcut ? "IS a real shortcut, will escort to its lever" : "not worth a detour");

                if (isRealShortcut)
                {
                    best = candidate;
                    bestCost = probeCost;
                }
            }

            if (best == null)
            {
                FollowHonestPath();
                yield break;
            }

            currentObstacle = best;
            remainingRoots = new List<DaggerfallAction>(best.Roots);
            visitedRoots = new HashSet<DaggerfallAction>();
            obstacleEscortIsSearchDriven = false;
            obstacleEscortIsShortcut = true;
            shortcutEscortStartTime = Time.time;
            attemptedObstacles.Add(best);

            Debug.LogFormat(
                "Astrid's Guide to Necormancy: Seance escorting player to shortcut lever for obstacle '{0}' ({1} root(s)) - " +
                "opening it cuts the route cost from {2:F1} to {3:F1}.",
                best.ObstacleAction.name, best.Roots.Count, honestPathCost, bestCost);

            TryLeadToNextRoot();
        }

        // Gated Door/Teleporter obstacles whose position lies close to the honest route, nearest first. A
        // barrier off in an unrelated part of the dungeon is not a shortcut worth testing.
        List<SeanceGatedObstacle> FindGatedObstaclesNearHonestPath(SeanceDungeonGrid grid)
        {
            List<SeanceGatedObstacle> near = new List<SeanceGatedObstacle>();
            List<float> nearDistances = new List<float>();

            List<SeanceGatedObstacle> obstacles = grid.GatedObstacles;
            for (int i = 0; i < obstacles.Count; i++)
            {
                SeanceGatedObstacle candidate = obstacles[i];
                if (candidate.Kind == SeanceGatedObstacle.KindTrapdoor || candidate.Roots.Count == 0)
                    continue;

                float bestDistanceSq = float.MaxValue;
                for (int w = 0; w < honestPath.Count; w++)
                {
                    float distanceSq = (honestPath[w].Position - candidate.ObstaclePosition).sqrMagnitude;
                    if (distanceSq < bestDistanceSq)
                        bestDistanceSq = distanceSq;
                }

                if (bestDistanceSq > shortcutRouteNearDistance * shortcutRouteNearDistance)
                    continue;

                // Insertion sort by distance - nearest barriers are the likeliest real shortcuts, and only
                // the first maxShortcutCandidates get a search spent on them.
                int insertAt = near.Count;
                for (int k = 0; k < nearDistances.Count; k++)
                {
                    if (bestDistanceSq < nearDistances[k])
                    {
                        insertAt = k;
                        break;
                    }
                }
                near.Insert(insertAt, candidate);
                nearDistances.Insert(insertAt, bestDistanceSq);
            }

            return near;
        }

        // Returns whether an escort was actually started. "No candidate" is a return value, not a terminal
        // action - Phase 2.6 needs to fall through to the climb pass on false rather than have this method
        // decide the fallback itself; the other two call sites (DebugForceObstacleEscort,
        // TryLeadToNextRoot's "try a different obstacle" branch) still call BeginNoPathFallback themselves
        // on false, preserving their existing behaviour exactly.
        bool TryStartObstacleEscort()
        {
            SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
            if (grid == null || obstacleEscortAttemptCount >= maxObstacleEscortAttempts)
                return false;

            Vector3 referencePosition = lastMainSearchClosestApproach;

            // Candidacy is decided by whether an obstacle BORDERS the reachable region, not by how close it
            // is to where the search happened to exhaust. Those are different places: the search exhausts at
            // the cell nearest the TARGET, while the barrier that blocked it can sit anywhere on the region's
            // boundary. Measuring from the closest-approach point rejected the lever-gated cage that was
            // genuinely blocking the route because it sat 27m from that point - the ghost then led the player
            // to a dead end mid-hall instead of to the lever.
            HashSet<SeanceCell> reachable = lastMainSearchReachable;

            SeanceGatedObstacle nearest = null;
            float nearestDistanceSq = float.MaxValue;
            List<SeanceGatedObstacle> obstacles = grid.GatedObstacles;
            for (int i = 0; i < obstacles.Count; i++)
            {
                SeanceGatedObstacle candidate = obstacles[i];
                if (candidate.Kind == SeanceGatedObstacle.KindTrapdoor || attemptedObstacles.Contains(candidate))
                    continue;

                if (!BordersReachableRegion(candidate.ObstaclePosition, reachable))
                    continue;

                // Already open - do not send the player back to a lever they have already thrown. Asked
                // state-free of the grid rather than tracked: if the barrier's own cell reads passable now,
                // the barrier is not blocking anything, whatever its action state says. Without this, a cage
                // the player had already opened kept being offered as the thing to go and operate.
                if (IsObstacleAlreadyOpen(grid, candidate))
                    continue;

                // Among the obstacles that DO border the region, prefer the one nearest where the search got
                // stuck - that is still the best available hint about which barrier is on the way to the goal.
                float distanceSq = (candidate.ObstaclePosition - referencePosition).sqrMagnitude;
                if (distanceSq < nearestDistanceSq)
                {
                    nearestDistanceSq = distanceSq;
                    nearest = candidate;
                }
            }

            if (nearest == null)
                return false;

            obstacleEscortAttemptCount++;
            attemptedObstacles.Add(nearest);
            currentObstacle = nearest;
            remainingRoots = new List<DaggerfallAction>(nearest.Roots);
            visitedRoots = new HashSet<DaggerfallAction>();
            obstacleEscortIsSearchDriven = true;

            Debug.LogFormat(
                "Astrid's Guide to Necormancy: Seance ghost attempting to escort player to obstacle '{0}' (kind={1}, {2} root(s)).",
                nearest.ObstacleAction.name, nearest.Kind, nearest.Roots.Count);

            TryLeadToNextRoot();
            return true;
        }

        // Picks the unvisited root nearest to the ghost's CURRENT position (recomputed fresh each
        // call, not just once, since "nearest" changes as a multi-root tour moves the ghost around)
        // and starts a fresh A* leg toward it. If every root has been tried without success, this
        // obstacle is abandoned - see the two branches below for what "abandoned" means for a
        // search-driven obstacle (try a different one) versus a trapdoor encountered mid-route
        // (fall back to hovering, since there's nowhere more useful to go).
        void TryLeadToNextRoot()
        {
            DaggerfallAction chosen = null;
            float nearestDistanceSq = float.MaxValue;
            for (int i = 0; i < remainingRoots.Count; i++)
            {
                DaggerfallAction candidate = remainingRoots[i];
                if (visitedRoots.Contains(candidate))
                    continue;

                float distanceSq = (candidate.transform.position - transform.position).sqrMagnitude;
                if (distanceSq < nearestDistanceSq)
                {
                    nearestDistanceSq = distanceSq;
                    chosen = candidate;
                }
            }

            if (chosen == null)
            {
                Debug.LogFormat(
                    "Astrid's Guide to Necormancy: Seance ghost exhausted every root for obstacle '{0}' ({1} candidate(s)) " +
                    "without success.", currentObstacle.ObstacleAction.name, currentObstacle.Roots.Count);

                if (obstacleEscortIsShortcut)
                {
                    // A shortcut is strictly optional - the honest route already works, so an unreachable
                    // lever just means take the long way.
                    AbandonShortcutAndReplan();
                }
                else if (obstacleEscortIsSearchDriven)
                {
                    // This obstacle isn't panning out (every root was unreachable - the classic
                    // "lever behind its own locked door" trap layout included, since chain analysis
                    // never verifies spatial reachability up front - see
                    // SeanceDungeonGrid.FindPlayerActivatableRoots's own comment for why). Try a
                    // different obstacle instead of getting stuck on this one. Preserves existing
                    // behaviour on failure (fall back rather than retry climbing mid-cascade, which is
                    // out of scope for Phase 2.6's reorder).
                    currentObstacle = null;
                    if (!TryStartObstacleEscort())
                        BeginNoPathFallback();
                }
                else
                {
                    BeginHoveringAtObstacle();
                }
                return;
            }

            currentRoot = chosen;

            // Toward the root's OWN position, even for a self-triggerable obstacle whose position IS the
            // blocking geometry - the pathfinder's own goal passage (SeanceVoxelPathfinder.
            // GoalPassageRadius) force-opens the cells right at the goal, which covers exactly that case.
            // An earlier version aimed at a previously-captured closest-approach point instead; that went
            // stale as soon as more than one obstacle was involved in a cascade, and aiming searches at
            // arbitrary dead-end points is what made stale force-open pockets accumulate.
            SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
            pathfinder = new SeanceVoxelPathfinder(grid, transform.position, chosen.transform.position);
            searchStartRealtime = Time.realtimeSinceStartup;
            state = StateSearching;
            StartCoroutine(RunLeverSearch());
        }

        IEnumerator RunLeverSearch()
        {
            yield return pathfinder.Search();

            float elapsedSeconds = Time.realtimeSinceStartup - searchStartRealtime;
            Debug.LogFormat("Astrid's Guide to Necormancy: Seance lever-leg search took {0:F2}s real time.\n{1}",
                elapsedSeconds, pathfinder.DebugSummary());

            if (pathfinder.Status == SeanceVoxelPathfinder.StatusSuccess)
            {
                path = pathfinder.Result;
                waypointIndex = 0;
                ResetPathFollowing();
                lastProgressDistance = float.MaxValue;
                lastProgressTime = Time.time;
                pathFollowReturnState = StateLeadToLever;
                state = StateLeadToLever;
                yield break;
            }

            // This specific root is unreachable from here - try another one for the same obstacle.
            visitedRoots.Add(currentRoot);
            TryLeadToNextRoot();
        }

        // Near-identical to UpdateFollowingPath's own movement/wait/stuck logic, just with a
        // different arrival action (BeginWaitForActivation instead of BeginArrival) - kept as a
        // separate method rather than unified via a shared helper, to avoid risking the
        // already-working main path-following code under this rework.
        void UpdateLeadToLever()
        {
            if (path == null || waypointIndex >= path.Count)
            {
                BeginWaitForActivation();
                return;
            }

            Transform playerTransform = GameManager.Instance.PlayerObject.transform;
            float playerDistance = Vector3.Distance(transform.position, playerTransform.position);
            bool hasLineOfSight = HasLineOfSightToPlayer(playerTransform);

            if (!hasLineOfSight)
                loseSightTimer += Time.deltaTime;
            else
                loseSightTimer = 0f;

            if (playerDistance > waitDistance || loseSightTimer > loseSightGiveUpTime)
            {
                pathFollowReturnState = StateLeadToLever;
                state = StateWaitingForPlayer;
                return;
            }

            float threshold = waypointIndex == path.Count - 1 ? finalArrivalThreshold : waypointArrivalThreshold;
            Vector3 waypoint = GetWaypointTarget(waypointIndex);
            float distanceToWaypoint = Vector3.Distance(transform.position, waypoint);

            if (distanceToWaypoint < threshold)
            {
                waypointIndex++;
                lastProgressDistance = float.MaxValue;
                lastProgressTime = Time.time;

                // Same teleport-crossing detection as UpdateFollowingPath - a lever can legitimately
                // sit on the far side of an active teleporter. Missing this here (a real bug found in
                // testing) sent the ghost walking MoveTowards straight at an arbitrarily distant
                // waypoint instead of recognizing the crossing - observed as the ghost falling
                // through the floor partway there.
                if (waypointIndex < path.Count && path[waypointIndex].MoveType == SeanceMoveType.Teleport)
                {
                    pathFollowReturnState = StateLeadToLever;
                    state = StateWaitingAtTeleporter;
                }

                return;
            }

            if (distanceToWaypoint < lastProgressDistance - 0.1f)
            {
                lastProgressDistance = distanceToWaypoint;
                lastProgressTime = Time.time;
            }
            else if (Time.time - lastProgressTime > stuckThreshold)
            {
                pathFollowReturnState = StateLeadToLever;
                BeginStuckRecovery();
                return;
            }

            AdvanceAlongSegment(waypoint, moveSpeed);
        }

        void BeginWaitForActivation()
        {
            state = StateWaitForActivation;
            rootWaitStartTime = Time.time;
            lastActivationMessageTime = -999f;
            lastRouteRecheckTime = Time.time;
            VoxsNecromancy.PlaySeanceUseThisSound();

            if (currentObstacle.Kind == SeanceGatedObstacle.KindTeleporter)
            {
                teleportActivationDetected = false;
                DaggerfallAction.OnTeleportAction += OnAnyTeleportFired;
            }
            else
            {
                obstacleActivationCountAtWaitStart = currentObstacle.ObstacleAction.activationCount;
            }
        }

        // Watches the OBSTACLE's own state, never the root/lever's - per design, a multi-root tour's
        // only real exit condition is the obstacle itself opening, regardless of which lever (or how
        // many) actually caused it.
        void UpdateWaitForActivation()
        {
            if (IsObstacleNowOpen(currentObstacle))
            {
                OnObstacleActivated();
                return;
            }

            // A shortcut escort must never strand the player at a lever they do not want to use - the honest
            // route is known to work, so give up after a while and just take it. Skips the periodic route
            // recheck too: that recheck exists to notice a blocked route becoming passable, but here the main
            // route was passable all along, so it would abandon the escort within seconds every time.
            if (obstacleEscortIsShortcut)
            {
                if (Time.time - shortcutEscortStartTime > shortcutEscortTimeout)
                {
                    Debug.LogFormat(
                        "Astrid's Guide to Necormancy: Seance shortcut lever for '{0}' went unused for {1:F0}s - taking the " +
                        "longer route instead.", currentObstacle.ObstacleAction.name, shortcutEscortTimeout);
                    AbandonShortcutAndReplan();
                    return;
                }
            }
            else
            {
                TryPeriodicRouteRecheck();
            }

            Transform faceTarget = currentRoot != null ? currentRoot.transform : GameManager.Instance.PlayerObject.transform;
            FaceTowards(faceTarget.position);

            if (Time.time - lastActivationMessageTime > activationMessageRepeatInterval)
            {
                lastActivationMessageTime = Time.time;
                bool isPasswordRoot = currentRoot != null && currentRoot.ActionFlag == DFBlock.RdbActionFlags.ShowTextWithInput;
                DaggerfallUI.AddHUDText(isPasswordRoot
                    ? "The spirit waits for the word to be spoken."
                    : "The spirit gestures insistently at the mechanism.");

                // Temporary diagnostic for the "asks again and again" report - prints exactly what the
                // obstacle's own action state/activation count are doing on this cadence, so the next
                // repro tells us directly whether activationCount is changing at all when the player
                // interacts, or changing but landing on even parity, rather than guessing further.
                Debug.LogFormat(
                    "Astrid's Guide to Necormancy: Seance still waiting on obstacle '{0}' (kind={1}) - " +
                    "ObstacleAction.activationCount={2} (baseline {3}), CurrentState={4}, IsPlaying={9}, root='{5}' " +
                    "root.activationCount={6}, root.TriggerFlag={7}, root.ActionFlag={8}.",
                    currentObstacle.ObstacleAction.name, currentObstacle.Kind,
                    currentObstacle.ObstacleAction.activationCount, obstacleActivationCountAtWaitStart,
                    currentObstacle.ObstacleAction.CurrentState,
                    currentRoot != null ? currentRoot.name : "(none)",
                    currentRoot != null ? currentRoot.activationCount : -1,
                    currentRoot != null ? currentRoot.TriggerFlag.ToString() : "(none)",
                    currentRoot != null ? currentRoot.ActionFlag.ToString() : "(none)",
                    currentObstacle.ObstacleAction.IsPlaying());
            }

            // Multi-root tour: if this lever hasn't been thrown in a while and there's another
            // candidate left, move on to it - the player solves the actual lever combination, the
            // ghost's job is only to reveal where they all are.
            if (remainingRoots.Count > 1 && Time.time - rootWaitStartTime > multiRootCycleTimeout)
            {
                visitedRoots.Add(currentRoot);
                TryLeadToNextRoot();
            }
        }

        // CurrentState == ActionState.End (the original check here) only catches a barrier sitting
        // still in its fully-open state at the exact moment this per-frame poll runs - a barrier that
        // auto-closes or otherwise toggles back quickly can pass back through End and never be seen
        // there, leaving the ghost waiting forever even though the player DID activate it (reported
        // in-game as "asks me to use the torch again and again"). activationCount increments once per
        // real Receive() activation regardless of how briefly the resulting state lasts, so counting
        // activations since this wait began - and requiring an ODD number of them - detects "flipped to
        // open" even for a fast auto-close/toggle, while still correctly treating an EVEN number (e.g.
        // toggled open then closed again before OnObstacleActivated could poll it) as still closed.
        bool IsObstacleNowOpen(SeanceGatedObstacle obstacle)
        {
            if (obstacle.Kind == SeanceGatedObstacle.KindTeleporter)
                return teleportActivationDetected;

            // activationCount increments synchronously the instant Receive() accepts the trigger -
            // BEFORE Play() has actually finished (or even started) moving the obstacle. Without this
            // gate, a barrier with a multi-second open animation gets detected as "open" the moment it's
            // clicked, while still physically closed - a fresh path search runs too early, fails, and
            // the ghost falls back to escorting again, which is exactly the "asks again and again"
            // report. IsPlaying() (true while PlayingForward/PlayingReverse, chaining through
            // NextObject) is DFU's own signal for "still mid-animation" - wait for it to clear first.
            if (obstacle.ObstacleAction.IsPlaying())
                return false;

            int activationsSinceWaitStart = obstacle.ObstacleAction.activationCount - obstacleActivationCountAtWaitStart;
            return activationsSinceWaitStart % 2 == 1;
        }

        // Called from both WaitForActivation and HoveringAtObstacle - see the field comment on
        // lastRouteRecheckTime for why this exists alongside (not instead of) IsObstacleNowOpen.
        void TryPeriodicRouteRecheck()
        {
            if (currentObstacle.Kind == SeanceGatedObstacle.KindTeleporter || routeRecheckInProgress)
                return;

            if (Time.time - lastRouteRecheckTime < routeRecheckInterval)
                return;

            lastRouteRecheckTime = Time.time;
            StartCoroutine(RecheckMainRouteWhileWaiting());
        }

        IEnumerator RecheckMainRouteWhileWaiting()
        {
            SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
            if (grid == null)
                yield break;

            // Force a fresh physics re-probe around the obstacle we're stuck on instead of trusting
            // whatever the grid cached the first time it saw this area - InvalidateArea normally only
            // runs from OnObstacleActivated, which never fires for an obstacle whose own activation
            // signal never arrives (a misregistered/non-blocking mover, or one triggered a way this mod
            // doesn't detect). Without this, every recheck would just re-consult the same stale cached
            // "blocked" cells and fail identically forever, even if the passage was never truly blocked.
            grid.InvalidateArea(currentObstacle.ObstaclePosition, obstacleInvalidateRadius);

            routeRecheckInProgress = true;
            SeanceVoxelPathfinder recheckPathfinder = new SeanceVoxelPathfinder(grid, transform.position, rawTargetPosition);
            yield return recheckPathfinder.Search();
            routeRecheckInProgress = false;

            // The wait may have already resolved normally (or been abandoned) by the time this
            // finishes - only act on a still-relevant, still-waiting result.
            if (recheckPathfinder.Status != SeanceVoxelPathfinder.StatusSuccess
                || (state != StateWaitForActivation && state != StateHoveringAtObstacle))
            {
                yield break;
            }

            Debug.LogFormat(
                "Astrid's Guide to Necormancy: Seance periodic route recheck succeeded while waiting on obstacle '{0}' " +
                "(kind={1}) - resuming main route without waiting for its own activation.",
                currentObstacle.ObstacleAction.name, currentObstacle.Kind);

            path = recheckPathfinder.Result;
            waypointIndex = 0;
            ResetPathFollowing();
            lastProgressDistance = float.MaxValue;
            lastProgressTime = Time.time;
            pathFollowReturnState = StateFollowingPath;
            state = StateFollowingPath;
        }

        // Fires whenever ANY teleporter in the game activates - DaggerfallAction.OnTeleportAction is a
        // static, dungeon-wide event, so this only reacts when triggerObj matches the SPECIFIC
        // teleporter this ghost is currently waiting on.
        void OnAnyTeleportFired(GameObject triggerObj, GameObject nextObj)
        {
            bool isWatchedObstacle = currentObstacle != null && currentObstacle.Kind == SeanceGatedObstacle.KindTeleporter
                && currentObstacle.ObstacleAction != null && triggerObj == currentObstacle.ObstacleAction.gameObject;

            Debug.LogFormat(
                "Astrid's Guide to Necormancy: Seance OnTeleportAction fired - triggerObj='{0}' (id={1}), watching='{2}' (id={3}), match={4}.",
                triggerObj != null ? triggerObj.name : "(null)", triggerObj != null ? triggerObj.GetInstanceID() : 0,
                currentObstacle != null && currentObstacle.ObstacleAction != null ? currentObstacle.ObstacleAction.name : "(none)",
                currentObstacle != null && currentObstacle.ObstacleAction != null ? currentObstacle.ObstacleAction.gameObject.GetInstanceID() : 0,
                isWatchedObstacle);

            if (isWatchedObstacle)
                teleportActivationDetected = true;
        }

        void OnObstacleActivated()
        {
            // The barrier is open now, so whatever it was gating is a normal route from here on - the
            // shortcut framing (and its give-up timeout) no longer applies.
            obstacleEscortIsShortcut = false;

            if (currentObstacle.Kind == SeanceGatedObstacle.KindTeleporter)
                DaggerfallAction.OnTeleportAction -= OnAnyTeleportFired;

            Debug.LogFormat("Astrid's Guide to Necormancy: Seance gated obstacle '{0}' activated (kind={1}).",
                currentObstacle.ObstacleAction.name, currentObstacle.Kind);

            SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
            if (grid != null)
                grid.InvalidateArea(currentObstacle.ObstaclePosition, obstacleInvalidateRadius);

            if (currentObstacle.Kind == SeanceGatedObstacle.KindTeleporter)
            {
                if (grid != null)
                    grid.ActivateTeleportObstacle(currentObstacle);

                StartCoroutine(CrossActivatedTeleporterThenResearch());
                return;
            }

            bool wasTrapdoorEncounter = currentObstacle.Kind == SeanceGatedObstacle.KindTrapdoor;
            currentObstacle = null;

            if (wasTrapdoorEncounter)
            {
                // The route already correctly continues past this point (a trapdoor was never
                // actually blocking the path to begin with) - just resume exactly where
                // path-following left off, no fresh search needed.
                pathFollowReturnState = StateFollowingPath;
                state = StateFollowingPath;
            }
            else
            {
                // Door/portcullis: the grid's cache for its footprint is now cleared, so a fresh
                // search from here toward the ORIGINAL overall target will naturally find the route
                // through it (deliberately re-searching rather than trying to resume a precomputed
                // multi-leg route - activation can change dungeon state, so re-planning from current
                // position each time is more robust than trusting a stale plan).
                BeginSearch();
            }
        }

        // Mirrors Stage 1's own vanish/reposition/reveal crossing exactly, just triggered by a LEVER
        // firing the teleporter rather than the ghost/player simply walking up to an already-active
        // one - the real player has ALREADY been relocated by the time this runs (DaggerfallAction.
        // Teleport() moves the player immediately and unconditionally the moment the chain reaches
        // it, confirmed by direct engine source reading - there is no separate "walk through it"
        // step for a lever-triggered teleport), so the ghost's own job is just to mirror that jump.
        IEnumerator CrossActivatedTeleporterThenResearch()
        {
            if (ghostRenderer != null)
                ghostRenderer.enabled = false;
            VoxsNecromancy.PlaySeanceTeleportSound();

            yield return new WaitForSeconds(teleportVanishDuration);

            Vector3 destination = currentObstacle.TeleportDestinationPosition;
            RaycastHit hit;
            int mask = 1 << LayerMask.NameToLayer("Default");
            if (Physics.Raycast(destination + Vector3.up * 0.5f, Vector3.down, out hit, 4.5f, mask, QueryTriggerInteraction.Ignore))
                transform.position = new Vector3(destination.x, hit.point.y + hoverHeight, destination.z);
            else
                transform.position = destination;

            if (ghostRenderer != null)
                ghostRenderer.enabled = true;

            currentObstacle = null;
            BeginSearch();
        }

        // Terminal fallback for an obstacle with no resolvable root (or one whose roots were all
        // unreachable) that the ghost is already standing at (a Trapdoor encountered mid-route - see
        // CheckForTrapdoorObstacle). Keeps polling the obstacle's own state regardless, so it still
        // resumes if the barrier opens by some other means - a lever this mod's own chain analysis
        // didn't find, or the player simply discovering it on their own.
        void BeginHoveringAtObstacle()
        {
            state = StateHoveringAtObstacle;
            obstacleActivationCountAtWaitStart = currentObstacle.ObstacleAction.activationCount;
            lastRouteRecheckTime = Time.time;
            Debug.LogFormat("Astrid's Guide to Necormancy: Seance ghost hovering at unresolvable obstacle '{0}' (kind={1}).",
                currentObstacle.ObstacleAction.name, currentObstacle.Kind);
        }

        void UpdateHoveringAtObstacle()
        {
            if (IsObstacleNowOpen(currentObstacle))
            {
                OnObstacleActivated();
                return;
            }

            TryPeriodicRouteRecheck();

            FaceTowards(GameManager.Instance.PlayerObject.transform.position);
        }

        // Trapdoors are always physically walkable (see SeanceDungeonGrid's own header comment), so
        // unlike Teleporter/Door obstacles they're never discovered via a failed search - they're
        // discovered by simply noticing the ghost is standing at one while following its ALREADY
        // valid route. Checked only during ordinary path-following (not lever legs - a lever leg
        // crossing a SEPARATE gated trapdoor is a rare-enough combination to leave unhandled for now).
        // True only if the remaining route actually goes DOWN through this specific hatch: some upcoming
        // waypoint sits meaningfully below it AND within its own footprint. Both halves matter - the
        // height test alone would fire for a route that descends somewhere else entirely later on, and
        // the footprint test alone would fire for a route merely crossing the hatch on the level.
        const float trapdoorDescentMargin = 1.5f;
        const float trapdoorFootprintRadius = 2.5f;
        const int trapdoorLookaheadWaypoints = 8;

        bool RouteDescendsThroughTrapdoor(SeanceGatedObstacle trapdoor)
        {
            if (path == null)
                return false;

            float hatchY = trapdoor.ObstaclePosition.y;
            int lookaheadEnd = Mathf.Min(path.Count, waypointIndex + trapdoorLookaheadWaypoints);

            for (int i = waypointIndex; i < lookaheadEnd; i++)
            {
                Vector3 waypoint = path[i].Position;
                if (waypoint.y > hatchY - trapdoorDescentMargin)
                    continue;

                float dx = waypoint.x - trapdoor.ObstaclePosition.x;
                float dz = waypoint.z - trapdoor.ObstaclePosition.z;
                if (dx * dx + dz * dz <= trapdoorFootprintRadius * trapdoorFootprintRadius)
                    return true;
            }

            return false;
        }

        bool CheckForTrapdoorObstacle()
        {
            SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
            if (grid == null)
                return false;

            List<SeanceGatedObstacle> obstacles = grid.GatedObstacles;
            for (int i = 0; i < obstacles.Count; i++)
            {
                SeanceGatedObstacle candidate = obstacles[i];
                if (candidate.Kind != SeanceGatedObstacle.KindTrapdoor || encounteredTrapdoors.Contains(candidate))
                    continue;

                float distanceSq = (candidate.ObstaclePosition - transform.position).sqrMagnitude;
                if (distanceSq > trapdoorProximityRadius * trapdoorProximityRadius)
                    continue;

                // Proximity alone is NOT enough. A trapdoor is a floor hatch - it can only obstruct a
                // route that actually descends through it, and trapdoors are already treated as ordinary
                // walkable floor (see SeanceDungeonGrid.BuildExclusionSets), so walking past or over one
                // is never blocked. Confirmed live: a lever-gated trapdoor whose lever opens something
                // completely unrelated stopped the ghost on every single run, purely because the route
                // happened to pass within 2m of it.
                if (!RouteDescendsThroughTrapdoor(candidate))
                    continue;

                encounteredTrapdoors.Add(candidate);
                currentObstacle = candidate;
                remainingRoots = new List<DaggerfallAction>(candidate.Roots);
                visitedRoots = new HashSet<DaggerfallAction>();
                obstacleEscortIsSearchDriven = false;

                Debug.LogFormat("Astrid's Guide to Necormancy: Seance ghost encountered gated trapdoor '{0}' along its route.",
                    candidate.ObstacleAction.name);

                TryLeadToNextRoot();
                return true;
            }

            return false;
        }

        // Set once per cast so the closest-approach lead is only ever attempted once - reset in
        // BeginSearch. Without it, a failed lead would re-enter this same fallback forever.
        bool closestApproachLeadAttempted;

        // Below this the ghost is already effectively at the closest reachable point, so leading there
        // would be a no-op - just dissipate where it stands.
        const float closestApproachLeadMinDistance = 1.5f;

        // Sanity bound: ClosestApproachPosition is only meaningful if a search actually ran and produced
        // one. Guards against leading the ghost to a stale or default (0,0,0) position.
        const float closestApproachLeadMaxDistance = 200f;

        void BeginNoPathFallback()
        {
            // The exact target being unreachable is usually BY DESIGN rather than a pathfinding failure: a
            // dungeon exit's StartMarker is embedded in the exit door's own geometry, and a caged quest
            // target is sealed in deliberately. In both cases the useful thing is to lead the player to the
            // nearest point that IS reachable - in front of the door, or just outside the cage - instead of
            // drifting through a wall toward a spot they can never stand on. Tried only after the
            // lever-escort cascade has been exhausted, so a gated barrier still gets its lever first.
            if (!closestApproachLeadAttempted && TryLeadToClosestApproach())
                return;

            BeginNoPathDrift();
        }

        bool TryLeadToClosestApproach()
        {
            closestApproachLeadAttempted = true;

            SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
            if (grid == null || pathfinder == null)
                return false;

            Vector3 target = lastMainSearchClosestApproach;
            float distance = Vector3.Distance(target, transform.position);
            if (distance < closestApproachLeadMinDistance || distance > closestApproachLeadMaxDistance)
                return false;

            Debug.LogFormat(
                "Astrid's Guide to Necormancy: Seance target unreachable - leading player to the closest reachable point {0} " +
                "({1:F1}m from the ghost) instead of the target itself.", target, distance);

            pathfinder = new SeanceVoxelPathfinder(grid, transform.position, target);
            searchStartRealtime = Time.realtimeSinceStartup;
            state = StateSearching;
            StartCoroutine(RunClosestApproachSearch());
            return true;
        }

        IEnumerator RunClosestApproachSearch()
        {
            yield return pathfinder.Search();

            Debug.LogFormat("Astrid's Guide to Necormancy: Seance closest-approach lead search took {0:F2}s real time.\n{1}",
                Time.realtimeSinceStartup - searchStartRealtime, pathfinder.DebugSummary());

            if (pathfinder.Status == SeanceVoxelPathfinder.StatusSuccess)
            {
                path = pathfinder.Result;
                waypointIndex = 0;
                ResetPathFollowing();
                lastProgressDistance = float.MaxValue;
                lastProgressTime = Time.time;
                pathFollowReturnState = StateFollowingPath;
                state = StateFollowingPath;
            }
            else
            {
                BeginNoPathDrift();
            }
        }

        void BeginNoPathDrift()
        {
            state = StateNoPathFallback;
            noPathFallbackStartTime = Time.time;

            DaggerfallDungeon dungeon = GameManager.Instance.PlayerEnterExit != null
                ? GameManager.Instance.PlayerEnterExit.Dungeon
                : null;
            Debug.LogFormat(
                "Astrid's Guide to Necormancy: Seance voxel pathfinder found no traversable route in dungeon '{0}' - " +
                "falling back to noclip drift.", dungeon != null ? dungeon.name : "(unknown)");
        }

        void Update()
        {
            CheckEmbeddedFailsafe();

            if (enemySenses != null)
            {
                enemySenses.Target = null;
                enemySenses.SecondaryTarget = null;
            }

            if (!GameManager.Instance.IsPlayerInsideDungeon)
            {
                GameObject.Destroy(gameObject);
                return;
            }

            switch (state)
            {
                case StateFollowingPath:
                    UpdateFollowingPath();
                    break;
                case StateWaitingForPlayer:
                    UpdateWaitingForPlayer();
                    break;
                case StateStuckRecovery:
                    UpdateStuckRecovery();
                    break;
                case StateNoPathFallback:
                    UpdateNoPathFallback();
                    break;
                case StateArriving:
                    UpdateArriving();
                    break;
                case StateWaitingAtTeleporter:
                    UpdateWaitingAtTeleporter();
                    break;
                case StateTeleporting:
                    UpdateTeleporting();
                    break;
                case StateWaitingAfterTeleporter:
                    UpdateWaitingAfterTeleporter();
                    break;
                case StateLeadToLever:
                    UpdateLeadToLever();
                    break;
                case StateWaitForActivation:
                    UpdateWaitForActivation();
                    break;
                case StateHoveringAtObstacle:
                    UpdateHoveringAtObstacle();
                    break;
            }

            ForceOpenNearbyDoor();
            ClearHostileTargetingOfSelf();
        }

        void UpdateFollowingPath()
        {
            if (CheckForTrapdoorObstacle())
                return;

            if (path == null || waypointIndex >= path.Count)
            {
                BeginArrival();
                return;
            }

            Transform playerTransform = GameManager.Instance.PlayerObject.transform;
            float playerDistance = Vector3.Distance(transform.position, playerTransform.position);
            bool hasLineOfSight = HasLineOfSightToPlayer(playerTransform);

            if (!hasLineOfSight)
                loseSightTimer += Time.deltaTime;
            else
                loseSightTimer = 0f;

            if (playerDistance > waitDistance || loseSightTimer > loseSightGiveUpTime)
            {
                pathFollowReturnState = StateFollowingPath;
                state = StateWaitingForPlayer;
                return;
            }

            float threshold = waypointIndex == path.Count - 1 ? finalArrivalThreshold : waypointArrivalThreshold;
            Vector3 waypoint = GetWaypointTarget(waypointIndex);
            float distanceToWaypoint = Vector3.Distance(transform.position, waypoint);

            if (distanceToWaypoint < threshold)
            {
                waypointIndex++;
                lastProgressDistance = float.MaxValue;
                lastProgressTime = Time.time;

                // The ghost has just arrived at the teleporter's TRIGGER cell (the waypoint it was
                // walking toward) - path[waypointIndex] (the new, incremented index) is the far side of
                // a teleport edge, which can be arbitrarily distant across the dungeon and must never be
                // walked toward directly. Wait for the player here instead of continuing normal movement.
                if (waypointIndex < path.Count && path[waypointIndex].MoveType == SeanceMoveType.Teleport)
                {
                    pathFollowReturnState = StateFollowingPath;
                    state = StateWaitingAtTeleporter;
                }

                return;
            }

            if (distanceToWaypoint < lastProgressDistance - 0.1f)
            {
                lastProgressDistance = distanceToWaypoint;
                lastProgressTime = Time.time;
            }
            else if (Time.time - lastProgressTime > stuckThreshold)
            {
                pathFollowReturnState = StateFollowingPath;
                BeginStuckRecovery();
                return;
            }

            AdvanceAlongSegment(waypoint, moveSpeed);
        }

        void UpdateWaitingForPlayer()
        {
            Transform playerTransform = GameManager.Instance.PlayerObject.transform;
            FaceTowards(playerTransform.position);

            float playerDistance = Vector3.Distance(transform.position, playerTransform.position);
            if (playerDistance <= resumeDistance)
            {
                loseSightTimer = 0f;
                lastProgressDistance = float.MaxValue;
                lastProgressTime = Time.time;
                state = pathFollowReturnState;
            }
        }

        // Ghost hovers at the teleporter trigger, facing the player, until they're close enough to
        // follow through - no timeout, no re-pathing: per design, the player may be fighting or
        // exploring, and the ghost simply keeps waiting no matter how long that takes.
        void UpdateWaitingAtTeleporter()
        {
            Transform playerTransform = GameManager.Instance.PlayerObject.transform;
            FaceTowards(playerTransform.position);

            float playerDistance = Vector3.Distance(transform.position, playerTransform.position);
            if (playerDistance <= resumeDistance)
                BeginTeleportCrossing();
        }

        void BeginTeleportCrossing()
        {
            state = StateTeleporting;
            teleportCrossingStartTime = Time.time;

            if (ghostRenderer != null)
                ghostRenderer.enabled = false;
            VoxsNecromancy.PlaySeanceTeleportSound();

            Debug.LogFormat("Astrid's Guide to Necormancy: Seance ghost crossing teleporter to {0}.",
                path[waypointIndex].Position);
        }

        // Brief vanish, then reposition directly to the destination-side waypoint - a real transform
        // jump rather than a glide, since the two sides of a teleporter can be arbitrarily far apart in
        // the same dungeon and gliding that distance would look identical to (and be indistinguishable
        // from) the ghost simply being lost.
        void UpdateTeleporting()
        {
            if (Time.time - teleportCrossingStartTime < teleportVanishDuration)
                return;

            transform.position = GetWaypointTarget(waypointIndex);

            if (ghostRenderer != null)
                ghostRenderer.enabled = true;

            state = StateWaitingAfterTeleporter;
        }

        // Mirrors UpdateWaitingForPlayer - waits for the player to come through the REAL teleporter
        // (an ordinary DFU action object the player triggers themselves; the ghost's own crossing is
        // purely cosmetic/logical) before resuming the route from the destination side.
        void UpdateWaitingAfterTeleporter()
        {
            Transform playerTransform = GameManager.Instance.PlayerObject.transform;
            FaceTowards(playerTransform.position);

            float playerDistance = Vector3.Distance(transform.position, playerTransform.position);
            if (playerDistance <= resumeDistance)
            {
                lastProgressDistance = float.MaxValue;
                lastProgressTime = Time.time;
                // Resume whichever leg was actually in progress (the main route, or a lever leg -
                // hardcoding StateFollowingPath here was a real bug found in testing: a teleport
                // crossing encountered while leading to a lever would silently drop the escort and
                // jump back to the main route instead of continuing toward the lever).
                state = pathFollowReturnState;
            }
        }

        // Should be unreachable now that movement is pure transform interpolation (distance to the
        // waypoint strictly decreases every frame), but kept as a cheap defensive failsafe in case a
        // future change reintroduces anything that can block progress.
        void BeginStuckRecovery()
        {
            state = StateStuckRecovery;
            stuckRecoveryStartTime = Time.time;
        }

        void UpdateStuckRecovery()
        {
            if (path == null || waypointIndex >= path.Count)
            {
                if (pathFollowReturnState == StateLeadToLever)
                    BeginWaitForActivation();
                else
                    BeginArrival();
                return;
            }

            // Routed through the same slide-and-clamp movement as everything else. This kept its 2x speed and
            // its purpose, but it used to write transform.position directly with no clearance test at all -
            // a hole in the "never through walls" guarantee, and a plausible source of both the observed wall
            // transits and the ghost appearing to jump a long distance, since it ran unchecked at double
            // speed. It can still noclip nothing; it simply slides along geometry now instead of through it.
            // Keeps its 2x speed and its purpose, but can no longer pass through a wall: thin geometry is
            // clipped cosmetically, thick geometry refuses the move and the existing timeout carries it on.
            Vector3 waypoint = GetWaypointTarget(waypointIndex);
            TryMoveUnvalidated(
                Vector3.MoveTowards(transform.position, waypoint, moveSpeed * 2f * Time.deltaTime), waypoint);

            bool reachedWaypoint = Vector3.Distance(transform.position, waypoint) < waypointArrivalThreshold;
            bool timedOut = Time.time - stuckRecoveryStartTime > stuckRecoveryDuration;

            if (reachedWaypoint || timedOut)
            {
                waypointIndex++;
                lastProgressDistance = float.MaxValue;
                lastProgressTime = Time.time;
                state = pathFollowReturnState;
            }
        }

        void UpdateNoPathFallback()
        {
            Vector3 target = destinationTransform != null ? destinationTransform.position : rawTargetPosition;
            Vector3 offset = target - transform.position;
            float distance = offset.magnitude;

            if (distance > 0.05f)
            {
                // Also routed through slide-and-clamp. This used to drift straight through geometry toward
                // the target, which broke the invariant outright. It now stops at the first wall and
                // dissipates there - a ghost fading against a wall in the target's direction still reads
                // correctly, and no longer sails through it.
                // Stops at the first thick blockage and dissipates there rather than sailing through it - a
                // ghost fading against a wall in the target's direction still reads correctly.
                Vector3 direction = offset / distance;
                if (!TryMoveUnvalidated(transform.position + direction * moveSpeed * 1.5f * Time.deltaTime, target))
                {
                    DaggerfallUI.AddHUDText("The spirit wails - it cannot find a path a mortal may walk.");
                    GameObject.Destroy(gameObject);
                    return;
                }
            }

            if (Time.time - noPathFallbackStartTime > noPathFallbackDuration)
            {
                DaggerfallUI.AddHUDText("The spirit wails - it cannot find a path a mortal may walk.");
                GameObject.Destroy(gameObject);
            }
        }

        void BeginArrival()
        {
            state = StateArriving;
            arrivalStartTime = Time.time;
        }

        void UpdateArriving()
        {
            Vector3 center = destinationTransform != null ? destinationTransform.position : rawTargetPosition;
            float angle = (Time.time - arrivalStartTime) * arrivalCircleSpeed;
            Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * arrivalCircleRadius;
            transform.position = center + Vector3.up * hoverHeight + offset;
            FaceTowards(center + offset * 2f);

            if (Time.time - arrivalStartTime > arrivalCircleDuration)
            {
                VoxsNecromancy.PlaySeanceDissipateSound();
                GameObject.Destroy(gameObject);
            }
        }

        // The motion leash. A hard structural invariant that the ghost never ends a frame inside geometry -
        // what a CharacterController would give us, except we VETO motion rather than resolving collisions,
        // so it reintroduces none of the stutter that got the ghost's collider removed. On a correct path it
        // never fires at all.
        //
        // Applies only to ordinary path following (UpdateFollowingPath / UpdateLeadToLever, the two callers
        // of MoveTowards). Stuck recovery and the no-path drift move the transform directly and are
        // deliberately allowed to noclip - they are the escape hatches, not the failure.

        // BODY radius - the ghost's own half-width, measured from the prefab at spawn, NOT the search or
        // clearance radius. The leash exists to guarantee the ghost is never INSIDE geometry, which is a
        // property of its body.
        //
        // Phase 1 used the search radius here and it was wrong: cell probing only guarantees clearance at
        // cell CENTRES, while the ghost flies between smoothed, hover-adjusted positions. At 0.45 that
        // reduced to "veto whenever within 0.45m of any wall" - always true in a corridor - so the ghost
        // vetoed a zero-length move, re-pathed, and dissipated after hitting the veto cap.
        // Measuring this from the prefab was tried and does not work. The ghost's CharacterController.radius
        // is 0.40 - DFU's enemy collision capsule, sized for gameplay - which is WIDER than the search radius
        // itself, so leashing against it vetoes ordinary movement exactly as the search radius did. The
        // renderer bounds are worse still (~0.5 for a billboard sprite).
        //
        // For a ghost whose collision was deliberately removed, "inside geometry" is a visual judgement, not
        // a physical one: the sprite may overlap a wall slightly without looking wrong, and the leash only
        // needs to stop it sailing bodily through walls. So this is an explicit small constant, and the one
        // number to tune if the ghost either clips visibly (raise it) or gets vetoed on legitimate movement
        // (lower it).
        const float bodyRadius = 0.15f;

        // ARC-LENGTH FOLLOWING (Phase 3).
        //
        // The old model was "steer toward the current waypoint, switch when close enough". That cuts every
        // corner: between two waypoints the ghost heads straight at the NEXT one, so its line bulges off the
        // validated polyline and clips the inside of each turn - which is what pinned it against corners and
        // burned through the leash's veto budget until it dissipated.
        //
        // Instead the ghost advances a scalar distance along the CURRENT segment and derives its position by
        // interpolating that segment. It is therefore mathematically on the validated polyline every frame,
        // and corner cutting is impossible by construction rather than absorbed by tolerance. Facing is
        // unconstrained - rotating the sprite does not move the body.
        Vector3 segmentStart;
        float segmentTravelled;
        int segmentIndex = -1;

        void ResetPathFollowing()
        {
            segmentIndex = -1;
            segmentTravelled = 0f;
            BuildFlightPositions();
        }

        // Returns true when the ghost reached the end of the current segment this frame (caller then handles
        // waypoint arrival: teleporter hand-off, index advance, arrival).
        bool AdvanceAlongSegment(Vector3 target, float speed)
        {
            // Anchor on the VALIDATED POLYLINE, not on wherever the ghost happens to be.
            //
            // This previously used transform.position so a re-path or teleport arrival would ease in rather
            // than snap - but that traded away the whole guarantee. BuildFlightPositions verifies segments
            // (flightPositions[i-1] -> flightPositions[i]); anchoring anywhere else means the line actually
            // flown is one nothing ever checked. Measured: a run with "0 segments still blocked" and "0 thin
            // passes" still put the ghost inside walls three times, because it was not flying the verified
            // line. A single visible snap onto the path is the cheaper price.
            //
            // The first segment has no preceding waypoint, so it necessarily starts from the ghost's real
            // position; BuildFlightPositions validates that approach hop separately.
            if (segmentIndex != waypointIndex)
            {
                segmentIndex = waypointIndex;
                segmentStart = (waypointIndex > 0 && waypointIndex - 1 < flightPositions.Count)
                    ? flightPositions[waypointIndex - 1]
                    : transform.position;
                segmentTravelled = 0f;
            }

            float segmentLength = Vector3.Distance(segmentStart, target);
            if (segmentLength < 0.0001f)
                return true;

            segmentTravelled += speed * Time.deltaTime;
            if (segmentTravelled >= segmentLength)
            {
                MoveToValidated(target, target);
                return true;
            }

            MoveToValidated(Vector3.Lerp(segmentStart, target, segmentTravelled / segmentLength), target);
            return false;
        }

        void MoveToValidated(Vector3 destination, Vector3 faceTarget)
        {
            MoveTowardsInternal(destination, faceTarget);
        }

        void MoveTowards(Vector3 target, float speed)
        {
            MoveTowardsInternal(Vector3.MoveTowards(transform.position, target, speed * Time.deltaTime), target);
        }

        // Moves to an ABSOLUTE destination under the leash. Arc-length following computes exactly where the
        // ghost should be this frame, so it needs a positional move rather than a steer-toward.
        // Movement is now pure polyline following with NO runtime collision logic at all.
        //
        // Every per-frame correction previously tried here (clamp, collide-and-slide, depenetration nudge) was
        // structurally incompatible with arc-length following: AdvanceAlongSegment derives the ghost's
        // position each frame from the polyline alone, ignoring where the ghost currently is, so any sideways
        // displacement was simply discarded on the next frame. Slide, snap back, slide, snap back - net
        // progress zero. The corrections were removed rather than the arc-length following, which works.
        //
        // Wall crossing is instead made impossible at BUILD time: BuildFlightPositions resolves every thick
        // blockage before the ghost moves a single frame (see thinPassThreshold). What remains here is
        // deliberately trivial.
        void MoveTowardsInternal(Vector3 desired, Vector3 target)
        {
            transform.position = desired;
            FaceTowards(target);
        }

        // Used by the two paths that move OUTSIDE the validated polyline - stuck recovery and the no-path
        // drift. They have no build-time resolution to rely on, so they apply the same thin/thick rule per
        // move: thin geometry is passed through cosmetically, thick geometry stops them. Returns false when
        // the move was refused, so the no-path drift can dissipate where it stands.
        bool TryMoveUnvalidated(Vector3 desired, Vector3 target)
        {
            SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
            if (grid == null)
            {
                transform.position = desired;
                FaceTowards(target);
                return true;
            }

            Collider blocker;
            bool blockedByActionObject;
            if (!grid.IsSegmentClear(transform.position, desired, bodyRadius, true, out blocker, out blockedByActionObject))
            {
                float thickness = grid.MeasureBlockageThickness(transform.position, desired, bodyRadius);
                if (thickness >= thinPassThreshold)
                    return false;
            }

            transform.position = desired;
            FaceTowards(target);
            return true;
        }



        // Where the ghost should actually fly for a given waypoint: cell centers can sit anywhere in
        // the 1m cell (and the path may weave between the two open cell-layers above a floor), so a
        // fixed offset above the RAW waypoint rode the ghost 2.5-3m high - into (or visually through)
        // the solid wall above every doorway. Ground-relative instead: find the real floor under the
        // waypoint and hover a fixed height above THAT, dipping lower through door passages so the
        // sprite threads the opening rather than the lintel. Waypoints with no floor within reach
        // (mid-shaft rides, drop columns) are used as-is.
        // The positions the ghost ACTUALLY flies: each waypoint lifted to hover height, then repaired so the
        // resulting polyline is itself clear.
        //
        // This matters because hover adjustment moves a waypoint roughly a metre vertically - the ghost
        // therefore travels through a completely different set of cells than the ones A* validated, and
        // nothing had ever checked the line it really flies. That is why it drove into wall corners the grid
        // considered fine: the geometry a metre above the validated cell is simply different.
        List<Vector3> flightPositions = new List<Vector3>();

        void BuildFlightPositions()
        {
            flightPositions.Clear();
            if (path == null)
                return;

            for (int i = 0; i < path.Count; i++)
                flightPositions.Add(ComputeHoverPosition(i));

            SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
            if (grid == null)
                return;

            // A blocked hovered segment is resolved by THICKNESS, once, here - which is what lets the runtime
            // fly this polyline with no collision logic whatsoever.
            //
            // Thin blockage (a corner, a doorframe, a bit of moulding) is accepted as-is: the ghost clips it
            // cosmetically and keeps moving smoothly, which is the explicit trade being made. Thick blockage
            // is a wall or floor and falls back to the waypoint's own validated position - those came from
            // cells whose connecting edge A* already checked at the probe radius, so genuinely thin walls
            // BETWEEN cells were already caught during the search. Only hover-adjusted segments and smoothed
            // shortcuts reach this code, which bounds the risk.
            // Repeated until nothing changes, because reverting waypoint [i-1] retroactively alters segment
            // (i-2 -> i-1), which this loop has already passed. A single sweep therefore cannot hold the
            // invariant it claims, and the earlier single-sweep version demonstrably shipped blocked segments
            // into the polyline - the runtime then flew them faithfully, which is what put the ghost inside
            // walls with zero thin-passes recorded.
            bool changed = true;
            for (int pass = 0; pass < maxFlightRepairPasses && changed; pass++)
            {
                changed = false;

                for (int i = 1; i < flightPositions.Count; i++)
                {
                    Collider blocker;
                    bool blockedByActionObject;
                    if (grid.IsSegmentClear(flightPositions[i - 1], flightPositions[i], bodyRadius, true,
                            out blocker, out blockedByActionObject))
                        continue;

                    float thickness = grid.MeasureBlockageThickness(flightPositions[i - 1], flightPositions[i], bodyRadius);
                    if (thickness < thinPassThreshold)
                    {
                        thinPassCount++;
                        Debug.LogFormat(
                            "Astrid's Guide to Necormancy: Seance accepting thin pass-through #{3} at {0} - measured {1:F3}m " +
                            "thick (threshold {2:F2}m). Ghost will clip this cosmetically.",
                            flightPositions[i], thickness, thinPassThreshold, thinPassCount);
                        continue;
                    }

                    if (flightPositions[i] != path[i].Position)
                    {
                        flightPositions[i] = path[i].Position;
                        changed = true;
                    }

                    if (grid.IsSegmentClear(flightPositions[i - 1], flightPositions[i], bodyRadius, true,
                            out blocker, out blockedByActionObject))
                        continue;

                    if (flightPositions[i - 1] != path[i - 1].Position)
                    {
                        flightPositions[i - 1] = path[i - 1].Position;
                        changed = true;
                    }
                }
            }

            // The APPROACH HOP: the ghost's real position to the first waypoint. This is the one segment with
            // no preceding waypoint to anchor on or revert to, so it cannot be repaired - only reported.
            if (flightPositions.Count > 0)
            {
                Collider approachBlocker;
                bool approachBlockedByAction;
                if (!grid.IsSegmentClear(transform.position, flightPositions[0], bodyRadius, true,
                        out approachBlocker, out approachBlockedByAction))
                {
                    float approachThickness = grid.MeasureBlockageThickness(
                        transform.position, flightPositions[0], bodyRadius);

                    if (approachThickness >= thinPassThreshold)
                    {
                        Debug.LogWarningFormat(
                            "Astrid's Guide to Necormancy: Seance approach hop {0} -> {1} crosses {2:F3}m of geometry " +
                            "(blocker '{3}'). No earlier waypoint exists to revert to, so the ghost will pass through it.",
                            transform.position, flightPositions[0], approachThickness,
                            approachBlocker != null ? approachBlocker.name : "(unknown)");
                    }
                }
            }

            // Anything still blocked with BOTH endpoints back at their A*-validated positions is a different
            // bug - the search itself approved a segment that is not clear - and must not fly silently.
            for (int i = 1; i < flightPositions.Count; i++)
            {
                Collider blocker;
                bool blockedByActionObject;
                if (grid.IsSegmentClear(flightPositions[i - 1], flightPositions[i], bodyRadius, true,
                        out blocker, out blockedByActionObject))
                    continue;

                Debug.LogWarningFormat(
                    "Astrid's Guide to Necormancy: Seance flight segment {0} -> {1} is STILL blocked after full " +
                    "reversion to A*-validated waypoints (blocker '{2}', thickness {3:F3}m). The search approved a " +
                    "segment that is not clear - the ghost will pass through this.",
                    flightPositions[i - 1], flightPositions[i],
                    blocker != null ? blocker.name : "(unknown)",
                    grid.MeasureBlockageThickness(flightPositions[i - 1], flightPositions[i], bodyRadius));
            }
        }

        // Two would suffice for a single retroactive break; four leaves headroom for a short cascade without
        // risking a long stall on a pathological path.
        const int maxFlightRepairPasses = 4;

        // A corner graze or doorframe is a few centimetres of geometry along the direction of travel; a wall
        // or floor is a third of a metre or more. Not to be tuned until the thin-pass log gives a reason.
        const float thinPassThreshold = 0.20f;
        int thinPassCount;

        // DIAGNOSTIC ONLY. Reports that something upstream built a bad polyline; it deliberately does NOT
        // move the ghost, re-path, or dissipate - correcting here is exactly what proved incompatible with
        // arc-length following. Silence is the expected result.
        const int embeddedCheckFrameInterval = 10;
        int embeddedCheckCounter;
        Vector3 lastFailsafePosition;
        float lastEmbeddedLogTime = -999f;
        const float embeddedLogInterval = 5f;

        void CheckEmbeddedFailsafe()
        {
            if (++embeddedCheckCounter < embeddedCheckFrameInterval)
                return;

            embeddedCheckCounter = 0;

            SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
            Vector3 position = transform.position;
            Vector3 travelled = position - lastFailsafePosition;
            lastFailsafePosition = position;

            if (grid == null || Time.time - lastEmbeddedLogTime < embeddedLogInterval)
                return;

            // Uses the SAME criterion BuildFlightPositions validates with (passage exemption ON). The raw
            // Physics.CheckSphere previously used here counts a doorway's own frame as geometry, while the
            // polyline check exempts it - so a ghost passing legitimately through a door tripped this warning.
            // One of the three reported positions sat 1.3m from a known door passage.
            Collider embeddedBlocker;
            bool embeddedByActionObject;
            if (grid.IsSegmentClear(position, position, bodyRadius, true, out embeddedBlocker, out embeddedByActionObject))
                return;

            // Confirm it is real depth along the direction of travel rather than an incidental graze.
            if (travelled.sqrMagnitude < 0.0001f)
                return;

            Vector3 ahead = position + travelled.normalized * (thinPassThreshold * 2f);
            if (grid.MeasureBlockageThickness(position, ahead, bodyRadius) < thinPassThreshold)
                return;

            lastEmbeddedLogTime = Time.time;

            Collider[] hits = Physics.OverlapSphere(position, bodyRadius, grid.BlockingMask, QueryTriggerInteraction.Ignore);
            DaggerfallDungeon dungeon = GameManager.Instance.PlayerEnterExit != null
                ? GameManager.Instance.PlayerEnterExit.Dungeon
                : null;

            Debug.LogWarningFormat(
                "Astrid's Guide to Necormancy: Seance ghost is embedded in thick geometry at {0} in '{1}' (collider '{2}') - " +
                "a bad flight polyline was built upstream. Not correcting; this is a diagnostic only.",
                position, dungeon != null ? dungeon.name : "(unknown)",
                hits.Length > 0 && hits[0] != null ? hits[0].name : "(unknown)");
        }

        Vector3 GetWaypointTarget(int index)
        {
            if (index >= 0 && index < flightPositions.Count)
                return flightPositions[index];

            return ComputeHoverPosition(index);
        }

        Vector3 ComputeHoverPosition(int index)
        {
            Vector3 waypoint = path[index].Position;

            // Swim waypoints normally keep their route depth as-is - ground-relative hover over water
            // snaps to the LAKE BED, sending the ghost cruising along the bottom while the player
            // swims at the surface (observed in a real flooded dungeon: read as "the ghost dove away
            // into the deep" when it was actually mid-route toward the exit). But a cell's own center
            // can sit AT OR BELOW the real floor in shallow water (an ankle-deep puddle still gets
            // classified as swimmable - see SeanceDungeonGrid's own water-depth comments - yet its
            // cell center is nowhere near as far below the surface as a real lake's mid-water point
            // would be), which visually reads as the ghost sinking into/through the floor (confirmed
            // in real testing: happened specifically in "watery floor, not deep enough to swim" rooms,
            // never in a genuine lake). A short downward check catches this: if real floor is close
            // by, hover a small amount above it - still low enough to read as wading/swimming, not the
            // full walking height - rather than trusting the raw cell center. Genuine deep water has no
            // floor within this short range, so it falls through unchanged to the raw waypoint.
            if (path[index].MoveType == SeanceMoveType.Swim)
            {
                RaycastHit swimFloorHit;
                int swimMask = 1 << LayerMask.NameToLayer("Default");
                if (Physics.Raycast(waypoint + Vector3.up * 0.5f, Vector3.down, out swimFloorHit, shallowWaterCheckDepth,
                    swimMask, QueryTriggerInteraction.Ignore))
                {
                    float minSwimHeight = swimFloorHit.point.y + shallowWaterHoverHeight;
                    if (waypoint.y < minSwimHeight)
                        return new Vector3(waypoint.x, minSwimHeight, waypoint.z);
                }

                return waypoint;
            }

            RaycastHit hit;
            int mask = 1 << LayerMask.NameToLayer("Default");
            if (!Physics.Raycast(waypoint + Vector3.up * 0.5f, Vector3.down, out hit, 4.5f, mask, QueryTriggerInteraction.Ignore))
                return waypoint;

            float height = hoverHeight;
            SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
            if (grid != null && grid.IsInDoorPassage(waypoint))
                height = doorHoverHeight;

            return new Vector3(waypoint.x, hit.point.y + height, waypoint.z);
        }

        void FaceTowards(Vector3 target)
        {
            Vector3 flatDirection = target - transform.position;
            flatDirection.y = 0f;
            if (flatDirection.sqrMagnitude < 0.0001f)
                return;

            transform.rotation = Quaternion.RotateTowards(
                transform.rotation, Quaternion.LookRotation(flatDirection), turnSpeed * Time.deltaTime);
        }

        bool HasLineOfSightToPlayer(Transform playerTransform)
        {
            Vector3 origin = transform.position;
            Vector3 target = playerTransform.position + Vector3.up;
            float distance = Vector3.Distance(origin, target);
            if (distance < 0.01f)
                return true;

            int mask = 1 << LayerMask.NameToLayer("Default");
            return !Physics.Linecast(origin, target, mask, QueryTriggerInteraction.Ignore);
        }

        // No Physics.IgnoreCollision bookkeeping needed anymore - the ghost's own collider is disabled
        // for its whole lifetime (see Awake), so there is nothing to ignore, and Unity errors outright
        // when IgnoreCollision is called with a disabled collider.
        void OnEnable()
        {
            activeInstance = this;
        }

        void OnDisable()
        {
            if (activeInstance == this)
                activeInstance = null;

            // Defensive - only actually subscribed while StateWaitForActivation is waiting on a
            // gated teleporter, but -= on a delegate that was never added is a safe no-op, and this
            // guarantees a destroyed ghost never leaves a dangling reference on this static, dungeon-
            // wide event.
            DaggerfallAction.OnTeleportAction -= OnAnyTeleportFired;

            // Belt-and-braces alongside the try/finally in EvaluateGatedShortcuts. The grid is per-dungeon
            // and outlives this ghost, so a hypothetically-opened barrier left applied would make every
            // later cast route through solid geometry.
            SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
            if (grid != null)
                grid.ClearHypotheticalOpen();
        }

        // The grid treats every door as already-open space for pathing purposes (see
        // SeanceDungeonGrid's own header comment), so the REAL door object still needs to be
        // physically opened as the ghost actually reaches it.
        void ForceOpenNearbyDoor()
        {
            Collider[] hits = Physics.OverlapSphere(transform.position, doorCheckRadius);
            for (int i = 0; i < hits.Length; i++)
            {
                DaggerfallActionDoor door = hits[i].GetComponent<DaggerfallActionDoor>();
                if (door != null && !door.IsOpen)
                    door.SetOpen(true, true, true);
            }
        }

        // Belt-and-suspenders safety net for hostile targeting - EnemySenses.GetTargets()'s own default
        // team rules shouldn't select this ghost at all, but other AI-enhancing mods in this heavily
        // modded environment could plausibly add their own targeting logic outside DFU's rules. (No
        // per-enemy collision handling needed - the ghost's own collider is disabled outright.)
        void ClearHostileTargetingOfSelf()
        {
            if (Time.time < nextHostileTargetClearTime)
                return;
            nextHostileTargetClearTime = Time.time + hostileTargetClearInterval;

            foreach (DaggerfallEntityBehaviour behaviour in ActiveGameObjectDatabase.GetActiveEnemyBehaviours())
            {
                if (behaviour == null || behaviour == entityBehaviour)
                    continue;

                EnemySenses senses = behaviour.GetComponent<EnemySenses>();
                if (senses != null)
                {
                    if (senses.Target == entityBehaviour)
                        senses.Target = null;
                    if (senses.SecondaryTarget == entityBehaviour)
                        senses.SecondaryTarget = null;
                }
            }
        }
    }
}
