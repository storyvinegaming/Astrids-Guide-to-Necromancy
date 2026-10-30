// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Bakes a runtime NavMesh for the CURRENT dungeon, on demand, the first time Seance is cast there -
// this mod's first-ever use of Unity's NavMesh system. Reachable with just the engine's own built-in
// com.unity.modules.ai package (already present) - no NavMeshSurface/AI Navigation package needed,
// since only the low-level static NavMeshBuilder API is used directly.
//
// Every RDB block in a dungeon parents directly under DaggerfallDungeon's own transform (confirmed),
// and the whole dungeon is instantiated synchronously in one pass on entry (DaggerfallDungeon.
// LayoutDungeon) - no streaming, so a single bake per dungeon visit always has the complete geometry
// available. Source geometry is collected from BOTH physics MeshColliders AND render meshes, merged
// together - not either exclusively (see Bake() itself for the two separate real failure modes, one
// per collection mode, that made a single choice insufficient). This does mean coverage can depend in
// part on the user's own "Add Mesh Colliders" setting (DaggerfallUnity.Option_AddMeshColliders), which
// defaults to true (confirmed in GameObjectHelper.cs) - but since render meshes are always collected
// too regardless, a player with that setting off still gets the same bake this mod originally shipped
// with, just without the collider-side improvements. Scoped to the Default layer only, since dungeon
// wall/floor geometry has no dedicated layer of its own but every enemy (including this mod's own
// allies and the Seance ghost itself) is explicitly placed on a separate "Enemies" layer
// (SetupDemoEnemy.cs) - so this naturally excludes all actors from becoming accidental static
// obstacles baked into the mesh.
//
// Doors: DaggerfallActionDoor is its own GameObject (not baked into the surrounding wall's combined
// mesh) - excluded from the bake's source collection entirely via NavMeshBuildMarkup.ignoreFromBuild,
// so every doorway gap reads as open floor in the baked mesh regardless of a given door's actual
// locked/closed state. This is deliberately NOT "force every door open, bake, then close them back" -
// DaggerfallActionDoor.Open() unconditionally zeroes CurrentLockValue with no restore path anywhere
// in Close()/OnCompleteClose(), so that approach would have permanently unlocked every door in the
// dungeon the first time Seance was ever cast there. Excluding the door from the bake instead never
// touches its real lock/open state at all - the actual physical door still needs to be opened at
// runtime as the ghost reaches it, which SeanceGhostGuide's own door-detection already handles.
//
// AddDoorFloorPatches: excluding a door assumes the surrounding floor mesh already covers the
// threshold underneath/around it - real in-dungeon testing found at least one doorway where that
// wasn't true (NavMesh.CalculatePath had to detour through a long, unrelated chain of other floors
// instead of the short, direct route through that one door, and still failed to fully connect). Rather
// than assume every door is fine, an explicit synthetic walkable floor patch is added at every door's
// own footprint (see AddDoorFloorPatches below) so threshold continuity never depends on whatever the
// underlying floor mesh does or doesn't cover there.
//
// Bake radius/height are taken from the PLAYER's own CharacterController (GameManager.Instance.
// PlayerController), not just whatever the ghost itself happens to need - the ghost is meant to lead
// the player through the dungeon (including flying it across an unwalkable gap - see
// SeanceGhostGuide), so every walkable surface, edge point, and flight landing point this bake
// produces needs to actually be wide/tall enough for the PLAYER to physically follow through, not
// just for the ghost's own (potentially much slimmer) collision capsule. Confirmed via grep that
// PlayerMotor never mutates the controller's radius/height at runtime (no crouch-style resizing), so
// reading it once at bake time reflects the player's real, stable dimensions. minAgentRadius/
// minAgentHeight below are just a defensive floor in case PlayerController is ever unexpectedly null
// this early - not the normal case.
//
// Elevators: some "unreachable gap" cases aren't a genuine open pit/slope at all - they're a classic
// Daggerfall elevator (a DaggerfallAction-driven moving platform), frozen in whatever position it
// happened to occupy at bake time, so the space it should currently occupy elsewhere reads as an
// ordinary hole in the mesh. FindElevatorBetween lets SeanceGhostGuide check for one of these before
// falling back to flying straight across - flying through what's actually solid floor with a shaft cut
// into it is not something the player can follow, confirmed via real in-dungeon testing. There's no
// dedicated "this is an elevator" flag anywhere in DFU's own data (grepped RDBLayout.cs - nothing) -
// the heuristic below (vertical-dominant ActionTranslation of meaningful span, non-flat, currently
// idle) is a best-effort signal, not a guarantee.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;

