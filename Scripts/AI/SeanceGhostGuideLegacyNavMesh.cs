// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// LEGACY / INERT - kept as a reference/rollback option, not currently used. SeanceEffect.cs now
// attaches the voxel-grid-A*-driven SeanceGhostGuide instead (see that file's own header comment
// for why: this NavMesh-based approach repeatedly hit cave dungeons with zero bakeable coverage that
// no amount of bake-parameter tuning ever fixed). Flip SeanceEffect.UseVoxelPathfinding to false to
// re-enable this class instead - it and its dependency SeanceDungeonNavMesh.cs are both left fully
// intact, unchanged, and untouched by the new system, so reverting is a one-line change if the new
// pathfinder ever needs to be backed out.
//
// Everything below this point is exactly as it was before the voxel-grid rework, just under a
// renamed class (SeanceGhostGuide -> SeanceGhostGuideLegacyNavMesh) so both guides can coexist in the
// same assembly without a name collision.
//
// Attached to the single ghost Seance summons. Drives movement via a real UnityEngine.AI.NavMeshAgent
// against the runtime NavMesh SeanceDungeonNavMesh bakes for the current dungeon, replacing this
// mod's own earlier simplified direct-line movement (copied from AllyCombatController) - real
// in-dungeon testing showed that approach just ran the ghost straight at the target and clipped
// through walls instead of navigating corridors, so this mod's first-ever NavMesh usage exists to
// fix that properly.
//
// Hybrid Walking/Riding/Flying movement: some dungeons connect levels with a genuine sheer drop/climb
// the NavMesh bake can never represent as walkable (DFU has no special-cased ramp/stair geometry, and
// confirmed in-game testing found real ~90 degree gaps between levels). When FindReachablePointNear
// can't verify a complete path at any search radius, the ghost still walks to the furthest point it
// CAN confirm reachable (the gap's near edge) same as before, but once it settles there it now decides
// between two ways across: if SeanceDungeonNavMesh.FindElevatorBetween finds a real DaggerfallAction
// elevator connecting the two floors, it walks onto it and Rides it (see BeginRidingElevator/
// UpdateRidingElevator/EndRidingElevator) rather than flying - confirmed via real in-dungeon testing
// that a "gap" here is often actually solid floor with a moving-platform shaft cut into it, frozen at
// bake time in whatever position it happened to occupy, and flying straight through that is not
// something the player can follow. Only when no such elevator is found does it fall back to Flying - a
// direct, un-collided transform interpolation straight to a landing point sampled near the real target
// on the other side - then hands control back to the NavMeshAgent to resume ordinary walking either
// way. Flying mirrors DFU's own vanilla flying-enemy movement (confirmed in EnemyMotor.cs: flying
// enemies just move directly toward their target via CharacterController.Move(), no pathfinding at
// all) and the temporary-flight-toggle pattern already established by Levitate.cs; Riding mirrors
// PlayerGroundMotor.MoveWithMovingPlatform's own moving-platform adhesion logic (confirmed there's no
// existing non-player-specific equivalent anywhere in the engine). Both are implemented locally here
// since this ghost's own CharacterController is disabled in favor of the NavMeshAgent by default (see
// below) - there's no vanilla EnemyMotor flight flag or PlayerMotor ActivePlatform left active to
// toggle. The CharacterController is no longer PERMANENTLY disabled, though - see Following below.
//
// Never fly through solid geometry: IsFlightPathBlocked linecasts the straight line to the flight
// landing point against the Default layer before ever committing to BeginFlight - DFU dungeon geometry
// (both the combined static mesh and standalone action objects) gets a real MeshCollider by default
// (DaggerfallUnity.Option_AddMeshColliders defaults true, confirmed via GameObjectHelper.cs), so this
// reliably distinguishes a genuinely open pit/slope (safe to fly across) from actual solid floor with
// an elevator shaft cut into it elsewhere (real in-dungeon testing showed the ghost visibly clipping
// through solid floor to reach a target one level away - not something the player can follow). If
// blocked, TryBoardNearbyElevator gets one more attempt from this closer vantage point before falling
// back to Following (below) instead of flying through the wall regardless.
//
// Following: a fourth, lowest-priority fallback, engaged only once Walking/Riding/Flying have all been
// tried and failed (see BeginFollowing's five call sites). Real in-dungeon testing found a cave-style
// dungeon room where the NavMesh bake had literally zero coverage despite confirmed nearby collider
// geometry on the correct layer - not a bake-tuning problem (extensive agentSlope/agentClimb/voxel
// experiments never fixed it), a genuine hole no amount of NavMesh parameter tuning can paper over.
// Following re-enables the CharacterController temporarily and drives it directly toward
// destinationTransform via CharacterController.Move() - real wall-sliding collision, unlike this file's
// own original pre-NavMesh movement (see the very first paragraph above) - plus a simple downward
// raycast just ahead of each step to avoid blindly walking off the exact kind of ledge/gap that put it
// in this fallback to begin with. TryReacquireNavMesh runs on the same periodic cadence as everything
// else here (reusing FindReachablePointNear exactly as-is - no new verification logic needed) and
// switches back to ordinary Walking the moment a genuinely complete path exists again.
//
// EnemyMotor.FixedUpdate() unconditionally calls ApplyGravity() every tick (not gated on having a
// Target), which drives CharacterController.Move()/SimpleMove() whenever ungrounded - left enabled,
// this would fight the NavMeshAgent's own transform writes. Both enemyMotor and the CharacterController
// are disabled outright once the agent takes over; EnemySenses stays enabled (it never moves the
// transform itself, and its controller.center/height reads for LOS raycasts stay valid even while the
// CharacterController is disabled) purely so the still-unconditional per-tick Target/SecondaryTarget
// clear keeps anything else that reads EnemySenses from thinking this ghost has picked a fight.
//
// Doors: the bake itself treats every doorway as already-open floor (see SeanceDungeonNavMesh's own
// header comment for why - forcing every door open before baking would have permanently unlocked
// them), so the NavMesh path can freely route through a closed or locked door. The REAL door object
// still needs to be physically opened as the ghost actually reaches it - a lightweight local overlap
// check every tick force-opens the nearest closed DaggerfallActionDoor once it's close enough,
// exactly as this component always has, just no longer tied to movement/obstacle detection at all.
//
// At most one Seance ghost ever exists at a time - casting again destroys the previous one
// (DespawnActive) before a new one spawns, so there's no need to ignore collision against "other"
// instances of this same component.
//
// Hostile targeting: EnemySenses.GetTargets()'s own default-settings branch (EnemyInfighting off,
// the common case) already means a hostile creature's normal target-ACQUISITION scan can only ever
// select the player, never another non-player entity - so under plain vanilla rules hostiles
// shouldn't newly notice this ghost at all. That's not airtight though: EnemyMotor.
// MakeEnemyHostileToAttacker (the RETALIATION path, used whenever one AI entity damages another)
// has no team check at all, and this heavily-modded environment has other AI-enhancing mods that
// could plausibly add their own targeting logic outside DFU's own rules. Since the ghost never
// fights back regardless, ClearHostileTargetingOfSelf periodically scans active enemies and clears
// any Target/SecondaryTarget that ends up pointing at it anyway - the same defensive "detect and
// clear" technique AllyCombatController.ClearAllyTargeting already uses for the analogous
// ally-vs-ally case.

