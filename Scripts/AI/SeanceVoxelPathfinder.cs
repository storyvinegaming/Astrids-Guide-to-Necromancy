// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Player-traversable 3D A* over a SeanceDungeonGrid. Plain C# class (not a MonoBehaviour) - one
// instance per search, driven by the ghost's own StartCoroutine calling Search(). The coroutine's own
// suspended state IS the resume state (no manual "resume from node N" bookkeeping needed) - it just
// yields whenever it's used its frame time budget and picks back up next frame exactly where it left
// off, since everything it needs (the open heap, the node list, expandedCount) lives in instance
// fields, not locals that would be lost across a yield.
//
// Two-pass search, not one search with a single "climbing is expensive" cost constant: to guarantee
// climbing never wins over any walkable alternative, that constant would have to be so large that A*
// ends up exploring almost the entire reachable dungeon before ever accepting a climb edge - turning
// "climbing is a last resort" into "every failed walkable search is enormous." Instead, pass 1 runs
// with no climb edges at all (a lower node cap, so a genuinely-climb-only case fails fast); pass 2 only
// runs if pass 1 fails, with climbing enabled at a real, finite cost. Both passes share the exact same
// SeanceDungeonGrid cache, so pass 2 costs almost nothing extra in physics queries - only the graph
// search itself re-runs.
//
// No enum anywhere in this file - see SeanceCell.cs's own header comment for why (a nested enum
// elsewhere in this mod once broke the mod loader's reflection scan outright). Move types are the
// plain byte constants in SeanceMoveType.