namespace VoxsNecromancyMod.AI
{
    public static class SeanceDungeonNavMesh
    {
        // Fixed, code-only agent type - used identically for the bake settings and every
        // NavMeshAgent.agentTypeID below, so this never depends on the host game's own
        // ProjectSettings/NavMeshAreas.asset (a runtime-loaded mod can't ship/modify that asset).
        public const int AgentTypeID = 0;

        const float minAgentRadius = 0.4f;
        const float minAgentHeight = 1.8f;

        // Deliberately generous - DFU has no special-cased ramp/stair handling at all (grepped
        // RDBLayout.cs: nothing), so inter-level connections are baked directly into the same
        // static mesh geometry as everything else, and classic Daggerfall's own dungeon ramps are
        // notoriously steep. A tighter agentSlope/agentClimb here silently splits the NavMesh into
        // one disconnected island per floor - the ghost then just walks to the edge of its own
        // floor, right at the ramp it can't legally step onto, and stops (exactly the "walks to a
        // corner near the ramp down and can't proceed" symptom this was tuned to fix). agentClimb in
        // particular is what lets a staircase's individual step risers be treated as walkable steps
        // rather than each one being judged as a near-vertical wall against agentSlope.
        //
        // Raised further again (agentClimb was 2.5, agentSlope was 70) after a cave-style dungeon
        // showed a spot with confirmed nearby collider geometry (on the correct Default layer, well
        // within range - checked directly via Physics.OverlapSphere) that NavMesh sampling still
        // wouldn't mark walkable at all. Natural/organic cave floor geometry plausibly has many small
        // local facets at steeper angles than a smooth block-dungeon floor even where the room reads
        // as easily walkable overall - a real player never notices individual triangle slopes, but
        // NavMesh's per-triangle agentSlope check does.
        const float agentClimb = 4f;
        const float agentSlope = 85f;

        // Was agentRadius / 6 (finer than the NavMeshSurface component's own editor-side "auto"
        // heuristic of roughly agentRadius / 3) specifically to improve odds of rasterizing a steep or
        // chunky block-dungeon ramp correctly. Real testing in a CAVE-style dungeon then found the
        // opposite problem: two points, each individually confirmed to have nearby NavMesh coverage,
        // came back entirely disconnected (PathInvalid, not even a partial route) despite a shallow
        // ~31 degree slope between them - well within agentSlope's tolerance, so this isn't a slope
        // rejection, more likely the very fine voxel size fragmenting naturally rough/irregular cave
        // terrain into disconnected micro-islands rather than one continuous walkable surface.
        // Reverted to the coarser agentRadius / 3 heuristic as a direct experiment against that theory
        // - trades a little precision on chunky block-dungeon ramps for hopefully more robust
        // connectivity on rough organic terrain.
        const float voxelSizeDivisor = 3f;

        // Elevator detection heuristic - see the "no dedicated elevator flag exists" note in this
        // file's header comment. minVerticalRatio requires the translation to be dominated by Y
        // (filters out horizontal moving walls); minElevatorSpan requires a meaningful floor-to-floor
        // height (filters out small vertical nudges like a trap dip using the same generic component).
        const float minVerticalRatio = 0.8f;
        const float minElevatorSpan = 3f;