using UnityEngine;
using UnityEngine.AI;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;

namespace VoxsNecromancyMod.AI
{
    public class SeanceGhostGuideLegacyNavMesh : MonoBehaviour
    {
        static SeanceGhostGuideLegacyNavMesh activeInstance;

        public static void DespawnActive()
        {
            if (activeInstance != null)
                GameObject.Destroy(activeInstance.gameObject);
        }

        EnemyMotor enemyMotor;
        EnemySenses enemySenses;
        DaggerfallEntityBehaviour entityBehaviour;
        CharacterController controller;
        NavMeshAgent agent;

        // Plain bool rather than a custom enum - DFU's own mod loader does a reflection scan over
        // every field in the mod assembly, and a nested enum type here made it fail outright
        // ("Field 'mode' is an enum type with a bad underlying type"), which silently broke this
        // mod's whole effect registration (Seance included) rather than just this one field.
        bool isFlying;
        Transform destinationTransform;
        Vector3 lastKnownDestination;
        bool pendingPathCheck;
        float nextRetargetTime;
        float nextHostileTargetClearTime;

        // Set once the ghost has walked to and settled at a confirmed gap edge (see
        // destinationIsApproximate) - checked each Walking tick, and cleared the moment it decides
        // whether to board a nearby elevator or fly instead.
        bool awaitingFlightTrigger;

