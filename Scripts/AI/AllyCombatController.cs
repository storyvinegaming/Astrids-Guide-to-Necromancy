// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Attached to every allied undead - reanimated or subjugated alike. Works alongside DFU's existing
// EnemySenses/EnemyMotor (which already handle ally-vs-hostile-vs-player targeting rules for free
// via MobileTeams.PlayerAlly - see EnemySenses.GetTargets()) rather than replacing them:
//   - Focus-fire: every LateUpdate, acts on whatever VoxsFocusFireBlackboard says to focus - a soft
//     one-shot MakeEnemyHostileToAttacker() nudge whenever the player's own attack just landed on
//     something (see PlayerAttackWatcher).
//   - Follow when idle: when not currently pursuing a visible target, walk toward the player.
//     There's no lighter built-in hook for this (EnemyMotor has no public "just walk toward X"
//     entry point - its movement is entirely tied to its own pursuit state machine), so this
//     follows the same shape as ChebsNecromancyPacified/Scripts/UndeadMinion.cs's own
//     FollowPlayer/AttemptMove/ObstacleCheck/FallCheck, adjusted to target the player directly
//     instead of the camera.

using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Utility;

namespace VoxsNecromancyMod.AI
{
    public class AllyCombatController : MonoBehaviour
    {
        // Every currently active allied undead (Reanimate or Subjugate alike, since both attach this
        // component) registers here - no cap enforcement anymore (see MagickaUpkeep below), just a
        // live roster for counting/summing purposes.
        static readonly List<AllyCombatController> activeAllies = new List<AllyCombatController>();

        // Exposed for VoxsNecromancyEffect's own "you have X undead servants" HUD message after each
        // Reanimate cast.
        public static int ActiveAllyCount => activeAllies.Count;

        // This specific ally's own upkeep cost - computed ONCE (VoxsNecromancy.CalculateMagickaUpkeep)
        // at the moment this ally entered service (ReanimationSpawner.CreateUndead for a
        // fresh/restored reanimation, SubjugateUndeadEffect for a freshly subjugated one) and never
        // recomputed afterward, so it stays fixed for this ally's whole service regardless of the
        // caster's Mysticism changing later. Purely a budget-accounting number - see VoxsNecromancy.
        // CanAffordUpkeep/DescribeUpkeepBudget. Nothing in this mod ever modifies MaxMagicka/
        // MaxMagickaModifier; the player's real Magicka behaves exactly as vanilla.
        public int MagickaUpkeep;

        // Sum of every currently active ally's own MagickaUpkeep - the budget check
        // (VoxsNecromancy.CanAffordUpkeep) compares this plus a prospective new cost against
        // VoxsNecromancy.GetUpkeepBudget() (MAX Magicka + Mysticism skill).
        public static int TotalUpkeep()
        {
            int total = 0;
            for (int i = 0; i < activeAllies.Count; i++)
                total += activeAllies[i].MagickaUpkeep;

            return total;
        }

        // Defensive copy for the "list_upkeep" console command - callers outside this class should
        // never hold/mutate the live activeAllies list directly.
        public static AllyCombatController[] GetActiveAlliesSnapshot()
        {
            return activeAllies.ToArray();
        }

        // Called when the player explicitly chose to raise/subjugate a SPECIFIC undead type (a
        // fixed-tier Reanimate spell, or Subjugate Undead) and its own upkeep would otherwise push
        // the total over budget. Honors that explicit choice by killing the OLDEST currently active
        // ally (any type, in creation order - see activeAllies's own header comment) repeatedly until
        // there's room, rather than refusing the cast outright. Callers are expected to have already
        // confirmed additionalCost alone is <= VoxsNecromancy.GetUpkeepBudget() (i.e. this is
        // achievable at all) before calling this - it does not check that itself, so a caller that
        // skips the check would end up culling the entire active roster for nothing.
        //
        // Deliberately NOT used by the cascading "Reanimate" spell (ReanimateEffect.cs) - that spell
        // already only ever selects a tier that fits within the CURRENT budget without displacing
        // anything (VoxsNecromancy.SelectBestAvailableUndeadType), so this situation never arises for
        // it in the first place.
        public static void CullOldestUntilAffordable(int additionalCost)
        {
            while (TotalUpkeep() + additionalCost > VoxsNecromancy.GetUpkeepBudget() && activeAllies.Count > 0)
            {
                AllyCombatController oldest = activeAllies[0];
                activeAllies.RemoveAt(0);
                CullAlly(oldest);
            }
        }