        // How close (in world units) the raw target's own Y needs to be to one of a candidate
        // elevator's two resting levels to count as "this elevator services the target's floor." No
        // confirmed real floor-to-floor height to calibrate against, so this is generous on purpose -
        // a quest marker isn't guaranteed to sit exactly at floor level either.
        const float floorMatchTolerance = 8f;

        static NavMeshDataInstance activeInstance;
        static bool hasActiveInstance;
        static int bakedDungeonID = int.MinValue;

        static DaggerfallDungeon GetCurrentDungeon()
        {
            return GameManager.Instance.PlayerEnterExit != null
                ? GameManager.Instance.PlayerEnterExit.Dungeon
                : null;
        }

        // Called once from SeanceEffect.DoEffect(), right after the "must be in a dungeon" gate
        // passes. Rebakes only if this is a genuinely different dungeon than the last bake (or
        // nothing has been baked yet this session) - recasting Seance repeatedly in the same
        // dungeon visit never re-bakes.
        public static void EnsureBakedForCurrentDungeon()
        {
            DaggerfallDungeon dungeon = GetCurrentDungeon();
            if (dungeon == null)
                return;

            int currentID = dungeon.Summary.ID;
            if (hasActiveInstance && currentID == bakedDungeonID)
                return;

            ClearActiveBake();
            Bake(dungeon.transform);
            bakedDungeonID = currentID;
        }

        // Hooked from VoxsNecromancy.Init() alongside its existing OnTransitionDungeonInterior
        // subscriptions - frees a stale bake the moment the player leaves for a different dungeon,
        // rather than waiting for the next Seance cast elsewhere to notice. Does NOT eagerly bake
        // the new dungeon - that still only happens lazily, inside EnsureBakedForCurrentDungeon,
        // and only if Seance is actually cast there.
        public static void OnDungeonTransition(DaggerfallDungeon newDungeon)
        {
            int newID = newDungeon != null ? newDungeon.Summary.ID : bakedDungeonID;
            if (newID != bakedDungeonID)
            {
                ClearActiveBake();
                bakedDungeonID = int.MinValue;
            }
        }

        static void ClearActiveBake()
        {
            if (hasActiveInstance)
            {
                NavMesh.RemoveNavMeshData(activeInstance);
                hasActiveInstance = false;
            }
        }

        static void Bake(Transform dungeonRoot)
        {
            Renderer[] renderers = dungeonRoot.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                VoxsNecromancy.LogError("SeanceDungeonNavMesh: no renderers found under dungeon root - nothing to bake.");
                return;
            }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            DaggerfallActionDoor[] doors = dungeonRoot.GetComponentsInChildren<DaggerfallActionDoor>();
            List<NavMeshBuildMarkup> markups = new List<NavMeshBuildMarkup>(doors.Length);
            for (int i = 0; i < doors.Length; i++)
                markups.Add(new NavMeshBuildMarkup { root = doors[i].transform, ignoreFromBuild = true });

            CharacterController playerController = GameManager.Instance.PlayerController;
            float effectiveAgentRadius = playerController != null
                ? Mathf.Max(playerController.radius, minAgentRadius)
                : minAgentRadius;
            float effectiveAgentHeight = playerController != null
                ? Mathf.Max(playerController.height, minAgentHeight)
                : minAgentHeight;

            NavMeshBuildSettings settings = new NavMeshBuildSettings
            {
                agentTypeID = AgentTypeID,
                agentRadius = effectiveAgentRadius,
                agentHeight = effectiveAgentHeight,
                agentClimb = agentClimb,
                agentSlope = agentSlope,
                overrideVoxelSize = true,
                voxelSize = effectiveAgentRadius / voxelSizeDivisor,
            };

            int defaultLayerMask = 1 << LayerMask.NameToLayer("Default");
            List<NavMeshBuildSource> sources = new List<NavMeshBuildSource>();