        Vector3 flightDestination;
        float flightStartTime;
        int consecutiveFlightTimeouts;
        bool flightAutoTriggerSuspended;

        // Set while walking toward a real elevator found via SeanceDungeonNavMesh.FindElevatorBetween -
        // null the rest of the time. UpdateWalking() branches on this before running its normal
        // retarget/pendingPathCheck logic, so the walk-to-the-elevator leg never gets fought by the
        // "quest target moved" retarget check.
        DaggerfallAction boardingElevator;

        bool isRidingElevator;
        DaggerfallAction ridingElevator;
        Vector3 lastElevatorPosition;

        // Last-resort mode, lower priority than Riding/Flying - engaged only once every other option
        // has been tried and failed (see the five call sites of BeginFollowing). Direct pursuit of
        // destinationTransform via CharacterController.Move() (real wall-sliding collision, unlike this
        // mod's original pre-NavMesh movement), with a periodic background check for whether a complete
        // NavMesh path has become available again.
        bool isFollowing;
        bool hasShownFollowingMessage;
        float nextFollowRecheckTime;

        const float stopDistance = 2f;
        const float destinationMoveThreshold = 0.5f;
        const float doorCheckRadius = 2.5f;
        const float hostileTargetClearInterval = 0.5f;

        const float flightArrivalDistance = 1f;
        const float flightTurnSpeed = 240f;

        const float followTurnSpeed = 180f;

        // How often, while Following, to re-check whether a complete NavMesh path exists again -
        // reuses the same 2s cadence already established for retargetCooldown.
        const float followRecheckInterval = 2f;

        // Ledge safety check while Following: exactly the kind of gap that put the ghost into this
        // fallback mode in the first place could be directly ahead, and unlike NavMeshAgent/the old
        // AllyCombatController-style obstacle bypass, there's no pathfinding here to route around it -
        // a short downward raycast just ahead of the next step is enough to avoid blindly walking off
        // it, without the complexity (or past problems) of a full ObstacleCheck/FallCheck port.
        const float followLedgeCheckAhead = 1f;
        const float followLedgeCheckDepth = 4f;

        // Sized with headroom over a full sampleSearchRadius-diagonal flight at typical ghost speed -
        // bounds a broken/unreachable flight in time rather than letting it run forever.
        const float maxFlightDuration = 20f;

        // If flight times out this many times in a row (no real progress confirmed in between - see
        // EndFlight), stop auto-triggering flight and fall back to Following instead of flapping
        // between walk/fly forever against a pathological dungeon shape (e.g. an isolated landing
        // pocket).
        const int maxConsecutiveFlightTimeouts = 2;

        // The quest target can be a wandering NPC, not a fixed point - confirmed via diagnostic
        // logging that its raw position was drifting on almost every single check. Without a
        // cooldown, destinationMoveThreshold alone gets tripped nearly every frame while it's
        // moving, so TrySetDestination (and the fresh NavMesh sample + path recalculation it
        // triggers) kept firing over and over, before the agent could ever settle into and actually
        // walk a full path - it was perpetually restarting its route instead of making progress.
        const float retargetCooldown = 2f;