        // Called by the cascading "Reanimate" spell specifically (ReanimateEffect.cs) when the budget
        // is maxed out entirely - no tier, even the cheapest, has any headroom
        // (VoxsNecromancy.SelectBestAvailableUndeadType returned null). Rather than failing the cast,
        // retires the single oldest active ally (any type, in creation order) and reports what type
        // it actually was (read straight off its own EnemyEntity.MobileEnemy.ID rather than
        // ReanimatedLifetime.FoeType, since a subjugated-from-hostile ally may never have had a
        // ReanimatedLifetime component at all) so the caller can raise an exact replacement in its
        // place - a straight swap, not a net increase in legion size. Returns false (no-op, nothing
        // killed) if there's no ally to retire, or its entity is unexpectedly gone.
        public static bool TryRetireOldestForReplacement(out MobileTypes foeType)
        {
            foeType = default(MobileTypes);
            if (activeAllies.Count == 0)
                return false;

            AllyCombatController oldest = activeAllies[0];
            EnemyEntity enemyEntity = oldest.entityBehaviour != null ? oldest.entityBehaviour.Entity as EnemyEntity : null;
            if (enemyEntity == null)
                return false;

            foeType = (MobileTypes)enemyEntity.MobileEnemy.ID;

            activeAllies.RemoveAt(0);
            CullAlly(oldest);
            return true;
        }

        // Immediate, real death (SetHealth(0), same primitive ReanimatedLifetime's own expiry uses) -
        // routes through the normal OnDeath -> EnemyDeath.CompleteDeath() flow, so it's a real death
        // with a corpse, not a silent despawn. Caller must already have removed ally from
        // activeAllies (see CullOldestUntilAffordable above) - not done here, since OnDisable firing
        // from SetHealth(0) may lag behind by a frame or more depending on the death
        // animation/EnemyDeath pipeline, and TotalUpkeep() needs to reflect the cull immediately.
        static void CullAlly(AllyCombatController ally)
        {
            DaggerfallEntityBehaviour behaviour = ally.entityBehaviour;
            if (behaviour == null || behaviour.Entity == null || behaviour.Entity.CurrentHealth <= 0)
                return;

            EnemyEntity enemyEntity = behaviour.Entity as EnemyEntity;
            if (enemyEntity != null)
            {
                string name = TextManager.Instance.GetLocalizedEnemyName(enemyEntity.MobileEnemy.ID);
                DaggerfallUI.AddHUDText(string.Format("Your hold falters - the {0} collapses.", name));
            }

            behaviour.Entity.SetHealth(0);
        }

        EnemyMotor enemyMotor;
        EnemySenses enemySenses;
        DaggerfallEntityBehaviour entityBehaviour;
        CharacterController controller;
        MobileUnit mobile;

        float originalHeight;
        bool obstacleDetected;
        bool fallDetected;
        bool foundUpwardSlope;
        bool foundDoor;
        Vector3 destination;

        const float stopDistance = 4f;
        const float turnSpeed = 20f;
        const float teleportDistance = 20f;

        int ignoreMask;

        // ---- Engagement tiers ---------------------------------------------------------------------
        //
        // Plain byte constants, not an enum - matches SeanceMoveType's own pattern elsewhere in this
        // mod (a nested enum once broke the mod loader's reflection scan). currentTier is diagnostic
        // state, not itself a control-flow switch - FixedUpdate's own priority chain is what actually
        // decides behaviour each tick.
        const byte TierEngage = 0;
        const byte TierFollow = 1;
        byte currentTier = TierFollow;

        // Diagnostic only - logs every senses.Target change with enough context (LOS, GiveUpTimer,
        // tier) to see exactly how a stuck-on-a-wall commitment happened, if one still does. A cheap
        // reference comparison, not gated on anything.
        DaggerfallEntityBehaviour lastLoggedTarget;

        // Tier 1 (ENGAGE) is always on, dungeons and overland - a hostile in the same room does not
        // need a spell to be worth fighting. Scanned on a short interval rather than every tick since
        // it walks the whole active-enemy list (see
        // FindEngageTarget) - 0.15s is the same order of magnitude as PlayerAttackWatcher's own 0.1s
        // poll, just slightly wider since this does more per-candidate work (a distance check plus an
        // EnemySenses lookup, not just a health delta).
        const float engageRadius = 20f;
        const float engageScanInterval = 0.15f;
        float nextEngageScanTime;

