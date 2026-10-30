// Project:         Astrid's Guide to Necormancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Lazy voxel sampler for the current dungeon - samples cells/edges on demand directly from physics
// colliders and caches results for the lifetime of the dungeon visit, rather than pre-voxelizing the
// whole volume up front. No A* logic lives here at all (see SeanceVoxelPathfinder.cs for that) - this
// is purely "is this cell open, does it have floor beneath it, is this edge clear, is this a water
// cell, is this an elevator shaft column."
//
// Replaces SeanceDungeonNavMesh.cs's runtime NavMesh bake (see SeanceGhostGuideLegacyNavMesh.cs for
// why that approach kept failing on cave dungeons/elevators/pits). That file and its legacy guide are
// left completely untouched as an inert rollback option - this grid doesn't reference either one, by
// design, so the legacy system's own files never depend on anything the new system changes.
//
// Layer scoping matches the legacy bake exactly: "Default" only, since dungeon wall/floor geometry has
// no dedicated layer of its own, while every actor (enemies, this mod's own allies, the Seance ghost
// itself) sits on a separate "Enemies"/"Player" layer (SetupDemoEnemy.cs) - so actors are naturally
// excluded from ever becoming accidental obstacles here.
//
// Two different exclusion sets, not one:
// - excludeFromOpen: every DaggerfallActionDoor's own collider(s), every DaggerfallAction whose
//   ModelDescription is "TRP" (DFU has no dedicated trapdoor class - this string check is the only way
//   to identify one, confirmed via RDBLayout.cs), and every DaggerfallAction with a real (non-flat,
//   >5cm) ActionTranslation - i.e. anything that moves. Doors/trapdoors are excluded so a closed one
//   never blocks the path (the ghost still physically opens real doors via ForceOpenNearbyDoor, same
//   logic SeanceGhostGuide already had - ForceOpenNearbyDoor doesn't care how the door was found
//   reachable). Movers are excluded so a mid-search elevator shift can't bake its transient position
//   into the lifetime cache as if it were permanent geometry.
// - excludeFromSupport: movers only. A closed trapdoor/door DOES count as solid ground to stand on
//   (that's how "closed trapdoor reads as open floor to walk across, but also solid floor to stand on"
//   works), but a moving platform's floor is never trustworthy to cache as permanent support for
//   exactly the same staleness reason it's excluded from excludeFromOpen.
//
// IsTriggerWhenOpen: an OPENED action door's collider becomes a TRIGGER, not disabled (confirmed in
// DaggerfallActionDoor.cs) - every physics query below passes QueryTriggerInteraction.Ignore so an
// already-open door never gets mistaken for a solid obstruction later in the same dungeon visit.

using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;

namespace VoxsNecromancyMod.AI
{
    // A physical obstacle (an inactive/gated teleporter, a lever-gated door/portcullis, or a
    // lever-gated trapdoor) whose CURRENT state blocks the route, but which SeanceGhostGuide's own
    // multi-leg escort state machine can resolve by leading the player to a player-activatable root
    // action first (a lever, switch, or password-input object) - see SeanceDungeonGrid.
    // BuildGatedObstacleTable for how Roots is found via reverse chain analysis. Plain byte constants
    // for Kind, not an enum - see SeanceCell.cs's own header comment for why (a nested enum elsewhere
    // in this mod once broke the mod loader's reflection scan outright).
    public class SeanceGatedObstacle
    {
        public const byte KindTeleporter = 0;
        public const byte KindDoor = 1;
        public const byte KindTrapdoor = 2;

        public byte Kind;
        public DaggerfallAction ObstacleAction;
        public Vector3 ObstaclePosition;

        // Only meaningful when Kind == KindTeleporter - the teleporter's own NextObject position,
        // exactly as Stage 1's already-active teleporters use it.
        public Vector3 TeleportDestinationPosition;

        // Every player-activatable action found walking the obstacle's own action chain backward -
        // usually one (a single lever), occasionally more (a door gated behind multiple levers, all
        // of which must be found and toured). Never empty - an obstacle with no resolvable root is
        // simply never registered at all (see BuildGatedObstacleTable).
        public List<DaggerfallAction> Roots;
    }


    public class SeanceDungeonGrid
    {
        public const float CellSize = 1.0f;

        // Derived from the LIVE player capsule, not guessed. DaggerfallUnityGame.unity instantiates
        // PlayerAdvanced.prefab: CharacterController radius 0.35, height 1.8, skinWidth 0.06. Unity lets
        // colliders penetrate a CharacterController by up to its skin width, so the player's TRUE minimum
        // squeeze radius is 0.35 - 0.06 = 0.29, not 0.35.
        //
        // That sets both bounds, and the usable window is narrow - [0.29, 0.32]:
        //  - below 0.29 the ghost would route the player through gaps they physically cannot enter;
        //  - 0.33 and above seals the staircase arch in Privateer's Hold (the starter dungeon, and so the
        //    first place most players ever cast Seance). Measured live by bisection: 0.34 and 0.33 both
        //    stranded the search at 725/726 expanded nodes, 0.32 reached the goal with 9268 - a single arch
        //    of roughly 0.65m clearance gating the entire dungeon.
        //
        // 0.32 keeps a ~0.03 margin over the player's real minimum (so a route is never impossible to
        // follow) while staying under that arch. Being this tight a window, expect future dungeons to
        // press on it - seance_probe exists to re-bisect without a rebuild.
        public const float DefaultProbeRadius = 0.32f;

        // SEARCH radius. Answers only "does a path exist?" - it is the tiered value (0.45 -> 0.32) the
        // clearance ladder walks. Settable (not const) so seance_probe can bisect it in-game without a
        // rebuild, and static because it is a global tuning knob rather than per-dungeon state.
        //
        // Deliberately NOT the same number as clearance or body size. One value used to do all three jobs,
        // which meant dropping a tier for feasibility silently degraded path QUALITY as a side effect - and
        // quality is what was failing.
        public static float ProbeRadius = DefaultProbeRadius;

        // CLEARANCE radius. Answers "is this path comfortable?" - a cell open at ProbeRadius but not roomy at
        // this radius is walkable yet hugs a wall, so A* pays a penalty to prefer corridor centres. Also the
        // radius the path-smoothing shortcut test must satisfy, and the cap on how far a waypoint may be
        // pushed toward open space.
        public const float ClearanceRadius = 0.6f;

        // Left at 0.4 deliberately. A WIDER support probe is the permissive direction (floor is easier to
        // find, ledges/drops behave as before), so it is kept out of the openness change to hold the
        // regression surface down.
        const float SupportProbeRadius = 0.4f;

        // Sub-cell sample offsets in the XZ plane, tried in order when the cell centre itself is blocked.
        // Magnitudes stay under CellSize/2 so every sample is still genuinely inside its own cell.
        static readonly Vector3[] freeSampleOffsets =
        {
            new Vector3(0.30f, 0f, 0f),
            new Vector3(-0.30f, 0f, 0f),
            new Vector3(0f, 0f, 0.30f),
            new Vector3(0f, 0f, -0.30f),
            new Vector3(0.30f, 0f, 0.30f),
            new Vector3(0.30f, 0f, -0.30f),
            new Vector3(-0.30f, 0f, 0.30f),
            new Vector3(-0.30f, 0f, -0.30f),
        };
        const float SupportProbeDepthCells = 2f;
        const float MaxDropSearch = 60f;

        // Same tuned heuristic constants SeanceDungeonNavMesh.FindElevatorBetween already uses - not
        // shared code (this grid deliberately doesn't reference that file - see the file header
        // comment), just the same numbers, since they already proved reliable there.
        const float MinElevatorVerticalRatio = 0.8f;
        const float MinElevatorSpan = 3f;

        static SeanceDungeonGrid current;
        static int cachedDungeonID = int.MinValue;

        public static SeanceDungeonGrid Current
        {
            get { return current; }
        }

        // Mirrors SeanceDungeonNavMesh.EnsureBakedForCurrentDungeon's own lazy/rebuild-only-if-different
        // pattern - called once from SeanceEffect.DoEffect, right after the "must be in a dungeon" gate
        // passes. Recasting Seance repeatedly in the same dungeon visit never rebuilds the grid.
        public static void EnsureForCurrentDungeon()
        {
            DaggerfallDungeon dungeon = GetCurrentDungeon();
            if (dungeon == null)
                return;

            int currentID = dungeon.Summary.ID;
            if (current != null && currentID == cachedDungeonID)
                return;

            current = new SeanceDungeonGrid(dungeon);
            cachedDungeonID = currentID;
        }

        // Hooked from VoxsNecromancy.Init() alongside the existing OnTransitionDungeonInterior
        // subscriptions. Always invalidates - even re-entering the SAME dungeon rebuilds every
        // GameObject from scratch, so the collider instance IDs in the exclusion sets (and every
        // cached cell probed against them) are stale after any transition. This is unlike the legacy
        // NavMesh's bakedDungeonID pattern, which could safely survive a same-dungeon re-entry because
        // baked mesh data holds no object references.
        public static void OnDungeonTransition(DaggerfallDungeon newDungeon)
        {
            current = null;
            cachedDungeonID = int.MinValue;
        }

        static DaggerfallDungeon GetCurrentDungeon()
        {
            return GameManager.Instance.PlayerEnterExit != null
                ? GameManager.Instance.PlayerEnterExit.Dungeon
                : null;
        }

        Transform dungeonRoot;
        int blockingMask;
        HashSet<int> excludeFromOpen;
        HashSet<int> excludeFromSupport;
        List<Bounds> doorPassages;

        // Parallel to doorPassages, same indices. Diagnostic-only: nothing in the grid's own hot paths
        // reads it, but seance_doorcheck cannot say WHICH door built a volume without it.
        List<Component> doorPassageOwners;
        Dictionary<SeanceCell, SeanceCellRecord> cells;

        struct WaterBlock
        {
            public float MinX, MaxX, MinZ, MaxZ;
            public float SurfaceY;
        }
        List<WaterBlock> waterBlocks;

        // XZ footprint plus a generous margin, not a single exact cell - real testing found the
        // elevator action's own transform.position can sit a couple of cells away from where a player
        // actually stands at the shaft's edge (the platform model is narrower than the shaft cavity
        // itself), and the two resting Y positions can likewise miss the real floor level by close to a
        // cell's worth. See BuildElevatorTable/IsElevatorColumn for the margins applied.
        struct ElevatorColumn
        {
            public float MinX, MaxX, MinZ, MaxZ;
            public float MinY, MaxY;
        }
        List<ElevatorColumn> elevatorColumns;

        const float ElevatorFootprintMargin = 2.5f;
        const float ElevatorVerticalMargin = 2.5f;

        // A teleporter's trigger is a VOLUME of cells, not one snapped cell. Daggerfall's teleporters
        // are "red brick door" models (RDBLayout.redBrickDoorModelID == 72100) - fake brick walls the
        // player walks INTO. The engine explicitly denies them a DaggerfallActionDoor component
        // ("Always reject red brick doors, they are not action doors despite having DOR attached",
        // RDBLayout.IsActionDoor), they carry no translation/rotation so IsElevatorShaped is false, and
        // their ModelDescription is "DOR" not "TRP" - so NONE of BuildExclusionSets' four exclusions
        // cover them and their collider blocks the grid like any wall. That is correct physically (you
        // bump into a brick wall; the teleport is a side effect of touching it), but it means the cell
        // AT the teleporter is never open.
        //
        // The original single pre-snapped trigger cell came from TrySnapToOpenCell, which - finding the
        // teleporter's own cell blocked - fell through to an expanding shell search and took the first
        // open cell up to TeleportSnapRadius away. That cell is geometrically arbitrary (confirmed live:
        // a trigger at (6.4, 6.4, 12.8) snapped to cell (7,7,11), not (6,6,12)), and A* then had to land
        // on exactly it or the teleporter effectively did not exist. Stage 1 only ever "worked" where
        // that arbitrary snap happened to be reachable.
        //
        // Instead: every open+supported cell touching the teleporter's own collider bounds is a trigger.
        // That is what "walk into the brick wall from the floor in front of it" actually means, needs no
        // force-opening of solid geometry, and cannot miss by one cell.
        class TeleportEdge
        {
            public string Name;
            public Transform TriggerTransform;      // null once promoted from a Stage-2 gated obstacle
            public Vector3 TriggerPosition;
            public Vector3 DestinationPosition;
            public SeanceCell DestinationCell;
            public List<SeanceCell> TriggerCells;
        }
        List<TeleportEdge> teleportEdges;

        // Rebuilt from teleportEdges whenever trigger cells change - TryGetTeleportDestination runs per
        // node expansion during A*, so this must stay an O(1) lookup rather than a nested scan.
        Dictionary<SeanceCell, SeanceCell> teleportTriggerLookup = new Dictionary<SeanceCell, SeanceCell>();

        const float TeleportSnapRadius = 6f;

        // How far past the teleporter's own collider bounds to look for standable trigger cells. One
        // cell is enough to catch the floor immediately in front of the panel on either side; going
        // wider starts picking up cells around a corner that never touch the teleporter at all.
        const float TeleportTriggerBoundsMargin = 1f;

        // Stage 2 - every gated obstacle with at least one resolvable player-activatable root. Built
        // once at grid-init time from static chain data (which action links to which never changes
        // for a given dungeon visit); whether a specific obstacle has ACTUALLY been activated yet is
        // tracked by SeanceGhostGuide, not here.
        List<SeanceGatedObstacle> gatedObstacles;

        static Collider[] overlapBuffer = new Collider[16];
        static RaycastHit[] castBuffer = new RaycastHit[16];

        // Force-open volume around the CURRENT search's goal, set per-search rather than scanned once at
        // grid-init time like door passages, since the grid is built before any specific cast's target is
        // resolved. Same mechanism, same reasoning as door passages: a caged/barred quest target (or any
        // lever-gated enclosure a mod's own known limitations already accept routing around rather than
        // solving - see SeanceEffect's own header comment on teleporters/portcullises) should read as
        // reachable, the same way a closed trapdoor already does - the path leads the player TO the
        // obstacle; finding the actual way in (a lever, a key) is the player's job, not the ghost's.
        //
        // Exactly ONE is kept. These used to accumulate in a list that was never cleared, on the
        // reasoning that "an extra open pocket near an old target position is never wrong" - which turned
        // out to be false once searches started being aimed at arbitrary dead-end points: each stale
        // pocket permanently force-opened a 5m cube of whatever geometry happened to be there, and the
        // force-opened result got cached, so the ghost would happily hover inside solid rock or clip up
        // into a ceiling. Replacing the old passage also invalidates the cells it opened, so its effect
        // does not outlive it.
        bool hasGoalPassage;
        Bounds goalPassage;

        // Drops the current goal passage and re-probes the cells it was force-opening, so its effect does
        // not outlive it. Called at the start of every search - the passage is a last resort now, not a
        // standing rule (see SeanceVoxelPathfinder.Search).
        public void ClearGoalPassage()
        {
            if (!hasGoalPassage)
                return;

            Bounds stale = goalPassage;
            hasGoalPassage = false;
            InvalidateCells(stale.center, stale.extents.magnitude + CellSize);
        }