            // Collected from BOTH physics colliders and render meshes, not either exclusively - real
            // in-dungeon testing found two DIFFERENT failure modes, each only fixed by the OTHER
            // collection mode:
            // - A plain, doorless corridor in a block-based dungeon was entirely missing from a
            //   render-mesh bake (a plausible rasterization defect where two RDB blocks tile together -
            //   each dungeon is built from multiple blocks, see this file's own header comment) but was
            //   fine once colliders were used instead.
            // - A cave-style dungeon then showed the opposite: a whole area had NO NavMesh coverage at
            //   all under collider-only collection (not even a single samplable point within several
            //   units) - RDBLayout.cs exempts certain models from ever getting a collider at all (e.g.
            //   tapestries/banners, "to avoid problem" per its own comment, added individually via
            //   AddStandaloneModel's ignoreCollider param) specifically so the player can't get stuck on
            //   them - cave rock/terrain models plausibly get the same treatment, and colliders can't
            //   cover geometry that was never given one. Render meshes always exist regardless.
            // Collecting both and merging is strictly more permissive than either alone (a harmless
            // amount of overlapping/duplicate source geometry, not a correctness problem) - this covers
            // both known failure modes at once rather than picking whichever fixed the last one tested.
            NavMeshBuilder.CollectSources(bounds, defaultLayerMask, NavMeshCollectGeometry.PhysicsColliders, 0, markups, sources);
            int colliderSourceCount = sources.Count;
            NavMeshBuilder.CollectSources(bounds, defaultLayerMask, NavMeshCollectGeometry.RenderMeshes, 0, markups, sources);

            int patchedDoorCount = AddDoorFloorPatches(doors, sources);

            NavMeshData data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, dungeonRoot.position, dungeonRoot.rotation);
            if (data == null)
            {
                VoxsNecromancy.LogError("SeanceDungeonNavMesh: BuildNavMeshData returned null.");
                return;
            }

            activeInstance = NavMesh.AddNavMeshData(data);
            hasActiveInstance = true;