        // How far NavMesh.SamplePosition is allowed to search for the nearest walkable point to a
        // raw world position (the quest target's own Transform, or the ghost's own spawn point).
        // Confirmed via diagnostic logging that a real quest target's nearest valid point can be
        // well over 10 units away (measured 15.8 in one case) - a quest resource is placed at its
        // own natural position, not snapped to the floor, so this needs real headroom rather than a
        // small, "should be close enough" radius.
        const float sampleSearchRadius = 40f;

        void Awake()
        {
            enemyMotor = GetComponent<EnemyMotor>();
            enemySenses = GetComponent<EnemySenses>();
            entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
            controller = GetComponent<CharacterController>();

            // Must happen before the NavMeshAgent is ever asked to move anything - see the file
            // header comment for why leaving either enabled would fight the agent's own transform
            // writes.
            enemyMotor.enabled = false;
            controller.enabled = false;

            agent = gameObject.AddComponent<NavMeshAgent>();
            agent.agentTypeID = SeanceDungeonNavMesh.AgentTypeID;
            agent.radius = controller.radius > 0f ? controller.radius : 0.4f;
            agent.height = controller.height > 0f ? controller.height : 1.8f;
            agent.speed = ((entityBehaviour.Entity.Stats.LiveSpeed + PlayerSpeedChanger.dfWalkBase) * MeshReader.GlobalScale) * 1.5f;
            agent.stoppingDistance = stopDistance;
            agent.autoBraking = true;
            agent.updateRotation = true;

            // NavMeshAgent enforces its OWN transform sitting exactly on the mesh surface
            // (baseOffset defaults to 0). Under normal DFU physics, transform.position is the
            // CENTER of the CharacterController's capsule (offset by controller.center on top of
            // that), and gravity keeps the capsule's BOTTOM on the floor - so transform.position
            // normally sits at floorY + height/2 - center.y, not at floor level itself. Confirmed
            // directly against SetupDemoEnemy.cs that controller.center is only ever adjusted away
            // from Unity's own (0,0,0) default for flying or very short creatures - for a
            // normal-height Ghost it stays zero, so this isn't a no-op fallback case, it's the
            // actual common case. Without this, baseOffset defaulting to 0 leaves the ghost sitting
            // a full half-height too low - visibly buried in the floor.
            agent.baseOffset = (controller.height / 2f) - controller.center.y;

            lastKnownDestination = transform.position;
        }

        // Called once by SeanceEffect right after spawning - target may be null (no quest resource
        // currently placed in this dungeon), in which case the ghost just idles at its spawn point.
        public void Initialize(Transform target)
        {
            destinationTransform = target;
            if (target == null)
                return;

            // The ghost's spawn point (offset from the caster) may not land exactly on the baked
            // mesh surface - snap it on first, so SetDestination has a valid starting point to path
            // from instead of silently failing.
            NavMeshHit spawnHit;
            if (NavMesh.SamplePosition(transform.position, out spawnHit, sampleSearchRadius, NavMesh.AllAreas))
                agent.Warp(spawnHit.position);

            TrySetDestination(target.position);
        }

        // rawPosition is the quest resource's own actual Transform position - never guaranteed to
        // sit exactly on the baked mesh (an NPC/item is placed at its own natural height, not
        // snapped to a walkable polygon). Sampling it onto the mesh first avoids SetDestination
        // silently resolving toward the nearest reachable point to a slightly-off raw coordinate
        // instead of the geometrically correct route - the same reasoning already applied to the
        // ghost's own spawn point above.
        void TrySetDestination(Vector3 rawPosition)
        {
            lastKnownDestination = rawPosition;
            nextRetargetTime = Time.time + retargetCooldown;

            Vector3 reachablePoint = FindReachablePointNear(rawPosition, out destinationIsApproximate);

            // Check for a usable elevator from HERE, right now, before ever committing to walking
            // toward the geometric "closest point to target" edge - real testing showed that edge can
            // be a totally different, unrelated area of the dungeon (the NavMesh path toward the raw
            // target doesn't know an elevator exists at all), and by the time the ghost arrived there
            // the elevator it started right next to was no longer reachable from that overshot
            // position. Checking immediately, from the position we're already confirmed to be at,
            // avoids ever walking past a usable elevator on the way to a dead end.
            if (destinationIsApproximate && !flightAutoTriggerSuspended && TryBoardNearbyElevator(rawPosition))
                return;

            agent.SetDestination(reachablePoint);
            pendingPathCheck = true;
        }