        // CURRENTLY UNUSED - kept only so the mechanism is discoverable, and deliberately left uncalled.
        // Force-opening a volume around the goal ALSO makes every unit edge inside it passable
        // (IsEdgeClear), which tunnels straight through any wall near the target. That produced a real bug:
        // at the Privateer's Hold exit the StartMarker sits inside the exit door's geometry, so the search
        // "succeeded" on a straight line through the wall in ~10 nodes and never led the player to the door.
        // The replacement is SeanceGhostGuide.TryLeadToClosestApproach - lead to the nearest REACHABLE point
        // instead, which serves a sealed-in target (exit marker, barred cage) correctly and cannot pass
        // through geometry. Do not re-enable this without solving that.
        // ClearGoalPassage still runs at the start of every search so no stale passage can survive.
        public void AddGoalPassage(Vector3 position, float radius)
        {
            Bounds replacement = new Bounds(position, Vector3.one * (radius * 2f));
            if (hasGoalPassage && replacement.center == goalPassage.center && replacement.size == goalPassage.size)
                return;

            if (hasGoalPassage)
            {
                // Drop the cells the outgoing passage force-opened, or its effect persists in the cache
                // long after the passage itself is gone. InvalidateCells (not InvalidateArea) - the
                // teleport-trigger refresh would re-enter this same bookkeeping mid-update.
                Bounds stale = goalPassage;
                hasGoalPassage = false;
                InvalidateCells(stale.center, stale.extents.magnitude + CellSize);
            }

            goalPassage = replacement;
            hasGoalPassage = true;

            // Cells inside the incoming passage may already be cached as blocked from before it existed.
            InvalidateCells(replacement.center, replacement.extents.magnitude + CellSize);
        }

        SeanceDungeonGrid(DaggerfallDungeon dungeon)
        {
            dungeonRoot = dungeon.transform;
            blockingMask = 1 << LayerMask.NameToLayer("Default");
            cells = new Dictionary<SeanceCell, SeanceCellRecord>(4096);

            BuildExclusionSets(dungeon);
            BuildWaterTable(dungeon);
            BuildElevatorTable(dungeon);
            BuildTeleportTable(dungeon);
            BuildGatedObstacleTable(dungeon);

            Debug.LogFormat(
                "Astrid's Guide to Necormancy: SeanceDungeonGrid initialized for dungeon {0} - {1} excluded-from-open " +
                "collider(s), {2} excluded-from-support collider(s), {3} door passage(s), {4} water block(s), " +
                "{5} elevator column(s), {6} active teleporter edge(s), {7} resolvable gated obstacle(s).",
                dungeon.name, excludeFromOpen.Count, excludeFromSupport.Count, doorPassages.Count,
                waterBlocks.Count, elevatorColumns.Count, teleportEdges.Count, gatedObstacles.Count);
        }

        void BuildExclusionSets(DaggerfallDungeon dungeon)
        {
            excludeFromOpen = new HashSet<int>();
            excludeFromSupport = new HashSet<int>();
            doorPassages = new List<Bounds>();
            doorPassageOwners = new List<Component>();

            DaggerfallActionDoor[] doors = dungeon.transform.GetComponentsInChildren<DaggerfallActionDoor>();
            for (int i = 0; i < doors.Length; i++)
            {
                AddColliders(doors[i].transform, excludeFromOpen);
                AddDoorPassage(doors[i]);
            }

            // "Special" doors are a completely separate class (DaggerfallActionDoorSpecial derives
            // from MonoBehaviour directly, NOT from DaggerfallActionDoor - confirmed in the engine
            // source) attached to lever-operated secret doors the player can't open by hand. Same
            // treatment as trapdoors: excluded so the path leads the player TO the door (they then
            // find the lever), rather than reading it as permanent solid wall.
            DaggerfallActionDoorSpecial[] specialDoors = dungeon.transform.GetComponentsInChildren<DaggerfallActionDoorSpecial>();
            for (int i = 0; i < specialDoors.Length; i++)
            {
                AddColliders(specialDoors[i].transform, excludeFromOpen);
                AddDoorPassage(specialDoors[i]);
            }

            DaggerfallAction[] actions = dungeon.transform.GetComponentsInChildren<DaggerfallAction>();
            for (int i = 0; i < actions.Length; i++)
            {
                DaggerfallAction action = actions[i];
                bool isTrapdoor = action.ModelDescription == "TRP";

                if (isTrapdoor)
                    AddColliders(action.transform, excludeFromOpen);

                // Only ELEVATOR-shaped movers are still blanket-excluded - riding one is a dedicated
                // mechanism (SeanceVoxelPathfinder's elevator edges) that never cares about the
                // platform's own mid-transit physical position. A non-elevator mover (a portcullis, a
                // sliding stone door, a swinging gate) used to get the SAME blanket exclusion, but real
                // testing found that let the ghost path straight through a still-closed, lever-locked
                // gate as if it weren't there - the player then got physically stuck at the real
                // (still solid) barrier while the ghost sailed on ahead. Those now read as genuinely
                // BLOCKING, matching their real current geometry, and become SeanceGatedObstacle
                // candidates instead (see BuildGatedObstacleTable) - the path only ever routes through
                // one after its actual chain has fired and its cached cells are invalidated.
                if (IsElevatorShaped(action))
                {
                    AddColliders(action.transform, excludeFromOpen);
                    AddColliders(action.transform, excludeFromSupport);
                }
            }
        }

        static bool IsElevatorShaped(DaggerfallAction action)
        {
            if (action.IsFlat)
                return false;

            Vector3 translation = action.ActionTranslation;
            float span = translation.magnitude;
            if (span < MinElevatorSpan)
                return false;

            float verticalRatio = span > 0.001f ? Mathf.Abs(translation.y) / span : 0f;
            return verticalRatio >= MinElevatorVerticalRatio;
        }

        static void AddColliders(Transform root, HashSet<int> set)
        {
            Collider[] colliders = root.GetComponentsInChildren<Collider>();
            for (int i = 0; i < colliders.Length; i++)
                set.Add(colliders[i].GetInstanceID());
        }

        // Every door gets an explicit force-open "passage" volume - the voxel-grid analogue of the
        // legacy NavMesh system's AddDoorFloorPatches (see SeanceDungeonNavMesh.cs), and needed for the
        // same underlying reason: doorway thresholds can't be trusted to sampling. Confirmed via
        // seance_gridcheck at a real door: the door PANEL's collider is excluded from openness, but the
        // FRAME around it is ordinary wall geometry, and a ~1.1m-wide opening vs. a 0.4m-radius probe at
        // world-grid-aligned cell centers means any doorway whose opening doesn't happen to line up with
        // the grid reads fully blocked (measured: center 0.25m from the frame edge). A player fits by
        // centering themselves in the opening; a fixed grid can't - so cells inside this volume are
        // force-open, and unit edges touching it skip the physics cast (see ProbeOpen/IsEdgeClear).
        //
        // The volume must be ORIENTATION-INDEPENDENT: the panel may be swung open at grid-build time
        // (a door's open/closed state persists in saves), and an open panel's renderer bounds sit
        // rotated ~90 degrees beside the opening - any "expand along the thin axis" logic then pushes
        // the volume along the wall instead of through the doorway, leaving the frame-blocked cells in
        // the opening uncovered (confirmed in a real dungeon: an already-open door's passage volume
        // missed the opening entirely and the doorway stayed sealed). So both horizontal axes get the
        // panel's larger horizontal extent plus a generous margin. The margin must cover the WORST
        // case reach from an open panel's center to the far side of its own opening: panel-offset
        // (~one panel width, since an open panel's center swings away from mid-opening) + wall
        // thickness + the far cell center - measured ~2.4m in a real dungeon where 1.2 left the far
        // doorway cells sealed by 0.05-0.15m. This does bleed ~2.4m along the wall on either side,
        // meaning a path can legally cut the frame corner right at a door - cosmetically imperfect,
        // but the player is walking through that same doorway anyway, and the long smoothing casts
        // never get the passage override so it can't compound.
        // Restored to 1.8 alongside reverting the closed-centre reconstruction - the two only made sense
        // together, since the small margin relied on the volume being correctly centred on the threshold.
        const float doorPassageMargin = 1.8f;
        const float doorPassageHeightExpand = 0.5f;

        // The passage volume above is deliberately generous (~4.9m across) because it must still cover a
        // doorway when the door PANEL has swung aside - the panel's bounds centre then sits ~2.4m from the
        // far side of its own opening, and a smaller reach left doorways sealed. That is the right size for
        // deciding OPENNESS.
        //
        // It is far too blunt for deciding whether an EDGE may ignore geometry: at that size, every wall
        // within ~2.45m of any door became crossable, and with 78 doors in one dungeon the ghost simply flew
        // through walls beside doorways. Edge overrides therefore use a shrunken "core" of the same volume.
        //
        // Most of the safety comes from requiring BOTH endpoints inside the core rather than either - that
        // alone stops an edge running from inside the doorway out through the wall beside it. The scale is
        // empirical: 0.45 sealed doorways outright, 1.0 let the ghost cross walls beside them.
        const float doorPassageCoreScale = 0.7f;

        bool IsInDoorPassageCore(Vector3 point)
        {
            for (int i = 0; i < doorPassages.Count; i++)
            {
                Bounds core = new Bounds(doorPassages[i].center, doorPassages[i].size * doorPassageCoreScale);
                if (core.Contains(point))
                    return true;
            }

            return false;
        }

        void AddDoorPassage(Component door)
        {
            Renderer doorRenderer = door.GetComponentInChildren<Renderer>();
            if (doorRenderer == null)
                return;

            // Reconstructing the panel's CLOSED centre (local offset re-applied through ClosedRotation) was
            // tried here and reverted: it is unverified maths on a load-bearing volume, and if it is wrong the
            // exemption lands somewhere arbitrary - which is worse than a volume that is merely too generous,
            // because an exemption over open wall lets the ghost through it. Revisit only with a diagnostic
            // that prints the computed centre next to the door's real position.
            Bounds bounds = doorRenderer.bounds;
            float halfExtent = Mathf.Max(bounds.extents.x, bounds.extents.z) + doorPassageMargin;

            doorPassages.Add(new Bounds(
                bounds.center,
                new Vector3(halfExtent * 2f, bounds.size.y + doorPassageHeightExpand * 2f, halfExtent * 2f)));
            doorPassageOwners.Add(door);
        }

        // Public: SeanceGhostGuide also uses this to lower its hover height while passing a door, so
        // the sprite threads the opening rather than the lintel.
        public bool IsInDoorPassage(Vector3 point)
        {
            for (int i = 0; i < doorPassages.Count; i++)
            {
                if (doorPassages[i].Contains(point))
                    return true;
            }

            return false;
        }

        // Used everywhere a cell/edge needs to check EITHER kind of forced-open volume (door passages
        // OR goal passages) - IsInDoorPassage stays door-only and public since SeanceGhostGuide uses
        // it specifically for its door-hover-dip visual, which has no equivalent reason to trigger
        // around a quest target.
        bool IsInAnyPassage(Vector3 point)
        {
            if (IsInDoorPassage(point))
                return true;

            return hasGoalPassage && goalPassage.Contains(point);
        }

        void BuildWaterTable(DaggerfallDungeon dungeon)
        {
            waterBlocks = new List<WaterBlock>();

            DFLocation.DungeonBlock[] blocks = dungeon.Summary.LocationData.Dungeon.Blocks;
            if (blocks == null)
                return;

            for (int i = 0; i < blocks.Length; i++)
            {
                DFLocation.DungeonBlock block = blocks[i];
                if (block.WaterLevel == 10000)
                    continue;

                // Mirrors DaggerfallDungeon.GetPlayerBlockIndex's own real rect construction exactly,
                // including its use of dungeonRoot.position.y (not .z) for the Z bound - this table
                // only needs to agree with what that method actually returns for a world position, not
                // with what the formula "should" say. DFU dungeon roots sit at world Y 0 in practice,
                // which is what makes that odd-looking axis reuse harmless there and here alike.
                float minX = dungeonRoot.position.x + block.X * RDBLayout.RDBSide;
                float minZ = dungeonRoot.position.y + block.Z * RDBLayout.RDBSide;

                // Same formula PlayerEnterExit.IsPlayerSwimming uses for its own world-Y submersion
                // test, minus the player-capsule-specific foot-offset correction term
                // (50*GlobalScale - 0.95f) - that term converts a CharacterController's own
                // transform.position (its capsule CENTER) into a foot position, which has no meaning
                // for an arbitrary world-space cell center like the ones this grid tests.
                float surfaceY = -block.WaterLevel * MeshReader.GlobalScale;

                WaterBlock waterBlock = new WaterBlock();
                waterBlock.MinX = minX;
                waterBlock.MaxX = minX + RDBLayout.RDBSide;
                waterBlock.MinZ = minZ;
                waterBlock.MaxZ = minZ + RDBLayout.RDBSide;
                waterBlock.SurfaceY = surfaceY;
                waterBlocks.Add(waterBlock);
            }
        }

        void BuildElevatorTable(DaggerfallDungeon dungeon)
        {
            elevatorColumns = new List<ElevatorColumn>();

            DaggerfallAction[] actions = dungeon.transform.GetComponentsInChildren<DaggerfallAction>();
            for (int i = 0; i < actions.Length; i++)
            {
                DaggerfallAction action = actions[i];
                if (action.IsFlat)
                    continue;

                Vector3 translation = action.ActionTranslation;
                float span = translation.magnitude;
                if (span < 0.5f)
                    continue;

                float verticalRatio = span > 0.001f ? Mathf.Abs(translation.y) / span : 0f;
                bool passesSpan = span >= MinElevatorSpan;
                bool passesRatio = verticalRatio >= MinElevatorVerticalRatio;

                if (passesSpan && passesRatio)
                {
                    float restY1 = action.StartingPosition.y;
                    float restY2 = (action.StartingPosition + translation).y;

                    // The action's own real footprint (renderer bounds), not just its transform point -
                    // a shaft a player/ghost stands in is typically wider than the platform model
                    // itself, plus a further fixed margin on top of that footprint (see the field
                    // declaration's own comment for why both the XZ footprint and the Y range need
                    // slack, confirmed via seance_gridcheck against a real shaft that missed by ~2
                    // cells in XZ and was sitting just outside the Y range entirely).
                    Renderer actionRenderer = action.GetComponentInChildren<Renderer>();
                    Bounds footprint = actionRenderer != null
                        ? actionRenderer.bounds
                        : new Bounds(action.transform.position, Vector3.one * CellSize);

                    ElevatorColumn column = new ElevatorColumn();
                    column.MinX = footprint.min.x - ElevatorFootprintMargin;
                    column.MaxX = footprint.max.x + ElevatorFootprintMargin;
                    column.MinZ = footprint.min.z - ElevatorFootprintMargin;
                    column.MaxZ = footprint.max.z + ElevatorFootprintMargin;
                    column.MinY = Mathf.Min(restY1, restY2);
                    column.MaxY = Mathf.Max(restY1, restY2);
                    elevatorColumns.Add(column);

                    Debug.LogFormat(
                        "Astrid's Guide to Necormancy: SeanceDungeonGrid elevator candidate '{0}' ACCEPTED - position {1}, " +
                        "footprint x[{2:F1},{3:F1}] z[{4:F1},{5:F1}] (margin included), span {6:F2}, " +
                        "verticalRatio {7:F2}, minY {8:F2}, maxY {9:F2}.",
                        action.name, action.transform.position, column.MinX, column.MaxX, column.MinZ, column.MaxZ,
                        span, verticalRatio, column.MinY, column.MaxY);
                }
                else
                {
                    // Logged for every non-trivial mover, not just the ones that pass - the threshold
                    // constants (MinElevatorSpan/MinElevatorVerticalRatio) were tuned against a
                    // different NavMesh-era dungeon and may not fit every real elevator shape; this is
                    // what lets a near-miss actually be seen instead of guessed at.
                    Debug.LogFormat(
                        "Astrid's Guide to Necormancy: SeanceDungeonGrid elevator candidate '{0}' REJECTED - position {1}, " +
                        "span {2:F2} (need >= {3:F2}), verticalRatio {4:F2} (need >= {5:F2}).",
                        action.name, action.transform.position, span, MinElevatorSpan, verticalRatio, MinElevatorVerticalRatio);
                }
            }
        }