            Debug.LogFormat(
                "Vox's Necromancy: SeanceDungeonNavMesh baked dungeon {0} - {1} renderers, {2} doors excluded ({3} patched), " +
                "{4} sources ({5} from colliders, {6} from render meshes), bounds {7} (size {8}), agentRadius {9:F2} / " +
                "agentHeight {10:F2} (from player controller: {11}).",
                dungeonRoot.name, renderers.Length, doors.Length, patchedDoorCount, sources.Count,
                colliderSourceCount, sources.Count - colliderSourceCount, bounds, bounds.size,
                effectiveAgentRadius, effectiveAgentHeight, playerController != null);
        }

        // Real in-dungeon testing found a specific doorway where the direct, short route through it was
        // entirely missing from the bake - NavMesh.CalculatePath had to detour through a long, unrelated
        // chain of other floors instead, and still failed to fully connect. Excluding a door's own mesh
        // via NavMeshBuildMarkup.ignoreFromBuild (see the file header comment for why doors are excluded
        // at all) assumes the surrounding floor mesh already covers the threshold underneath/around it -
        // that's usually true, but evidently isn't always, and there's no way to tell which doors are
        // affected without inspecting each one's own real geometry, which isn't practical here. Rather
        // than guess further, this adds an explicit synthetic walkable floor patch at every door's own
        // footprint (derived from its own Renderer bounds, not a guessed constant), guaranteeing threshold
        // continuity through every doorway regardless of whatever the underlying floor mesh does or
        // doesn't cover there. A small margin extends each patch slightly past the door's own bounds so
        // it's guaranteed to overlap the surrounding floor rather than risk a hairline seam gap.
        const float doorPatchThickness = 0.3f;
        const float doorPatchMargin = 0.5f;

        static int AddDoorFloorPatches(DaggerfallActionDoor[] doors, List<NavMeshBuildSource> sources)
        {
            int patchedCount = 0;

            for (int i = 0; i < doors.Length; i++)
            {
                Renderer doorRenderer = doors[i].GetComponentInChildren<Renderer>();
                if (doorRenderer == null)
                    continue;

                Bounds bounds = doorRenderer.bounds;
                Vector3 patchCenter = new Vector3(bounds.center.x, bounds.min.y + (doorPatchThickness / 2f), bounds.center.z);
                Vector3 patchSize = new Vector3(
                    bounds.size.x + doorPatchMargin, doorPatchThickness, bounds.size.z + doorPatchMargin);

                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Box,
                    transform = Matrix4x4.TRS(patchCenter, Quaternion.identity, Vector3.one),
                    size = patchSize,
                    area = 0,
                });

                patchedCount++;
            }

            return patchedCount;
        }

        // Called by SeanceGhostGuide once it's confirmed no complete NavMesh path exists between its
        // current position and the raw target - before falling back to flying across the gap, this
        // looks for an actual DaggerfallAction elevator that already connects the two floors involved,
        // since flying straight through what's really solid floor (with an elevator shaft cut into it)
        // is exactly the "the ghost went through the floor and I couldn't follow" failure this exists
        // to avoid. No dedicated "this is an elevator" flag exists anywhere in DFU's own data (see file
        // header) - this is a best-effort heuristic, not a guarantee.
        public static DaggerfallAction FindElevatorBetween(Vector3 fromPosition, Vector3 towardPosition)
        {
            DaggerfallDungeon dungeon = GetCurrentDungeon();
            if (dungeon == null)
                return null;

            DaggerfallAction best = null;
            float bestHorizontalDistance = float.MaxValue;
            int candidateCount = 0;

            DaggerfallAction[] actions = dungeon.transform.GetComponentsInChildren<DaggerfallAction>();
            for (int i = 0; i < actions.Length; i++)
            {
                DaggerfallAction action = actions[i];

                // Flats are billboards (e.g. levers), not ride-able platforms; only a currently-idle
                // action has a stable position to walk onto and a predictable direction to trigger -
                // one already mid-transit is skipped rather than chased.
                if (action.IsFlat || action.IsPlaying())
                    continue;

                Vector3 translation = action.ActionTranslation;
                float span = translation.magnitude;
                if (span < minElevatorSpan)
                    continue;
                if (Mathf.Abs(translation.y) / span < minVerticalRatio)
                    continue;

                candidateCount++;

                float restY1 = action.StartingPosition.y;
                float restY2 = (action.StartingPosition + translation).y;

                // Does EITHER of this candidate's two resting levels service the target's own floor?
                // Deliberately says nothing about the ghost's own current position - with three or
                // more floors the ghost is very often standing on a THIRD floor unrelated to this
                // specific elevator's own two endpoints (e.g. elevator spans bottom<->center while the
                // ghost currently stands on the top floor), so anchoring the check to the ghost's own
                // Y produced false rejections of the exact right elevator. Whether the ghost can
                // actually walk there from here is verified separately (FindReachablePointNear, back
                // in SeanceGhostGuide) - that's the real reachability gate, not this Y comparison.
                float towardDist1 = Mathf.Abs(towardPosition.y - restY1);
                float towardDist2 = Mathf.Abs(towardPosition.y - restY2);
                bool servicesTargetFloor = towardDist1 <= floorMatchTolerance || towardDist2 <= floorMatchTolerance;

                if (!servicesTargetFloor)
                    continue;

                Vector2 actionXZ = new Vector2(action.transform.position.x, action.transform.position.z);
                Vector2 towardXZ = new Vector2(towardPosition.x, towardPosition.z);
                float horizontalDistance = Vector2.Distance(actionXZ, towardXZ);

                if (horizontalDistance < bestHorizontalDistance)
                {
                    bestHorizontalDistance = horizontalDistance;
                    best = action;
                }
            }

            Debug.LogFormat(
                "Vox's Necromancy: SeanceDungeonNavMesh FindElevatorBetween scanned {0} actions ({1} elevator-shaped) - " +
                "result: {2}.",
                actions.Length, candidateCount, best != null ? best.name : "none found");

            return best;
        }
    }
}