        // Player-centric eligibility gate (spec: "Stop Engaging Things That Can't Threaten the
        // Player", Step 1). engageRadius above is the ally's own reach limit and stays as-is; this is
        // a SEPARATE test that a hostile must also pass: either it's close enough to the player to
        // matter, or it's already attacking the player right now (an archer across a room still
        // counts regardless of distance). Distance-to-ally alone says nothing about whether a target
        // is a real threat - a wall that separates the ally from a rat almost always separates the
        // player from it too, so this is a direct measure of "can this thing threaten the player"
        // rather than the indirect proxy engageRadius/LOS was standing in for.
        const float engagePlayerRadius = 15f;

        void Awake()
        {
            enemyMotor = GetComponent<EnemyMotor>();
            enemySenses = GetComponent<EnemySenses>();
            entityBehaviour = GetComponent<DaggerfallEntityBehaviour>();
            controller = GetComponent<CharacterController>();
            mobile = GetComponentInChildren<MobileUnit>();
            originalHeight = controller.height;
            ignoreMask = ~(1 << LayerMask.NameToLayer("SpellMissiles") | 1 << LayerMask.NameToLayer("Ignore Raycast"));
        }

        // Lets the player walk straight through an ally instead of getting physically blocked by it
        // in a doorway/hallway - CharacterController IS a Collider in Unity, so this is a normal
        // pairwise ignore, not a layer-wide change (allies still collide with the player's other
        // colliders, e.g. weapon hitboxes, and still collide with everything else normally).
        // Unconditional - the player should always be able to walk through their own legion.
        //
        // Ally-vs-ally collision is a separate, OPT-IN case (VoxsNecromancy.
        // AlliedUndeadIgnoreEachOtherCollision, off by default): normally a legion physically jostles
        // for space like any other pack of creatures; enabling the setting extends the same
        // pass-through treatment pairwise to every other active ally, so they never jam up a corridor.
        //
        // OnEnable/OnDisable (rather than Awake/OnDestroy) so this is correctly restored the moment
        // this component goes away for ANY reason - SetActive(false) on death (ReanimatedLifetime's
        // own expiry, Dismiss Undead), or Destroy(this component) specifically (SubjugationTimer
        // reverting an ally back to hostile) - both fire OnDisable even though only the second one
        // also fires OnDestroy.
        void OnEnable()
        {
            if (controller != null && GameManager.Instance.PlayerController != null)
                Physics.IgnoreCollision(controller, GameManager.Instance.PlayerController, true);

            if (VoxsNecromancy.AlliedUndeadIgnoreEachOtherCollision)
                SetIgnoreCollisionWithOtherAllies(true);

            activeAllies.Add(this);
        }

        void OnDisable()
        {
            if (controller != null && GameManager.Instance.PlayerController != null)
                Physics.IgnoreCollision(controller, GameManager.Instance.PlayerController, false);

            // Distinguishes a genuine death (SetHealth(0) already ran before whatever deactivated
            // this GameObject/component - ReanimatedLifetime's own expiry, combat death, Dismiss
            // Undead, or CullAlly above) from a non-death removal that ALSO fires OnDisable: a
            // SubjugationTimer reversion (the creature survives, just stops being allied) or a
            // scene-transition teardown (RecordActiveReanimations deactivates the GameObject to
            // snapshot it, then restores it moments later on the other side - never actually dead in
            // between). Only a real death gets the "legion just got smaller" message below; the other
            // two would be misleading.
            bool died = entityBehaviour == null || entityBehaviour.Entity == null || entityBehaviour.Entity.CurrentHealth <= 0;

            activeAllies.Remove(this);

            if (VoxsNecromancy.AlliedUndeadIgnoreEachOtherCollision)
                SetIgnoreCollisionWithOtherAllies(false);

            if (died)
            {
                DaggerfallUI.AddHUDText(string.Format("Your legion numbers {0}. Upkeep: {1}.",
                    ActiveAllyCount, VoxsNecromancy.DescribeUpkeepBudget()));
            }
        }

        void SetIgnoreCollisionWithOtherAllies(bool ignore)
        {
            if (controller == null)
                return;

            foreach (AllyCombatController other in activeAllies)
            {
                if (other == this || other.controller == null)
                    continue;

                Physics.IgnoreCollision(controller, other.controller, ignore);
            }
        }