        // Stage 1 of teleporter support - only ACTIVE teleporters become edges; a teleporter whose
        // TriggerFlag is None can only ever be fired via an incoming chain link (a lever elsewhere),
        // so it is not something the player could walk up and use right now - treated as if it does
        // not exist at all here (a later stage adds escorting the player to the enabling lever first).
        //
        // Detection is exactly ActionFlag == Teleport (confirmed via direct engine source reading -
        // NOT identifiable by model/texture, since the same visual can be a teleport, a damage trap,
        // or a no-op in classic data). Destination is action.NextObject.transform.position - already
        // world space, and NextObject is very often a bare editor marker with no DaggerfallAction
        // component of its own, so it must be read as a plain Transform, never assumed to itself be
        // another action. IsPlaying() (recursive down the action's own NextObject chain) and
        // isActiveAndEnabled are checked as a one-time snapshot at grid-build time, matching how this
        // grid already treats elevator/water state as fixed for the whole dungeon visit rather than
        // polled continuously.
        void BuildTeleportTable(DaggerfallDungeon dungeon)
        {
            teleportEdges = new List<TeleportEdge>();

            DaggerfallAction[] actions = dungeon.transform.GetComponentsInChildren<DaggerfallAction>();
            for (int i = 0; i < actions.Length; i++)
            {
                DaggerfallAction action = actions[i];
                if (action.ActionFlag != DFBlock.RdbActionFlags.Teleport)
                    continue;

                if (action.NextObject == null)
                {
                    Debug.LogFormat(
                        "Astrid's Guide to Necormancy: SeanceDungeonGrid teleporter '{0}' at {1} has no NextObject (destination) - skipped.",
                        action.name, action.transform.position);
                    continue;
                }

                if (!action.isActiveAndEnabled || action.IsPlaying())
                {
                    Debug.LogFormat(
                        "Astrid's Guide to Necormancy: SeanceDungeonGrid teleporter '{0}' at {1} REJECTED - inactive/mid-transit " +
                        "at grid-build time.", action.name, action.transform.position);
                    continue;
                }

                if (action.TriggerFlag == DFBlock.RdbTriggerFlags.None)
                {
                    Debug.LogFormat(
                        "Astrid's Guide to Necormancy: SeanceDungeonGrid teleporter '{0}' at {1} REJECTED - TriggerFlag is None " +
                        "(chain-gated, only reachable via another action's link - not player-usable directly).",
                        action.name, action.transform.position);
                    continue;
                }

                Vector3 triggerPosition = action.transform.position;
                Vector3 destinationPosition = action.NextObject.transform.position;

                SeanceCell destinationCell;

                // No support requirement on the destination - same reasoning as goal snapping (the
                // exit may drop into a pit; the existing descent rules take over from there).
                if (!TrySnapToOpenCell(destinationPosition, TeleportSnapRadius, false, out destinationCell))
                {
                    Debug.LogFormat(
                        "Astrid's Guide to Necormancy: SeanceDungeonGrid teleporter '{0}' REJECTED - destination position {1} " +
                        "did not snap to any open cell within {2}m.", action.name, destinationPosition, TeleportSnapRadius);
                    continue;
                }

                TeleportEdge edge = new TeleportEdge();
                edge.Name = action.name;
                edge.TriggerTransform = action.transform;
                edge.TriggerPosition = triggerPosition;
                edge.DestinationPosition = destinationPosition;
                edge.DestinationCell = destinationCell;
                edge.TriggerCells = CollectTeleportTriggerCells(action.transform, triggerPosition);
                teleportEdges.Add(edge);

                if (edge.TriggerCells.Count == 0)
                {
                    Debug.LogFormat(
                        "Astrid's Guide to Necormancy: SeanceDungeonGrid teleporter '{0}' at {1} has NO standable trigger cell " +
                        "yet - kept anyway, RefreshTeleportTriggerCells retries after any nearby geometry opens.",
                        action.name, triggerPosition);
                }

                Debug.LogFormat(
                    "Astrid's Guide to Necormancy: SeanceDungeonGrid teleporter '{0}' ACCEPTED - trigger {1} ({2} trigger cell(s)) -> " +
                    "destination {3} (cell {4}), TriggerFlag={5}.",
                    action.name, triggerPosition, edge.TriggerCells.Count, destinationPosition, destinationCell, action.TriggerFlag);
            }

            RebuildTeleportTriggerLookup();
        }

        // Every open+supported cell touching the teleporter's own collider bounds - see the TeleportEdge
        // comment for why this replaced a single arbitrarily-snapped cell. Falls back to the bare
        // position when the object has no collider at all (a promoted gated obstacle whose transform is
        // gone, or a bare editor marker).
        List<SeanceCell> CollectTeleportTriggerCells(Transform triggerTransform, Vector3 triggerPosition)
        {
            List<SeanceCell> result = new List<SeanceCell>();

            Bounds bounds = new Bounds(triggerPosition, Vector3.one * CellSize);
            HashSet<int> ownColliders = new HashSet<int>();
            if (triggerTransform != null)
            {
                Collider[] colliders = triggerTransform.GetComponentsInChildren<Collider>();
                bool foundAny = false;
                for (int i = 0; i < colliders.Length; i++)
                {
                    if (colliders[i] == null)
                        continue;

                    ownColliders.Add(colliders[i].GetInstanceID());

                    if (!foundAny)
                    {
                        bounds = colliders[i].bounds;
                        foundAny = true;
                    }
                    else
                    {
                        bounds.Encapsulate(colliders[i].bounds);
                    }
                }
            }

            Vector3 surfaceTarget = bounds.center;
            bounds.Expand(TeleportTriggerBoundsMargin * 2f);

            SeanceCell min = SeanceCell.FromWorld(bounds.min, CellSize);
            SeanceCell max = SeanceCell.FromWorld(bounds.max, CellSize);

            for (int x = min.X; x <= max.X; x++)
            {
                for (int y = min.Y; y <= max.Y; y++)
                {
                    for (int z = min.Z; z <= max.Z; z++)
                    {
                        SeanceCell candidate = new SeanceCell(x, y, z);
                        if (!IsOpen(candidate) || !HasSupport(candidate))
                            continue;

                        // Being NEAR the teleporter is not enough - the teleport fires on physically
                        // TOUCHING the brick door, so a cell only counts if nothing solid stands between
                        // it and the door's own surface. This is what makes a barrier parked in front of a
                        // teleporter (the classic bookcase-hiding-a-portal) correctly gate it: the cells
                        // beside the bookcase stay open and walkable, but none of them can reach the door,
                        // so the teleporter has no usable trigger until the bookcase moves. Confirmed live
                        // as a real regression when this check was missing - the ghost routed straight
                        // through a still-closed bookcase and never asked for the torch.
                        if (!CanReachTeleporterSurface(FreePosition(candidate), surfaceTarget, ownColliders))
                            continue;

                        result.Add(candidate);
                    }
                }
            }

            return result;
        }

        // True if a sweep from `from` toward the teleporter's own surface reaches one of its colliders
        // before anything else solid. Doors/movers already excluded from openness do not count as
        // blockers (an open action door in the way is not really in the way).
        bool CanReachTeleporterSurface(Vector3 from, Vector3 surfaceTarget, HashSet<int> ownColliders)
        {
            // No collider to reason about (bare editor marker, or a promoted obstacle whose transform is
            // gone) - fall back to the old proximity-only behaviour rather than rejecting everything.
            if (ownColliders.Count == 0)
                return true;

            Vector3 offset = surfaceTarget - from;
            float distance = offset.magnitude;
            if (distance < 0.01f)
                return true;

            Vector3 direction = offset / distance;

            // Deliberately thinner than ProbeRadius: this asks "can the door be touched from here", not
            // "can a body fit through here", and a full-width sweep grazes the door frame around almost
            // any real doorway.
            float castRadius = ProbeRadius * 0.5f;
            int hitCount = Physics.SphereCastNonAlloc(
                from, castRadius, direction, castBuffer, distance + CellSize, blockingMask, QueryTriggerInteraction.Ignore);

            float nearestOwn = float.MaxValue;
            float nearestBlocker = float.MaxValue;

            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = castBuffer[i].collider;
                if (hit == null)
                    continue;

                float hitDistance = castBuffer[i].distance;
                if (ownColliders.Contains(hit.GetInstanceID()))
                {
                    if (hitDistance < nearestOwn)
                        nearestOwn = hitDistance;
                }
                else if (!excludeFromOpen.Contains(hit.GetInstanceID()))
                {
                    if (hitDistance < nearestBlocker)
                        nearestBlocker = hitDistance;
                }
            }

            if (nearestOwn == float.MaxValue)
                return false;

            return nearestOwn <= nearestBlocker;
        }

        // Trigger cells are derived from live physics, so anything that changes nearby geometry (a gated
        // barrier sliding out of the way, in particular) can turn a teleporter that had NO standable
        // trigger cell into a usable one. InvalidateArea calls this for exactly that reason.
        public void RefreshTeleportTriggerCells()
        {
            if (teleportEdges == null)
                return;

            for (int i = 0; i < teleportEdges.Count; i++)
            {
                TeleportEdge edge = teleportEdges[i];
                edge.TriggerCells = CollectTeleportTriggerCells(edge.TriggerTransform, edge.TriggerPosition);
            }

            RebuildTeleportTriggerLookup();
        }

        // Debug aid for the seance_telecheck console command. Reports every teleport edge with its
        // trigger cells and their CURRENT open/support state, so "the ghost ignored a teleporter" can be
        // answered directly (no standable trigger cell / trigger cell unreachable / no edge at all)
        // instead of inferred from a pathfinder failure's aggregate node count.
        public string DebugDescribeTeleporters(Vector3 playerPosition)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            SeanceCell playerCell = SeanceCell.FromWorld(playerPosition, CellSize);

            sb.AppendFormat("Player at {0} -> cell {1}.", playerPosition, playerCell);

            SeanceCell playerTeleportDestination;
            sb.AppendFormat("\nPlayer's own cell is {0}a teleport trigger.",
                TryGetTeleportDestination(playerCell, out playerTeleportDestination) ? "" : "NOT ");

            sb.AppendFormat("\n{0} teleport edge(s), {1} distinct trigger cell(s) in lookup:",
                teleportEdges != null ? teleportEdges.Count : 0, teleportTriggerLookup.Count);

            if (teleportEdges == null)
                return sb.ToString();

            for (int i = 0; i < teleportEdges.Count; i++)
            {
                TeleportEdge edge = teleportEdges[i];
                float distance = Vector3.Distance(edge.TriggerPosition, playerPosition);

                sb.AppendFormat(
                    "\n  [{0}] '{1}' trigger {2} ({3:F1}m away) -> dest {4} (cell {5}, IsOpen={6}); {7} trigger cell(s)",
                    i, edge.Name, edge.TriggerPosition, distance, edge.DestinationPosition, edge.DestinationCell,
                    IsOpen(edge.DestinationCell), edge.TriggerCells.Count);

                if (edge.TriggerCells.Count == 0)
                {
                    sb.Append("\n        (none standable - this teleporter is currently unusable by the ghost)");
                    continue;
                }

                for (int c = 0; c < edge.TriggerCells.Count; c++)
                {
                    SeanceCell cell = edge.TriggerCells[c];
                    sb.AppendFormat("\n        {0} IsOpen={1} HasSupport={2}{3}",
                        cell, IsOpen(cell), HasSupport(cell), cell.Equals(playerCell) ? "  <-- player is here" : "");
                }
            }