using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace VoxsNecromancyMod.AI
{
    public class SeanceVoxelPathfinder
    {
        public struct SeancePathPoint
        {
            public Vector3 Position;
            public byte MoveType;

            // Distance to the nearest surface at this waypoint, capped at SeanceDungeonGrid.ClearanceRadius.
            // Phase 3's organic drift clamps its amplitude to this so the ghost can float freely in a hall
            // but threads a doorway precisely.
            public float Clearance;
        }

        public const int StatusRunning = 0;
        public const int StatusSuccess = 1;
        public const int StatusFailed = 2;

        public int Status { get; private set; }
        public List<SeancePathPoint> Result { get; private set; }

        // The goal node's own accumulated G cost. Meaningful only on success. Lets two routes be compared on
        // the search's real cost model (which prices drops, jumps, elevator rides and teleports differently
        // from plain distance) rather than on raw path length, which post-smoothing would be misleading.
        public float ResultCost { get; private set; }

        // Set false to run pass 1 only (no climb edges at all). Used by the clearance ladder so every
        // clearance is tried on foot before climbing is considered anywhere.
        public bool AllowClimbFallback = true;

        // ---- Diagnostics (seance_reachreport) ----------------------------------------------------------
        //
        // Instrumented inside the REAL expansion code rather than reimplemented in the console command, so
        // the report cannot drift away from what A* actually does. Off by default and free when off.
        public const byte RejectNotOpen = 0;
        public const byte RejectNoSupport = 1;
        public const byte RejectEdgeBlocked = 2;
        public const byte RejectNoRule = 3;
        public const byte RejectVisited = 4;
        // Phase 2.6 Step (b): a climb edge whose destination was wall-adjacent but failed IsClimbableLedge -
        // i.e. wall-adjacency alone would have accepted it, and only the new ledge test caught it. Only ever
        // incremented when allowClimb is true (pass 2), same as the edge it replaces would have been.
        public const byte RejectClimbLedge = 5;
        public const int RejectBucketCount = 6;

        public bool CollectDiagnostics;

        // Diagnostic-only: never terminate on reaching the goal, so the search exhausts the entire reachable
        // region. Needed because seance_reachreport's first run settled 3 cells - the player happened to be
        // standing 1.2m from the goal, so it "succeeded" instantly and reported nothing useful.
        public bool FloodMode;
        public readonly int[] RejectCounts = new int[RejectBucketCount];
        public readonly List<Vector3>[] RejectSamples =
        {
            new List<Vector3>(), new List<Vector3>(), new List<Vector3>(), new List<Vector3>(),
            new List<Vector3>(), new List<Vector3>()
        };

        const int MaxRejectSamples = 5;

        // A rejection is identified by the DIRECTED PAIR it rejected, so the same edge failing repeatedly
        // counts once. Without this the histogram measures how often a region was re-expanded rather than
        // how many distinct edges were refused - and EDGE_BLOCKED inflates itself, because a failed lazy
        // validation triggers RepairAfterInvalidEdge (reopening the node) and is then recounted on every
        // cached-edge lookup. Measured: 3.5M rejections + 3.9M visited against ~625k possible pairs, i.e.
        // ~12x more events than pairs exist.
        struct RejectedPair : System.IEquatable<RejectedPair>
        {
            public SeanceCell From;
            public SeanceCell To;

            public RejectedPair(SeanceCell from, SeanceCell to)
            {
                From = from;
                To = to;
            }

            public bool Equals(RejectedPair other)
            {
                return From.Equals(other.From) && To.Equals(other.To);
            }

            public override bool Equals(object obj)
            {
                return obj is RejectedPair && Equals((RejectedPair)obj);
            }

            public override int GetHashCode()
            {
                unchecked { return (From.GetHashCode() * 397) ^ To.GetHashCode(); }
            }
        }

        // Raw event counts, kept alongside the deduped ones - the ratio between them is itself the measure
        // of how much re-expansion the search is doing.
        public readonly int[] RejectRawCounts = new int[RejectBucketCount];
        readonly HashSet<RejectedPair>[] rejectedPairs =
        {
            new HashSet<RejectedPair>(), new HashSet<RejectedPair>(), new HashSet<RejectedPair>(),
            new HashSet<RejectedPair>(), new HashSet<RejectedPair>(), new HashSet<RejectedPair>()
        };

        void Reject(byte bucket, SeanceCell from, SeanceCell to)
        {
            if (!CollectDiagnostics)
                return;

            RejectRawCounts[bucket]++;

            if (!rejectedPairs[bucket].Add(new RejectedPair(from, to)))
                return;

            RejectCounts[bucket]++;
            if (RejectSamples[bucket].Count < MaxRejectSamples)
                RejectSamples[bucket].Add(to.ToWorldCenter(SeanceDungeonGrid.CellSize));
        }

        public static string RejectBucketName(int bucket)
        {
            switch (bucket)
            {
                case RejectNotOpen: return "NOT_OPEN";
                case RejectNoSupport: return "NO_SUPPORT";
                case RejectEdgeBlocked: return "EDGE_BLOCKED";
                case RejectNoRule: return "NO_RULE";
                case RejectVisited: return "VISITED";
                case RejectClimbLedge: return "CLIMB_LEDGE";
                default: return "(unknown)";
            }
        }

        // Best position reached toward the goal even on failure (whichever pass actually ran last) -
        // used by SeanceGhostGuide to pick the gated obstacle nearest to where a failed search actually
        // got stuck, rather than nearest to the ghost's own start position.
        public Vector3 ClosestApproachPosition
        {
            get { return pass2Ran ? pass2ClosestPosition : pass1ClosestPosition; }
        }

        // How close to the target the search actually got before exhausting. This distinguishes "the route
        // is blocked, tens of metres short" from "the target itself is sealed in and we are standing right
        // outside it" - which need completely different responses.
        public float ClosestApproachDistance
        {
            get { return pass2Ran ? pass2ClosestDistance : pass1ClosestDistance; }
        }

        const float StartSnapRadius = 6f;
        const float GoalSnapRadius = 10f;

        // (A goal force-open volume used to be declared here. It was removed deliberately - forcing edges
        // passable around the goal tunnelled through walls near the target. A sealed-in target is now handled
        // by SeanceGhostGuide leading to the closest REACHABLE point instead; see SeanceDungeonGrid.
        // AddGoalPassage's own comment for the full reasoning.)

        // Originally 35k/60k on the estimate that 60k open cells (~a 39m solid cube) exceeds any real
        // dungeon region - which held until real WATER dungeons disproved it twice: submerged cells
        // allow free 26-way movement, so a flooded region is a 3D volume (tens of thousands of cells
        // per dungeon block) that legitimately qualifies for expansion under the search bound and
        // drains the budget before a long stair route out of the water can complete. Measured: a
        // stair route from a flooded lower level was cut off at 60k expansions while already CLIMBING
        // the stairs, 24m from the entrance. Pass 1's cap must therefore fit [whole flooded volume +
        // longest route] with headroom. Cost stays acceptable: the grid caches every physics probe
        // (shared across both passes and across casts), so extra expansions are cheap graph work, and
        // the per-frame time budget below keeps even a worst-case exhaustive search from ever
        // hitching a frame - it just takes more background frames to conclude.
        const int Pass1NodeCap = 150000;
        const int Pass2NodeCap = 300000;

        // Weighted A*: the heuristic is deliberately inflated, sacrificing path optimality (the found
        // route can cost up to this factor more than the true shortest) to slash the explored volume.
        // Plain A* must expand every node whose f-value undercuts the true route cost before it can
        // commit - and a flooded dungeon supplies a practically bottomless reserve of such nodes
        // (free 26-way movement makes water a 3D volume, not a surface), measured blowing straight
        // through a 200k node cap while still 24m short of an entrance the player had literally just
        // walked from. A guide ghost doesn't need the mathematically shortest path, it needs A
        // traversable path found reliably - weighted A* is the standard trade for exactly this.
        const float HeuristicWeight = 2.0f;

        // Raised from an overly conservative 1.5, then again from 4 - measured directly: a real
        // search needing 354,688 total expansions (a fully-exhausted 150k pass 1 - no walk-only route
        // existed - plus 204,688 more to climb out) took 13.21 REAL seconds at 4ms/frame, and the
        // yield-per-chunk scheduling overhead turned out to dominate that time even more than the
        // raw computation: at ~4ms of work between each `yield return null`, a search this size needs
        // several hundred yields, each one costing a full frame wait (~16ms+) on top of its own 4ms of
        // work - i.e. the search was mostly idle, not mostly computing. 20ms trades a briefly heavier
        // frame or two (during the ghost's stationary "gathering" pause right after casting, never
        // during normal movement) for roughly a 5x cut in yield count. Node caps are NOT the lever for
        // this - lowering them risks recreating the exact "cut off mid-climb" bug that got them raised
        // in an earlier session; this dungeon's scale genuinely needs the expansions it used.
        const double FrameBudgetMs = 20.0;

        // Was 128 when every expansion was uniformly cheap - with lazy edge validation, an individual
        // pop can now carry a real spherecast, so coarser checking risked overshooting the frame
        // budget by tens of milliseconds per chunk (a measurable FPS dip during long searches).
        const int ExpansionsPerStopwatchCheck = 16;

        // Roughly one extra metre of walking to squeeze past a wall. Big enough to reroute around a pinch
        // point when a roomier way exists, small enough that it never rules one out.
        const float TightClearanceCost = 1.0f;

        const int MaxJumpGapCells = 2;
        const float JumpCostPerCell = 2.0f;
        const float StepCost = 0.5f;
        const float DropCostPerMeter = 0.35f;
        const float ElevatorCostPerMeter = 2.5f;
        // Deliberately CHEAP, not a deterrent: the two-pass design is what keeps climbing a last
        // resort (these edges don't exist at all in pass 1, which always gets first claim on any
        // walkable route), so by the time they're in play, climbing is mandatory and pricing it
        // punitively only sabotages the search. Measured with the old 10 + 3/m deterrent pricing:
        // each meter climbed raised f by ~11 while horizontal wandering could LOWER it, so pass 2
        // flooded every cheap pocket in a large water dungeon - a full 200k node cap - while ending
        // 13.5m short of an entrance that needed one ~30m climb chain.
        const float ClimbBaseCost = 1.0f;
        const float ClimbCostPerMeter = 2.0f;

        // How far (world units) a straight smoothed segment is allowed to drift from the original
        // path's own height profile at any intermediate waypoint - keeps smoothing from turning a
        // stair route into a diagonal flight through open air the player can't follow.
        const float MaxSmoothHeightDeviation = 1.5f;

        class NodeInfo
        {
            public SeanceCell Cell;
            public float G;
            public int ParentIndex;
            public byte MoveType;
            public bool Closed;

            // Lazy edge validation (the single biggest search-speed win, measured ~10x fewer physics
            // casts): adjacent-move edges are NOT spherecast during relaxation - only the incoming
            // edge of a node actually POPPED as current-best gets validated, one cast per settled
            // node instead of one per candidate neighbor (up to 26 in water). False = incoming edge
            // still needs its physics check at pop time.
            public bool EdgeValidated;
        }

        SeanceDungeonGrid grid;
        Vector3 startWorld;
        Vector3 goalWorld;

        SeanceCell goalCell;
        List<NodeInfo> nodes;
        Dictionary<SeanceCell, int> cellToIndex;
        SeanceMinHeap openHeap;
        bool allowClimb;
        int nodeCap;
        int expandedCount;
        int goalIndex;

        Stopwatch frameStopwatch = new Stopwatch();

        // Debug-only diagnostics, populated during Search() regardless of outcome - see DebugSummary,
        // used by the seance_pathcheck console command to see exactly where/why a search failed
        // (bad start/goal snap vs. a genuinely disconnected region vs. hitting a node cap) instead of
        // only ever seeing the ghost's own generic "cannot find a path" HUD message.
        bool startSnapOk;
        SeanceCell startCellDebug;
        bool goalSnapOk;
        SeanceCell goalCellDebug;
        int pass1Expanded;
        bool pass1Success;
        Vector3 pass1ClosestPosition;
        float pass1ClosestDistance;
        bool pass2Ran;
        int pass2Expanded;
        bool pass2Success;
        Vector3 pass2ClosestPosition;
        float pass2ClosestDistance;


        // Teleport instrumentation. Node counts alone cannot answer "did the ghost actually use that
        // teleporter" - FindClosestToGoal reports the closest CREATED node, not the closest EXPANDED
        // one, so a trigger cell showing up as closest-approach proves only that something relaxed
        // toward it. These separate "the trigger cell was reached and its edge relaxed" from "the
        // destination node was actually settled and explored onward from".
        int teleportEdgesRelaxed;
        int teleportNodesExpanded;
        List<SeanceCell> teleportDestinationsExpanded = new List<SeanceCell>();
        int pass1TeleportRelaxed;
        int pass1TeleportExpanded;
        string pass1TeleportDetail = "";
        int pass2TeleportRelaxed;
        int pass2TeleportExpanded;
        string pass2TeleportDetail = "";

        string DescribeTeleportDestinations()
        {
            if (teleportDestinationsExpanded.Count == 0)
                return "none";

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < teleportDestinationsExpanded.Count && i < 8; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(teleportDestinationsExpanded[i].ToString());
            }
            if (teleportDestinationsExpanded.Count > 8)
                sb.AppendFormat(", +{0} more", teleportDestinationsExpanded.Count - 8);

            return sb.ToString();
        }

        public SeanceVoxelPathfinder(SeanceDungeonGrid grid, Vector3 startWorld, Vector3 goalWorld)
        {
            this.grid = grid;
            this.startWorld = startWorld;
            this.goalWorld = goalWorld;
            Status = StatusRunning;
        }

        public IEnumerator Search()
        {
            // The goal passage force-opens a 5m cube around the target AND makes every unit edge inside it
            // passable regardless of geometry (see SeanceDungeonGrid.IsEdgeClear). That is right for a
            // caged/barred target the ghost can only lead the player TO, but as an always-on rule it
            // tunnels straight through walls near the goal: confirmed live at the Privateer's Hold exit,
            // where the exit sits behind thin geometry and the search "succeeded" in 10 expanded nodes on a
            // straight line through the wall, never routing the player to the door they actually needed.
            //
            // So it is now a LAST RESORT. Search honestly first; only if the target is genuinely
            // unreachable does the passage get switched on to salvage a lead-to-the-obstacle route.
            grid.ClearGoalPassage();

            SeanceCell startCell;
            startSnapOk = grid.TrySnapToOpenCell(startWorld, StartSnapRadius, true, out startCell);
            startCellDebug = startCell;
            if (!startSnapOk)
            {
                Status = StatusFailed;
                yield break;
            }

            goalSnapOk = grid.TrySnapToOpenCell(goalWorld, GoalSnapRadius, false, out goalCell);
            goalCellDebug = goalCell;
            if (!goalSnapOk)
            {
                Status = StatusFailed;
                yield break;
            }

            InitializePass(startCell, false, Pass1NodeCap);
            IEnumerator pass1 = RunSearchLoop();
            while (pass1.MoveNext())
                yield return null;
            pass1Expanded = expandedCount;
            pass1Success = goalIndex >= 0;
            pass1TeleportRelaxed = teleportEdgesRelaxed;
            pass1TeleportExpanded = teleportNodesExpanded;
            pass1TeleportDetail = DescribeTeleportDestinations();
            FindClosestToGoal(out pass1ClosestPosition, out pass1ClosestDistance);

            if (pass1Success)
            {
                ResultCost = nodes[goalIndex].G;
                Result = BuildSmoothedPath();
                Status = StatusSuccess;
                yield break;
            }

            // Climbing is meant to be an absolute last resort. When the caller is walking a clearance
            // ladder it must be able to say "no climbing yet" - otherwise a wide-clearance pass would
            // accept a wall climb before a tighter-clearance pass ever got the chance to find an ordinary
            // walking route, which is exactly backwards. Seen live in Privateer's Hold: the ghost led the
            // player up a sheer wall into a solid ceiling instead of taking the stairs a tier below.
            if (!AllowClimbFallback)
            {
                Status = StatusFailed;
                yield break;
            }

            pass2Ran = true;
            InitializePass(startCell, true, Pass2NodeCap);
            IEnumerator pass2 = RunSearchLoop();
            while (pass2.MoveNext())
                yield return null;
            pass2Expanded = expandedCount;
            pass2Success = goalIndex >= 0;
            pass2TeleportRelaxed = teleportEdgesRelaxed;
            pass2TeleportExpanded = teleportNodesExpanded;
            pass2TeleportDetail = DescribeTeleportDestinations();
            FindClosestToGoal(out pass2ClosestPosition, out pass2ClosestDistance);

            if (pass2Success)
            {
                ResultCost = nodes[goalIndex].G;
                Result = BuildSmoothedPath();
                Status = StatusSuccess;
                yield break;
            }

            // Genuinely unreachable by honest geometry. Deliberately does NOT fall back to force-opening
            // around the goal: an unreachable target is usually unreachable BY DESIGN, not by mistake - a
            // dungeon exit's StartMarker sits inside the exit door's own geometry (measured at Privateer's
            // Hold: entire dungeon searched, 17k nodes, closest approach 2.2m), and a caged quest target is
            // sealed in on purpose. Tunnelling to the marker walked the ghost through the wall and never
            // showed the player the door they actually needed. SeanceGhostGuide handles this instead by
            // leading to ClosestApproachPosition - in front of the door, or just outside the cage - which is
            // the useful answer in both cases and never passes through geometry.
            Status = StatusFailed;
        }

        // For seance_pathcheck - Search() must have already been run to completion (synchronously
        // pumping MoveNext() is fine for a manual diagnostic command; the ghost's own real usage still
        // goes through the normal frame-budgeted coroutine).
        // Every cell the most recent pass actually SETTLED (popped and expanded), not merely created -
        // i.e. the true reachable set under the real movement rules. Used by seance_reachcheck to
        // characterise a region's shape and work out which teleporters it can and cannot get to.
        public HashSet<SeanceCell> GetExpandedCells()
        {
            HashSet<SeanceCell> result = new HashSet<SeanceCell>();
            if (nodes == null)
                return result;

            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Closed)
                    result.Add(nodes[i].Cell);
            }

            return result;
        }

        public string DebugSummary()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendFormat("Start {0} -> snap {1} ({2}).", startWorld, startSnapOk ? "OK" : "FAILED", startCellDebug);
            sb.AppendFormat("\nGoal {0} -> snap {1} ({2}).", goalWorld, goalSnapOk ? "OK" : "FAILED", goalCellDebug);

            if (!startSnapOk || !goalSnapOk)
                return sb.ToString();

            sb.AppendFormat("\nPass 1 (no climb): expanded {0}/{1} nodes, {2}. Closest approach to goal: {3} ({4:F1}m away).",
                pass1Expanded, Pass1NodeCap, pass1Success ? "SUCCESS" : "failed", pass1ClosestPosition, pass1ClosestDistance);
            sb.AppendFormat("\n  Teleport: {0} edge(s) relaxed, {1} destination node(s) expanded [{2}].",
                pass1TeleportRelaxed, pass1TeleportExpanded, pass1TeleportDetail);

            if (pass2Ran)
            {
                sb.AppendFormat("\nPass 2 (climb enabled): expanded {0}/{1} nodes, {2}. Closest approach to goal: {3} ({4:F1}m away).",
                    pass2Expanded, Pass2NodeCap, pass2Success ? "SUCCESS" : "failed", pass2ClosestPosition, pass2ClosestDistance);
                sb.AppendFormat("\n  Teleport: {0} edge(s) relaxed, {1} destination node(s) expanded [{2}].",
                    pass2TeleportRelaxed, pass2TeleportExpanded, pass2TeleportDetail);
            }

            // A raw FAILED here does NOT mean the ghost gives up - SeanceGhostGuide still escorts to a gated
            // obstacle, or leads to the closest reachable point when the target is sealed in by design. Said
            // explicitly because a bare "FAILED" was misread as a broken cast when the ghost was in fact
            // completing the journey.
            if (Status == StatusFailed && startSnapOk && goalSnapOk)
            {
                sb.AppendFormat(
                    "\nNote: the raw search failed, but the ghost does not stop here - it would {0}.",
                    ClosestApproachDistance <= 4f
                        ? string.Format("treat the target as sealed in by design and lead the player to {0} ({1:F1}m from it)",
                            ClosestApproachPosition, ClosestApproachDistance)
                        : "try escorting the player to a nearby lever-gated obstacle first, then fall back to leading to "
                          + string.Format("{0} ({1:F1}m from the target)", ClosestApproachPosition, ClosestApproachDistance));
            }

            sb.AppendFormat("\nFinal status: {0}.", Status == StatusSuccess ? "SUCCESS"
                : Status == StatusFailed ? "FAILED" : "RUNNING (incomplete)");

            if (Result != null)
            {
                sb.AppendFormat(" Path has {0} waypoint(s):", Result.Count);
                int unfittable = 0;
                for (int i = 0; i < Result.Count; i++)
                {
                    // Only WALK waypoints are tested. Drop/Jump/Climb/Elevator/Swim/Teleport are transit -
                    // the player passes THROUGH those cells (falling, leaping, riding) rather than standing
                    // in them, so floor-anchored standing clearance is the wrong question and flags every
                    // step of a normal descent: measured on one staircase, all 9 flags were Drop moves and
                    // none of the 54 Walk moves, which is the signature of a false positive, not a real
                    // blockage.
                    bool isTransit = Result[i].MoveType != SeanceMoveType.Walk;
                    bool fits = isTransit || grid.PlayerCapsuleFits(Result[i].Position);
                    if (!fits)
                        unfittable++;

                    sb.AppendFormat("\n  [{0}] {1} {2}{3}", i, MoveTypeName(Result[i].MoveType), Result[i].Position,
                        fits ? "" : "   <-- PLAYER CANNOT FIT HERE (1.8m capsule blocked)");
                }

                if (unfittable > 0)
                {
                    sb.AppendFormat(
                        "\n  WARNING: {0} of {1} waypoint(s) are passable to the ghost but NOT to the player - " +
                        "this route cannot actually be followed.", unfittable, Result.Count);
                }
            }

            return sb.ToString();
        }

        public static string MoveTypeName(byte moveType)
        {
            switch (moveType)
            {
                case SeanceMoveType.Walk: return "Walk";
                case SeanceMoveType.Drop: return "Drop";
                case SeanceMoveType.Jump: return "Jump";
                case SeanceMoveType.Elevator: return "Elevator";
                case SeanceMoveType.Climb: return "Climb";
                case SeanceMoveType.Swim: return "Swim";
                case SeanceMoveType.Teleport: return "Teleport";
                default: return "Unknown";
            }
        }

        // Debug-only - scans every node discovered so far (must be called before InitializePass wipes
        // the list for the next pass) to find how close the search actually got to the goal, even on
        // failure. Distinguishes "got stuck in a small pocket right at the start" from "got most of the
        // way there and stalled" - information the plain expanded-node count alone can't show.
        // Only EXPANDED (settled) nodes count. A node that was merely created and queued was never proven
        // reachable - it sits in the open set precisely because the search had not got there yet, and on a
        // failed search many such nodes are ones it never could reach. Reporting one of those as the
        // "closest approach" is actively harmful: SeanceGhostGuide leads the player to this position when the
        // target is sealed in, and a real cast failed exactly that way (closest approach reported 1.4m away,
        // the lead search to it then failed and the ghost gave up, while the genuinely-reachable 2.0m cell
        // would have worked). Also uses each cell's probed free position so the reported point is somewhere
        // the ghost can actually be.
        void FindClosestToGoal(out Vector3 closestPosition, out float closestDistance)
        {
            closestDistance = float.MaxValue;
            closestPosition = Vector3.zero;

            Vector3 goalWorldCenter = goalCell.ToWorldCenter(SeanceDungeonGrid.CellSize);
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!nodes[i].Closed)
                    continue;

                Vector3 position = grid.FreePosition(nodes[i].Cell);
                float distance = Vector3.Distance(position, goalWorldCenter);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestPosition = position;
                }
            }

            // Nothing settled at all (start cell itself rejected) - fall back to the start so callers never
            // receive a (0,0,0) that would send the ghost to the world origin.
            if (closestDistance == float.MaxValue)
            {
                closestPosition = startWorld;
                closestDistance = Vector3.Distance(startWorld, goalWorldCenter);
            }
        }

        void InitializePass(SeanceCell startCell, bool climbEnabled, int cap)
        {
            allowClimb = climbEnabled;
            nodeCap = cap;
            expandedCount = 0;
            goalIndex = -1;
            teleportEdgesRelaxed = 0;
            teleportNodesExpanded = 0;
            teleportDestinationsExpanded.Clear();

            nodes = new List<NodeInfo>(256);
            cellToIndex = new Dictionary<SeanceCell, int>(256);
            openHeap = new SeanceMinHeap(256);

            int startIndex = GetOrCreateNode(startCell);
            nodes[startIndex].G = 0f;
            nodes[startIndex].EdgeValidated = true;
            openHeap.Push(startIndex, Heuristic(startCell));
        }

        IEnumerator RunSearchLoop()
        {
            frameStopwatch.Reset();
            frameStopwatch.Start();
            int sinceCheck = 0;

            while (openHeap.Count > 0)
            {
                float poppedF;
                int currentIndex = openHeap.Pop(out poppedF);
                NodeInfo currentInfo = nodes[currentIndex];

                if (currentInfo.Closed)
                    continue;

                // Stale heap entry of a node that was reset by edge-repair below (G back to infinity,
                // no parent) and hasn't been re-reached via an alternative edge since.
                if (currentInfo.ParentIndex < 0 && currentInfo.G == float.MaxValue)
                    continue;

                // Lazy edge validation: this node's incoming edge gets its (single) physics cast only
                // now that the node is actually the current best - not during relaxation, where up to
                // 26 candidate edges per expansion would each pay a cast for routes mostly never
                // taken. IsEdgeClear caches the result either way, so re-validation is free.
                if (!currentInfo.EdgeValidated && currentInfo.ParentIndex >= 0)
                {
                    if (grid.IsEdgeClear(nodes[currentInfo.ParentIndex].Cell, currentInfo.Cell))
                    {
                        currentInfo.EdgeValidated = true;
                    }
                    else
                    {
                        Reject(RejectEdgeBlocked, nodes[currentInfo.ParentIndex].Cell, currentInfo.Cell);
                        RepairAfterInvalidEdge(currentInfo);
                        continue;
                    }
                }

                currentInfo.Closed = true;
                expandedCount++;

                if (currentInfo.MoveType == SeanceMoveType.Teleport)
                {
                    teleportNodesExpanded++;
                    teleportDestinationsExpanded.Add(currentInfo.Cell);
                }

                if (currentInfo.Cell.Equals(goalCell))
                {
                    goalIndex = currentIndex;

                    // FloodMode keeps going so the whole reachable set is settled - a diagnostic flood must
                    // not stop the moment it happens to touch the goal. Off for every real search.
                    if (!FloodMode)
                        yield break;
                }

                if (expandedCount >= nodeCap)
                    yield break;

                ExpandNode(currentIndex, currentInfo);

                sinceCheck++;
                if (sinceCheck >= ExpansionsPerStopwatchCheck)
                {
                    sinceCheck = 0;
                    if (frameStopwatch.Elapsed.TotalMilliseconds >= FrameBudgetMs)
                    {
                        yield return null;
                        frameStopwatch.Reset();
                        frameStopwatch.Start();
                    }
                }
            }
        }

        // A deferred edge turned out blocked at pop time (a thin wall the cell probes straddled - the
        // rare case lazy validation bets against). Reset the node so the discredited route through it
        // dies, then re-expand its already-settled neighbors: their relaxations re-reach this node via
        // any alternative edge (the blocked one is now cached known-impassable, so the known-edge
        // check in relaxation prunes it), and re-relaxing everything else they touch is harmless -
        // improvements-only, against fully cached cells. A node whose only alternative approach is a
        // non-adjacent move (a drop from above, a jump) is not recovered by this and stays dead -
        // accepted: those combinations are rare, and the cost is at worst a slightly longer route or a
        // clean no-path fallback rather than a wrong path.
        void RepairAfterInvalidEdge(NodeInfo info)
        {
            info.G = float.MaxValue;
            info.ParentIndex = -1;
            info.EdgeValidated = false;

            for (int dirIndex = 0; dirIndex < SeanceDirections.Count; dirIndex++)
            {
                SeanceCell neighborCell = info.Cell.Offset(
                    SeanceDirections.DX[dirIndex], SeanceDirections.DY[dirIndex], SeanceDirections.DZ[dirIndex]);

                int neighborIndex;
                if (cellToIndex.TryGetValue(neighborCell, out neighborIndex))
                {
                    NodeInfo neighborInfo = nodes[neighborIndex];
                    if (neighborInfo.Closed)
                        ExpandNode(neighborIndex, neighborInfo);
                }
            }
        }

        void ExpandNode(int currentIndex, NodeInfo currentInfo)
        {
            SeanceCell currentCell = currentInfo.Cell;
            bool inWater = grid.IsWater(currentCell);

            for (int dirIndex = 0; dirIndex < SeanceDirections.Count; dirIndex++)
            {
                int dx = SeanceDirections.DX[dirIndex];
                int dy = SeanceDirections.DY[dirIndex];
                int dz = SeanceDirections.DZ[dirIndex];

                bool isPureVertical = dx == 0 && dz == 0;
                if (isPureVertical && !inWater)
                {
                    Reject(RejectNoRule, currentCell, currentCell.Offset(dx, dy, dz));
                    continue;
                }

                SeanceCell neighborCell = currentCell.Offset(dx, dy, dz);

                if (inWater)
                {
                    TryAddWaterEdge(currentIndex, currentInfo, neighborCell);
                    continue;
                }

                if (!grid.IsOpen(neighborCell))
                {
                    Reject(RejectNotOpen, currentCell, neighborCell);
                    continue;
                }

                bool neighborIsGoal = neighborCell.Equals(goalCell);

                // An elevator shaft's own interior is unsupported by definition (it's an open vertical
                // column, not floor) - without this, there was no way to ever step OFF solid floor and
                // INTO the shaft to begin riding it at all, since an ordinary horizontal move requires
                // support and the shaft is neither within jump range nor actually a "gap" in the sense
                // TryAddJumpEdges models. Boarding the shaft this way is a plain Walk-cost move; the
                // elevator cost only applies once actually riding vertically (TryAddVerticalStep).
                bool neighborIsElevator = grid.IsElevatorColumn(neighborCell);

                if (grid.HasSupport(neighborCell) || neighborIsGoal || neighborIsElevator)
                {
                    TryAddHorizontalEdge(currentIndex, currentInfo, neighborCell, dy);
                }
                else if (dy <= 0)
                {
                    // Only attempt a jump from a level or descending direction - an unsupported cell
                    // one step ABOVE the current one isn't a gap to hop, it's just unsupported air.
                    TryAddJumpEdges(currentIndex, currentInfo, currentCell, dx, dz);
                }
                else
                {
                    Reject(RejectNoSupport, currentCell, neighborCell);
                }
            }

            if (!inWater)
            {
                TryAddDropEdge(currentIndex, currentInfo, currentCell);
                TryAddVerticalEdges(currentIndex, currentInfo, currentCell);
            }

            // Independent of water/climb state - an active teleporter is available in both passes,
            // same as elevator riding (only CLIMBING itself is pass-2-gated).
            TryAddTeleportEdge(currentIndex, currentInfo, currentCell);
        }

        // Moderate fixed cost (spec: ~10-15m walking equivalent) rather than the edge's own real
        // Euclidean distance (which can be enormous - a teleporter can connect opposite ends of the
        // dungeon) - the ghost should prefer a teleporter when it meaningfully beats the walkable
        // route, not dart into one to save a few meters, and pricing it at its true spatial distance
        // would make it always-neutral-at-best against the equivalent walk. No physics to validate
        // (it's not a real spatial edge), so this relaxes as already-validated.
        const float TeleportCost = 12f;

        void TryAddTeleportEdge(int currentIndex, NodeInfo currentInfo, SeanceCell currentCell)
        {
            SeanceCell destination;
            if (!grid.TryGetTeleportDestination(currentCell, out destination))
                return;

            teleportEdgesRelaxed++;
            RelaxEdge(currentIndex, currentInfo.G, destination, TeleportCost, SeanceMoveType.Teleport, true);
        }

        void TryAddHorizontalEdge(int currentIndex, NodeInfo currentInfo, SeanceCell neighborCell, int dy)
        {
            SeanceCell currentCell = currentInfo.Cell;

            float distance = Vector3.Distance(
                currentCell.ToWorldCenter(SeanceDungeonGrid.CellSize), neighborCell.ToWorldCenter(SeanceDungeonGrid.CellSize));
            float cost = distance;
            if (dy > 0)
                cost += StepCost;

            // Moderate penalty for entering a cell that is passable but not roomy. Keeps the heuristic
            // admissible (costs only ever rise) while making A* prefer corridor centres, so the ghost stops
            // hugging walls and grazing doorframes wherever the geometry gives it a choice. It still takes
            // the tight cell when that is the only way through - this is a preference, not a prohibition.
            if (!grid.IsRoomy(neighborCell))
                cost += TightClearanceCost;

            RelaxLazyEdge(currentIndex, currentInfo, neighborCell, cost, SeanceMoveType.Walk);
        }

        void TryAddJumpEdges(int currentIndex, NodeInfo currentInfo, SeanceCell currentCell, int dx, int dz)
        {
            for (int jumpCells = 1; jumpCells <= MaxJumpGapCells; jumpCells++)
            {
                int landingDistance = jumpCells + 1;
                SeanceCell landingCell = currentCell.Offset(dx * landingDistance, 0, dz * landingDistance);

                bool landingIsGoal = landingCell.Equals(goalCell);
                if (!grid.IsOpen(landingCell) || !(grid.HasSupport(landingCell) || landingIsGoal))
                    continue;

                bool clearPath = true;
                SeanceCell previous = currentCell;
                for (int step = 1; step <= landingDistance; step++)
                {
                    SeanceCell stepCell = currentCell.Offset(dx * step, 0, dz * step);
                    if (!grid.IsOpen(stepCell) || !grid.IsEdgeClear(previous, stepCell))
                    {
                        clearPath = false;
                        break;
                    }
                    previous = stepCell;
                }

                if (!clearPath)
                    continue;

                float distance = Vector3.Distance(
                    currentCell.ToWorldCenter(SeanceDungeonGrid.CellSize), landingCell.ToWorldCenter(SeanceDungeonGrid.CellSize));
                float cost = distance + JumpCostPerCell * jumpCells;

                // Jump chains keep EAGER edge validation (the per-step IsEdgeClear calls above) - a
                // jump isn't a single adjacent edge, so pop-time parent-edge validation can't cover it.
                RelaxEdge(currentIndex, currentInfo.G, landingCell, cost, SeanceMoveType.Jump, true);

                // Only add the shortest viable jump across this gap direction - a longer jump over the
                // same gap is strictly worse (more cost for the same or a riskier landing).
                break;
            }
        }

        void TryAddDropEdge(int currentIndex, NodeInfo currentInfo, SeanceCell currentCell)
        {
            SeanceCell landing;
            float dropDistance;
            if (!grid.TryFindDropLanding(currentCell, out landing, out dropDistance))
                return;

            if (dropDistance < 0.01f || landing.Equals(currentCell))
                return;

            // Drop edges are physics-validated by construction (TryFindDropLanding IS the cast).
            float cost = dropDistance + DropCostPerMeter * dropDistance;
            RelaxEdge(currentIndex, currentInfo.G, landing, cost, SeanceMoveType.Drop, true);
        }

        // Elevator riding is symmetric (up OR down) - a real shaft can need either direction (e.g. the
        // quest marker sitting on a floor the shaft merely passes through on its way further down), so
        // this is checked for both dy=+1 and dy=-1. Climbing is ascent-only; there's no "climb down" -
        // descending without an elevator is already covered by the ordinary collapsed drop edge
        // (TryAddDropEdge), which handles arbitrary-depth falls in one shot.
        void TryAddVerticalEdges(int currentIndex, NodeInfo currentInfo, SeanceCell currentCell)
        {
            TryAddVerticalStep(currentIndex, currentInfo, currentCell, 1);
            TryAddVerticalStep(currentIndex, currentInfo, currentCell, -1);
        }

        void TryAddVerticalStep(int currentIndex, NodeInfo currentInfo, SeanceCell currentCell, int dy)
        {
            SeanceCell neighbor = currentCell.Offset(0, dy, 0);
            if (!grid.IsOpen(neighbor))
                return;

            if (grid.IsElevatorColumn(currentCell) && grid.IsElevatorColumn(neighbor))
            {
                float elevatorCost = SeanceDungeonGrid.CellSize * ElevatorCostPerMeter;
                RelaxLazyEdge(currentIndex, currentInfo, neighbor, elevatorCost, SeanceMoveType.Elevator);
                return;
            }

            if (dy <= 0 || !allowClimb)
                return;

            // Climb ascent (pass 2 only) requires the DESTINATION to be wall-adjacent, so a climb edge
            // never ends hanging in open space - models Daggerfall's climbing skill/Levitate along a
            // real wall face, not free-floating flight through a void.
            if (!grid.IsWallAdjacent(neighbor))
                return;

            // Phase 2.6 Step (b): wall-adjacent alone is not enough - a climb must also terminate on a real
            // ledge (upward-facing surface within a short reach), not merely "floor somewhere within
            // HasSupport's 2m walk-support window." That window is exactly what let a doorway's own headroom
            // read as climbable: real floor sat 1.5m below, nothing beside it was actually climbable, and
            // wall-adjacency alone didn't catch it (see IsClimbableLedge's own comment).
            if (!grid.IsClimbableLedge(neighbor))
            {
                Reject(RejectClimbLedge, currentCell, neighbor);
                return;
            }

            float climbCost = ClimbBaseCost + ClimbCostPerMeter * SeanceDungeonGrid.CellSize;
            RelaxLazyEdge(currentIndex, currentInfo, neighbor, climbCost, SeanceMoveType.Climb);
        }

        // Swimming carries a mild per-meter premium over walking (originally flat Euclidean per the
        // spec) - without it, a route OUT of a flooded dungeon would happily detour back through the
        // lake even when dry stairs exist, since water's free 26-way movement often measures shorter
        // than the winding walkable route. The premium keeps dry routes winning whenever they're
        // comparable, while still swimming without hesitation when the target genuinely lies in or
        // beyond water (a submerged quest marker still resolves fine - there's simply no dry
        // alternative to outbid it).
        const float SwimCostMultiplier = 1.4f;

        void TryAddWaterEdge(int currentIndex, NodeInfo currentInfo, SeanceCell neighborCell)
        {
            if (!grid.IsOpen(neighborCell))
                return;

            float distance = Vector3.Distance(
                currentInfo.Cell.ToWorldCenter(SeanceDungeonGrid.CellSize), neighborCell.ToWorldCenter(SeanceDungeonGrid.CellSize));
            RelaxLazyEdge(currentIndex, currentInfo, neighborCell, distance * SwimCostMultiplier, SeanceMoveType.Swim);
        }

        // Adjacent-move relaxation with lazy edge validation: consults ONLY the edge cache (free) -
        // already-known-blocked edges are pruned outright, already-known-clear ones relax as validated,
        // and unknown ones relax optimistically, deferring their physics cast to pop time (see
        // RunSearchLoop). This is where the bulk of the search's former spherecast cost disappeared.
        void RelaxLazyEdge(int currentIndex, NodeInfo currentInfo, SeanceCell neighborCell, float edgeCost, byte moveType)
        {
            bool knownPassable;
            if (grid.TryGetKnownEdge(currentInfo.Cell, neighborCell, out knownPassable))
            {
                if (knownPassable)
                    RelaxEdge(currentIndex, currentInfo.G, neighborCell, edgeCost, moveType, true);
                else
                    Reject(RejectEdgeBlocked, currentInfo.Cell, neighborCell);   // known-impassable, never reaches RelaxEdge

                return;
            }

            RelaxEdge(currentIndex, currentInfo.G, neighborCell, edgeCost, moveType, false);
        }

        void RelaxEdge(int currentIndex, float currentG, SeanceCell neighborCell, float edgeCost, byte moveType, bool edgeValidated)
        {
            int neighborIndex = GetOrCreateNode(neighborCell);
            NodeInfo neighborInfo = nodes[neighborIndex];
            if (neighborInfo.Closed)
            {
                Reject(RejectVisited, nodes[currentIndex].Cell, neighborCell);
                return;
            }

            float tentativeG = currentG + edgeCost;
            if (tentativeG < neighborInfo.G)
            {
                neighborInfo.EdgeValidated = edgeValidated;
                neighborInfo.G = tentativeG;
                neighborInfo.ParentIndex = currentIndex;
                neighborInfo.MoveType = moveType;
                openHeap.Push(neighborIndex, tentativeG + Heuristic(neighborCell));
            }
        }

        int GetOrCreateNode(SeanceCell cell)
        {
            int index;
            if (cellToIndex.TryGetValue(cell, out index))
                return index;

            NodeInfo info = new NodeInfo();
            info.Cell = cell;
            info.G = float.MaxValue;
            info.ParentIndex = -1;
            info.MoveType = SeanceMoveType.Walk;
            info.Closed = false;

            index = nodes.Count;
            nodes.Add(info);
            cellToIndex[cell] = index;
            return index;
        }

        // Pre-weighted (see HeuristicWeight) - every caller uses this as the f-value's h term directly.
        float Heuristic(SeanceCell cell)
        {
            return HeuristicWeight * Vector3.Distance(
                cell.ToWorldCenter(SeanceDungeonGrid.CellSize), goalCell.ToWorldCenter(SeanceDungeonGrid.CellSize));
        }

        List<SeancePathPoint> BuildSmoothedPath()
        {
            List<SeancePathPoint> raw = new List<SeancePathPoint>();
            int index = goalIndex;
            while (index >= 0)
            {
                NodeInfo info = nodes[index];
                SeancePathPoint point = new SeancePathPoint();
                // The position the grid actually probed as free, not the cell centre - a waypoint at the
                // centre of a cell that is only open off-centre would send the ghost into the geometry
                // beside the opening it just routed through.
                point.Position = grid.FreePosition(info.Cell);
                point.MoveType = info.MoveType;
                raw.Add(point);
                index = info.ParentIndex;
            }
            raw.Reverse();

            return SmoothPath(raw);
        }

        // String-pulling: from each waypoint, find the furthest later waypoint reachable in a straight
        // line without breaking traversability (no non-Walk segment skipped over, and the straight line
        // never drifts far from the original path's own height profile - see CanSmoothSkip), and jump
        // straight to it, discarding everything in between. Cuts down a dense cell-by-cell raw path into
        // a small number of real waypoints for the guide to move between.
        List<SeancePathPoint> SmoothPath(List<SeancePathPoint> raw)
        {
            if (raw.Count <= 2)
                return raw;

            List<SeancePathPoint> smoothed = new List<SeancePathPoint>();
            smoothed.Add(raw[0]);

            int currentIndex = 0;
            while (currentIndex < raw.Count - 1)
            {
                int furthest = currentIndex + 1;

                for (int candidate = raw.Count - 1; candidate > currentIndex + 1; candidate--)
                {
                    if (CanSmoothSkip(raw, currentIndex, candidate))
                    {
                        furthest = candidate;
                        break;
                    }
                }

                smoothed.Add(raw[furthest]);
                currentIndex = furthest;
            }

            PushWaypointsTowardClearance(smoothed);
            return smoothed;
        }

        // How many times to re-centre each waypoint. Moving on one axis changes what the others see, so a
        // couple of passes settles it; more than that buys nothing measurable.
        const int ClearancePushIterations = 3;

        // Waypoints leave A* as grid-cell positions, which is why the ghost grazed doorframes even on a
        // valid route - the lattice, not the geometry, decided where it flew. Nudging each one toward the
        // middle of its own free space turns them into continuous world positions and fixes grid-alignment
        // artifacts without shrinking the cell size.
        //
        // Transit waypoints (Drop/Jump/Elevator/Climb/Teleport) are left exactly where they are: their
        // positions are chosen by the movement rule that created them, and shifting a drop's launch point or
        // a teleporter's trigger cell would break it.
        void PushWaypointsTowardClearance(List<SeancePathPoint> path)
        {
            // Kept so a push that breaks its own segment can be undone - the un-pushed positions came from
            // cells whose connecting edge A* had already validated.
            List<Vector3> originalPositions = new List<Vector3>(path.Count);
            for (int i = 0; i < path.Count; i++)
                originalPositions.Add(path[i].Position);

            for (int i = 0; i < path.Count; i++)
            {
                SeancePathPoint point = path[i];

                if (point.MoveType != SeanceMoveType.Walk)
                {
                    point.Clearance = grid.MeasureClearance(point.Position, SeanceDungeonGrid.ClearanceRadius);
                    path[i] = point;
                    continue;
                }

                float clearance;
                point.Position = grid.PushTowardClearance(point.Position, ClearancePushIterations, out clearance);
                point.Clearance = clearance;
                path[i] = point;
            }

            RepairPushedSegments(path, originalPositions);
        }

        // Pushing waypoints MOVES the polyline, which silently invalidates the edges A* checked between cell
        // centres. Two waypoints can each have good clearance while the straight line between them grazes a
        // corner that protrudes into it - and since the ghost now follows that line exactly (arc-length
        // following), it drives straight into the corner, gets vetoed, re-paths to the identical route, and
        // repeats until it gives up. Measured live: stuck at (4.8, 33.4, 17.4), a pushed position 0.3m off
        // its cell centre.
        //
        // So every consecutive pair is re-checked after pushing, and any pair that no longer clears is
        // reverted - the un-pushed positions came from cells whose edge A* had already validated.
        void RepairPushedSegments(List<SeancePathPoint> path, List<Vector3> originalPositions)
        {
            for (int i = 1; i < path.Count; i++)
            {
                if (path[i].MoveType != SeanceMoveType.Walk && path[i - 1].MoveType != SeanceMoveType.Walk)
                    continue;

                Collider blocker;
                bool blockedByActionObject;
                if (grid.IsSegmentClear(path[i - 1].Position, path[i].Position, SeanceDungeonGrid.ProbeRadius,
                        out blocker, out blockedByActionObject))
                    continue;

                // Revert the later waypoint first - it is the one the ghost is heading toward.
                SeancePathPoint later = path[i];
                later.Position = originalPositions[i];
                path[i] = later;

                if (grid.IsSegmentClear(path[i - 1].Position, path[i].Position, SeanceDungeonGrid.ProbeRadius,
                        out blocker, out blockedByActionObject))
                    continue;

                // Still blocked - the earlier push is at fault too.
                SeancePathPoint earlier = path[i - 1];
                earlier.Position = originalPositions[i - 1];
                path[i - 1] = earlier;
            }
        }

        bool CanSmoothSkip(List<SeancePathPoint> raw, int fromIndex, int toIndex)
        {
            for (int i = fromIndex + 1; i <= toIndex; i++)
            {
                if (raw[i].MoveType != SeanceMoveType.Walk)
                    return false;
            }

            Vector3 from = raw[fromIndex].Position;
            Vector3 to = raw[toIndex].Position;
            float totalDistance = Vector3.Distance(from, to);

            for (int i = fromIndex + 1; i < toIndex; i++)
            {
                // Approximates each intermediate waypoint's position along the straight segment by
                // distance-from-start rather than a true closest-point projection - the raw path is
                // already roughly monotonic along its own travel direction, so this is accurate enough
                // to guard against a smoothed segment cutting through a real height change (a stairwell,
                // a pit) without the cost of a full segment-projection calculation per candidate.
                float partialDistance = Vector3.Distance(from, raw[i].Position);
                float t = totalDistance > 0.001f ? Mathf.Clamp01(partialDistance / totalDistance) : 0f;
                Vector3 interpolated = Vector3.Lerp(from, to, t);

                if (Mathf.Abs(interpolated.y - raw[i].Position.y) > MaxSmoothHeightDeviation)
                    return false;
            }

            // Tested at the CLEARANCE radius, not the search radius. A shortcut is optional by definition -
            // if the straight line only fits by the same margin that barely got us through in the first
            // place, it is not worth taking, and taking it is exactly how the ghost ended up grazing the
            // corners it cut. Uses the sampled segment test, so a segment starting beside a wall cannot
            // silently ignore that wall the way a SphereCast sweep did.
            Collider blocker;
            bool blockedByActionObject;
            return grid.IsSegmentClear(from, to, SeanceDungeonGrid.ClearanceRadius, out blocker, out blockedByActionObject);
        }
    }
}