        void FixedUpdate()
        {
            ClearAllyTargeting();

            if (enemySenses.Target != lastLoggedTarget)
            {
                lastLoggedTarget = enemySenses.Target;
                if (lastLoggedTarget == null)
                {
                    Debug.LogFormat("Astrid's Guide to Necormancy: Ally target cleared - ally={0}, time={1:F2}.",
                        GetInstanceID(), Time.time);
                }
                else
                {
                    bool los = HasLineOfSight(transform.position, lastLoggedTarget.transform.position);
                    Debug.LogFormat(
                        "Astrid's Guide to Necormancy: Ally target changed to '{0}' - ally={1}, LOS={2}, GiveUpTimer={3}, " +
                        "tier={4}, time={5:F2}.",
                        lastLoggedTarget.name, GetInstanceID(), los, enemyMotor.GiveUpTimer, currentTier, Time.time);
                }
            }

            // Committed via the SAME condition EnemyMotor's own HandleNoAction() uses to decide
            // whether IT is still actively pursuing (Target set, GiveUpTimer > 0) - not the raw,
            // per-tick TargetInSight this used to check. Traced in Step 0: TargetInSight is a fresh
            // line-of-sight raycast every FixedUpdate with no hysteresis of its own, while EnemyMotor's
            // own persistence (GiveUpTimer, reset to 200 whenever DetectedTarget is true, decremented
            // only while it isn't) is far more forgiving. Gating on the raw flag made this class
            // abandon and start moving the ally itself on a bare sight flicker - while EnemyMotor,
            // whose own gate never flickered, was very likely STILL actively driving the SAME
            // CharacterController toward the SAME target that same frame (EnemyMotor.TakeAction calls
            // its own private AttemptMove -> controller.Move, gated only by HandleNoAction's
            // Target/GiveUpTimer check, never by TargetInSight). Gating on the identical condition
            // EnemyMotor itself uses means the two can never disagree about whether a fight is still
            // on - and this class makes NO movement call at all while committed, handing movement
            // entirely to EnemyMotor as intended.
            if (enemySenses.Target != null && enemyMotor.GiveUpTimer > 0)
            {
                // Vanilla EnemySenses.GetTargets() runs independently of anything in this file, on its
                // own ~0.87s real-time cadence (measured: EnemySenses.cs's classicTargetUpdateTimer
                // divisor works out to that, not the "~5s" this comment previously assumed), and can
                // assign Target through a wall on its own: its own rejection test is
                // "if (targetSenses && !targetSenses.WouldBeSpawnedInClassic && !see) continue;" -
                // WouldBeSpawnedInClassic is a proximity check relative to the PLAYER, not a line-of-
                // sight one, so a candidate merely near the player is accepted even when !see (the real
                // LOS raycast) is false. FindEngageTarget's own filters never see this acquisition at
                // all, since it happens entirely inside EnemySenses, not through
                // MakeEnemyHostileToAttacker.
                //
                // A LOS-only check here is not enough: a target can have a genuine sight-line (a gap, a
                // barred window, a dungeon block seam) without there being any walkable route to it, in
                // which case this branch would otherwise commit and return every tick forever - handing
                // 100% of movement to vanilla EnemyMotor, which has no pathfinding and just walks
                // straight at the target, forever, since GiveUpTimer never decays while DetectedTarget
                // stays true. Gating on whether the target can actually threaten the PLAYER (close to
                // them, or already attacking them) rather than just whether this ally can see it is a
                // direct fix for that: a wall separating the ally from a rat almost always separates
                // the player from it too, so this kills the false-commit case without needing stuck
                // detection.
                DaggerfallEntityBehaviour committedTarget = enemySenses.Target;
                DaggerfallEntityBehaviour player = GameManager.Instance.PlayerEntityBehaviour;
                EnemySenses committedTargetSenses = committedTarget.GetComponent<EnemySenses>();
                bool committedAttackingPlayer = committedTargetSenses != null && committedTargetSenses.Target == player;
                bool committedNearPlayer = (committedTarget.transform.position - player.transform.position).sqrMagnitude
                    <= engagePlayerRadius * engagePlayerRadius;

                if ((committedAttackingPlayer || committedNearPlayer)
                    && HasLineOfSight(transform.position, committedTarget.transform.position))
                {
                    currentTier = TierEngage;
                    return;
                }

                // Not a real threat to the player (or not visible to THIS ally) - clear vanilla's own
                // acquisition rather than trust it, so Tier 1/2/3 below get a chance to run instead of
                // standing still or walking into a wall forever. Vanilla can re-acquire the same target
                // on its own next ~0.87s cycle, but this check runs every physics tick, so in practice
                // that reads as a brief flicker rather than a persistent stuck state.
                enemySenses.Target = null;
                enemySenses.SecondaryTarget = null;
            }

            // Tier 1 - ENGAGE: nothing currently committed. Look for a hostile within close range and
            // nudge onto it via the same soft idiom the focus-fire case already uses - always on,
            // dungeons and overland.
            if (Time.time >= nextEngageScanTime)
            {
                nextEngageScanTime = Time.time + engageScanInterval;

                DaggerfallEntityBehaviour engageTarget = FindEngageTarget();
                if (engageTarget != null)
                {
                    currentTier = TierEngage;
                    enemyMotor.MakeEnemyHostileToAttacker(engageTarget);
                    return;
                }
            }

            // Tier 2 - FOLLOW.
            currentTier = TierFollow;
            FollowPlayer();
        }