            return sb.ToString();
        }

        // Given a reachable set (SeanceVoxelPathfinder.GetExpandedCells), reports which teleporters that
        // region can actually get to and which it cannot. This is the question that matters when a route
        // fails in a teleporter-maze dungeon: the ghost is not stuck because of local geometry, it is
        // stuck because the pocket it is in contains no onward teleporter trigger.
        public string DescribeTeleportReachability(HashSet<SeanceCell> reachable)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            if (teleportEdges == null)
                return "No teleport edges.";

            sb.Append("\nTeleporters REACHABLE from this region:");
            bool anyReachable = false;
            for (int i = 0; i < teleportEdges.Count; i++)
            {
                TeleportEdge edge = teleportEdges[i];
                int reachableCells = 0;
                for (int c = 0; c < edge.TriggerCells.Count; c++)
                {
                    if (reachable.Contains(edge.TriggerCells[c]))
                        reachableCells++;
                }

                if (reachableCells > 0)
                {
                    anyReachable = true;
                    sb.AppendFormat("\n  [{0}] trigger {1} -> dest {2} (cell {3}) - {4}/{5} trigger cell(s) reachable, dest reached={6}",
                        i, edge.TriggerPosition, edge.DestinationPosition, edge.DestinationCell,
                        reachableCells, edge.TriggerCells.Count, reachable.Contains(edge.DestinationCell));
                }
            }
            if (!anyReachable)
                sb.Append(" none.");

            sb.Append("\nTeleporters NOT reachable:");
            bool anyUnreachable = false;
            for (int i = 0; i < teleportEdges.Count; i++)
            {
                TeleportEdge edge = teleportEdges[i];
                bool any = false;
                for (int c = 0; c < edge.TriggerCells.Count; c++)
                {
                    if (reachable.Contains(edge.TriggerCells[c]))
                    {
                        any = true;
                        break;
                    }
                }

                if (!any)
                {
                    anyUnreachable = true;
                    sb.AppendFormat("\n  [{0}] trigger {1} (cell {2}) -> dest {3}; {4} trigger cell(s)",
                        i, edge.TriggerPosition, SeanceCell.FromWorld(edge.TriggerPosition, CellSize),
                        edge.DestinationPosition, edge.TriggerCells.Count);
                }
            }
            if (!anyUnreachable)
                sb.Append(" none.");

            return sb.ToString();
        }

        void RebuildTeleportTriggerLookup()
        {
            teleportTriggerLookup.Clear();
            if (teleportEdges == null)
                return;

            for (int i = 0; i < teleportEdges.Count; i++)
            {
                TeleportEdge edge = teleportEdges[i];
                for (int c = 0; c < edge.TriggerCells.Count; c++)
                {
                    // First edge to claim a cell wins - two teleporters close enough to share a standable
                    // cell is pathological, and picking either is better than throwing.
                    if (!teleportTriggerLookup.ContainsKey(edge.TriggerCells[c]))
                        teleportTriggerLookup.Add(edge.TriggerCells[c], edge.DestinationCell);
                }
            }
        }

        // Stage 2: identifies every teleporter/door/portcullis/trapdoor whose current state gates the
        // route, but which has at least one player-activatable action (a lever, switch, or
        // password-input object) somewhere in its own action chain that can open it. An obstacle with
        // no resolvable root at all is simply never added here - it stays genuinely impassable, no
        // different from any other permanent wall (matches the Stage 1 "treat as nonexistent" fallback
        // for teleporters, and is the correct terminal fallback for doors/trapdoors too, since a
        // barrier with no lever anywhere is not something the ghost could ever lead the player past).
        void BuildGatedObstacleTable(DaggerfallDungeon dungeon)
        {
            gatedObstacles = new List<SeanceGatedObstacle>();

            DaggerfallAction[] actions = dungeon.transform.GetComponentsInChildren<DaggerfallAction>();
            Dictionary<DaggerfallAction, List<DaggerfallAction>> predecessors = BuildActionPredecessorMap(actions);

            for (int i = 0; i < actions.Length; i++)
            {
                DaggerfallAction action = actions[i];

                if (action.ActionFlag == DFBlock.RdbActionFlags.Teleport)
                {
                    TryRegisterTeleporterObstacle(action, predecessors, dungeon.name);
                    continue;
                }

                if (action.ModelDescription == "TRP")
                {
                    TryRegisterTrapdoorObstacle(action, predecessors, dungeon.name);
                    continue;
                }

                bool moves = !action.IsFlat
                    && (action.ActionTranslation.magnitude > 0.05f || action.ActionRotation.magnitude > 0.5f);
                if (moves && !IsElevatorShaped(action))
                    TryRegisterDoorObstacle(action, predecessors, dungeon.name);
            }
        }

        // Forward chain links only (A.NextObject == B means triggering A eventually activates B, via
        // DaggerfallAction.Play()'s own ActivateNext() calling B.Receive(_, TriggerTypes.ActionObject) -
        // confirmed by direct engine source reading). This reverses that into "B's predecessors are
        // every A that points to it" so the obstacle's own chain can be walked BACKWARD to find what
        // activates it. A single flat map over every action in the dungeon is exactly equivalent to
        // building one map per RDB block - action links are confirmed block-local in DFU's own data,
        // so cross-block entries simply never occur.
        static Dictionary<DaggerfallAction, List<DaggerfallAction>> BuildActionPredecessorMap(DaggerfallAction[] actions)
        {
            Dictionary<DaggerfallAction, List<DaggerfallAction>> predecessors = new Dictionary<DaggerfallAction, List<DaggerfallAction>>();

            for (int i = 0; i < actions.Length; i++)
            {
                DaggerfallAction action = actions[i];
                if (action.NextObject == null)
                    continue;

                // The chain target is very often a bare editor marker with no DaggerfallAction of its
                // own (e.g. a teleporter's own destination) - only a target that can itself receive
                // and propagate a trigger belongs in this map.
                DaggerfallAction next = action.NextObject.GetComponent<DaggerfallAction>();
                if (next == null)
                    continue;

                List<DaggerfallAction> list;
                if (!predecessors.TryGetValue(next, out list))
                {
                    list = new List<DaggerfallAction>();
                    predecessors[next] = list;
                }
                list.Add(action);
            }

            return predecessors;
        }

        // A player-activatable action is one with a REAL physical trigger (Direct/WalkInto/Attack/etc)
        // - TriggerFlag == None means it can only ever be reached via an incoming chain link, never
        // directly by the player. Same predicate Stage 1 already uses to decide teleporter activeness.
        static bool IsPlayerActivatable(DaggerfallAction action)
        {
            return action.TriggerFlag != DFBlock.RdbTriggerFlags.None;
        }

        // Chains are small and block-local in practice; this is a generous safety net, not a tuned
        // real-world limit.
        const int ChainDepthCap = 16;

        // Reverse-BFS from the obstacle up through its own predecessors, collecting EVERY
        // player-activatable action reached - not just the first, since a real (if uncommon) case
        // gates one obstacle behind multiple levers that must all be found. The visited set makes
        // this naturally cycle-safe (a cycle just stops contributing new nodes rather than looping
        // forever) without needing a separately-tracked "is this cyclic" flag. Deliberately does NOT
        // verify a found root is physically reachable without crossing the obstacle itself (the
        // classic "lever behind its own locked door" trap) - SeanceGhostGuide's own LeadToLever leg
        // already answers that far more reliably, by simply trying to path there for real, than any
        // offline spatial guess made from chain data alone could.
        List<DaggerfallAction> FindPlayerActivatableRoots(
            DaggerfallAction obstacle, Dictionary<DaggerfallAction, List<DaggerfallAction>> predecessors, string dungeonName)
        {
            List<DaggerfallAction> roots = new List<DaggerfallAction>();
            HashSet<DaggerfallAction> visited = new HashSet<DaggerfallAction>();
            Queue<DaggerfallAction> queue = new Queue<DaggerfallAction>();
            Queue<int> depths = new Queue<int>();

            visited.Add(obstacle);
            queue.Enqueue(obstacle);
            depths.Enqueue(0);

            bool hitDepthCap = false;

            while (queue.Count > 0)
            {
                DaggerfallAction current = queue.Dequeue();
                int depth = depths.Dequeue();

                if (depth >= ChainDepthCap)
                {
                    hitDepthCap = true;
                    continue;
                }

                List<DaggerfallAction> preds;
                if (!predecessors.TryGetValue(current, out preds))
                    continue;

                for (int p = 0; p < preds.Count; p++)
                {
                    DaggerfallAction predecessor = preds[p];
                    if (visited.Contains(predecessor))
                        continue;

                    visited.Add(predecessor);

                    if (IsPlayerActivatable(predecessor))
                        roots.Add(predecessor);

                    // Keep walking backward through it too, even if it's already a root itself - a
                    // chain can have more than one player-activatable point along the same branch.
                    queue.Enqueue(predecessor);
                    depths.Enqueue(depth + 1);
                }
            }

            if (roots.Count == 0)
            {
                Debug.LogFormat(
                    "Astrid's Guide to Necormancy: SeanceDungeonGrid gave up resolving chain for '{0}' in dungeon '{1}' - " +
                    "no player-activatable root found ({2} action(s) examined backward, depth cap hit: {3}). " +
                    "This obstacle will be treated as unopenable.",
                    obstacle.name, dungeonName, visited.Count, hitDepthCap);
            }

            return roots;
        }

        void TryRegisterTeleporterObstacle(
            DaggerfallAction action, Dictionary<DaggerfallAction, List<DaggerfallAction>> predecessors, string dungeonName)
        {
            // TriggerFlag != None is Stage 1's own "already active" case, handled entirely by
            // BuildTeleportTable already - not a gated obstacle at all.
            if (action.TriggerFlag != DFBlock.RdbTriggerFlags.None || action.NextObject == null)
                return;

            List<DaggerfallAction> roots = FindPlayerActivatableRoots(action, predecessors, dungeonName);
            if (roots.Count == 0)
                return;

            SeanceGatedObstacle obstacle = new SeanceGatedObstacle();
            obstacle.Kind = SeanceGatedObstacle.KindTeleporter;
            obstacle.ObstacleAction = action;
            obstacle.ObstaclePosition = action.transform.position;
            obstacle.TeleportDestinationPosition = action.NextObject.transform.position;
            obstacle.Roots = roots;
            gatedObstacles.Add(obstacle);

            Debug.LogFormat(
                "Astrid's Guide to Necormancy: SeanceDungeonGrid gated teleporter '{0}' RESOLVED - {1} root(s).",
                action.name, roots.Count);
        }

        void TryRegisterTrapdoorObstacle(
            DaggerfallAction action, Dictionary<DaggerfallAction, List<DaggerfallAction>> predecessors, string dungeonName)
        {
            // A directly player-activatable trapdoor is already handled as ordinary always-open
            // terrain (see BuildExclusionSets) - no escort needed at all.
            if (action.TriggerFlag != DFBlock.RdbTriggerFlags.None)
                return;

            // Registered regardless of whether a root was found (unlike Teleporter/Door) - a
            // trapdoor is always physically walkable already (see BuildExclusionSets' own comment),
            // so the ghost's route naturally reaches it whether or not its chain is resolvable. It
            // still needs to be discoverable here so SeanceGhostGuide recognizes "I've arrived at a
            // gated trapdoor" and applies the baseline hover-and-wait fallback rather than silently
            // walking over it with no acknowledgment at all when no lever could be found.
            List<DaggerfallAction> roots = FindPlayerActivatableRoots(action, predecessors, dungeonName);

            SeanceGatedObstacle obstacle = new SeanceGatedObstacle();
            obstacle.Kind = SeanceGatedObstacle.KindTrapdoor;
            obstacle.ObstacleAction = action;
            obstacle.ObstaclePosition = action.transform.position;
            obstacle.Roots = roots;
            gatedObstacles.Add(obstacle);

            Debug.LogFormat(
                "Astrid's Guide to Necormancy: SeanceDungeonGrid gated trapdoor '{0}' registered - {1} root(s){2}.",
                action.name, roots.Count,
                roots.Count == 0 ? " (none - ghost will hover if its route ever reaches it)" : "");
        }

        void TryRegisterDoorObstacle(
            DaggerfallAction action, Dictionary<DaggerfallAction, List<DaggerfallAction>> predecessors, string dungeonName)
        {
            // A directly player-activatable barrier (TriggerFlag != None, e.g. a portcullis you can
            // walk into/click yourself) is its own single root - the escort's "lever leg" simply ends
            // up zero-length, since the root IS the obstacle. Otherwise, walk the chain backward.
            List<DaggerfallAction> roots = IsPlayerActivatable(action)
                ? new List<DaggerfallAction> { action }
                : FindPlayerActivatableRoots(action, predecessors, dungeonName);

            if (roots.Count == 0)
                return; // no way to open it at all - stays genuinely blocking, like any other wall

            SeanceGatedObstacle obstacle = new SeanceGatedObstacle();
            obstacle.Kind = SeanceGatedObstacle.KindDoor;
            obstacle.ObstacleAction = action;
            obstacle.ObstaclePosition = action.transform.position;
            obstacle.Roots = roots;
            gatedObstacles.Add(obstacle);

            Debug.LogFormat(
                "Astrid's Guide to Necormancy: SeanceDungeonGrid gated door/portcullis '{0}' registered at {1} - {2} root(s){3}.",
                action.name, obstacle.ObstaclePosition, roots.Count, roots.Count == 1 && roots[0] == action ? " (self-triggerable)" : "");
        }

        // One-directional by construction - a two-way portal is two separate teleport action objects
        // in the real data, so no reverse edge is ever synthesized here.
        public bool TryGetTeleportDestination(SeanceCell cell, out SeanceCell destination)
        {
            return teleportTriggerLookup.TryGetValue(cell, out destination);
        }

        // Promotes a just-activated gated teleporter into a real, always-available edge for the rest
        // of this dungeon visit - from this point on TryGetTeleportDestination no longer distinguishes
        // how it came to be usable, it just behaves like a Stage-1 already-active teleporter.
        public void ActivateTeleportObstacle(SeanceGatedObstacle obstacle)
        {
            SeanceCell destinationCell;
            if (!TrySnapToOpenCell(obstacle.TeleportDestinationPosition, TeleportSnapRadius, false, out destinationCell))
                return;

            Transform triggerTransform = obstacle.ObstacleAction != null ? obstacle.ObstacleAction.transform : null;

            TeleportEdge edge = new TeleportEdge();
            edge.Name = obstacle.ObstacleAction != null ? obstacle.ObstacleAction.name : "(activated gated teleporter)";
            edge.TriggerTransform = triggerTransform;
            edge.TriggerPosition = obstacle.ObstaclePosition;
            edge.DestinationPosition = obstacle.TeleportDestinationPosition;
            edge.DestinationCell = destinationCell;
            edge.TriggerCells = CollectTeleportTriggerCells(triggerTransform, obstacle.ObstaclePosition);
            teleportEdges.Add(edge);

            RebuildTeleportTriggerLookup();
        }

        // Every known gated obstacle (Stage 2) with at least one resolvable player-activatable root -
        // SeanceGhostGuide scans this after its own search fails (looking for the nearest one to try
        // escorting to) and, for Trapdoor kind specifically, checks proximity while following an
        // already-valid route that happens to cross one.
        public List<SeanceGatedObstacle> GatedObstacles
        {
            get { return gatedObstacles; }
        }

        // Clears cached cell/edge records within radius of a position, forcing fresh physics probes
        // next time they're queried - used once a gated obstacle's chain has genuinely fired (its real
        // collider has physically moved/opened), so a stale "still blocked" cache entry doesn't linger.
        // Radius should comfortably cover the obstacle's own footprint PLUS at least one neighboring
        // cell in every direction: an edge cached on a NEIGHBOR cell (just outside a tighter radius)
        // pointing INTO the obstacle's own cells would otherwise still read as stale "known blocked"
        // even after the obstacle's own records are cleared, since edges are cached per-cell on BOTH
        // ends (see IsEdgeClear).
        public void InvalidateArea(Vector3 position, float radius)
        {
            InvalidateCells(position, radius);

            // Geometry here just changed, which can turn a teleporter that previously had no standable
            // trigger cell (its approach blocked by the barrier that just opened) into a usable one.
            RefreshTeleportTriggerCells();
        }

        void InvalidateCells(Vector3 position, float radius)
        {
            List<SeanceCell> toRemove = new List<SeanceCell>();
            float radiusSq = radius * radius;

            foreach (KeyValuePair<SeanceCell, SeanceCellRecord> pair in cells)
            {
                Vector3 cellCenter = pair.Key.ToWorldCenter(CellSize);
                if ((cellCenter - position).sqrMagnitude <= radiusSq)
                    toRemove.Add(pair.Key);
            }

            for (int i = 0; i < toRemove.Count; i++)
                cells.Remove(toRemove[i]);
        }

        // Debug aid for SeanceGridCheck console command - reports the given world position's cell
        // state plus every elevator column detected in this dungeon, so a suspect shaft's actual
        // recorded Y range/cell can be compared directly against where the player is standing.
        public string DebugDescribe(Vector3 worldPosition)
        {
            SeanceCell cell = SeanceCell.FromWorld(worldPosition, CellSize);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            Vector3 cellCentre = cell.ToWorldCenter(CellSize);
            Vector3 free = FreePosition(cell);
            sb.AppendFormat(
                "Position {0} -> cell {1} (center {2}). IsOpen={3} HasSupport={4} IsWater={5} IsElevatorColumn={6} " +
                "InAnyPassage={7}.",
                worldPosition, cell, cellCentre, IsOpen(cell), HasSupport(cell), IsWater(cell),
                IsElevatorColumn(cell), IsInAnyPassage(cellCentre));
            sb.AppendFormat("\nProbe radius {0:F3}. Free position {1}{2}.",
                ProbeRadius, free,
                (free - cellCentre).sqrMagnitude > 0.0001f ? " (OFF-CENTRE - only fits beside the cell centre)" : " (cell centre)");

            // ALWAYS reported, even for a cell that reads open. Blocker lines used to print only for closed
            // cells, which meant a force-opened cell (inside a door passage) hid whatever was actually
            // standing in it - exactly the case where knowing matters most. A lever-gated cage inside a
            // doorway looked like empty floor and cost a full diagnosis round to identify.
            sb.Append("\nGeometry overlapping this cell (regardless of open/closed):");
            DescribeBlockers(free, sb);

            // Water blocks whose XZ footprint contains this position (regardless of the Y test) - so
            // we can see the RAW SurfaceY/depth-cap numbers directly rather than just the pass/fail
            // IsWater result above.
            int matchingWaterBlocks = 0;
            for (int i = 0; i < waterBlocks.Count; i++)
            {
                WaterBlock block = waterBlocks[i];
                if (worldPosition.x < block.MinX || worldPosition.x > block.MaxX
                    || worldPosition.z < block.MinZ || worldPosition.z > block.MaxZ)
                    continue;

                matchingWaterBlocks++;
                sb.AppendFormat(
                    "\nWater block [{0}]: SurfaceY={1:F2}, depth-cap floor={2:F2} (position.y={3:F2} is " +
                    "{4}surface, {5}cap floor).",
                    i, block.SurfaceY, block.SurfaceY - MaxWaterDepth, worldPosition.y,
                    worldPosition.y < block.SurfaceY ? "below " : "above ",
                    worldPosition.y >= block.SurfaceY - MaxWaterDepth ? "above " : "below ");
            }
            if (matchingWaterBlocks == 0)
                sb.Append("\nNo water block's XZ footprint contains this position.");

            // Straight down and up, using the SAME cache/probe path the pathfinder itself uses - the
            // exact query that matters for "can the ghost swim/step through here."
            SeanceCell below = cell.Offset(0, -1, 0);
            SeanceCell above = cell.Offset(0, 1, 0);
            sb.AppendFormat(
                "\nVertical: below cell {0} IsOpen={1} IsWater={2} HasSupport={3} IsEdgeClear={4}; " +
                "above cell {5} IsOpen={6} IsWater={7} IsEdgeClear={8}.",
                below, IsOpen(below), IsWater(below), HasSupport(below), IsEdgeClear(cell, below),
                above, IsOpen(above), IsWater(above), IsEdgeClear(cell, above));

            // Per-direction breakdown at the SAME level - lets an ordinary-looking floor tile that
            // still fails to path be diagnosed immediately (which specific neighbor(s)/edge(s) are
            // rejecting the move) rather than only ever seeing the pathfinder's own aggregate "expanded
            // N nodes, failed" result.
            string[] directionNames = { "+X", "-X", "+Z", "-Z", "+X+Z", "+X-Z", "-X+Z", "-X-Z" };
            int[] ddx = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] ddz = { 0, 0, 1, -1, 1, -1, 1, -1 };

            sb.Append("\nHorizontal neighbors (same level):");
            for (int i = 0; i < 8; i++)
            {
                SeanceCell neighbor = cell.Offset(ddx[i], 0, ddz[i]);
                bool open = IsOpen(neighbor);
                sb.AppendFormat("\n  {0}: cell {1} IsOpen={2} HasSupport={3} IsEdgeClear={4}",
                    directionNames[i], neighbor, open, HasSupport(neighbor), IsEdgeClear(cell, neighbor));

                if (!open)
                    DescribeBlockers(neighbor.ToWorldCenter(CellSize), sb);
            }

            int nearbyPassages = 0;
            for (int i = 0; i < doorPassages.Count; i++)
            {
                Bounds passage = doorPassages[i];
                if (passage.SqrDistance(worldPosition) <= 25f)
                {
                    if (nearbyPassages == 0)
                        sb.Append("\nDoor passages within 5m:");
                    sb.AppendFormat("\n  center {0} size {1}", passage.center, passage.size);
                    nearbyPassages++;
                }
            }
            if (nearbyPassages == 0)
                sb.Append("\nNo door passages within 5m.");

            if (hasGoalPassage && goalPassage.SqrDistance(worldPosition) <= 25f)
                sb.AppendFormat("\nGoal passage within 5m: center {0} size {1}", goalPassage.center, goalPassage.size);
            else if (hasGoalPassage)
                sb.AppendFormat("\nNo goal passage within 5m (current one is at {0}).", goalPassage.center);
            else
                sb.Append("\nNo goal passage set.");

            sb.AppendFormat("\n{0} elevator column(s) detected in this dungeon:", elevatorColumns.Count);
            for (int i = 0; i < elevatorColumns.Count; i++)
            {
                ElevatorColumn c = elevatorColumns[i];
                sb.AppendFormat("\n  [{0}] x[{1:F1},{2:F1}] z[{3:F1},{4:F1}] minY={5:F2} maxY={6:F2}",
                    i, c.MinX, c.MaxX, c.MinZ, c.MaxZ, c.MinY, c.MaxY);
            }

            return sb.ToString();
        }

        // Debug aid: identifies WHAT is physically blocking a closed cell - collider name plus which
        // door/action component (if any) lives on it, and whether the exclusion sets already know
        // about it. This is what turns "IsOpen=False" from a dead end into an actionable answer
        // (e.g. "blocked by a DaggerfallActionDoorSpecial we never collected").
        void DescribeBlockers(Vector3 center, System.Text.StringBuilder sb)
        {
            int hitCount = Physics.OverlapSphereNonAlloc(center, ProbeRadius, overlapBuffer, blockingMask, QueryTriggerInteraction.Ignore);
            int reported = 0;

            for (int i = 0; i < hitCount && reported < 3; i++)
            {
                Collider hit = overlapBuffer[i];
                if (hit == null)
                    continue;

                string kind = "static";
                DaggerfallActionDoor door = hit.GetComponentInParent<DaggerfallActionDoor>();
                DaggerfallActionDoorSpecial specialDoor = hit.GetComponentInParent<DaggerfallActionDoorSpecial>();
                DaggerfallAction action = hit.GetComponentInParent<DaggerfallAction>();
                if (door != null)
                    kind = "ActionDoor(open=" + door.IsOpen + ")";
                else if (specialDoor != null)
                    kind = "ActionDoorSpecial(open=" + specialDoor.IsOpen + ")";
                else if (action != null)
                    kind = string.Format("Action(desc={0} trans={1:F1} rot={2:F0})",
                        action.ModelDescription, action.ActionTranslation.magnitude, action.ActionRotation.magnitude);

                sb.AppendFormat("\n      blocker: '{0}' [{1}] excludedFromOpen={2}",
                    hit.name, kind, excludeFromOpen.Contains(hit.GetInstanceID()));
                reported++;
            }

            // Neutral wording: this helper is used both for a cell reported CLOSED (where finding nothing
            // means the cached result is stale) and for the always-on overlap report (where finding nothing
            // simply means the cell is genuinely empty).
            if (reported == 0)
                sb.Append("\n      (nothing overlapping this position right now - if the cell was reported " +
                          "CLOSED then its cached result is stale and the geometry has since moved or opened)");
        }

        SeanceCellRecord GetOrCreateRecord(SeanceCell cell)
        {
            SeanceCellRecord record;
            if (cells.TryGetValue(cell, out record))
                return record;

            Vector3 worldCenter = cell.ToWorldCenter(CellSize);
            record = new SeanceCellRecord();
            Vector3 freePosition;
            record.IsOpen = TryProbeOpen(worldCenter, out freePosition);
            record.FreePosition = freePosition;

            // Both probed from the position the ghost would actually occupy, not the cell centre - a cell
            // that is only open off-centre must have its floor and water tested there too, or it reads as
            // open-but-unsupported and no walkable edge is ever built through it.
            record.IsWater = ProbeWater(freePosition);

            // Deliberately does NOT require the player's full standing capsule to fit. That was tried and
            // reverted: gating walkability on a 1.8m floor-anchored capsule closed the staircase arch in
            // Privateer's Hold outright (the starter dungeon - search collapsed from 9268 expanded nodes to
            // 697, stranded 31m short), because real Daggerfall arches are low as well as narrow yet the
            // player traverses them fine. Clearance is instead handled by SeanceGhostGuide's probe-radius
            // ladder, which prefers a route with room to spare and only accepts a tighter one when nothing
            // else exists. PlayerCapsuleFits survives as a DIAGNOSTIC (seance_pathcheck annotates waypoints
            // the player could not stand in), which is where it earns its keep without breaking routes.
            record.HasSupport = ProbeSupport(freePosition);

            // One extra CheckSphere per cell, cached alongside the rest. Only meaningful where the cell is
            // open at all.
            record.IsRoomy = record.IsOpen && IsPositionRoomy(freePosition);
            cells[cell] = record;
            return record;
        }

        // The position inside this cell the ghost actually fits at - the cell centre for the overwhelming
        // majority of cells, an off-centre sample where geometry intrudes (see SeanceCellRecord.
        // FreePosition). Callers that need a world position for a cell should prefer this over
        // ToWorldCenter so movement, edge sweeps and waypoints all agree with what was probed.
        public Vector3 FreePosition(SeanceCell cell)
        {
            return GetOrCreateRecord(cell).FreePosition;
        }

        // Colliders THIS mechanism added to excludeFromOpen, so releasing a hypothetical never removes an
        // exclusion that was there for another reason (a trapdoor, an elevator platform).
        List<int> hypotheticalOpenAdded = new List<int>();
        Vector3 hypotheticalOpenPosition;
        bool hypotheticalOpenActive;

        // Force-releases any hypothetical left applied. This matters because the grid OUTLIVES the ghost: if
        // a cast is interrupted (recast, spell expiry, dungeon exit) between applying and releasing, the
        // barrier would stay excluded from blocking for every later cast in this dungeon - i.e. the ghost
        // would happily route through a solid portcullis forever after. Cheap and idempotent, so it is safe
        // to call defensively before starting any new evaluation.
        public void ClearHypotheticalOpen()
        {
            if (!hypotheticalOpenActive)
                return;

            for (int i = 0; i < hypotheticalOpenAdded.Count; i++)
                excludeFromOpen.Remove(hypotheticalOpenAdded[i]);

            hypotheticalOpenAdded.Clear();
            hypotheticalOpenActive = false;
            InvalidateArea(hypotheticalOpenPosition, hypotheticalOpenInvalidateRadius);

            Debug.Log("Astrid's Guide to Necormancy: SeanceDungeonGrid released a leftover hypothetical-open barrier.");
        }
        const float hypotheticalOpenInvalidateRadius = 6f;

        // Temporarily treats a gated obstacle as non-blocking, so "what would the route be if this were
        // open?" can actually be measured rather than guessed. Must be paired with a matching false call.
        // The cell cache is invalidated on BOTH transitions, since every cached openness/edge result near the
        // obstacle changes meaning in each direction.
        public void SetHypotheticalOpen(SeanceGatedObstacle obstacle, bool open)
        {
            if (obstacle == null || obstacle.ObstacleAction == null)
                return;

            if (open)
            {
                // Never stack two hypotheticals - release any previous one first, or its colliders would be
                // orphaned in excludeFromOpen with no record of how to take them back out.
                ClearHypotheticalOpen();

                Collider[] colliders = obstacle.ObstacleAction.transform.GetComponentsInChildren<Collider>();
                for (int i = 0; i < colliders.Length; i++)
                {
                    if (colliders[i] == null)
                        continue;

                    int id = colliders[i].GetInstanceID();
                    if (excludeFromOpen.Add(id))
                        hypotheticalOpenAdded.Add(id);
                }

                hypotheticalOpenPosition = obstacle.ObstaclePosition;
                hypotheticalOpenActive = true;
                InvalidateArea(obstacle.ObstaclePosition, hypotheticalOpenInvalidateRadius);
            }
            else
            {
                ClearHypotheticalOpen();
            }
        }

        // PlayerAdvanced.prefab's CharacterController height. The grid's normal openness test is a single
        // sphere at cell-centre height, which only answers "could the GHOST hover here" - the player is a
        // 1.8m capsule and cannot follow through a gap that happens to be clear at one height.
        public const float PlayerCapsuleHeight = 1.8f;
        const float PlayerCapsuleFloorLift = 0.05f;

        // Can the PLAYER actually stand at this position, floor-anchored? Returns true when there is no floor
        // beneath at all - such a cell is air used for jumps/drops, not somewhere the player stands, so it is
        // not this test's business to judge.
        public bool PlayerCapsuleFits(Vector3 position)
        {
            RaycastHit floorHit;
            if (!Physics.Raycast(position + Vector3.up * 0.1f, Vector3.down, out floorHit,
                    SupportProbeDepthCells * CellSize, blockingMask, QueryTriggerInteraction.Ignore))
                return true;

            float floorY = floorHit.point.y;
            Vector3 lower = new Vector3(position.x, floorY + ProbeRadius + PlayerCapsuleFloorLift, position.z);
            Vector3 upper = new Vector3(position.x, floorY + PlayerCapsuleHeight - ProbeRadius, position.z);
            if (upper.y < lower.y)
                upper = lower;

            if (!Physics.CheckCapsule(lower, upper, ProbeRadius, blockingMask, QueryTriggerInteraction.Ignore))
                return true;

            int hitCount = Physics.OverlapCapsuleNonAlloc(
                lower, upper, ProbeRadius, overlapBuffer, blockingMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = overlapBuffer[i];
                if (hit == null || excludeFromOpen.Contains(hit.GetInstanceID()))
                    continue;

                return false;
            }

            return true;
        }

        // Drops every cached cell so the next query re-probes from live physics. Used by seance_probe
        // after changing ProbeRadius, which invalidates every cached openness/edge result.
        public void ClearCellCache()
        {
            cells.Clear();
            RefreshTeleportTriggerCells();
        }

        public bool IsOpen(SeanceCell cell)
        {
            return GetOrCreateRecord(cell).IsOpen;
        }

        public bool HasSupport(SeanceCell cell)
        {
            return GetOrCreateRecord(cell).HasSupport;
        }

        public bool IsWater(SeanceCell cell)
        {
            return GetOrCreateRecord(cell).IsWater;
        }

        public bool IsElevatorColumn(SeanceCell cell)
        {
            if (elevatorColumns.Count == 0)
                return false;

            Vector3 world = cell.ToWorldCenter(CellSize);
            for (int i = 0; i < elevatorColumns.Count; i++)
            {
                ElevatorColumn column = elevatorColumns[i];
                if (world.x >= column.MinX && world.x <= column.MaxX
                    && world.z >= column.MinZ && world.z <= column.MaxZ
                    && world.y >= column.MinY - ElevatorVerticalMargin && world.y <= column.MaxY + ElevatorVerticalMargin)
                    return true;
            }

            return false;
        }

        static readonly int[] wallCheckDX = { 1, -1, 0, 0 };
        static readonly int[] wallCheckDZ = { 0, 0, 1, -1 };

        // 4-connected (not 8) - a diagonal corner merely touching a wall isn't a real climbable face.
        public bool IsWallAdjacent(SeanceCell cell)
        {
            SeanceCellRecord record = GetOrCreateRecord(cell);
            if (record.WallAdjacencyKnown)
                return record.IsWallAdjacent;

            bool adjacent = false;
            for (int i = 0; i < 4; i++)
            {
                SeanceCell neighbor = cell.Offset(wallCheckDX[i], 0, wallCheckDZ[i]);
                if (!IsOpen(neighbor))
                {
                    adjacent = true;
                    break;
                }
            }

            record.IsWallAdjacent = adjacent;
            record.WallAdjacencyKnown = true;
            return adjacent;
        }

        // Phase 2.6 Step (b): a climb must terminate on a real LEDGE, not merely land somewhere HasSupport's
        // 2m walk-support window would call standable. That window is right for the walk rule - Daggerfall's
        // unaligned floor heights genuinely need it - but it also made a doorway's own headroom pass as a
        // climb destination: real floor sat 1.5m below, nothing beside it was actually climbable, and
        // IsWallAdjacent was satisfied by the doorframe alone. HasSupport and SupportProbeRadius/
        // SupportProbeDepthCells are completely untouched by this - only climb-ascent edges consult this.
        const float ClimbLedgeDepth = 0.6f;

        // A downward ray grazing the SIDE of a wall/doorframe can still return a hit within ClimbLedgeDepth,
        // so distance alone cannot tell "floor beneath" from "wall beside." The hit's surface normal can:
        // a real ledge faces up (normal.y close to 1), while a wall's normal points roughly sideways
        // (normal.y close to 0). 0.5 requires the surface to be closer to horizontal than a 60-degree slope -
        // generous enough for uneven stone, strict enough to reject a vertical face.
        const float ClimbLedgeNormalThreshold = 0.5f;

        // Small ray cluster, not one ray down the exact centre: a real ledge can sit slightly off-centre in
        // the cell (same reasoning as freeSampleOffsets), and a single miss would wrongly reject a genuine
        // climb destination. One qualifying hit among these is enough.
        static readonly Vector3[] climbLedgeRayOffsets =
        {
            Vector3.zero,
            new Vector3(0.2f, 0f, 0f),
            new Vector3(-0.2f, 0f, 0f),
            new Vector3(0f, 0f, 0.2f),
            new Vector3(0f, 0f, -0.2f),
        };

        // Raycast, not a sphere march: a sphere has width and reports overlap at every depth beside a wall,
        // which is exactly the false-positive class this is meant to avoid (the same shape of bug as the
        // SphereCast blind spot, just from a different cause - width instead of a missed start-overlap). A
        // zero-width ray can only report what is directly beneath the origin. The origin is the cell's own
        // free position, already certified clear by TryProbeOpen, so the ray cannot start embedded in
        // anything.
        public bool IsClimbableLedge(SeanceCell cell)
        {
            Vector3 freePosition = FreePosition(cell);

            for (int i = 0; i < climbLedgeRayOffsets.Length; i++)
            {
                Vector3 origin = freePosition + climbLedgeRayOffsets[i];

                RaycastHit hit;
                if (!Physics.Raycast(origin, Vector3.down, out hit, ClimbLedgeDepth, blockingMask, QueryTriggerInteraction.Ignore))
                    continue;

                // Same exclusion as the walk rule's own support probe - a mover's surface is never
                // trustworthy to cache as permanent, whether you are walking on it or climbing onto it.
                if (hit.collider != null && excludeFromSupport.Contains(hit.collider.GetInstanceID()))
                    continue;

                if (hit.normal.y >= ClimbLedgeNormalThreshold)
                    return true;
            }

            return false;
        }

        // Cache-only edge lookup - NO physics. Returns false when the edge has never been probed
        // (passable is then meaningless); the pathfinder uses this to prune already-known-blocked
        // edges during relaxation while deferring fresh edges' physics to pop time (lazy validation).
        public bool TryGetKnownEdge(SeanceCell a, SeanceCell b, out bool passable)
        {
            passable = false;

            int dx = b.X - a.X;
            int dy = b.Y - a.Y;
            int dz = b.Z - a.Z;
            if (dx < -1 || dx > 1 || dy < -1 || dy > 1 || dz < -1 || dz > 1)
                return false;

            SeanceCellRecord record;
            if (!cells.TryGetValue(a, out record))
                return false;

            int directionIndex = SeanceDirections.IndexOf[dx + 1, dy + 1, dz + 1];
            uint bit = 1u << directionIndex;
            if ((record.EdgeKnownMask & bit) == 0)
                return false;

            passable = (record.EdgePassableMask & bit) != 0;
            return true;
        }

        // Validates the EDGE between two immediate (26-connected) neighbors with a spherecast, not
        // just their two cell centers - cell-center sampling alone can miss walls thinner than the
        // spacing between sample spheres, which would otherwise let a path pierce solid geometry
        // between two cells that each individually read as open. Cached per-cell as a 26-bit
        // known/passable mask, mirrored onto both cells involved (an edge is symmetric).
        public bool IsEdgeClear(SeanceCell a, SeanceCell b)
        {
            int dx = b.X - a.X;
            int dy = b.Y - a.Y;
            int dz = b.Z - a.Z;

            if (dx < -1 || dx > 1 || dy < -1 || dy > 1 || dz < -1 || dz > 1)
                return ProbeEdge(FreePosition(a), FreePosition(b), excludeFromOpen);

            int directionIndex = SeanceDirections.IndexOf[dx + 1, dy + 1, dz + 1];
            uint bit = 1u << directionIndex;

            SeanceCellRecord recordA = GetOrCreateRecord(a);
            if ((recordA.EdgeKnownMask & bit) != 0)
                return (recordA.EdgePassableMask & bit) != 0;

            // Swept between the positions the ghost actually occupies in each cell, not the cell centres
            // - see SeanceCellRecord.FreePosition. Sweeping centre-to-centre would re-introduce exactly
            // the wall the off-centre sampling just proved the ghost can get past.
            Vector3 aCenter = FreePosition(a);
            Vector3 bCenter = FreePosition(b);

            // Passage override applies to UNIT edges only (this cached branch), never to the
            // arbitrary-length casts above used by path smoothing - a smoothed segment merely anchored
            // at a doorway/goal must still be genuinely clear along its whole length, or smoothing
            // could straighten a route through the wall beside the door/cage.
            //
            // And it never overrides a BARRIER. A lever-gated cage standing inside a doorway sits squarely
            // within that door's own passage volume, and overriding there let the ghost walk through it and
            // keep leading the player to a room they could not enter (Blurk's Guard, obstacle 41313).
            // BOTH endpoints must be in the doorway core, not either. "Either" let an edge run from inside the
            // doorway straight out through the wall beside it, which is exactly how the ghost ended up
            // crossing walls near doors.
            bool blockedByActionObject;
            bool clear = ProbeEdge(aCenter, bCenter, excludeFromOpen, out blockedByActionObject);
            bool passable = clear
                || (!blockedByActionObject && IsInDoorPassageCore(aCenter) && IsInDoorPassageCore(bCenter));

            recordA.EdgeKnownMask |= bit;
            if (passable)
                recordA.EdgePassableMask |= bit;

            SeanceCellRecord recordB = GetOrCreateRecord(b);
            int oppositeIndex = SeanceDirections.Opposite[directionIndex];
            uint oppositeBit = 1u << oppositeIndex;
            recordB.EdgeKnownMask |= oppositeBit;
            if (passable)
                recordB.EdgePassableMask |= oppositeBit;

            return passable;
        }

        // Collapses an entire pit into ONE physics query and one A* edge, rather than one edge per
        // intermediate cell - this is what makes "drops of any depth" cheap instead of an explosion in
        // search size. Uses the SUPPORT-style mask (raw blocking geometry, doors/trapdoors count as a
        // valid landing surface) since landing on a closed trapdoor is exactly the intended behavior.
        public bool TryFindDropLanding(SeanceCell from, out SeanceCell landing, out float dropDistance)
        {
            Vector3 origin = FreePosition(from);
            int hitCount = Physics.SphereCastNonAlloc(
                origin, SupportProbeRadius, Vector3.down, castBuffer, MaxDropSearch, blockingMask, QueryTriggerInteraction.Ignore);

            float bestDistance = float.MaxValue;
            bool found = false;

            for (int i = 0; i < hitCount; i++)
            {
                Collider hitCollider = castBuffer[i].collider;
                if (hitCollider != null && excludeFromSupport.Contains(hitCollider.GetInstanceID()))
                    continue;

                if (castBuffer[i].distance < bestDistance)
                {
                    bestDistance = castBuffer[i].distance;
                    found = true;
                }
            }

            if (!found)
            {
                landing = default(SeanceCell);
                dropDistance = 0f;
                return false;
            }

            Vector3 landingSurface = origin + Vector3.down * bestDistance;
            landing = SeanceCell.FromWorld(landingSurface + Vector3.up * (CellSize * 0.5f), CellSize);
            dropDistance = bestDistance;
            return true;
        }

        // Start/goal snapping: quest markers (and the ghost's own spawn point) are sometimes placed
        // inside geometry or floating in open air (known Daggerfall data quirks), not snapped to a
        // walkable cell. Tries the raw cell first, then a single downward ray (the cheap, common case
        // of "placed at its own natural height, standing on real floor below"), then an expanding shell
        // search as a last resort. requireSupport is false for goal snapping (the spec calls for no
        // support requirement on the goal/its immediate approach, since a marker can legitimately sit
        // over a trapdoor, ledge, or other unusual spot) and true for the ghost's own start position.
        public bool TrySnapToOpenCell(Vector3 worldPosition, float maxRadius, bool requireSupport, out SeanceCell result)
        {
            SeanceCell raw = SeanceCell.FromWorld(worldPosition, CellSize);
            if (IsOpen(raw) && (!requireSupport || HasSupport(raw)))
            {
                result = raw;
                return true;
            }

            RaycastHit downHit;
            if (Physics.Raycast(worldPosition, Vector3.down, out downHit, maxRadius, blockingMask, QueryTriggerInteraction.Ignore))
            {
                SeanceCell landed = SeanceCell.FromWorld(downHit.point + Vector3.up * (CellSize * 0.5f), CellSize);
                if (IsOpen(landed) && (!requireSupport || HasSupport(landed)))
                {
                    result = landed;
                    return true;
                }
            }

            int maxCellRadius = Mathf.CeilToInt(maxRadius / CellSize);
            for (int radius = 1; radius <= maxCellRadius; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        for (int dz = -radius; dz <= radius; dz++)
                        {
                            int maxAbs = Mathf.Max(Mathf.Abs(dx), Mathf.Max(Mathf.Abs(dy), Mathf.Abs(dz)));
                            if (maxAbs != radius)
                                continue;

                            SeanceCell candidate = raw.Offset(dx, dy, dz);
                            if (IsOpen(candidate) && (!requireSupport || HasSupport(candidate)))
                            {
                                result = candidate;
                                return true;
                            }
                        }
                    }
                }
            }

            result = raw;
            return false;
        }

        // Reports whether the ghost fits ANYWHERE in this cell, and where. The centre gets the full
        // treatment (cheap CheckSphere, then exclusion-aware overlap, then passage override); the
        // off-centre samples only accept a completely clear CheckSphere. That asymmetry is deliberate on
        // cost grounds - solid rock is the common case and pays 8 extra allocation-free CheckSphere calls
        // rather than 8 more OverlapSphere-plus-exclusion-set scans, and the door/mover and passage cases
        // are already fully handled at the centre.
        bool TryProbeOpen(Vector3 center, out Vector3 freePosition)
        {
            freePosition = center;

            if (!Physics.CheckSphere(center, ProbeRadius, blockingMask, QueryTriggerInteraction.Ignore))
                return true;

            bool centerBlockedByRealGeometry = false;
            bool centerBlockedByActionObject = false;
            int hitCount = Physics.OverlapSphereNonAlloc(center, ProbeRadius, overlapBuffer, blockingMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = overlapBuffer[i];
                if (hit == null || excludeFromOpen.Contains(hit.GetInstanceID()))
                    continue;

                centerBlockedByRealGeometry = true;

                // Is the thing in the way a BARRIER rather than architecture? Movers, portcullises,
                // secret doors and teleporter panels all carry an action component.
                if (hit.GetComponentInParent<DaggerfallAction>() != null
                    || hit.GetComponentInParent<DaggerfallActionDoor>() != null
                    || hit.GetComponentInParent<DaggerfallActionDoorSpecial>() != null)
                {
                    centerBlockedByActionObject = true;
                    break;
                }
            }

            if (!centerBlockedByRealGeometry)
                return true;

            // A BARRIER is never rescued - not by a passage volume, not by sub-cell sampling. Checked before
            // both, because either rescue silently tunnels through exactly the things the gated-obstacle
            // escort exists to handle. Two live regressions came from getting this order wrong:
            //  - sub-cell sampling rescued cells blocked by a bookcase and by a teleporter panel, so the
            //    teleporter's trigger cells moved INTO the door's own column and the ghost routed through a
            //    still-closed bookcase without ever asking for the torch;
            //  - a door passage rescued a lever-gated CAGE standing inside a doorway (Blurk's Guard, obstacle
            //    41313), so the ghost happily walked through and kept leading the player to a room they could
            //    not physically enter.
            // Door panels themselves are already in excludeFromOpen and so never reach this check.
            if (centerBlockedByActionObject)
                return false;

            // Blocked by real STATIC geometry. Sub-cell sampling runs BEFORE the passage blanket: a point
            // measured clear always beats a point merely asserted open.
            //
            // The old order short-circuited on the passage volume first, which returned with freePosition
            // still at the cell CENTRE - and a doorway cell's centre is routinely inside the frame, which is
            // the entire reason the volume exists. Every consumer then used that buried point as the cell's
            // position: edges swept from inside geometry (measured: MeasureBlockageThickness returning
            // float.MaxValue, i.e. "never re-emerges", on the two edges that severed Blurk's Guard), support
            // probes started buried, waypoints were placed in the frame.
            //
            // Worse, the blanket overrode cells that had a perfectly good free position available. Measured
            // at door 55005: one corridor at x~17.2, whose cells at z=12,13 sit outside the volume and
            // resolve honestly to CLEAR@offset(-0.3,0,0), while its cells at z=14..17 fall inside the volume
            // and were asserted at x=17.5 instead - buried, and stranded in neither component.
            for (int i = 0; i < freeSampleOffsets.Length; i++)
            {
                Vector3 sample = center + freeSampleOffsets[i];
                if (!Physics.CheckSphere(sample, ProbeRadius, blockingMask, QueryTriggerInteraction.Ignore))
                {
                    freePosition = sample;
                    return true;
                }
            }

            // Nothing in this cell is genuinely clear - so fall back to the assertion. A doorway's own FRAME
            // is static geometry, and an opening narrower than the probe can span leaves no clear sample
            // anywhere in the cell even though a player walks through it. See AddDoorPassage.
            if (IsInAnyPassage(center))
                return true;

            return false;
        }

        // Depth below the origin at which the stepped scan starts - one cell's own floor line, not the
        // origin itself. The origin's free position is only ever certified clear at the (often smaller)
        // openness ProbeRadius, not at SupportProbeRadius - re-testing it at the wider radius reaches back
        // into whatever nearby corner the openness check had just barely missed.
        const float SupportProbeStartDepth = CellSize * 0.5f;

        // A surface counts as floor only if it faces upward - grazing the SIDE of a wall/doorframe returns
        // a hit too, but its normal points sideways, not up. 0.5 requires the surface to be closer to
        // horizontal than a 60-degree slope - generous enough for uneven stone, strict enough to reject a
        // vertical face. Independent of ClimbLedgeNormalThreshold (same value, separate named constant) so
        // the walk and climb rules can be tuned independently later without silently coupling them.
        const float SupportNormalThreshold = 0.5f;

        // Small ray cluster, not one ray down the exact centre - the origin can be off-centre near a corner
        // (see freeSampleOffsets), and a single miss would wrongly reject a genuinely supported cell.
        static readonly Vector3[] supportConfirmRayOffsets =
        {
            Vector3.zero,
            new Vector3(0.2f, 0f, 0f),
            new Vector3(-0.2f, 0f, 0f),
            new Vector3(0f, 0f, 0.2f),
            new Vector3(0f, 0f, -0.2f),
        };

        // Second attempt at this probe. The first stepped-CheckSphere rewrite (session history: fixed a
        // confirmed SphereCast blind-spot corrupt-data signature against 'CombinedModels' at a doorway
        // ledge in Blurk's Guard) was reverted after it caused a real regression - it changed HasSupport
        // widely enough that a bogus CLIMB edge started succeeding through that same doorway ledge, and
        // since a climb-pass success was indistinguishable from an honest walkable one, it silently
        // disabled the lever escort. That regression is now fixed at its actual source (Phase 2.6 Step (a):
        // escort is tried before climbing at all; Step (b): a climb must terminate on a real ledge, not
        // merely land somewhere within this probe's own reach) - so this can be re-applied.
        //
        // A stepped CheckSphere/OverlapSphere march has a DIFFERENT false-positive mode than the original
        // SphereCast blind spot: overlap alone cannot tell "floor beneath" from "wall/corner beside," since
        // a sphere has width and reports overlap at every depth alongside a nearby wall - exactly the
        // doorway-ledge case measured here. So overlap is only a cheap coarse gate now (is anything roughly
        // around here at all - most cells resolve in a step or two, either genuinely supported or genuinely
        // not). Once it trips, a small cluster of zero-width downward RAYS from the cell's own free position
        // (not from the stepped sample point - the origin is certified clear by TryProbeOpen, so it cannot
        // start embedded, the same guarantee IsClimbableLedge relies on) confirms whether real floor exists,
        // by requiring an upward-facing normal. A ray simply cannot be fooled by something merely beside the
        // origin, since it has no width to graze it with.
        bool ProbeSupport(Vector3 center)
        {
            float totalDepth = SupportProbeDepthCells * CellSize;

            for (float depth = SupportProbeStartDepth; depth <= totalDepth; depth += SupportProbeRadius)
            {
                Vector3 point = center + Vector3.down * depth;

                if (!Physics.CheckSphere(point, SupportProbeRadius, blockingMask, QueryTriggerInteraction.Ignore))
                    continue;

                for (int i = 0; i < supportConfirmRayOffsets.Length; i++)
                {
                    Vector3 rayOrigin = center + supportConfirmRayOffsets[i];

                    RaycastHit hit;
                    if (!Physics.Raycast(rayOrigin, Vector3.down, out hit, depth + SupportProbeRadius, blockingMask, QueryTriggerInteraction.Ignore))
                        continue;

                    if (hit.collider != null && excludeFromSupport.Contains(hit.collider.GetInstanceID()))
                        continue;

                    if (hit.normal.y >= SupportNormalThreshold)
                        return true;
                }
            }

            return false;
        }

        // Generous enough to cover any real Daggerfall dungeon lake/pool, but NOT unbounded - a
        // per-block WaterLevel value has no lower bound on its own (it's just a rendering-plane
        // height), so without this cap ANY cell below the surface, no matter how far down, reads as
        // "water" - including cells beneath the actual playable floor. Real testing found this exact
        // failure: a shallow ankle-deep puddle (still a real WaterLevel for its block, just barely
        // above the floor) let swim mode's own no-support-required movement accept a "swim straight
        // down" edge into whatever gap exists beneath that floor, sinking the ghost through the world
        // with nothing to stop the descent.
        const float MaxWaterDepth = 8f;

        // Requires MEANINGFUL submersion below the water plane, not just "technically below it" -
        // confirmed via real testing that plain "center.y < SurfaceY" (no margin at all) let ordinary
        // floor unevenness in a shallow, walkable-depth room (a drain, a shallow dip - a few cm below
        // the water plane's own Y, nowhere near actual swimming depth) get classified as fully
        // swimmable, granting that one cell 26-directional no-support-required movement it should
        // never have had. DFU's own IsPlayerSwimming test has an analogous margin baked in (the
        // capsule-center-to-foot correction effectively requires being ~0.3 units below the surface,
        // not merely damp) - this uses a larger, more conservative margin since a per-block WaterLevel
        // says nothing about the real floor's exact shape at any given point within it.
        const float MinSwimDepthMargin = 1.2f;

        bool ProbeWater(Vector3 center)
        {
            for (int i = 0; i < waterBlocks.Count; i++)
            {
                WaterBlock block = waterBlocks[i];
                if (center.x >= block.MinX && center.x <= block.MaxX
                    && center.z >= block.MinZ && center.z <= block.MaxZ
                    && center.y < block.SurfaceY - MinSwimDepthMargin && center.y >= block.SurfaceY - MaxWaterDepth)
                {
                    return true;
                }
            }

            return false;
        }

        public int BlockingMask { get { return blockingMask; } }

        // One line of per-cell detail, shared by the seam report's straddling-pair and orphan sections so
        // both describe cells identically.
        public string DescribeCellBrief(SeanceCell cell)
        {
            Vector3 centre = cell.ToWorldCenter(CellSize);
            Vector3 free = FreePosition(cell);
            float offset = (free - centre).magnitude;

            return string.Format(
                "{0} free={1}{2} IsOpen={3} HasSupport={4} IsRoomy={5} inPassage={6} inCore={7}",
                cell, free,
                offset > 0.0001f ? string.Format(" (OFF-CENTRE by {0:F2}m)", offset) : "",
                IsOpen(cell), HasSupport(cell), IsRoomy(cell),
                IsInAnyPassage(centre), IsInDoorPassageCore(centre));
        }

        // Why an edge between two cells is refused, in the same vocabulary as the rejection histogram.
        public string DescribeEdgeRejection(SeanceCell a, SeanceCell b)
        {
            if (!IsOpen(b))
                return "NOT_OPEN (destination cell is solid)";

            if (!HasSupport(b) && !IsElevatorColumn(b))
                return "NO_SUPPORT (destination open but nothing to stand on)";

            Collider blocker;
            bool blockedByActionObject;
            if (IsSegmentClear(FreePosition(a), FreePosition(b), ProbeRadius, out blocker, out blockedByActionObject))
                return "edge is CLEAR (rejection must be a movement-rule issue, i.e. NO_RULE)";

            // Model the core rescue, or this reports false alarms: IsEdgeClear accepts a blocked edge when
            // BOTH endpoints are in the door-passage core and the blocker is not a barrier. Without this,
            // seance_trace listed edges the pathfinder happily crosses as impassable crossings.
            if (!blockedByActionObject && IsInDoorPassageCore(FreePosition(a)) && IsInDoorPassageCore(FreePosition(b)))
                return string.Format(
                    "edge is BLOCKED by '{0}' but RESCUED by the door-passage core (both endpoints inside) - passable",
                    blocker != null ? blocker.name : "(unknown)");

            float thickness = MeasureBlockageThickness(FreePosition(a), FreePosition(b), ProbeRadius);
            return string.Format(
                "EDGE_BLOCKED by '{0}' on layer '{1}' (actionObject={2}), blockage {3:F3}m thick",
                blocker != null ? blocker.name : "(unknown)",
                blocker != null ? LayerMask.LayerToName(blocker.gameObject.layer) : "(unknown)",
                blockedByActionObject,
                thickness == float.MaxValue ? -1f : thickness);
        }

        // What this grid was built from, in the same terms as the initialization log line. Lets a diagnostic
        // confirm it is looking at a freshly built grid rather than one a cast has already mutated.
        public string DescribeBuild()
        {
            return string.Format(
                "{0} excluded-from-open collider(s), {1} excluded-from-support collider(s), {2} door passage(s), " +
                "{3} water block(s), {4} elevator column(s), {5} active teleporter edge(s), {6} resolvable gated obstacle(s), " +
                "{7} cell(s) probed so far",
                excludeFromOpen.Count, excludeFromSupport.Count, doorPassages.Count,
                waterBlocks.Count, elevatorColumns.Count, teleportEdges.Count, gatedObstacles.Count, cells.Count);
        }

        // Replays TryProbeOpen's decision path for one cell and reports WHICH branch decided it. The whole
        // point is the distinction between "open because a clear sample was found" and "open because a
        // passage volume asserted it" - the second kind leaves freePosition at the cell centre, which may be
        // buried inside the doorframe, and that is invisible in every other diagnostic.
        //
        // Not a mirror: this runs the same queries in the same order against the same masks. It does not
        // touch the cell cache.
        public string DescribeOpenness(SeanceCell cell)
        {
            Vector3 center = cell.ToWorldCenter(CellSize);

            if (!Physics.CheckSphere(center, ProbeRadius, blockingMask, QueryTriggerInteraction.Ignore))
                return "CLEAR@centre";

            bool centerBlockedByRealGeometry = false;
            bool centerBlockedByActionObject = false;
            int hitCount = Physics.OverlapSphereNonAlloc(center, ProbeRadius, overlapBuffer, blockingMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = overlapBuffer[i];
                if (hit == null || excludeFromOpen.Contains(hit.GetInstanceID()))
                    continue;

                centerBlockedByRealGeometry = true;
                if (hit.GetComponentInParent<DaggerfallAction>() != null
                    || hit.GetComponentInParent<DaggerfallActionDoor>() != null
                    || hit.GetComponentInParent<DaggerfallActionDoorSpecial>() != null)
                {
                    centerBlockedByActionObject = true;
                    break;
                }
            }

            if (!centerBlockedByRealGeometry)
                return "CLEAR@centre (only excluded colliders overlapped)";

            if (centerBlockedByActionObject)
                return "CLOSED (barrier - never rescued)";

            for (int i = 0; i < freeSampleOffsets.Length; i++)
            {
                Vector3 sample = center + freeSampleOffsets[i];
                if (!Physics.CheckSphere(sample, ProbeRadius, blockingMask, QueryTriggerInteraction.Ignore))
                    return string.Format("CLEAR@offset{0}", freeSampleOffsets[i]);
            }

            if (IsInAnyPassage(center))
                return "PASSAGE-ASSERTED (no clear sample anywhere in cell; free position left at centre)";

            return "CLOSED (no clear sample anywhere in cell)";
        }

        // Everything known about the door passage covering (or nearest to) a point, plus a cell-by-cell walk
        // through the doorway along both horizontal axes. Exists to answer one question: is the core volume
        // DISPLACED from the real opening, or merely small?
        public string DescribeDoorPassage(Vector3 point)
        {
            if (doorPassages == null || doorPassages.Count == 0)
                return "No door passages built for this dungeon.";

            int best = -1;
            bool contained = false;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < doorPassages.Count; i++)
            {
                if (doorPassages[i].Contains(point))
                {
                    best = i;
                    contained = true;
                    break;
                }

                float distance = doorPassages[i].SqrDistance(point);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            Bounds passage = doorPassages[best];
            Bounds core = new Bounds(passage.center, passage.size * doorPassageCoreScale);
            Component owner = best < doorPassageOwners.Count ? doorPassageOwners[best] : null;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendFormat("DOOR CHECK at {0} -> cell {1} (probe radius {2:F3})",
                point, SeanceCell.FromWorld(point, CellSize), ProbeRadius);
            sb.AppendFormat("\n  {0} of {1} passage volume(s) - point is {2}",
                best + 1, doorPassages.Count,
                contained ? "INSIDE this one" : string.Format("OUTSIDE all of them; this is the nearest ({0:F2}m)", Mathf.Sqrt(bestDistance)));

            if (owner == null)
            {
                sb.Append("\n  owner: (not recorded)");
            }
            else
            {
                sb.AppendFormat("\n  owner: '{0}' ({1})", owner.name, owner.GetType().Name);
                sb.AppendFormat("\n    transform position {0}", owner.transform.position);
                sb.AppendFormat("\n    transform rotation {0} (euler {1})", owner.transform.rotation, owner.transform.rotation.eulerAngles);

                DaggerfallActionDoor actionDoor = owner as DaggerfallActionDoor;
                if (actionDoor != null)
                {
                    sb.AppendFormat("\n    ClosedRotation    {0} (euler {1})", actionDoor.ClosedRotation, actionDoor.ClosedRotation.eulerAngles);
                    sb.AppendFormat("\n    open? current vs closed differ by {0:F1} degrees",
                        Quaternion.Angle(owner.transform.rotation, actionDoor.ClosedRotation));
                }
                else
                {
                    // DaggerfallActionDoorSpecial does NOT derive from DaggerfallActionDoor, but it exposes
                    // its own identically-named ClosedRotation (both are just startingRotation) - so special
                    // doors are not the exception they look like, they simply need a second cast.
                    DaggerfallActionDoorSpecial special = owner as DaggerfallActionDoorSpecial;
                    if (special != null)
                    {
                        sb.AppendFormat("\n    ClosedRotation    {0} (euler {1}) [via DaggerfallActionDoorSpecial]",
                            special.ClosedRotation, special.ClosedRotation.eulerAngles);
                        sb.AppendFormat("\n    open? current vs closed differ by {0:F1} degrees",
                            Quaternion.Angle(owner.transform.rotation, special.ClosedRotation));
                    }
                    else
                    {
                        sb.Append("\n    ClosedRotation    (none - neither door type)");
                    }
                }

                Renderer doorRenderer = owner.GetComponentInChildren<Renderer>();
                if (doorRenderer != null)
                {
                    sb.AppendFormat("\n    renderer bounds   centre {0} size {1}", doorRenderer.bounds.center, doorRenderer.bounds.size);
                    sb.AppendFormat("\n    renderer centre - transform position = {0}", doorRenderer.bounds.center - owner.transform.position);
                }
            }

            AppendDoorColliders(sb, owner);

            sb.AppendFormat("\n  FULL passage: centre {0} size {1}", passage.center, passage.size);
            sb.AppendFormat("\n    x[{0:F2},{1:F2}] y[{2:F2},{3:F2}] z[{4:F2},{5:F2}]",
                passage.min.x, passage.max.x, passage.min.y, passage.max.y, passage.min.z, passage.max.z);
            sb.AppendFormat("\n  CORE (x{0:F2}): centre {1} size {2}", doorPassageCoreScale, core.center, core.size);
            sb.AppendFormat("\n    x[{0:F2},{1:F2}] y[{2:F2},{3:F2}] z[{4:F2},{5:F2}]",
                core.min.x, core.max.x, core.min.y, core.max.y, core.min.z, core.max.z);

            if (owner != null)
                sb.AppendFormat("\n  passage centre - door transform position = {0}", passage.center - owner.transform.position);

            SeanceCell centre = SeanceCell.FromWorld(point, CellSize);
            AppendDoorSpan(sb, centre, true);
            AppendDoorSpan(sb, centre, false);

            return sb.ToString();
        }

        // Which colliders belong to this door, and which of them the grid actually excludes. Listed two
        // ways deliberately: children of the door transform (what AddColliders walks), and everything
        // physically overlapping the panel's bounds (what the openness probe will actually hit). A collider
        // in the second list but not the first is a panel the grid treats as solid wall - and since a panel
        // MOVES when the door opens, that would make openness depend on door state.
        void AppendDoorColliders(System.Text.StringBuilder sb, Component owner)
        {
            if (owner == null)
                return;

            sb.Append("\n  COLLIDERS under the door transform:");
            Collider[] own = owner.GetComponentsInChildren<Collider>();
            if (own.Length == 0)
                sb.Append("\n    (none)");
            for (int i = 0; i < own.Length; i++)
                sb.AppendFormat("\n    {0}", DescribeColliderExclusion(own[i]));

            Renderer doorRenderer = owner.GetComponentInChildren<Renderer>();
            if (doorRenderer == null)
                return;

            sb.Append("\n  COLLIDERS overlapping the panel bounds (anything here NOT listed above blocks the grid):");
            Bounds panel = doorRenderer.bounds;
            Collider[] overlapping = Physics.OverlapBox(
                panel.center, panel.extents, Quaternion.identity, blockingMask, QueryTriggerInteraction.Ignore);
            if (overlapping.Length == 0)
                sb.Append("\n    (none)");
            for (int i = 0; i < overlapping.Length; i++)
                sb.AppendFormat("\n    {0}", DescribeColliderExclusion(overlapping[i]));
        }

        string DescribeColliderExclusion(Collider collider)
        {
            if (collider == null)
                return "(null)";

            int id = collider.GetInstanceID();
            return string.Format("'{0}' [id={1}] layer '{2}' excludeFromOpen={3} excludeFromSupport={4} bounds centre {5} size {6}",
                collider.name, id, LayerMask.LayerToName(collider.gameObject.layer),
                excludeFromOpen.Contains(id), excludeFromSupport.Contains(id),
                collider.bounds.center, collider.bounds.size);
        }

        void AppendDoorSpan(System.Text.StringBuilder sb, SeanceCell centre, bool alongX)
        {
            sb.AppendFormat("\n\n  SPAN along {0} through {1}:", alongX ? "X" : "Z", centre);
            for (int offset = -3; offset <= 3; offset++)
            {
                SeanceCell cell = alongX ? centre.Offset(offset, 0, 0) : centre.Offset(0, 0, offset);
                Vector3 cellCentre = cell.ToWorldCenter(CellSize);
                Vector3 free = FreePosition(cell);
                float off = (free - cellCentre).magnitude;

                sb.AppendFormat("\n    {0} IsOpen={1} HasSupport={2} free={3}{4} inPassage={5} inCore={6}  via {7}",
                    cell, IsOpen(cell), HasSupport(cell), free,
                    off > 0.0001f ? string.Format(" (OFF {0:F2}m)", off) : "",
                    IsInAnyPassage(cellCentre), IsInDoorPassageCore(cellCentre),
                    DescribeOpenness(cell));
            }
        }

        // Dissects ONE cell's support probe for seance_supportcheck. Reproduces exactly what ProbeSupport
        // does (the stepped-CheckSphere-plus-raycast-confirmation version - see ProbeSupport's own comment
        // for the history), printing every step and every confirming ray rather than short-circuiting.
        public string DebugDescribeSupport(Vector3 worldPosition)
        {
            SeanceCell cell = SeanceCell.FromWorld(worldPosition, CellSize);
            Vector3 probeOrigin = FreePosition(cell);

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendFormat("SUPPORT CHECK {0} -> cell {1} (centre {2})", worldPosition, cell, cell.ToWorldCenter(CellSize));
            sb.AppendFormat("\nProbe origin (cell free position): {0}", probeOrigin);
            sb.AppendFormat("\nSupportProbeRadius {0:F2}, depth {1:F2}m (stepped from {2:F2}m below origin), probe radius {3:F3}",
                SupportProbeRadius, SupportProbeDepthCells * CellSize, SupportProbeStartDepth, ProbeRadius);
            sb.AppendFormat("\nHasSupport reports: {0}   IsOpen reports: {1}", HasSupport(cell), IsOpen(cell));

            SeanceCell below = cell.Offset(0, -1, 0);
            sb.AppendFormat("\nCell below {0}: IsOpen={1} (a blocked cell below is what support SHOULD mean)",
                below, IsOpen(below));

            float totalDepth = SupportProbeDepthCells * CellSize;
            sb.Append("\n\nStepped coarse gate, with ray confirmation wherever it trips:");
            bool anyConfirmed = false;

            for (float depth = SupportProbeStartDepth; depth <= totalDepth; depth += SupportProbeRadius)
            {
                Vector3 point = probeOrigin + Vector3.down * depth;
                bool gateBlocked = Physics.CheckSphere(point, SupportProbeRadius, blockingMask, QueryTriggerInteraction.Ignore);

                sb.AppendFormat("\n  depth {0:F2}m at {1}: coarse gate {2}", depth, point, gateBlocked ? "OVERLAP" : "clear");
                if (!gateBlocked)
                    continue;

                for (int i = 0; i < supportConfirmRayOffsets.Length; i++)
                {
                    Vector3 rayOrigin = probeOrigin + supportConfirmRayOffsets[i];

                    RaycastHit hit;
                    if (!Physics.Raycast(rayOrigin, Vector3.down, out hit, depth + SupportProbeRadius, blockingMask, QueryTriggerInteraction.Ignore))
                    {
                        sb.AppendFormat("\n      ray from {0}: no hit within {1:F2}m", rayOrigin, depth + SupportProbeRadius);
                        continue;
                    }

                    bool excluded = hit.collider != null && excludeFromSupport.Contains(hit.collider.GetInstanceID());
                    bool floorLike = hit.normal.y >= SupportNormalThreshold;
                    sb.AppendFormat("\n      ray from {0}: hit '{1}' at {2}, normal.y={3:F2} excludedFromSupport={4}{5}",
                        rayOrigin, hit.collider != null ? hit.collider.name : "(null)", hit.point, hit.normal.y, excluded,
                        !excluded && floorLike ? "   <-- CONFIRMED FLOOR" : "");

                    if (!excluded && floorLike)
                        anyConfirmed = true;
                }
            }

            if (!anyConfirmed)
                sb.Append("\n  (no ray confirmed genuine floor within range)");

            return sb.ToString();
        }

        public bool IsRoomy(SeanceCell cell)
        {
            return GetOrCreateRecord(cell).IsRoomy;
        }

        bool IsPositionRoomy(Vector3 position)
        {
            if (!Physics.CheckSphere(position, ClearanceRadius, blockingMask, QueryTriggerInteraction.Ignore))
                return true;

            int hitCount = Physics.OverlapSphereNonAlloc(
                position, ClearanceRadius, overlapBuffer, blockingMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hitCount; i++)
            {
                Collider hit = overlapBuffer[i];
                if (hit == null || excludeFromOpen.Contains(hit.GetInstanceID()))
                    continue;

                return false;
            }

            return true;
        }

        static readonly Vector3[] clearanceDirections =
        {
            Vector3.right, Vector3.left, Vector3.forward, Vector3.back, Vector3.up, Vector3.down
        };

        // Distance to the nearest blocking surface from this position, capped at maxDistance. Excluded
        // colliders (open doors, movers) do not count as surfaces to keep away from.
        public float MeasureClearance(Vector3 position, float maxDistance)
        {
            float nearest = maxDistance;

            for (int i = 0; i < clearanceDirections.Length; i++)
            {
                RaycastHit hit;
                if (!Physics.Raycast(position, clearanceDirections[i], out hit, maxDistance, blockingMask,
                        QueryTriggerInteraction.Ignore))
                    continue;

                if (excludeFromOpen.Contains(hit.collider.GetInstanceID()))
                    continue;

                if (hit.distance < nearest)
                    nearest = hit.distance;
            }

            return nearest;
        }

        // Nudges a waypoint toward the middle of the free space around it, so the ghost stops grazing
        // doorframes and wall corners. Works per axis: whichever side is closer, shift away from it by half
        // the difference, which centres the point in that axis's free span. Iterated a few times because
        // moving on one axis changes what the others see.
        //
        // The result is only accepted if it is still reachable from where it started - a push must never
        // relocate a waypoint to the far side of a wall.
        public Vector3 PushTowardClearance(Vector3 position, int iterations, out float clearance)
        {
            Vector3 current = position;

            for (int step = 0; step < iterations; step++)
            {
                Vector3 shift = Vector3.zero;

                for (int axis = 0; axis < clearanceDirections.Length; axis += 2)
                {
                    float positive = RayDistance(current, clearanceDirections[axis], ClearanceRadius);
                    float negative = RayDistance(current, clearanceDirections[axis + 1], ClearanceRadius);

                    // Both sides open to the cap - already comfortably centred on this axis.
                    if (positive >= ClearanceRadius && negative >= ClearanceRadius)
                        continue;

                    shift += clearanceDirections[axis] * ((positive - negative) * 0.5f);
                }

                if (shift.sqrMagnitude < 0.0001f)
                    break;

                if (shift.magnitude > ClearanceRadius)
                    shift = shift.normalized * ClearanceRadius;

                Vector3 candidate = current + shift;

                Collider blocker;
                bool blockedByActionObject;
                if (!IsSegmentClear(current, candidate, ProbeRadius, out blocker, out blockedByActionObject))
                    break;

                current = candidate;
            }

            clearance = MeasureClearance(current, ClearanceRadius);
            return current;
        }

        float RayDistance(Vector3 origin, Vector3 direction, float maxDistance)
        {
            RaycastHit hit;
            if (!Physics.Raycast(origin, direction, out hit, maxDistance, blockingMask, QueryTriggerInteraction.Ignore))
                return maxDistance;

            if (excludeFromOpen.Contains(hit.collider.GetInstanceID()))
                return maxDistance;

            return hit.distance;
        }

        // Sample spacing as a fraction of the radius - under 1.0 so consecutive samples overlap and nothing
        // can slip between them.
        const float SegmentSampleSpacingFactor = 0.9f;

        // THE clearance primitive. Every "can something of this size travel from a to b" question routes
        // through here.
        //
        // Deliberately stepped CheckSphere/OverlapSphere rather than a SphereCast sweep: Unity's SphereCast
        // does NOT report colliders the sphere is already overlapping at the START of the sweep. Cells that
        // paths actually run through are routinely within one probe radius of a wall (that is what being in a
        // corridor means), so a cast originating there silently ignores that wall for its entire length - and
        // a long path-smoothing shortcut could therefore be validated straight through solid geometry. Overlap
        // queries report intersection regardless of origin, so this has no such blind spot.
        public bool IsSegmentClear(Vector3 a, Vector3 b, float radius, out Collider blocker, out bool blockedByActionObject)
        {
            return IsSegmentClear(a, b, radius, false, out blocker, out blockedByActionObject);
        }

        // allowPassageExemption: treat samples inside a door passage volume as clear when what blocks them is
        // STATIC geometry. A doorway's frame is part of the CombinedModels mesh and cannot be excluded by
        // component, which is the whole reason passage volumes exist - so any caller that must agree with the
        // grid's own notion of "this doorway is passable" has to apply the same exemption or it will
        // contradict the pathfinder at literally every door. The motion leash is such a caller.
        //
        // It stays OFF for path smoothing: a long smoothed segment merely anchored at a doorway must still be
        // genuinely clear along its whole length, or smoothing could straighten a route through the wall
        // beside the door. And it never applies to barriers (cages, portcullises, movers) at all.
        public bool IsSegmentClear(Vector3 a, Vector3 b, float radius, bool allowPassageExemption,
            out Collider blocker, out bool blockedByActionObject)
        {
            blocker = null;
            blockedByActionObject = false;

            int steps = SegmentSampleCount(a, b, radius);

            for (int i = 0; i <= steps; i++)
            {
                Vector3 point = Vector3.Lerp(a, b, i / (float)steps);
                if (!IsSampleBlocked(point, radius, allowPassageExemption, out blocker, out blockedByActionObject))
                    continue;

                return false;
            }

            blocker = null;
            blockedByActionObject = false;
            return true;
        }

        int SegmentSampleCount(Vector3 a, Vector3 b, float radius)
        {
            float distance = Vector3.Distance(a, b);
            return Mathf.Max(1, Mathf.CeilToInt(distance / Mathf.Max(0.01f, radius * SegmentSampleSpacingFactor)));
        }

        // One sample of the shared marching used by both IsSegmentClear and MeasureBlockageThickness.
        bool IsSampleBlocked(Vector3 point, float radius, bool allowPassageExemption,
            out Collider blocker, out bool blockedByActionObject)
        {
            blocker = null;
            blockedByActionObject = false;

            // Cheap allocation-free gate - the overwhelming majority of samples are in open air, and only
            // a sample that touches SOMETHING pays for the exclusion-aware pass.
            if (!Physics.CheckSphere(point, radius, blockingMask, QueryTriggerInteraction.Ignore))
                return false;

            // Evaluated once per sample rather than per collider - a doorway's own frame, inside that
            // doorway's core. Uses the SAME core volume the edge override uses, so callers agree by
            // construction: exempting the full passage here would re-open every wall within ~2.45m of a
            // door, which is the bug this core exists to prevent. Never granted to a barrier.
            bool passageExempt = allowPassageExemption && IsInDoorPassageCore(point);

            int hitCount = Physics.OverlapSphereNonAlloc(
                point, radius, overlapBuffer, blockingMask, QueryTriggerInteraction.Ignore);
            for (int h = 0; h < hitCount; h++)
            {
                Collider hit = overlapBuffer[h];
                if (hit == null || excludeFromOpen.Contains(hit.GetInstanceID()))
                    continue;

                bool isActionObject = hit.GetComponentInParent<DaggerfallAction>() != null
                    || hit.GetComponentInParent<DaggerfallActionDoor>() != null
                    || hit.GetComponentInParent<DaggerfallActionDoorSpecial>() != null;

                if (passageExempt && !isActionObject)
                    continue;

                blocker = hit;
                blockedByActionObject = isActionObject;
                return true;
            }

            return false;
        }

        // How much CONTINUOUS solid geometry lies along a->b, starting at the first blocked sample. This is
        // what distinguishes a corner from a wall: a corner graze, doorframe or bit of moulding is a few
        // centimetres thick, whereas a wall or floor is a third of a metre or more. Resolving that once at
        // path-build time is what lets the runtime fly the polyline with no collision logic at all.
        //
        // Returns 0 when the segment is clear, the LARGEST thickness when blocked in several places, and
        // float.MaxValue when it never re-emerges into clear space before the end of the segment.
        public float MeasureBlockageThickness(Vector3 a, Vector3 b, float radius)
        {
            int steps = SegmentSampleCount(a, b, radius);
            float segmentLength = Vector3.Distance(a, b);
            float stepLength = steps > 0 ? segmentLength / steps : segmentLength;

            Collider blocker;
            bool blockedByActionObject;

            float largest = 0f;
            int blockedRunStart = -1;

            for (int i = 0; i <= steps; i++)
            {
                Vector3 point = Vector3.Lerp(a, b, i / (float)steps);
                bool blocked = IsSampleBlocked(point, radius, false, out blocker, out blockedByActionObject);

                if (blocked)
                {
                    if (blockedRunStart < 0)
                        blockedRunStart = i;

                    continue;
                }

                if (blockedRunStart >= 0)
                {
                    float thickness = (i - blockedRunStart) * stepLength;
                    if (thickness > largest)
                        largest = thickness;

                    blockedRunStart = -1;
                }
            }

            // Still inside geometry at the far end - never re-emerged, so treat as a wall.
            if (blockedRunStart >= 0)
                return float.MaxValue;

            return largest;
        }

        bool ProbeEdge(Vector3 from, Vector3 to, HashSet<int> exclude)
        {
            bool blockedByActionObject;
            return ProbeEdge(from, to, exclude, out blockedByActionObject);
        }

        // blockedByActionObject reports whether what stopped the sweep was a BARRIER (cage, portcullis,
        // mover, teleporter panel) rather than static architecture. Callers use it to refuse the passage
        // override, so a passage volume can rescue a door frame without also tunnelling through a cage
        // standing in the same doorway.
        // Now a thin wrapper over IsSegmentClear - see that method for why the old SphereCast sweep was
        // structurally unable to see a wall it started inside. The `exclude` parameter is retained for call
        // compatibility but is always excludeFromOpen, which IsSegmentClear applies itself.
        bool ProbeEdge(Vector3 from, Vector3 to, HashSet<int> exclude, out bool blockedByActionObject)
        {
            Collider blocker;
            return IsSegmentClear(from, to, ProbeRadius, out blocker, out blockedByActionObject);
        }
    }
}