        // Shared by TrySetDestination (checked immediately when a gap is first detected) and the
        // flight-blocked fallback in UpdateWalking (checked again right before what would otherwise be
        // a blind flight through solid geometry). Returns true and starts walking toward the elevator
        // if one is found and its own position is genuinely walkable-reachable right now.
        bool TryBoardNearbyElevator(Vector3 rawTarget)
        {
            DaggerfallAction elevator = SeanceDungeonNavMesh.FindElevatorBetween(transform.position, rawTarget);
            if (elevator == null)
                return false;

            bool elevatorApproximate;
            Vector3 boardPoint = FindReachablePointNear(elevator.transform.position, out elevatorApproximate);
            if (elevatorApproximate)
                return false;

            boardingElevator = elevator;
            agent.SetDestination(boardPoint);
            return true;
        }

        // Dungeon geometry (both the combined floor/wall mesh and standalone action objects) gets a
        // real MeshCollider by default (DaggerfallUnity.Option_AddMeshColliders defaults true, confirmed
        // via GameObjectHelper.cs) - so a straight-line check against the Default layer (the same layer
        // the NavMesh bake itself is scoped to) reliably tells whether "the gap" is genuinely open air
        // (safe to fly across) or actual solid floor with a shaft cut into it elsewhere (not safe - the
        // player can't follow through solid geometry, confirmed via real in-dungeon testing).
        bool IsFlightPathBlocked(Vector3 from, Vector3 to)
        {
            int layerMask = 1 << LayerMask.NameToLayer("Default");
            return Physics.Linecast(from, to, layerMask);
        }

        // Plain NavMesh.SamplePosition only ever returns the CLOSEST point in raw 3D space - in a
        // multi-level dungeon that's frequently the wrong floor entirely, since a point one level
        // away can easily be closer in straight-line distance than the correct point on the
        // target's own level, even though only the latter is actually walkable to. Confirmed by a
        // real case: the ghost silently reached a "complete" path that landed a full level away
        // from the true target, with no partial/invalid path status to flag anything was wrong -
        // the destination itself was just resolved to the wrong place. This instead tries
        // progressively wider search radii and, at each one, verifies the sampled point actually
        // yields a genuinely complete path from the ghost's own current position before accepting
        // it - "nearest AND reachable," not just "nearest."
        static readonly float[] reachabilitySearchRadii = { 2f, 5f, 10f, 20f, sampleSearchRadius };

        bool destinationIsApproximate;

        Vector3 FindReachablePointNear(Vector3 rawPosition, out bool isApproximate)
        {
            isApproximate = false;
            NavMeshPath path = new NavMeshPath();

            for (int i = 0; i < reachabilitySearchRadii.Length; i++)
            {
                NavMeshHit hit;
                if (!NavMesh.SamplePosition(rawPosition, out hit, reachabilitySearchRadii[i], NavMesh.AllAreas))
                    continue;

                if (NavMesh.CalculatePath(transform.position, hit.position, NavMesh.AllAreas, path)
                    && path.status == NavMeshPathStatus.PathComplete)
                {
                    return hit.position;
                }
            }

            // Nothing at any radius produced a genuinely complete path - a real gap (a vertical
            // drop/climb, or the target on an unconnected part of the mesh), not something more
            // searching would fix. Rather than falling back to another raw nearest-point sample
            // (the exact flaw above, just recurring in the "truly unreachable" case), compute a
            // direct path toward the raw target and take its own last corner - the furthest point
            // Unity can actually walk the ghost to along its own reachable region. That point is
            // guaranteed to sit on the ghost's own mesh island, so it reliably walks all the way to
            // the correct edge instead of drifting toward an unrelated nearby point on some other
            // level - "take me to the spot I need to go down," not just "somewhere close."
            isApproximate = true;
            if (NavMesh.CalculatePath(transform.position, rawPosition, NavMesh.AllAreas, path) && path.corners.Length > 0)
                return path.corners[path.corners.Length - 1];

            NavMeshHit fallbackHit;
            if (NavMesh.SamplePosition(rawPosition, out fallbackHit, sampleSearchRadius, NavMesh.AllAreas))
                return fallbackHit.position;

            return rawPosition;
        }