        // VoxsFocusFireBlackboard holds whatever the player's own attack just landed on (set by
        // PlayerAttackWatcher) - a single EnemyMotor.MakeEnemyHostileToAttacker() nudge per LateUpdate.
        // It sets Target once, but EnemySenses.FixedUpdate()'s own periodic re-acquisition scan
        // (GetTargets(), roughly once a second) is free to overwrite it again right after, which is
        // exactly what we want: allies should keep responding to whatever is actually attacking them
        // or the player right now, not stay welded to whatever the player hit a few seconds ago.
        //
        // No longer touches the player's Magicka/MaxMagicka in any way - MagickaUpkeep here is purely
        // a budget-accounting number (see VoxsNecromancy.CanAffordUpkeep), not something reapplied to
        // the entity each frame. An earlier version of this system did exactly that (a
        // MaxMagickaModifier constant-effect reduction) and it was removed: MaxMagicka now behaves
        // exactly as vanilla.
        void LateUpdate()
        {
            ClearAllyTargeting();

            DaggerfallEntityBehaviour focusTarget = VoxsFocusFireBlackboard.GetValidTarget();
            if (focusTarget == null || focusTarget == entityBehaviour || focusTarget == GameManager.Instance.PlayerEntityBehaviour)
                return;

            enemyMotor.MakeEnemyHostileToAttacker(focusTarget);
        }

        // Two distinct engine-side ways an ally's Target/SecondaryTarget can end up pointing at
        // another ally, neither of which we can patch directly (base engine code, no Harmony here):
        //   - Vanilla retaliation (EnemyMotor.MakeEnemyHostileToAttacker, called from EnemyAttack.cs
        //     whenever one AI entity damages another) has no team check at all - AOE splash between
        //     allies, or a creature that was mid-fight the instant Subjugate Undead flipped its Team,
        //     would otherwise lock the damaged ally onto its own ally as a hostile Target.
        //   - EnemySenses.GetTargets()'s own candidate-scoring only excludes same-team candidates
        //     when DaggerfallUnity.Settings.EnemyInfighting is on (off by default) - and separately,
        //     whichever ally resolves a target this way reciprocally writes THAT target's own
        //     Target/SecondaryTarget back to itself, with no team check at all on that write. Because
        //     this reciprocal write happens from the OTHER ally's own FixedUpdate, not this one's,
        //     and Unity doesn't guarantee FixedUpdate ordering across different components, a bad
        //     assignment can land on this ally after ITS OWN FixedUpdate already ran this physics
        //     step - hence calling this from LateUpdate too (which always runs after every
        //     component's FixedUpdate/Update for the frame, regardless of ordering) rather than
        //     relying on FixedUpdate alone.
        // Simply clearing an already-set ally Target is safe either way - it won't be reacquired on
        // its own (nothing here re-derives a target from scratch).
        void ClearAllyTargeting()
        {
            if (IsAlly(enemySenses.Target))
                enemySenses.Target = null;

            if (IsAlly(enemySenses.SecondaryTarget))
                enemySenses.SecondaryTarget = null;
        }

        static bool IsAlly(DaggerfallEntityBehaviour target)
        {
            return target != null && target.Entity != null && target.Entity.Team == MobileTeams.PlayerAlly;
        }