        // Sibling to FindReachablePointNear, used once the ghost has already settled at a confirmed
        // gap edge and is about to fly across it. Reuses the same progressively-widening search radii,
        // but drops the PathComplete verification entirely - by definition no complete path exists yet
        // (that's why flight is happening at all), so this just returns the first successful sample,
        // smallest radius first (the most accurate one available). isApproximate only comes back true
        // if sampling fails at every radius up to sampleSearchRadius - meaning no walkable floor exists
        // anywhere near the real target at all, a genuinely unconnected target rather than a crossable
        // gap.
        Vector3 FindFlightLandingPoint(Vector3 rawTargetPosition, out bool isApproximate)
        {
            for (int i = 0; i < reachabilitySearchRadii.Length; i++)
            {
                NavMeshHit hit;
                if (NavMesh.SamplePosition(rawTargetPosition, out hit, reachabilitySearchRadii[i], NavMesh.AllAreas))
                {
                    isApproximate = false;
                    return hit.position;
                }
            }

            isApproximate = true;
            return rawTargetPosition;
        }

        void BeginFlight(Vector3 landingPoint)
        {
            isFlying = true;
            flightDestination = landingPoint;
            flightStartTime = Time.time;
            agent.enabled = false;
        }

        void EndFlight(bool timedOut)
        {
            isFlying = false;
            agent.enabled = true;
            agent.Warp(transform.position);

            if (timedOut)
                consecutiveFlightTimeouts++;
            else
                consecutiveFlightTimeouts = 0;

            if (consecutiveFlightTimeouts >= maxConsecutiveFlightTimeouts)
            {
                // Still worth suspending auto-flight specifically - prevents it from immediately
                // re-triggering the instant NavMesh reconnects to some OTHER still-unreachable gap -
                // but fall back to direct pursuit now instead of just giving up outright.
                flightAutoTriggerSuspended = true;
                BeginFollowing();
                return;
            }

            Vector3 rawTarget = destinationTransform != null ? destinationTransform.position : lastKnownDestination;
            TrySetDestination(rawTarget);
        }

        // Called once boardingElevator's own walking leg has arrived. Triggers the elevator directly
        // via TriggerTypes.ActionObject (bypasses the real TriggerFlag gate entirely - see the file
        // header comment - so this works regardless of whether the elevator is really Collision01/
        // Direct/etc.) and starts riding.
        void BeginRidingElevator()
        {
            ridingElevator = boardingElevator;
            boardingElevator = null;
            isRidingElevator = true;
            lastElevatorPosition = ridingElevator.transform.position;
            agent.enabled = false;

            ridingElevator.Receive(gameObject, DaggerfallAction.TriggerTypes.ActionObject);
        }

        void EndRidingElevator()
        {
            isRidingElevator = false;
            ridingElevator = null;
            agent.enabled = true;
            agent.Warp(transform.position);

            Vector3 rawTarget = destinationTransform != null ? destinationTransform.position : lastKnownDestination;
            TrySetDestination(rawTarget);
        }

        // Called from every point where the ghost would otherwise just give up and show a message -
        // real testing found dungeons where NavMesh coverage has genuine holes no amount of bake tuning
        // fixes, so rather than stop entirely, fall back to simple direct pursuit. Only shows its own
        // HUD message the first time per ghost, even if Following is entered/exited multiple times
        // across a single Seance's lifetime.
        void BeginFollowing()
        {
            if (isFollowing)
                return;

            isFollowing = true;
            agent.enabled = false;
            controller.enabled = true;
            nextFollowRecheckTime = Time.time + followRecheckInterval;

            if (!hasShownFollowingMessage)
            {
                hasShownFollowingMessage = true;
                DaggerfallUI.AddHUDText("The shade presses on, unguided.");
            }
        }

        void EndFollowing()
        {
            isFollowing = false;
            controller.enabled = false;
            agent.enabled = true;

            NavMeshHit hit;
            if (NavMesh.SamplePosition(transform.position, out hit, sampleSearchRadius, NavMesh.AllAreas))
                agent.Warp(hit.position);

            Vector3 rawTarget = destinationTransform != null ? destinationTransform.position : lastKnownDestination;
            TrySetDestination(rawTarget);
        }

        // Reuses FindReachablePointNear exactly as-is - it already does "is there a genuinely complete
        // path from here to the raw target," the same check that normally runs right after arriving at
        // a destination. No separate verification logic needed for the reacquisition check itself.
        void TryReacquireNavMesh()
        {
            Vector3 rawTarget = destinationTransform != null ? destinationTransform.position : lastKnownDestination;
            bool isApproximate;
            FindReachablePointNear(rawTarget, out isApproximate);

            if (!isApproximate)
                EndFollowing();
        }

        void UpdateFollowing()
        {
            if (destinationTransform == null)
                return;

            Vector3 toTarget = destinationTransform.position - transform.position;
            Vector3 flatDirection = new Vector3(toTarget.x, 0f, toTarget.z);

            if (flatDirection.magnitude > stopDistance)
            {
                flatDirection.Normalize();

                Vector3 aheadPoint = transform.position + flatDirection * followLedgeCheckAhead;
                RaycastHit hit;
                bool groundAhead = Physics.Raycast(aheadPoint + Vector3.up * 0.5f, Vector3.down, out hit,
                    followLedgeCheckDepth, 1 << LayerMask.NameToLayer("Default"));

                if (groundAhead)
                {
                    Vector3 motion = flatDirection * agent.speed;
                    motion.y = -2f;
                    controller.Move(motion * Time.deltaTime);
                }

                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation, Quaternion.LookRotation(flatDirection), followTurnSpeed * Time.deltaTime);
            }

            if (Time.time >= nextFollowRecheckTime)
            {
                nextFollowRecheckTime = Time.time + followRecheckInterval;
                TryReacquireNavMesh();
            }
        }

        void OnEnable()
        {
            if (controller != null && GameManager.Instance.PlayerController != null)
                Physics.IgnoreCollision(controller, GameManager.Instance.PlayerController, true);

            activeInstance = this;
        }

        void OnDisable()
        {
            if (controller != null && GameManager.Instance.PlayerController != null)
                Physics.IgnoreCollision(controller, GameManager.Instance.PlayerController, false);

            if (activeInstance == this)
                activeInstance = null;
        }

        // NavMeshAgent's own movement isn't a physics-step operation the way CharacterController.
        // Move() was, so there's no reason to stay on the fixed-timestep cadence the old
        // capsule-cast-based movement needed.
        void Update()
        {
            enemySenses.Target = null;
            enemySenses.SecondaryTarget = null;

            if (isFollowing)
                UpdateFollowing();
            else if (isRidingElevator)
                UpdateRidingElevator();
            else if (isFlying)
                UpdateFlying();
            else
                UpdateWalking();

            ForceOpenNearbyDoor();
            ClearHostileTargetingOfSelf();
        }