        void FollowPlayer()
        {
            Transform playerTransform = GameManager.Instance.PlayerEntityBehaviour.transform;
            float moveSpeed = ((entityBehaviour.Entity.Stats.LiveSpeed + PlayerSpeedChanger.dfWalkBase) * MeshReader.GlobalScale) * 1.5f;

            destination = playerTransform.position;

            Vector3 direction = (destination - transform.position).normalized;
            float distance = (destination - transform.position).magnitude;

            if (distance >= teleportDistance)
            {
                TeleportToPlayer(playerTransform);
                return;
            }

            if (distance >= stopDistance)
                AttemptMove(direction, moveSpeed);
            else if (!enemySenses.TargetIsWithinYawAngle(22.5f, destination))
                TurnToTarget(direction);
        }

        // Catches an ally that's fallen too far behind to ever catch up by walking (stuck on
        // geometry, left behind by fast travel/mounted movement, lost through a door the player
        // closed) - only reachable from FollowPlayer(), so it never interrupts active combat.
        // CharacterController must be disabled while moving the transform directly, or its own
        // collision resolution can fight the position change or ignore it outright.
        void TeleportToPlayer(Transform playerTransform)
        {
            Vector3 offset = UnityEngine.Random.insideUnitSphere * 3f;
            offset.y = 0f;
            Vector3 newPosition = playerTransform.position + offset;

            controller.enabled = false;
            transform.position = newPosition;
            controller.enabled = true;
        }

        // Called by the repurposed Command Undead spell - instantly gathers every currently active
        // ally to the player's side, wherever they currently are. Unconditional: an earlier version
        // (when this backed the old "Hunt the Living" recall) skipped anything whose LOS check read
        // true at the exact instant of casting, on the theory that it meant a "real fight" - but a
        // diagnostic log showed LOS to a given target flickering true/false multiple times per second
        // right at doorway/corner boundaries, exactly where allies got visually stuck, which made that
        // snapshot check misclassify a stuck ally as "fighting" and skip it. Unconditional avoids that
        // class of bug entirely and matches the spell's own point - the player wants everyone at their
        // side right now, no exceptions.
        public static void TeleportAllToPlayer()
        {
            DaggerfallEntityBehaviour player = GameManager.Instance.PlayerEntityBehaviour;
            if (player == null)
                return;

            for (int i = 0; i < activeAllies.Count; i++)
            {
                AllyCombatController ally = activeAllies[i];
                if (ally == null)
                    continue;

                ally.enemySenses.Target = null;
                ally.enemySenses.SecondaryTarget = null;
                ally.TeleportToPlayer(player.transform);
            }
        }

        void AttemptMove(Vector3 direction, float moveSpeed)
        {
            if (!enemySenses.TargetIsWithinYawAngle(5.625f, destination))
                TurnToTarget(direction);

            if (!CanFly() && !enemyMotor.IsLevitating && controller.isGrounded)
                direction.y = -2f;

            Vector3 motion = direction * moveSpeed;

            Vector3 direction2d = direction;
            if (!CanFly() && !enemyMotor.IsLevitating)
                direction2d.y = 0;

            ObstacleCheck(direction2d);
            FallCheck(direction2d);

            if (fallDetected || obstacleDetected)
                transform.position = Vector3.MoveTowards(transform.position, destination, moveSpeed * Time.deltaTime * 0.5f);
            else
                controller.Move(motion * Time.deltaTime);
        }

        void TurnToTarget(Vector3 targetDirection)
        {
            transform.forward = Vector3.RotateTowards(transform.forward, targetDirection, turnSpeed * Mathf.Deg2Rad, 0.0f);
        }

        bool CanFly()
        {
            return mobile.Enemy.Behaviour == MobileBehaviour.Flying || mobile.Enemy.Behaviour == MobileBehaviour.Spectral;
        }

        void FallCheck(Vector3 direction)
        {
            if (CanFly() || enemyMotor.IsLevitating || obstacleDetected || foundUpwardSlope || foundDoor)
            {
                fallDetected = false;
                return;
            }

            Vector3 rayOrigin = transform.position + direction;
            Ray ray = new Ray(rayOrigin, Vector3.down);
            RaycastHit hit;
            fallDetected = !Physics.Raycast(ray, out hit, (originalHeight * 0.5f) + 1.5f);
        }