        void UpdateWalking()
        {
            if (boardingElevator != null)
            {
                if (!agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + 0.05f
                    && (!agent.hasPath || agent.velocity.sqrMagnitude < 0.01f))
                    BeginRidingElevator();
                return;
            }

            if (destinationTransform != null && Time.time >= nextRetargetTime)
            {
                Vector3 currentPosition = destinationTransform.position;
                if (Vector3.Distance(currentPosition, lastKnownDestination) > destinationMoveThreshold)
                    TrySetDestination(currentPosition);
            }

            if (pendingPathCheck && !agent.pathPending)
            {
                pendingPathCheck = false;

                // Real progress confirmed (a genuinely complete path exists again) - clear the circuit
                // breaker so a later, unrelated gap elsewhere in the same dungeon isn't permanently
                // locked out of ever trying flight again.
                if (!destinationIsApproximate)
                    flightAutoTriggerSuspended = false;

                // destinationIsApproximate means FindReachablePointNear couldn't verify a genuinely
                // complete path at any search radius and fell back to the furthest point it could
                // confirm reachable - a real gap (vertical drop/climb), not something a bigger search
                // radius would fix. The ghost is already walking to that edge point as normal; once it
                // arrives (checked below) it flies across the gap instead of just stopping there.
                // agent.pathStatus itself should reliably read PathComplete for that edge point (we
                // already verified it's reachable before ever asking the agent to go there) - the
                // PathInvalid/PathPartial checks below are just a defensive fallback for anything
                // unexpected.
                if (destinationIsApproximate)
                    awaitingFlightTrigger = !flightAutoTriggerSuspended;
                else if (agent.pathStatus == NavMeshPathStatus.PathInvalid)
                    BeginFollowing();
                else if (agent.pathStatus == NavMeshPathStatus.PathPartial)
                    BeginFollowing();
            }

            if (awaitingFlightTrigger && !agent.pathPending
                && agent.remainingDistance <= agent.stoppingDistance + 0.05f
                && (!agent.hasPath || agent.velocity.sqrMagnitude < 0.01f))
            {
                awaitingFlightTrigger = false;

                Vector3 rawTarget = destinationTransform != null ? destinationTransform.position : lastKnownDestination;
                bool landingIsApproximate;
                Vector3 landingPoint = FindFlightLandingPoint(rawTarget, out landingIsApproximate);

                if (landingIsApproximate)
                {
                    BeginFollowing();
                }
                else if (IsFlightPathBlocked(transform.position, landingPoint))
                {
                    // Real solid geometry between here and the landing point, not open air - flying
                    // straight through it is exactly the "went through the floor, couldn't follow"
                    // failure this check exists to prevent. Try an elevator once more from this closer
                    // vantage point (the earlier TrySetDestination attempt ran from a different position
                    // and may not have found one usable from here); only if that also fails does the
                    // ghost fall back to direct pursuit instead.
                    if (!TryBoardNearbyElevator(rawTarget))
                        BeginFollowing();
                }
                else
                {
                    BeginFlight(landingPoint);
                }
            }
        }

        void UpdateRidingElevator()
        {
            Vector3 currentPosition = ridingElevator.transform.position;
            transform.position += currentPosition - lastElevatorPosition;
            lastElevatorPosition = currentPosition;

            if (!ridingElevator.IsPlaying())
                EndRidingElevator();
        }

        void UpdateFlying()
        {
            transform.position = Vector3.MoveTowards(transform.position, flightDestination, agent.speed * Time.deltaTime);

            Vector3 toDestination = flightDestination - transform.position;
            if (toDestination.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation, Quaternion.LookRotation(toDestination), flightTurnSpeed * Time.deltaTime);

            if (Vector3.Distance(transform.position, flightDestination) <= flightArrivalDistance)
            {
                EndFlight(timedOut: false);
                return;
            }

            if (Time.time - flightStartTime > maxFlightDuration)
                EndFlight(timedOut: true);
        }

        // Belt-and-suspenders safety net - see the file header comment for why this can't just rely
        // on EnemySenses.GetTargets()'s own default team rules alone. Runs on an interval rather than
        // every frame since it scans every currently active enemy in the dungeon, not just ones near
        // the ghost.
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
                if (senses == null)
                    continue;

                if (senses.Target == entityBehaviour)
                    senses.Target = null;
                if (senses.SecondaryTarget == entityBehaviour)
                    senses.SecondaryTarget = null;
            }
        }

        // The bake treats every doorway as already-open floor, so the path itself is never blocked
        // by a door - but the REAL door object still needs to be physically opened as the ghost
        // actually reaches it.
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

    }
}