        void ObstacleCheck(Vector3 direction)
        {
            obstacleDetected = false;
            foundUpwardSlope = false;
            foundDoor = false;
            float checkDistance = controller.radius / Mathf.Sqrt(2f);

            Vector3 p1 = transform.position + (Vector3.up * -originalHeight * 0.1388f);
            Vector3 p2 = p1 + (Vector3.up * originalHeight * 0.5f);

            RaycastHit hit;
            if (Physics.CapsuleCast(p1, p2, controller.radius / 2, direction, out hit, checkDistance, ignoreMask))
            {
                obstacleDetected = true;

                DaggerfallEntityBehaviour hitEntity = hit.transform.GetComponent<DaggerfallEntityBehaviour>();
                DaggerfallActionDoor door = hit.transform.GetComponent<DaggerfallActionDoor>();

                if (hitEntity != null)
                {
                    if (hitEntity == GameManager.Instance.PlayerEntityBehaviour)
                        obstacleDetected = false;
                }
                else if (door != null)
                {
                    obstacleDetected = false;
                    foundDoor = true;
                    if (enemySenses.TargetIsWithinYawAngle(22.5f, door.transform.position))
                    {
                        enemySenses.LastKnownDoor = door;
                        enemySenses.DistanceToDoor = Vector3.Distance(transform.position, door.transform.position);
                    }
                }
                else if (!CanFly() && !enemyMotor.IsLevitating)
                {
                    Vector3 checkUp = transform.position + direction;
                    checkUp.y++;
                    Vector3 upDirection = (checkUp - transform.position).normalized;
                    Vector3 up1 = transform.position + (Vector3.up * -originalHeight * 0.25f);
                    Vector3 up2 = up1 + (Vector3.up * originalHeight * 0.75f);

                    if (!Physics.CapsuleCast(up1, up2, controller.radius / 2, upDirection, checkDistance))
                    {
                        obstacleDetected = false;
                        foundUpwardSlope = true;
                    }
                }
            }
        }

        // Same team/alive-check idiom PlayerAttackWatcher.Poll already uses for its own enemy scan,
        // plus an EnemyMotor.IsHostile check on top. Team alone isn't enough: a creature pacified or
        // recruited by something other than this mod - vanilla Pacify/Charm, a quest's "restrained"
        // foe, or another mod's own taming system entirely (e.g. Language Skills Overhaul's
        // Necromancy persuasion skill converting a hostile undead into a follower) - never gets its
        // Team changed to PlayerAlly, only IsHostile cleared. IsHostile is the actual persistent
        // disposition flag every one of those mechanisms writes to (this mod's own
        // SubjugateUndeadEffect does the same when subjugating an undead), so it's the correct
        // generic "is this genuinely still hostile" signal, not Team by itself. A creature that
        // simply hasn't noticed the player yet keeps whatever IsHostile it spawned with - this does
        // not blunt Tier 1's proactive engage against ordinary, still-hostile monsters.
        static bool IsAliveHostile(DaggerfallEntityBehaviour behaviour)
        {
            if (behaviour == null || behaviour.Entity == null || behaviour.Entity.CurrentHealth <= 0)
                return false;

            EnemyEntity enemyEntity = behaviour.Entity as EnemyEntity;
            if (enemyEntity == null || enemyEntity.Team == MobileTeams.PlayerAlly)
                return false;

            EnemyMotor hostileMotor = behaviour.GetComponent<EnemyMotor>();
            return hostileMotor != null && hostileMotor.IsHostile;
        }

        // Wraps ActiveGameObjectDatabase.GetActiveEnemyBehaviours() with the team/alive filter
        // FindEngageTarget needs, in one place.
        static List<DaggerfallEntityBehaviour> ScanAliveHostiles()
        {
            List<DaggerfallEntityBehaviour> result = new List<DaggerfallEntityBehaviour>();
            foreach (DaggerfallEntityBehaviour behaviour in ActiveGameObjectDatabase.GetActiveEnemyBehaviours())
            {
                if (IsAliveHostile(behaviour))
                    result.Add(behaviour);
            }

            return result;
        }

        // Solid dungeon/building architecture - same "Default" layer this mod's own Seance voxel grid
        // treats as blocking geometry (AI/SeanceDungeonGrid.cs blockingMask).
        static readonly int engageLineOfSightMask = 1 << LayerMask.NameToLayer("Default");

        // A single ray at one fixed eye-height can clip a doorframe/lintel even when the doorway is
        // genuinely open and a player could see straight through it - measured live: allies would not
        // engage through an open door. Same "one fixed sample sometimes clips architecture" problem
        // this mod's own Seance grid solved repeatedly with small offset clusters (freeSampleOffsets,
        // IsClimbableLedge, the support-probe confirmation rays) - not an exact per-creature
        // measurement, just several plausible heights so a doorway only has to be clear at one of them.
        static readonly float[] engageEyeHeightFractions = { 0.5f, 0.75f, 0.9f };

        // Tier 1 must not engage a hostile through a wall just because it's within straight-line
        // range - a hostile in the next room can be well within engageRadius. "The current room" in
        // practice means a clear line to it. Without this, MakeEnemyHostileToAttacker commits the
        // ally to a target it can approach but never truly reach or lose - it never re-uncommits, so
        // it never resumes following.
        bool HasLineOfSight(Vector3 selfBase, Vector3 targetBase)
        {
            for (int i = 0; i < engageEyeHeightFractions.Length; i++)
            {
                Vector3 fromEye = selfBase + Vector3.up * (originalHeight * engageEyeHeightFractions[i]);
                Vector3 toEye = targetBase + Vector3.up * (originalHeight * engageEyeHeightFractions[i]);
                if (!Physics.Linecast(fromEye, toEye, engageLineOfSightMask, QueryTriggerInteraction.Ignore))
                    return true;
            }

            return false;
        }

        // Tier 1 target selection, in priority order: (1) hostiles whose OWN senses.Target is the
        // player - actively attacking the player right now, (2) hostiles nearest the player, (3)
        // hostiles nearest this ally. All three categories are drawn only from hostiles within
        // engageRadius of THIS ally, that ALSO either threaten the player (within engagePlayerRadius
        // of them, or already attacking them) AND have a clear line of sight to this ally - ties within
        // a priority are broken by nearest-to-self, which isn't specified by the spec but is a
        // reasonable default. The player is intercepted before "whatever happens to be two metres
        // closer to me."
        //
        // The player-threat gate exists because engageRadius/LOS alone measure "can THIS ally reach
        // it," not "can this thing threaten the player" - a rat visible through a gap or a dungeon
        // block seam can pass both of those while being nowhere near the player, which is exactly the
        // reported false-engage case.
        DaggerfallEntityBehaviour FindEngageTarget()
        {
            List<DaggerfallEntityBehaviour> hostiles = ScanAliveHostiles();
            DaggerfallEntityBehaviour player = GameManager.Instance.PlayerEntityBehaviour;
            Vector3 playerPosition = player.transform.position;
            float engageRadiusSq = engageRadius * engageRadius;
            float engagePlayerRadiusSq = engagePlayerRadius * engagePlayerRadius;

            DaggerfallEntityBehaviour attackingPlayer = null;
            float attackingPlayerSelfDistSq = float.MaxValue;
            DaggerfallEntityBehaviour nearestToPlayer = null;
            float nearestToPlayerDistSq = float.MaxValue;
            DaggerfallEntityBehaviour nearestToSelf = null;
            float nearestToSelfDistSq = float.MaxValue;

            for (int i = 0; i < hostiles.Count; i++)
            {
                DaggerfallEntityBehaviour hostile = hostiles[i];
                float selfDistSq = (hostile.transform.position - transform.position).sqrMagnitude;
                if (selfDistSq > engageRadiusSq)
                    continue;

                EnemySenses hostileSenses = hostile.GetComponent<EnemySenses>();
                bool attackingPlayerNow = hostileSenses != null && hostileSenses.Target == player;
                float playerDistSq = (hostile.transform.position - playerPosition).sqrMagnitude;

                if (!attackingPlayerNow && playerDistSq > engagePlayerRadiusSq)
                    continue;

                if (!HasLineOfSight(transform.position, hostile.transform.position))
                    continue;

                if (attackingPlayerNow && selfDistSq < attackingPlayerSelfDistSq)
                {
                    attackingPlayerSelfDistSq = selfDistSq;
                    attackingPlayer = hostile;
                }

                if (playerDistSq < nearestToPlayerDistSq)
                {
                    nearestToPlayerDistSq = playerDistSq;
                    nearestToPlayer = hostile;
                }

                if (selfDistSq < nearestToSelfDistSq)
                {
                    nearestToSelfDistSq = selfDistSq;
                    nearestToSelf = hostile;
                }
            }

            if (attackingPlayer != null)
                return attackingPlayer;
            if (nearestToPlayer != null)
                return nearestToPlayer;

            return nearestToSelf;
        }
    }
}
