// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Debug/testing console commands, following the exact registration pattern DFU's own
// VampirismEffect.cs uses (Wenzil.Console's ConsoleCommandsDatabase.RegisterCommand, no interface
// needed - just a static method matching the ConsoleCommandCallback delegate shape).

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Utility;
using Wenzil.Console;
using VoxsNecromancyMod.AI;

namespace VoxsNecromancyMod
{
    public static class VoxsNecromancyConsoleCommands
    {
        public static void RegisterCommands()
        {
            try
            {
                ConsoleCommandsDatabase.RegisterCommand(EmbraceDarkness.name, EmbraceDarkness.description, EmbraceDarkness.usage, EmbraceDarkness.Execute);
                ConsoleCommandsDatabase.RegisterCommand(TestCorpse.name, TestCorpse.description, TestCorpse.usage, TestCorpse.Execute);
                ConsoleCommandsDatabase.RegisterCommand(AstridBook.name, AstridBook.description, AstridBook.usage, AstridBook.Execute);
                ConsoleCommandsDatabase.RegisterCommand(ListUpkeep.name, ListUpkeep.description, ListUpkeep.usage, ListUpkeep.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceNavCheck.name, SeanceNavCheck.description, SeanceNavCheck.usage, SeanceNavCheck.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceGridCheck.name, SeanceGridCheck.description, SeanceGridCheck.usage, SeanceGridCheck.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceGridCheckAt.name, SeanceGridCheckAt.description, SeanceGridCheckAt.usage, SeanceGridCheckAt.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceTeleCheck.name, SeanceTeleCheck.description, SeanceTeleCheck.usage, SeanceTeleCheck.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceReachCheck.name, SeanceReachCheck.description, SeanceReachCheck.usage, SeanceReachCheck.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceSliceCheck.name, SeanceSliceCheck.description, SeanceSliceCheck.usage, SeanceSliceCheck.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceProbe.name, SeanceProbe.description, SeanceProbe.usage, SeanceProbe.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceForceEscort.name, SeanceForceEscort.description, SeanceForceEscort.usage, SeanceForceEscort.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceReachReport.name, SeanceReachReport.description, SeanceReachReport.usage, SeanceReachReport.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceSupportCheck.name, SeanceSupportCheck.description, SeanceSupportCheck.usage, SeanceSupportCheck.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceSeamReport.name, SeanceSeamReport.description, SeanceSeamReport.usage, SeanceSeamReport.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceGridBuild.name, SeanceGridBuild.description, SeanceGridBuild.usage, SeanceGridBuild.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceDoorCheck.name, SeanceDoorCheck.description, SeanceDoorCheck.usage, SeanceDoorCheck.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceTrace.name, SeanceTrace.description, SeanceTrace.usage, SeanceTrace.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceStepTest.name, SeanceStepTest.description, SeanceStepTest.usage, SeanceStepTest.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeancePathCheck.name, SeancePathCheck.description, SeancePathCheck.usage, SeancePathCheck.Execute);
                ConsoleCommandsDatabase.RegisterCommand(SeanceFloodCheck.name, SeanceFloodCheck.description, SeanceFloodCheck.usage, SeanceFloodCheck.Execute);
            }
            catch (System.Exception ex)
            {
                DaggerfallUnity.LogMessage(ex.Message, true);
            }
        }

        static class EmbraceDarkness
        {
            public static readonly string name = "embrace_darkness";
            public static readonly string description = "Adds every Astrid's Guide to Necromancy spell to the player's spellbook (skips any already known).";
            public static readonly string usage = "embrace_darkness";

            public static string Execute(params string[] args)
            {
                PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
                if (playerEntity == null)
                    return "No player entity found.";

                VoxsNecromancy.GrantAllSpells(playerEntity);
                return "All Astrid's Guide to Necromancy spells have been added to your spellbook.";
            }
        }

        static class AstridBook
        {
            public static readonly string name = "astrid_book";
            public static readonly string description = "Adds Astrid's Guide to Necromancy to the player's inventory, if they don't already have it.";
            public static readonly string usage = "astrid_book";

            public static string Execute(params string[] args)
            {
                return VoxsNecromancy.GiveAstridBookIfMissing();
            }
        }

        // Player-facing feedback for the Magicka upkeep budget (VoxsNecromancy's own "Magicka upkeep
        // budget" comment) - lists every currently active allied undead and exactly how much Magicka
        // each one costs in upkeep, alongside the player's own total upkeep and the budget itself
        // (VoxsNecromancy.GetUpkeepBudget: MAX Magicka + Mysticism skill - this mod never touches
        // Magicka/MaxMagicka itself) so the numbers make sense at a glance.
        static class ListUpkeep
        {
            public static readonly string name = "list_upkeep";
            public static readonly string description = "Lists every active allied undead and its Magicka upkeep cost, plus the total budget.";
            public static readonly string usage = "list_upkeep";

            public static string Execute(params string[] args)
            {
                PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
                if (playerEntity == null)
                    return "No player entity found.";

                AllyCombatController[] allies = AllyCombatController.GetActiveAlliesSnapshot();

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendFormat("Upkeep: {0} across {1} allied undead.",
                    VoxsNecromancy.DescribeUpkeepBudget(), allies.Length);

                for (int i = 0; i < allies.Length; i++)
                {
                    AllyCombatController ally = allies[i];
                    DaggerfallEntityBehaviour behaviour = ally.GetComponent<DaggerfallEntityBehaviour>();
                    EnemyEntity enemyEntity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
                    string name = enemyEntity != null
                        ? TextManager.Instance.GetLocalizedEnemyName(enemyEntity.MobileEnemy.ID)
                        : "(unknown)";

                    sb.AppendFormat("\n  {0} - upkeep {1}", name, ally.MagickaUpkeep);
                }

                return sb.ToString();
            }
        }

        static class TestCorpse
        {
            public static readonly string name = "test_corpse";
            public static readonly string description = "Spawns a human corpse at the player's location for testing corpse-consuming spells.";
            public static readonly string usage = "test_corpse";

            public static string Execute(params string[] args)
            {
                Transform playerTransform = GameManager.Instance.PlayerObject.transform;
                Vector3 position = playerTransform.position + playerTransform.forward * 2f;

                // Spawn a live classed-human enemy to get a fully-initialized EnemyEntity (stats,
                // MobileEnemy data, equipment/loot table) for free, then build the corpse marker
                // directly via the same public helper EnemyDeath.cs itself calls - rather than
                // killing the enemy and waiting for the engine's own OnDeath -> EnemyDeath.Update()
                // -> CompleteDeath() chain to get around to it. In this heavily-modded environment
                // that indirect chain was observed to silently produce no corpse at all (enemy died,
                // but zero loot objects were ever registered anywhere) with no exception logged -
                // likely some other mod's patch on the death pipeline interfering - and Harmony is
                // non-functional here, so that chain can't be patched/instrumented to find out why.
                // Calling CreateLootableCorpseMarker ourselves sidesteps it entirely.
                GameObject[] spawned = GameObjectHelper.CreateFoeGameObjects(position, MobileTypes.Knight, 1);
                if (spawned == null || spawned.Length == 0)
                    return "Failed to spawn test corpse.";

                GameObject enemyGameObject = spawned[0];
                DaggerfallEntityBehaviour behaviour = enemyGameObject.GetComponent<DaggerfallEntityBehaviour>();
                EnemyEntity enemyEntity = behaviour != null ? behaviour.Entity as EnemyEntity : null;
                if (enemyEntity == null)
                    return "Failed to spawn test corpse.";

                MobileEnemy mobileEnemy = enemyEntity.MobileEnemy;
                int corpseTexture = (mobileEnemy.Gender == MobileGender.Female && mobileEnemy.FemaleCorpseTexture != 0)
                    ? mobileEnemy.FemaleCorpseTexture
                    : mobileEnemy.CorpseTexture;

                DaggerfallLoot loot = GameObjectHelper.CreateLootableCorpseMarker(
                    GameManager.Instance.PlayerObject,
                    enemyGameObject,
                    enemyEntity,
                    corpseTexture,
                    DaggerfallUnity.NextUID);

                if (loot == null)
                    return "Failed to create test corpse.";

                behaviour.CorpseLootContainer = loot;
                loot.Items.TransferAll(enemyEntity.Items);

                // Deactivate the spawned enemy now that its data has been transferred to the corpse -
                // matches what EnemyDeath.CompleteDeath() does for a real kill, just done directly.
                enemyGameObject.SetActive(false);

                return "A test corpse has been placed nearby.";
            }
        }

        // Debug aid for tracking down Seance NavMesh connectivity issues - real testing kept finding
        // the ghost stopping at the same wrong spot regardless of where it started, even from places
        // with a genuinely complete walkable route to the target, pointing at a real gap somewhere in
        // the bake rather than the target being unreachable. Rather than guess at agentSlope/agentClimb
        // numbers again, this lets the actual height/angle of a suspect incline be measured directly:
        // run it once standing at the top of a suspicious slope, then again at the bottom, and it
        // reports the real height difference, angle, and whether NavMesh.CalculatePath actually
        // connects the two points.
        static class SeanceNavCheck
        {
            public static readonly string name = "seance_navcheck";
            public static readonly string description =
                "Logs the player's position and NavMesh state; if run twice, also reports the height/angle " +
                "and path status between the two checks - for diagnosing Seance's dungeon NavMesh bake.";
            public static readonly string usage = "seance_navcheck";

            static bool hasPrevious;
            static Vector3 previousPosition;

            public static string Execute(params string[] args)
            {
                Vector3 position = GameManager.Instance.PlayerObject.transform.position;

                NavMeshHit hit;
                bool onMesh = NavMesh.SamplePosition(position, out hit, 5f, NavMesh.AllAreas);

                string result = string.Format(
                    "Position: {0}. On NavMesh (5u search): {1}{2}.",
                    position, onMesh, onMesh ? " nearest point " + hit.position : "");

                if (!onMesh)
                {
                    // Try progressively wider radii - tells us whether there's genuinely NO coverage
                    // anywhere nearby (a real hole) versus coverage existing, just further away than a
                    // fixed 5u check would ever find.
                    float[] widerRadii = { 10f, 20f, 40f };
                    for (int i = 0; i < widerRadii.Length; i++)
                    {
                        NavMeshHit widerHit;
                        if (NavMesh.SamplePosition(position, out widerHit, widerRadii[i], NavMesh.AllAreas))
                        {
                            result += string.Format(
                                "\nNo coverage within 5u, but found nearest point {0} within {1:F0}u search.",
                                widerHit.position, widerRadii[i]);
                            break;
                        }
                        else if (i == widerRadii.Length - 1)
                        {
                            result += string.Format("\nNo NavMesh coverage at all within {0:F0}u.", widerRadii[i]);
                        }
                    }

                    // Check for ANY collider on ANY layer within range - tells us whether real geometry
                    // exists there at all (and what layer it's on, in case it's not on the "Default"
                    // layer the bake itself is scoped to) versus there being no collider/renderer there
                    // whatsoever.
                    Collider[] nearbyColliders = Physics.OverlapSphere(position, 5f);
                    if (nearbyColliders.Length == 0)
                    {
                        result += "\nNo colliders of any kind found within 5u (any layer).";
                    }
                    else
                    {
                        System.Text.StringBuilder colliderInfo = new System.Text.StringBuilder();
                        for (int i = 0; i < nearbyColliders.Length; i++)
                        {
                            Collider collider = nearbyColliders[i];
                            colliderInfo.AppendFormat("{0}'{1}' (layer {2}, {3})",
                                i > 0 ? ", " : "", collider.name, LayerMask.LayerToName(collider.gameObject.layer),
                                collider.transform.position);
                        }

                        result += string.Format("\n{0} collider(s) within 5u (any layer): {1}", nearbyColliders.Length, colliderInfo);
                    }
                }

                if (hasPrevious)
                {
                    Vector3 delta = position - previousPosition;
                    float horizontalDistance = new Vector2(delta.x, delta.z).magnitude;
                    float angle = horizontalDistance > 0.01f
                        ? Mathf.Atan2(Mathf.Abs(delta.y), horizontalDistance) * Mathf.Rad2Deg
                        : 90f;

                    NavMeshPath path = new NavMeshPath();
                    bool hasPath = NavMesh.CalculatePath(previousPosition, position, NavMesh.AllAreas, path);

                    System.Text.StringBuilder corners = new System.Text.StringBuilder();
                    for (int i = 0; i < path.corners.Length; i++)
                        corners.AppendFormat("{0}{1}", i > 0 ? " -> " : "", path.corners[i]);

                    result += string.Format(
                        "\nSince last check ({0}): height diff {1:F2}, horizontal diff {2:F2}, angle {3:F1} deg, " +
                        "CalculatePath returned {4}, status {5}, {6} corners: {7}",
                        previousPosition, delta.y, horizontalDistance, angle, hasPath, path.status, path.corners.Length,
                        corners.ToString());
                }

                hasPrevious = true;
                previousPosition = position;

                return result;
            }
        }

        // Debug aid for the voxel-grid pathfinder (see SeanceDungeonGrid.cs) - reports the player's
        // current cell state and every elevator column detected in this dungeon, since a "cannot find
        // a path" failure near a real elevator shaft is most likely either the elevator's own
        // ActionTranslation not passing the span/vertical-ratio heuristic, or its recorded Y range not
        // covering the floor the player is actually standing on. Requires Seance to have already been
        // cast at least once in this dungeon (that's what builds the grid in the first place).
        static class SeanceGridCheck
        {
            public static readonly string name = "seance_gridcheck";
            public static readonly string description =
                "Reports the player's current voxel-grid cell state (open/support/water/elevator) and lists " +
                "every detected elevator column in the current dungeon - for diagnosing Seance's voxel pathfinder.";
            public static readonly string usage = "seance_gridcheck";

            public static string Execute(params string[] args)
            {
                SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
                if (grid == null)
                    return "No active SeanceDungeonGrid - cast Seance at least once in this dungeon first.";

                Vector3 position = GameManager.Instance.PlayerObject.transform.position;
                return grid.DebugDescribe(position);
            }
        }

        // Same report as SeanceGridCheck, but for an arbitrary world position instead of wherever the
        // player currently stands - added because reaching a specific reported chokepoint coordinate
        // in-game (teleporters, no visible coordinate readout until a command is run) is often
        // impractical in practice.
        static class SeanceGridCheckAt
        {
            public static readonly string name = "seance_gridcheck_at";
            public static readonly string description =
                "Reports the voxel-grid cell state (open/support/water/elevator) at an arbitrary world " +
                "position instead of the player's current position - for checking a specific coordinate " +
                "(e.g. one reported by seance_pathcheck) without needing to physically stand there.";
            public static readonly string usage = "seance_gridcheck_at <x> <y> <z>";

            public static string Execute(params string[] args)
            {
                SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
                if (grid == null)
                    return "No active SeanceDungeonGrid - cast Seance at least once in this dungeon first.";

                float x, y, z;
                if (args.Length < 3 || !float.TryParse(args[0], out x) || !float.TryParse(args[1], out y) || !float.TryParse(args[2], out z))
                    return "Usage: " + usage;

                return grid.DebugDescribe(new Vector3(x, y, z));
            }
        }

        // Teleporters are the single most failure-prone part of the Seance route (Daggerfall's are "red
        // brick door" fake walls that block the grid like any wall - see SeanceDungeonGrid's TeleportEdge
        // comment), and a teleporter the ghost silently ignored is indistinguishable from a plain no-path
        // result in seance_pathcheck's output. This reports each edge's trigger cells directly.
        static class SeanceTeleCheck
        {
            public static readonly string name = "seance_telecheck";
            public static readonly string description =
                "Lists every teleport edge the Seance grid knows about, each one's standable trigger cells " +
                "and their current open/support state, and whether the player is standing on one - for " +
                "diagnosing a teleporter the ghost appears to ignore.";
            public static readonly string usage = "seance_telecheck";

            public static string Execute(params string[] args)
            {
                SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
                if (grid == null)
                    return "No active SeanceDungeonGrid - cast Seance at least once in this dungeon first.";

                return grid.DebugDescribeTeleporters(GameManager.Instance.PlayerObject.transform.position);
            }
        }

        // Phase 2.1 instrument. Floods the grid using the REAL search (so the rules cannot drift from A*'s)
        // and reports reachability plus a rejection histogram, which is what distinguishes "the route is
        // blocked" from "the floor map underneath is corrupt".
        static class SeanceReachReport
        {
            public static readonly string name = "seance_reachreport";
            public static readonly string description =
                "Floods the Seance grid from the player's position using the real A* rules and reports total " +
                "reachable cells, which quest resources are reachable, the reachable bounding box, and a " +
                "histogram of why neighbour expansions were rejected.";
            public static readonly string usage = "seance_reachreport";

            public static string Execute(params string[] args)
            {
                SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
                if (grid == null)
                    return "No active SeanceDungeonGrid - cast Seance at least once in this dungeon first.";

                DaggerfallDungeon dungeon = GameManager.Instance.PlayerEnterExit != null
                    ? GameManager.Instance.PlayerEnterExit.Dungeon
                    : null;

                Vector3 start = GameManager.Instance.PlayerObject.transform.position;

                // Aimed at a goal the flood is expected to FAIL to reach, so the search exhausts the whole
                // reachable set instead of stopping early on success.
                Vector3 goal = dungeon != null && dungeon.StartMarker != null
                    ? dungeon.StartMarker.transform.position
                    : start + Vector3.forward * 1000f;

                SeanceVoxelPathfinder pathfinder = new SeanceVoxelPathfinder(grid, start, goal);
                pathfinder.CollectDiagnostics = true;

                // Must not stop the moment it touches the goal - the first run settled 3 cells because the
                // player happened to be standing next to the exit marker.
                pathfinder.FloodMode = true;

                IEnumerator search = pathfinder.Search();
                int guard = 0;
                const int maxIterations = 2000000;
                while (search.MoveNext() && guard < maxIterations)
                    guard++;

                HashSet<SeanceCell> reachable = pathfinder.GetExpandedCells();

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendFormat("REACH REPORT from {0} (probe radius {1:F2})", start, SeanceDungeonGrid.ProbeRadius);
                sb.AppendFormat("\nSettled cells: {0}", reachable.Count);

                if (reachable.Count > 0)
                {
                    int minX = int.MaxValue, maxX = int.MinValue;
                    int minY = int.MaxValue, maxY = int.MinValue;
                    int minZ = int.MaxValue, maxZ = int.MinValue;
                    foreach (SeanceCell cell in reachable)
                    {
                        if (cell.X < minX) minX = cell.X;
                        if (cell.X > maxX) maxX = cell.X;
                        if (cell.Y < minY) minY = cell.Y;
                        if (cell.Y > maxY) maxY = cell.Y;
                        if (cell.Z < minZ) minZ = cell.Z;
                        if (cell.Z > maxZ) maxZ = cell.Z;
                    }

                    sb.AppendFormat("\nReachable bounds: x[{0},{1}] y[{2},{3}] z[{4},{5}]  ({6} x {7} x {8} cells)",
                        minX, maxX, minY, maxY, minZ, maxZ, maxX - minX + 1, maxY - minY + 1, maxZ - minZ + 1);
                }

                // Quest resources, filtered exactly as SeanceEffect filters them so this matches what the
                // spell would actually target.
                sb.Append("\n\nQuest resources:");
                QuestResourceBehaviour[] resources = Object.FindObjectsOfType<QuestResourceBehaviour>();
                int listed = 0;
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

                    listed++;
                    SeanceCell cell = SeanceCell.FromWorld(position, SeanceDungeonGrid.CellSize);
                    bool cellReachable = reachable.Contains(cell);
                    sb.AppendFormat("\n  {0} at {1} - {2} ({3:F1}m from player)",
                        resource.name, position, cellReachable ? "REACHABLE" : "NOT reachable",
                        Vector3.Distance(start, position));
                }
                if (listed == 0)
                    sb.Append(" none in this dungeon.");

                if (dungeon != null && dungeon.StartMarker != null)
                {
                    Vector3 exit = dungeon.StartMarker.transform.position;
                    bool exitReachable = reachable.Contains(SeanceCell.FromWorld(exit, SeanceDungeonGrid.CellSize));
                    sb.AppendFormat("\n  [dungeon exit StartMarker] at {0} - {1} ({2:F1}m from player)",
                        exit, exitReachable ? "REACHABLE" : "NOT reachable", Vector3.Distance(start, exit));
                }

                int totalRejects = 0;
                for (int b = 0; b < SeanceVoxelPathfinder.RejectBucketCount; b++)
                {
                    if (b == SeanceVoxelPathfinder.RejectVisited)
                        continue;

                    totalRejects += pathfinder.RejectCounts[b];
                }

                sb.AppendFormat("\n\nRejection histogram ({0} rejections, VISITED reported separately):", totalRejects);
                for (int b = 0; b < SeanceVoxelPathfinder.RejectBucketCount; b++)
                {
                    int count = pathfinder.RejectCounts[b];
                    bool isVisited = b == SeanceVoxelPathfinder.RejectVisited;
                    float percent = totalRejects > 0 && !isVisited ? (count * 100f) / totalRejects : 0f;

                    sb.AppendFormat("\n  {0,-13} {1,8} distinct{2}   (raw events {3})",
                        SeanceVoxelPathfinder.RejectBucketName(b), count,
                        isVisited ? "  (not a rejection)" : string.Format("  {0,5:F1}%", percent),
                        pathfinder.RejectRawCounts[b]);

                    List<Vector3> samples = pathfinder.RejectSamples[b];
                    for (int sIdx = 0; sIdx < samples.Count; sIdx++)
                        sb.AppendFormat("\n        at {0}", samples[sIdx]);
                }

                AppendFrontier(sb, grid, reachable);

                sb.AppendFormat("\n\n{0}", pathfinder.DebugSummary());
                return sb.ToString();
            }

            // Every place this component is actually SEALED, rather than a box and some scan-order samples.
            // Walks the settled set and finds neighbours that LOOK walkable (open, and supported or an
            // elevator column) but were never absorbed. That is not "the first N rejections a loop happened
            // to touch" - it is every cell the search could plausibly have continued through and didn't, which
            // is what "where does this component end, and why" actually means.
            //
            // Deliberately excludes NOT_OPEN/NO_SUPPORT neighbours: solid rock and open-air gaps border a
            // component everywhere and would drown the interesting cases. A PASSABLE-BUT-UNREACHED entry is
            // the one that should never appear - if it does, the edge is fine and something in the search
            // itself (not the geometry) is the reason this component stops here.
            const int MaxFrontierDetail = 60;

            static void AppendFrontier(System.Text.StringBuilder sb, SeanceDungeonGrid grid, HashSet<SeanceCell> reachable)
            {
                sb.Append("\n\nFRONTIER (why this component is sealed, not a bounding box):");

                if (reachable.Count == 0)
                {
                    sb.Append("\n  (component is empty)");
                    return;
                }

                Dictionary<string, List<string>> byBucket = new Dictionary<string, List<string>>();
                Dictionary<string, int> bucketCounts = new Dictionary<string, int>();
                int totalEdges = 0;

                foreach (SeanceCell cell in reachable)
                {
                    for (int dirIndex = 0; dirIndex < SeanceDirections.Count; dirIndex++)
                    {
                        SeanceCell neighbor = cell.Offset(
                            SeanceDirections.DX[dirIndex], SeanceDirections.DY[dirIndex], SeanceDirections.DZ[dirIndex]);

                        if (reachable.Contains(neighbor))
                            continue;
                        if (!grid.IsOpen(neighbor))
                            continue;
                        if (!grid.HasSupport(neighbor) && !grid.IsElevatorColumn(neighbor))
                            continue;

                        string reason = grid.DescribeEdgeRejection(cell, neighbor);
                        string bucket = CategorizeFrontierReason(reason);

                        totalEdges++;
                        bucketCounts[bucket] = bucketCounts.ContainsKey(bucket) ? bucketCounts[bucket] + 1 : 1;

                        List<string> lines;
                        if (!byBucket.TryGetValue(bucket, out lines))
                        {
                            lines = new List<string>();
                            byBucket[bucket] = lines;
                        }

                        if (lines.Count < MaxFrontierDetail)
                            lines.Add(string.Format("{0} -> {1}: {2}\n      inside:  {3}\n      outside: {4}",
                                cell, neighbor, reason, grid.DescribeCellBrief(cell), grid.DescribeCellBrief(neighbor)));
                    }
                }

                sb.AppendFormat("\n  {0} frontier edge(s) total (settled cell bordering an open+supported cell outside the component):", totalEdges);
                if (totalEdges == 0)
                {
                    sb.Append("\n  NONE - this component borders nothing that looks walkable. It is sealed by solid rock and unsupported air only.");
                    return;
                }

                foreach (KeyValuePair<string, int> entry in bucketCounts)
                {
                    sb.AppendFormat("\n\n  {0}: {1} edge(s)", entry.Key, entry.Value);
                    List<string> lines = byBucket[entry.Key];
                    for (int i = 0; i < lines.Count; i++)
                        sb.AppendFormat("\n    {0}", lines[i]);
                    if (entry.Value > lines.Count)
                        sb.AppendFormat("\n    ... {0} more of this kind not printed (cap {1}). Counts above are complete; do not select cells from a partial listing.",
                            entry.Value - lines.Count, MaxFrontierDetail);
                }
            }

            static string CategorizeFrontierReason(string reason)
            {
                if (reason.Contains("RESCUED"))
                    return "PASSABLE-BUT-UNREACHED (edge is fine - a search/rule bug, not geometry)";
                if (reason.StartsWith("edge is CLEAR"))
                    return "NO_RULE (edge is clear - no movement rule accepts this direction)";
                if (reason.Contains("actionObject=True"))
                    return "BARRIER (gated obstacle/door/mover)";

                return "EDGE_BLOCKED (static geometry)";
            }
        }

        // LOCAL instrument for the two-component problem. The global reach reports already established that
        // Blurk's Guard is two large healthy components with one missing join, so this stops measuring
        // globally and looks only at the seam between them.
        //
        // Run once in the first region to record seed A, walk to the other region, run again for the report.
        // Both floods are forced to the SAME probe radius, because the first pair of reach reports were run
        // at 0.32 and 0.29 and were therefore not comparable.
        static class SeanceSeamReport
        {
            public static readonly string name = "seance_seamreport";
            public static readonly string description =
                "Two-seed seam diagnostic: run once in one region, again in the other. Reports both components, " +
                "every adjacent pair that straddles them with the reason its edge is refused, and orphan cells " +
                "belonging to neither.";
            public static readonly string usage =
                "seance_seamreport   (auto: dungeon entrance vs where you stand)  |  seance_seamreport walk   (two-seed)";

            static HashSet<SeanceCell> seedACells;
            static Vector3 seedAPosition;
            static float seedARadius;

            const int MaxListed = 20;
            const int MaxSeamSearchRadius = 10;
            const int OrphanScanRadius = 12;

            public static string Execute(params string[] args)
            {
                SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
                if (grid == null)
                    return "No active SeanceDungeonGrid - cast Seance at least once in this dungeon first.";

                Vector3 position = GameManager.Instance.PlayerObject.transform.position;

                // Default: seed A is the dungeon's own StartMarker, resolved from the dungeon rather than
                // asked for as a coordinate. Nobody can navigate to a world position they cannot see, and the
                // two-seed flow also required remembering where seed A had been dropped. Auto-seeding removes
                // both problems - stand anywhere, run one command.
                bool walkMode = args.Length >= 1 && args[0].ToLowerInvariant() == "walk";
                if (!walkMode)
                {
                    DaggerfallDungeon autoDungeon = GameManager.Instance.PlayerEnterExit != null
                        ? GameManager.Instance.PlayerEnterExit.Dungeon
                        : null;
                    if (autoDungeon == null || autoDungeon.StartMarker == null)
                        return "This dungeon has no StartMarker to seed from - use 'seance_seamreport walk' instead.";

                    seedARadius = SeanceDungeonGrid.ProbeRadius;
                    seedAPosition = autoDungeon.StartMarker.transform.position;
                    seedACells = Flood(grid, seedAPosition);
                }
                else if (seedACells == null)
                {
                    seedARadius = SeanceDungeonGrid.ProbeRadius;
                    seedACells = Flood(grid, position);
                    seedAPosition = position;

                    return string.Format(
                        "Seed A recorded at {0} - {1} cells settled at probe radius {2:F3}.\n" +
                        "Now walk to the OTHER region and run seance_seamreport again to produce the report.",
                        position, seedACells.Count, seedARadius);
                }

                // Force B to A's radius so the two floods are comparable.
                string radiusNote = "";
                if (!Mathf.Approximately(SeanceDungeonGrid.ProbeRadius, seedARadius))
                {
                    radiusNote = string.Format(
                        "\n(Probe radius was {0:F3}; forced to seed A's {1:F3} so both floods match.)",
                        SeanceDungeonGrid.ProbeRadius, seedARadius);
                    SeanceDungeonGrid.ProbeRadius = seedARadius;
                    grid.ClearCellCache();
                }

                HashSet<SeanceCell> a = seedACells;
                HashSet<SeanceCell> b = Flood(grid, position);

                // One-shot: clear the seed so the next run starts a fresh pair.
                seedACells = null;

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendFormat("SEAM REPORT (probe radius {0:F3}){1}", seedARadius, radiusNote);
                sb.AppendFormat("\n  A = {0}, B = where you stand.",
                    walkMode ? "the seed you dropped earlier" : "the dungeon's StartMarker (entrance)");

                // ---- 1. Component summary -------------------------------------------------------------
                int shared = 0;
                foreach (SeanceCell cell in a)
                {
                    if (b.Contains(cell))
                        shared++;
                }

                sb.AppendFormat("\n\n[1] COMPONENTS");
                sb.AppendFormat("\n  A from {0}: {1} cells, {2}", seedAPosition, a.Count, DescribeBounds(a));
                sb.AppendFormat("\n  B from {0}: {1} cells, {2}", position, b.Count, DescribeBounds(b));
                sb.AppendFormat("\n  A n B = {0} cells {1}", shared,
                    shared == 0 ? "(disjoint, as expected)" : "(NOT disjoint - these are the same component)");

                DaggerfallDungeon dungeon = GameManager.Instance.PlayerEnterExit != null
                    ? GameManager.Instance.PlayerEnterExit.Dungeon
                    : null;

                sb.Append("\n  Quest resources:");
                QuestResourceBehaviour[] resources = Object.FindObjectsOfType<QuestResourceBehaviour>();
                int listedResources = 0;
                for (int i = 0; i < resources.Length; i++)
                {
                    QuestResourceBehaviour resource = resources[i];
                    if (resource == null || resource.IsFoeDead)
                        continue;

                    Vector3 resourcePosition = resource.transform.position;
                    if (dungeon != null && dungeon.GetPlayerBlockIndex(resourcePosition) < 0)
                        continue;

                    listedResources++;
                    SeanceCell cell = SeanceCell.FromWorld(resourcePosition, SeanceDungeonGrid.CellSize);
                    sb.AppendFormat("\n    {0} at {1} -> {2}", resource.name, resourcePosition,
                        a.Contains(cell) ? "in A" : b.Contains(cell) ? "in B" : "in NEITHER");
                }
                if (listedResources == 0)
                    sb.Append(" none.");

                if (dungeon != null && dungeon.StartMarker != null)
                {
                    SeanceCell exitCell = SeanceCell.FromWorld(dungeon.StartMarker.transform.position, SeanceDungeonGrid.CellSize);
                    sb.AppendFormat("\n    [exit StartMarker] at {0} -> {1}", dungeon.StartMarker.transform.position,
                        a.Contains(exitCell) ? "in A" : b.Contains(exitCell) ? "in B" : "in NEITHER");
                }

                // ---- 2. Adjacent straddling pairs -----------------------------------------------------
                List<SeanceCell> straddleFrom = new List<SeanceCell>();
                List<SeanceCell> straddleTo = new List<SeanceCell>();

                foreach (SeanceCell cell in a)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                if (dx == 0 && dy == 0 && dz == 0)
                                    continue;

                                SeanceCell neighbour = cell.Offset(dx, dy, dz);
                                if (!b.Contains(neighbour))
                                    continue;

                                straddleFrom.Add(cell);
                                straddleTo.Add(neighbour);
                            }
                        }
                    }
                }

                sb.AppendFormat("\n\n[2] ADJACENT STRADDLING PAIRS: {0}", straddleFrom.Count);
                if (straddleFrom.Count == 0)
                {
                    sb.Append("\n  NONE - the components never touch. The join spans more than one cell, which is a " +
                              "movement-RULE gap rather than an over-rejected edge.");
                }
                for (int i = 0; i < straddleFrom.Count && i < MaxListed; i++)
                {
                    sb.AppendFormat("\n  A: {0}", grid.DescribeCellBrief(straddleFrom[i]));
                    sb.AppendFormat("\n  B: {0}", grid.DescribeCellBrief(straddleTo[i]));
                    sb.AppendFormat("\n     edge A->B: {0}", grid.DescribeEdgeRejection(straddleFrom[i], straddleTo[i]));
                    sb.AppendFormat("\n     edge B->A: {0}\n", grid.DescribeEdgeRejection(straddleTo[i], straddleFrom[i]));
                }

                // ---- closest approach between the components ------------------------------------------
                SeanceCell closestA = default(SeanceCell);
                SeanceCell closestB = default(SeanceCell);
                int closestRing = -1;

                for (int ring = 1; ring <= MaxSeamSearchRadius && closestRing < 0; ring++)
                {
                    foreach (SeanceCell cell in a)
                    {
                        for (int dx = -ring; dx <= ring && closestRing < 0; dx++)
                        {
                            for (int dy = -ring; dy <= ring && closestRing < 0; dy++)
                            {
                                for (int dz = -ring; dz <= ring && closestRing < 0; dz++)
                                {
                                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Max(Mathf.Abs(dy), Mathf.Abs(dz))) != ring)
                                        continue;

                                    SeanceCell candidate = cell.Offset(dx, dy, dz);
                                    if (!b.Contains(candidate))
                                        continue;

                                    closestA = cell;
                                    closestB = candidate;
                                    closestRing = ring;
                                }
                            }
                        }

                        if (closestRing >= 0)
                            break;
                    }
                }

                if (closestRing < 0)
                {
                    sb.AppendFormat("\n\n  Closest approach: components are more than {0} cells apart.", MaxSeamSearchRadius);
                }
                else
                {
                    sb.AppendFormat("\n\n  Closest approach: {0} cell(s) apart - {1} <-> {2} ({3:F2}m)",
                        closestRing, closestA, closestB,
                        Vector3.Distance(closestA.ToWorldCenter(SeanceDungeonGrid.CellSize),
                                         closestB.ToWorldCenter(SeanceDungeonGrid.CellSize)));
                }

                // ---- 3. Orphan cells ------------------------------------------------------------------
                //
                // Scanned around the seam rather than across the whole dungeon - the union of the two
                // bounding boxes is ~1M cells, which is not worth walking to answer a local question.
                sb.Append("\n\n[3] ORPHAN CELLS (open + supported, in neither component)");

                Vector3 seamCentre = closestRing >= 0
                    ? Vector3.Lerp(closestA.ToWorldCenter(SeanceDungeonGrid.CellSize),
                                   closestB.ToWorldCenter(SeanceDungeonGrid.CellSize), 0.5f)
                    : Vector3.Lerp(seedAPosition, position, 0.5f);

                SeanceCell scanCentre = SeanceCell.FromWorld(seamCentre, SeanceDungeonGrid.CellSize);
                int orphanListed = 0;
                System.Text.StringBuilder orphanDetail = new System.Text.StringBuilder();
                HashSet<SeanceCell> orphans = new HashSet<SeanceCell>();

                for (int dx = -OrphanScanRadius; dx <= OrphanScanRadius; dx++)
                {
                    for (int dy = -OrphanScanRadius; dy <= OrphanScanRadius; dy++)
                    {
                        for (int dz = -OrphanScanRadius; dz <= OrphanScanRadius; dz++)
                        {
                            SeanceCell cell = scanCentre.Offset(dx, dy, dz);
                            if (a.Contains(cell) || b.Contains(cell))
                                continue;

                            if (!grid.IsOpen(cell) || !grid.HasSupport(cell))
                                continue;

                            orphans.Add(cell);
                            if (orphanListed < MaxListed)
                            {
                                orphanListed++;
                                orphanDetail.AppendFormat("\n  {0}", grid.DescribeCellBrief(cell));
                            }
                        }
                    }
                }

                sb.AppendFormat("\n  Scanned +/-{0} cells around the seam at {1}: {2} orphan(s).",
                    OrphanScanRadius, scanCentre, orphans.Count);
                sb.Append(orphanDetail.ToString());

                // ---- 1a. Orphan sub-components ---------------------------------------------------------
                //
                // Connectivity WITHIN the orphan set. One long chain means the staircase is intact and cut
                // only at its two ends; a scatter of tiny islands means it cannot even connect to itself,
                // which is what the tread/step pattern predicts.
                //
                // Approximation worth stating: this uses grid.IsEdgeClear plus open+supported, i.e. the
                // horizontal-move rule. It does not replay drop/jump/climb/elevator rules.
                List<List<SeanceCell>> components = ConnectedComponents(grid, orphans);
                components.Sort((x, y) => y.Count.CompareTo(x.Count));

                sb.AppendFormat("\n\n[3a] ORPHAN SUB-COMPONENTS: {0} distinct", components.Count);
                for (int i = 0; i < components.Count && i < 5; i++)
                {
                    HashSet<SeanceCell> set = new HashSet<SeanceCell>(components[i]);
                    sb.AppendFormat("\n  #{0}: {1} cell(s), {2}", i + 1, components[i].Count, DescribeBounds(set));
                }

                if (components.Count > 0)
                {
                    List<SeanceCell> largest = components[0];
                    sb.AppendFormat("\n\n  Largest orphan component ({0} cells) - why it cannot join either side:",
                        largest.Count);

                    SeanceCell fromA, toOrphanA;
                    if (NearestBetween(a, largest, out fromA, out toOrphanA))
                    {
                        sb.AppendFormat("\n    nearest A cell {0}", grid.DescribeCellBrief(fromA));
                        sb.AppendFormat("\n    nearest orphan {0}", grid.DescribeCellBrief(toOrphanA));
                        sb.AppendFormat("\n      A -> orphan: {0}", grid.DescribeEdgeRejection(fromA, toOrphanA));
                        sb.AppendFormat("\n      orphan -> A: {0}", grid.DescribeEdgeRejection(toOrphanA, fromA));
                    }
                    else
                    {
                        sb.Append("\n    no A cell within search range of this component.");
                    }

                    SeanceCell fromB, toOrphanB;
                    if (NearestBetween(b, largest, out fromB, out toOrphanB))
                    {
                        sb.AppendFormat("\n    nearest B cell {0}", grid.DescribeCellBrief(fromB));
                        sb.AppendFormat("\n    nearest orphan {0}", grid.DescribeCellBrief(toOrphanB));
                        sb.AppendFormat("\n      B -> orphan: {0}", grid.DescribeEdgeRejection(fromB, toOrphanB));
                        sb.AppendFormat("\n      orphan -> B: {0}", grid.DescribeEdgeRejection(toOrphanB, fromB));
                    }
                    else
                    {
                        sb.Append("\n    no B cell within search range of this component.");
                    }
                }

                // Kept for seance_steptest's bare form.
                LastOrphans = orphans;
                LastA = a;
                LastB = b;

                return sb.ToString();
            }

            // Shared with seance_steptest's bare form.
            public static HashSet<SeanceCell> LastOrphans;
            public static HashSet<SeanceCell> LastA;
            public static HashSet<SeanceCell> LastB;

            // Connected components within a cell set, using the horizontal-move rule (open + supported +
            // IsEdgeClear). Deliberately does not replay drop/jump/climb rules - stated as an approximation
            // rather than presented as the full ruleset.
            static List<List<SeanceCell>> ConnectedComponents(SeanceDungeonGrid grid, HashSet<SeanceCell> cells)
            {
                List<List<SeanceCell>> result = new List<List<SeanceCell>>();
                HashSet<SeanceCell> seen = new HashSet<SeanceCell>();

                foreach (SeanceCell seed in cells)
                {
                    if (seen.Contains(seed))
                        continue;

                    List<SeanceCell> component = new List<SeanceCell>();
                    Queue<SeanceCell> queue = new Queue<SeanceCell>();
                    queue.Enqueue(seed);
                    seen.Add(seed);

                    while (queue.Count > 0)
                    {
                        SeanceCell current = queue.Dequeue();
                        component.Add(current);

                        for (int dx = -1; dx <= 1; dx++)
                        {
                            for (int dy = -1; dy <= 1; dy++)
                            {
                                for (int dz = -1; dz <= 1; dz++)
                                {
                                    if (dx == 0 && dy == 0 && dz == 0)
                                        continue;

                                    SeanceCell neighbour = current.Offset(dx, dy, dz);
                                    if (seen.Contains(neighbour) || !cells.Contains(neighbour))
                                        continue;

                                    if (!grid.IsEdgeClear(current, neighbour))
                                        continue;

                                    seen.Add(neighbour);
                                    queue.Enqueue(neighbour);
                                }
                            }
                        }
                    }

                    result.Add(component);
                }

                return result;
            }

            // Closest pair between a set and a list, by expanding ring search.
            static bool NearestBetween(HashSet<SeanceCell> set, List<SeanceCell> list,
                out SeanceCell fromSet, out SeanceCell fromList)
            {
                fromSet = default(SeanceCell);
                fromList = default(SeanceCell);

                for (int ring = 1; ring <= MaxSeamSearchRadius; ring++)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        for (int dx = -ring; dx <= ring; dx++)
                        {
                            for (int dy = -ring; dy <= ring; dy++)
                            {
                                for (int dz = -ring; dz <= ring; dz++)
                                {
                                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Max(Mathf.Abs(dy), Mathf.Abs(dz))) != ring)
                                        continue;

                                    SeanceCell candidate = list[i].Offset(dx, dy, dz);
                                    if (!set.Contains(candidate))
                                        continue;

                                    fromSet = candidate;
                                    fromList = list[i];
                                    return true;
                                }
                            }
                        }
                    }
                }

                return false;
            }

            public static HashSet<SeanceCell> Flood(SeanceDungeonGrid grid, Vector3 from)
            {
                // Aimed far outside the dungeon so it can never terminate early; FloodMode makes it exhaust
                // the component regardless.
                SeanceVoxelPathfinder pathfinder = new SeanceVoxelPathfinder(grid, from, from + Vector3.forward * 5000f);
                pathfinder.FloodMode = true;

                IEnumerator search = pathfinder.Search();
                int guard = 0;
                const int maxIterations = 4000000;
                while (search.MoveNext() && guard < maxIterations)
                    guard++;

                return pathfinder.GetExpandedCells();
            }

            static string DescribeBounds(HashSet<SeanceCell> cells)
            {
                if (cells.Count == 0)
                    return "empty";

                int minX = int.MaxValue, maxX = int.MinValue;
                int minY = int.MaxValue, maxY = int.MinValue;
                int minZ = int.MaxValue, maxZ = int.MinValue;
                foreach (SeanceCell cell in cells)
                {
                    if (cell.X < minX) minX = cell.X;
                    if (cell.X > maxX) maxX = cell.X;
                    if (cell.Y < minY) minY = cell.Y;
                    if (cell.Y > maxY) maxY = cell.Y;
                    if (cell.Z < minZ) minZ = cell.Z;
                    if (cell.Z > maxZ) maxZ = cell.Z;
                }

                return string.Format("bounds x[{0},{1}] y[{2},{3}] z[{4},{5}]", minX, maxX, minY, maxY, minZ, maxZ);
            }
        }

        // Builds the voxel grid WITHOUT casting Seance. Every other seance_* command needed a grid, and the
        // only way to get one was to cast - which also spawns a ghost, runs the escort chain, and mutates
        // excludeFromOpen through SetHypotheticalOpen. So every measurement was taken through the machinery
        // that disturbs it, and two floods from the identical position minutes apart returned 1580 and 26349
        // cells. This gives a virgin grid to measure against.
        static class SeanceGridBuild
        {
            public static readonly string name = "seance_grid";
            public static readonly string description =
                "Builds the Seance voxel grid for the current dungeon without casting the spell, so diagnostics " +
                "run on a grid no ghost or escort chain has touched. 'rebuild' forces a fresh one.";
            public static readonly string usage = "seance_grid [rebuild]";

            public static string Execute(params string[] args)
            {
                if (!GameManager.Instance.IsPlayerInsideDungeon)
                    return "Not inside a dungeon - nothing to build a grid for.";

                bool rebuild = args.Length >= 1 && args[0].ToLowerInvariant() == "rebuild";
                bool existed = SeanceDungeonGrid.Current != null;

                if (rebuild)
                {
                    // Same reset the dungeon-transition hook performs - drops the grid so the next Ensure
                    // builds it from scratch rather than returning the cached one.
                    SeanceDungeonGrid.OnDungeonTransition(null);
                }

                SeanceDungeonGrid.EnsureForCurrentDungeon();

                SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
                if (grid == null)
                    return "Could not build a grid - PlayerEnterExit reported no dungeon.";

                string what = rebuild ? "REBUILT from scratch" : (existed ? "already existed (reused)" : "BUILT");
                return string.Format(
                    "Grid {0} at probe radius {1:F3}.\n  {2}\n" +
                    "No ghost was summoned and no escort chain ran - this grid is unmutated. " +
                    "Use 'seance_grid rebuild' after casting to get a clean one back.",
                    what, SeanceDungeonGrid.ProbeRadius, grid.DescribeBuild());
            }
        }

        // Prints everything about the door passage covering a point: the door, its rotation vs its
        // ClosedRotation, the full volume, the 0.7 core, and a cell-by-cell walk through the doorway showing
        // for each cell whether it is open because a clear sample was FOUND or because the passage volume
        // ASSERTED it.
        static class SeanceDoorCheck
        {
            public static readonly string name = "seance_doorcheck";
            public static readonly string description =
                "Dissects the door passage volume covering a point (bare = the player's position): door " +
                "rotation vs ClosedRotation, full volume, 0.7 core, and per-cell openness provenance.";
            public static readonly string usage = "seance_doorcheck [<x> <y> <z>]";

            public static string Execute(params string[] args)
            {
                SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
                if (grid == null)
                    return "No active SeanceDungeonGrid - cast Seance at least once in this dungeon first.";

                Vector3 point;
                if (args.Length >= 3)
                {
                    // Cell coordinates, matching seance_steptest - the doorway is discussed in cells.
                    int x, y, z;
                    if (!int.TryParse(args[0], out x) || !int.TryParse(args[1], out y) || !int.TryParse(args[2], out z))
                        return "Usage: " + usage + "   (cell coordinates)";

                    point = new SeanceCell(x, y, z).ToWorldCenter(SeanceDungeonGrid.CellSize);
                }
                else
                {
                    point = GameManager.Instance.PlayerObject.transform.position;
                }

                return grid.DescribeDoorPassage(point);
            }
        }

        // Records the route the player ACTUALLY walks, then replays it against the grid cell by cell.
        //
        // Every other diagnostic here infers where the route is - from a verbal description, from a flood's
        // bounding box, from whichever cell a scan loop happened to touch first. That inference is what
        // produced a whole geometric theory about a cell pair that turned out to be the corner of a scan box.
        // This measures the route instead, so "where do reality and the grid disagree" becomes a list rather
        // than an argument.
        static class SeanceTrace
        {
            public static readonly string name = "seance_trace";
            public static readonly string description =
                "seance_trace start begins recording the player's route; seance_trace report replays it " +
                "against the grid and lists every place the player crossed something the grid calls impassable.";
            public static readonly string usage = "seance_trace start | seance_trace report";

            // Detail lines are capped so a 20,000-sample walk cannot bury the summary. If this cap is hit the
            // report says so LOUDLY: a truncated listing is an arbitrary slice, and cells must never be
            // selected from one to support a theory.
            const int MaxDetailLines = 200;

            public static string Execute(params string[] args)
            {
                if (args.Length < 1)
                    return Status();

                string mode = args[0].ToLowerInvariant();
                if (mode == "start")
                    return Start();
                if (mode == "report")
                    return Report();

                return "Usage: " + usage;
            }

            static string Status()
            {
                SeanceTraceRecorder recorder = SeanceTraceRecorder.Instance;
                if (recorder == null || recorder.Samples.Count == 0)
                    return "No trail recorded. Run seance_trace start.\nUsage: " + usage;

                return string.Format("Trail: {0} player sample(s), {5} ghost sample(s), recording={1}{2}{3}\nUsage: {4}",
                    recorder.Samples.Count, recorder.Recording,
                    recorder.BufferFull ? " (BUFFER FULL - recording stopped)" : "",
                    recorder.LeftDungeon ? " (PLAYER LEFT DUNGEON - recording stopped)" : "",
                    usage, recorder.GhostSamples.Count);
            }

            static string Start()
            {
                if (!GameManager.Instance.IsPlayerInsideDungeon)
                    return "Not inside a dungeon - nothing to trace against.";

                Vector3 position = GameManager.Instance.PlayerObject.transform.position;
                SeanceTraceRecorder recorder = SeanceTraceRecorder.Ensure();
                recorder.Begin(position);

                return string.Format(
                    "Trace started at {0} -> cell {1}.\n" +
                    "Sampling every {2:F2}m of movement, up to {3} samples. Walk the route, then run seance_trace report.\n" +
                    "The GHOST is recorded too, in its own buffer - cast Seance with the trace already running.\n" +
                    "Casting Seance and running other console commands will NOT reset the trail - only seance_trace start does.",
                    position, SeanceCell.FromWorld(position, SeanceDungeonGrid.CellSize),
                    SeanceTraceRecorder.SampleSpacing, SeanceTraceRecorder.MaxSamples);
            }

            static string Report()
            {
                SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
                if (grid == null)
                    return "No active SeanceDungeonGrid - cast Seance at least once in this dungeon first.";

                SeanceTraceRecorder recorder = SeanceTraceRecorder.Instance;
                // One player sample is enough: the ghost may have flown off a ledge while the player stood
                // still, and that is precisely the case worth reporting.
                if (recorder == null || recorder.Samples.Count < 1)
                    return "No trail recorded. Run seance_trace start and walk the route.";

                List<SeanceTraceSample> samples = recorder.Samples;
                int count = samples.Count;

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendFormat("TRACE REPORT: {0} samples, probe radius {1:F3}", count, SeanceDungeonGrid.ProbeRadius);
                if (recorder.Recording)
                    sb.Append("\n  (still recording - report is a snapshot)");
                if (recorder.BufferFull)
                    sb.AppendFormat("\n  WARNING: sample buffer filled at {0} - the trail is TRUNCATED and stops early.",
                        SeanceTraceRecorder.MaxSamples);
                if (recorder.LeftDungeon)
                    sb.Append("\n  NOTE: recording stopped because the player left the dungeon.");

                // A and B come from the last seam report when there is one, so trail membership is stated in
                // the same terms as everything else measured this session.
                HashSet<SeanceCell> a = SeanceSeamReport.LastA;
                HashSet<SeanceCell> b = SeanceSeamReport.LastB;
                if (a == null || b == null)
                {
                    a = SeanceSeamReport.Flood(grid, samples[0].Position);
                    b = SeanceSeamReport.Flood(grid, samples[count - 1].Position);
                    sb.AppendFormat("\n  No seam report this session - A and B were flooded from the trail's own " +
                        "first and last samples instead ({0} and {1} cells).", a.Count, b.Count);
                }
                else
                {
                    sb.AppendFormat("\n  A/B taken from the last seam report ({0} and {1} cells).", a.Count, b.Count);
                }

                // ---- Per-sample resolution ------------------------------------------------------------
                SeanceCell[] cells = new SeanceCell[count];
                string[] membership = new string[count];
                HashSet<SeanceCell> distinct = new HashSet<SeanceCell>();
                int airborne = 0;
                int swimming = 0;

                for (int i = 0; i < count; i++)
                {
                    cells[i] = SeanceCell.FromWorld(samples[i].Position, SeanceDungeonGrid.CellSize);
                    distinct.Add(cells[i]);
                    membership[i] = Membership(grid, a, b, cells[i]);
                    if (!samples[i].Grounded)
                        airborne++;
                    if (samples[i].InWater)
                        swimming++;
                }

                // ---- Per-transition verdicts ----------------------------------------------------------
                // Only where the CELL changes; consecutive samples inside one cell are not a transition.
                string[] verdict = new string[count];      // null = no transition or transition accepted
                string[] bucket = new string[count];
                int notOpen = 0, noSupport = 0, edgeBlocked = 0, noRule = 0, skipped = 0;

                List<string> impassable = new List<string>();
                HashSet<string> impassableSeen = new HashSet<string>();

                for (int i = 1; i < count; i++)
                {
                    if (cells[i].Equals(cells[i - 1]))
                        continue;

                    int dx = cells[i].X - cells[i - 1].X;
                    int dy = cells[i].Y - cells[i - 1].Y;
                    int dz = cells[i].Z - cells[i - 1].Z;
                    int chebyshev = Mathf.Max(Mathf.Abs(dx), Mathf.Max(Mathf.Abs(dy), Mathf.Abs(dz)));

                    if (chebyshev > 1)
                    {
                        // The player moved more than one cell between two samples 0.25m apart - a fall, a
                        // teleport, or a load. Not an adjacency the grid was ever asked about.
                        skipped++;
                        bucket[i] = "SKIP";
                        verdict[i] = string.Format("MULTI-CELL JUMP {0}->{1} (delta {2},{3},{4}) - fall, teleport or load, not an adjacency",
                            cells[i - 1], cells[i], dx, dy, dz);
                        continue;
                    }

                    string reason = grid.DescribeEdgeRejection(cells[i - 1], cells[i]);
                    // "RESCUED" is a blocked segment the door-passage core legitimately accepts - the
                    // pathfinder crosses it, so it is not a disagreement with reality.
                    bool clear = reason.StartsWith("edge is CLEAR") || reason.Contains("RESCUED");

                    if (clear)
                    {
                        // Mirror (stated as such, not measured): the grid says the edge itself is fine, so
                        // acceptance now depends on the pathfinder's movement rules. ExpandNode refuses a
                        // PURE-VERTICAL move outright unless the mover is in water or the destination is an
                        // elevator column; every move with a horizontal component is a plain walk.
                        bool pureVertical = dx == 0 && dz == 0;
                        if (!pureVertical || samples[i].InWater || grid.IsWater(cells[i]) || grid.IsElevatorColumn(cells[i]))
                            continue;

                        noRule++;
                        bucket[i] = "NO_RULE";
                        verdict[i] = string.Format("NO_RULE {0}->{1}: pure-vertical move, not in water, destination is not an elevator column",
                            cells[i - 1], cells[i]);
                    }
                    else if (reason.StartsWith("NOT_OPEN"))
                    {
                        notOpen++;
                        bucket[i] = "NOT_OPEN";
                        verdict[i] = string.Format("NOT_OPEN {0}->{1}: {2}", cells[i - 1], cells[i], reason);
                    }
                    else if (reason.StartsWith("NO_SUPPORT"))
                    {
                        noSupport++;
                        bucket[i] = "NO_SUPPORT";
                        verdict[i] = string.Format("NO_SUPPORT {0}->{1}: {2} (a drop or jump rule may still cover this)",
                            cells[i - 1], cells[i], reason);
                    }
                    else
                    {
                        edgeBlocked++;
                        bucket[i] = "EDGE_BLOCKED";
                        verdict[i] = string.Format("EDGE_BLOCKED {0}->{1}: {2}", cells[i - 1], cells[i], reason);
                    }

                    // The headline list. Deduped by cell pair - walking the same doorway four times is one
                    // disagreement, not four.
                    if (bucket[i] == "EDGE_BLOCKED" || bucket[i] == "NOT_OPEN")
                    {
                        string key = cells[i - 1].ToString() + "->" + cells[i].ToString();
                        if (impassableSeen.Add(key))
                            impassable.Add(string.Format("{0}  [sample {1}, player at {2}, grounded={3}]",
                                verdict[i], i, samples[i].Position, samples[i].Grounded));
                    }
                }

                // ---- Collapsed trail ------------------------------------------------------------------
                sb.Append("\n\n[1] TRAIL");
                int detailLines = 0;
                int runStart = 0;
                string runMembership = membership[0];

                for (int i = 1; i < count; i++)
                {
                    bool membershipChanged = membership[i] != runMembership;
                    bool rejected = verdict[i] != null;
                    if (!membershipChanged && !rejected)
                        continue;

                    sb.AppendFormat("\n  samples {0}-{1}\t{2}\t(all edges clear)", runStart + 1, i, runMembership);

                    if (rejected && detailLines < MaxDetailLines)
                    {
                        detailLines++;
                        sb.AppendFormat("\n  samples {0}-{1}\t{2}\t{3}", i, i + 1, membership[i], verdict[i]);
                        sb.AppendFormat("\n      from {0}", grid.DescribeCellBrief(cells[i - 1]));
                        sb.AppendFormat("\n      to   {0}", grid.DescribeCellBrief(cells[i]));
                    }

                    if (membershipChanged && detailLines < MaxDetailLines)
                    {
                        detailLines++;
                        sb.AppendFormat("\n  >>> MEMBERSHIP {0} -> {1} at sample {2}, player at {3}",
                            runMembership, membership[i], i + 1, samples[i].Position);
                        sb.AppendFormat("\n      {0}", grid.DescribeCellBrief(cells[i]));
                    }

                    runStart = i;
                    runMembership = membership[i];
                }

                sb.AppendFormat("\n  samples {0}-{1}\t{2}\t(all edges clear)", runStart + 1, count, runMembership);

                if (detailLines >= MaxDetailLines)
                    sb.AppendFormat("\n  WARNING: detail truncated at {0} entries. This listing is now an ARBITRARY SLICE - " +
                        "do not select cells from it as evidence. The counts and headline list below are complete.",
                        MaxDetailLines);

                // ---- Endpoints ------------------------------------------------------------------------
                sb.Append("\n\n[2] ENDPOINTS");
                sb.AppendFormat("\n  first sample: {0} grounded={1} inWater={2} -> {3} [{4}]",
                    samples[0].Position, samples[0].Grounded, samples[0].InWater,
                    grid.DescribeCellBrief(cells[0]), membership[0]);
                sb.AppendFormat("\n  last  sample: {0} grounded={1} inWater={2} -> {3} [{4}]",
                    samples[count - 1].Position, samples[count - 1].Grounded, samples[count - 1].InWater,
                    grid.DescribeCellBrief(cells[count - 1]), membership[count - 1]);

                // ---- Summary --------------------------------------------------------------------------
                int inA = 0, inB = 0, inOrphan = 0, outside = 0;
                for (int i = 0; i < count; i++)
                {
                    if (membership[i] == "in A") inA++;
                    else if (membership[i] == "in B") inB++;
                    else if (membership[i] == "ORPHAN") inOrphan++;
                    else outside++;
                }

                sb.Append("\n\n[3] SUMMARY");
                sb.AppendFormat("\n  samples {0}, distinct cells {1}, airborne samples {2}, swimming samples {3}",
                    count, distinct.Count, airborne, swimming);
                sb.AppendFormat("\n  membership by sample: A {0}, B {1}, ORPHAN {2}, NOT-OPEN-OR-UNSUPPORTED {3}",
                    inA, inB, inOrphan, outside);
                sb.AppendFormat("\n  rejected transitions: NOT_OPEN {0}, NO_SUPPORT {1}, EDGE_BLOCKED {2}, NO_RULE {3}, multi-cell skips {4}",
                    notOpen, noSupport, edgeBlocked, noRule, skipped);

                sb.AppendFormat("\n\n[4] IMPASSABLE CROSSINGS: {0} distinct", impassable.Count);
                sb.Append("\n  (places the player physically walked through that the grid calls solid or blocked)");
                if (impassable.Count == 0)
                    sb.Append("\n  NONE - every cell the player entered was open, and every edge they crossed was clear.");
                for (int i = 0; i < impassable.Count; i++)
                    sb.AppendFormat("\n  {0}", impassable[i]);

                AppendGhostTrail(sb, grid, recorder, a, b);

                return sb.ToString();
            }

            // Where the GHOST actually went. The route it flies is derived from the polyline, not from the
            // grid directly, so a ghost inside solid rock or plummeting down a shaft is a disagreement
            // between the validated path and the world - which is exactly what "falls through the geometry"
            // looks like, and nothing that samples the player can see it.
            // Descent is accumulated over a RUN of consecutive descending samples, not measured between two
            // of them. The first version of this compared consecutive samples for a 2-cell drop - but samples
            // are taken every 0.25m of movement, so consecutive samples during a fall are 0.25m apart and the
            // threshold was mathematically unreachable. It reported NONE for a fall of any depth.
            const float FallMeters = 2f;

            // Below this, a sample counts as continuing the current descent rather than ending it - a fall is
            // not perfectly monotonic frame to frame.
            const float DescentTolerance = 0.05f;

            static void AppendGhostTrail(System.Text.StringBuilder sb, SeanceDungeonGrid grid,
                SeanceTraceRecorder recorder, HashSet<SeanceCell> a, HashSet<SeanceCell> b)
            {
                List<SeanceTraceSample> ghost = recorder.GhostSamples;
                sb.AppendFormat("\n\n[5] GHOST TRAIL: {0} sample(s)", ghost.Count);
                if (ghost.Count == 0)
                {
                    sb.Append("\n  No ghost was active while recording - cast Seance with the trace already running.");
                    return;
                }

                int insideSolid = 0;
                int unsupported = 0;
                int detail = 0;
                List<string> falls = new List<string>();

                SeanceCell previous = SeanceCell.FromWorld(ghost[0].Position, SeanceDungeonGrid.CellSize);
                int runStart = 0;
                string runState = StateOf(grid, a, b, previous);

                int descentStart = 0;
                float minY = ghost[0].Position.y;
                float maxY = ghost[0].Position.y;

                for (int i = 0; i < ghost.Count; i++)
                {
                    SeanceCell cell = SeanceCell.FromWorld(ghost[i].Position, SeanceDungeonGrid.CellSize);
                    bool open = grid.IsOpen(cell);
                    bool support = grid.HasSupport(cell);
                    if (!open)
                        insideSolid++;
                    if (open && !support)
                        unsupported++;

                    if (ghost[i].Position.y < minY)
                        minY = ghost[i].Position.y;
                    if (ghost[i].Position.y > maxY)
                        maxY = ghost[i].Position.y;

                    // Accumulate descent across a run. The run ends the moment the ghost stops descending.
                    if (i > 0)
                    {
                        if (ghost[i].Position.y <= ghost[i - 1].Position.y + DescentTolerance)
                        {
                            // still descending - keep the run open
                        }
                        else
                        {
                            RecordDescent(falls, grid, ghost, descentStart, i - 1);
                            descentStart = i;
                        }
                    }

                    string state = StateOf(grid, a, b, cell);
                    if (state != runState)
                    {
                        sb.AppendFormat("\n  samples {0}-{1}\t{2}", runStart + 1, i, runState);
                        if (detail < MaxDetailLines)
                        {
                            detail++;
                            sb.AppendFormat("\n  >>> {0} -> {1} at sample {2}, ghost at {3}",
                                runState, state, i + 1, ghost[i].Position);
                            sb.AppendFormat("\n      {0}", grid.DescribeCellBrief(cell));
                        }

                        runStart = i;
                        runState = state;
                    }

                    previous = cell;
                }

                RecordDescent(falls, grid, ghost, descentStart, ghost.Count - 1);

                sb.AppendFormat("\n  samples {0}-{1}\t{2}", runStart + 1, ghost.Count, runState);

                sb.AppendFormat("\n  ghost Y range: {0:F2} to {1:F2} (spread {2:F2}m)", minY, maxY, maxY - minY);
                sb.AppendFormat("\n  first: {0} -> {1}", ghost[0].Position,
                    grid.DescribeCellBrief(SeanceCell.FromWorld(ghost[0].Position, SeanceDungeonGrid.CellSize)));
                sb.AppendFormat("\n  last:  {0} -> {1}", ghost[ghost.Count - 1].Position,
                    grid.DescribeCellBrief(SeanceCell.FromWorld(ghost[ghost.Count - 1].Position, SeanceDungeonGrid.CellSize)));
                sb.AppendFormat("\n  samples inside NOT-OPEN cells: {0}, in open-but-unsupported cells: {1}",
                    insideSolid, unsupported);

                sb.AppendFormat("\n\n[6] GHOST DESCENTS (continuous runs losing >= {0:F1}m): {1}",
                    FallMeters, falls.Count);
                if (falls.Count == 0)
                    sb.Append("\n  NONE.");
                for (int i = 0; i < falls.Count; i++)
                    sb.AppendFormat("\n  {0}", falls[i]);
            }

            // One continuous descending run. Horizontal distance is printed alongside the drop because that
            // is what separates walking down a staircase from going through the floor: a staircase trades
            // roughly as much horizontal travel as vertical, a fall trades almost none. Support at the cell
            // it left says whether it dropped through something it was standing on.
            static void RecordDescent(List<string> falls, SeanceDungeonGrid grid,
                List<SeanceTraceSample> ghost, int from, int to)
            {
                if (to <= from)
                    return;

                Vector3 start = ghost[from].Position;
                Vector3 end = ghost[to].Position;
                float drop = start.y - end.y;
                if (drop < FallMeters)
                    return;

                float horizontal = new Vector2(end.x - start.x, end.z - start.z).magnitude;
                SeanceCell startCell = SeanceCell.FromWorld(start, SeanceDungeonGrid.CellSize);
                SeanceCell endCell = SeanceCell.FromWorld(end, SeanceDungeonGrid.CellSize);

                falls.Add(string.Format(
                    "samples {0}-{1}: dropped {2:F2}m over {3:F2}m horizontal\n" +
                    "      from {4} {5}\n" +
                    "        {6}\n" +
                    "      to   {7} {8}\n" +
                    "        {9}",
                    from + 1, to + 1, drop, horizontal,
                    start, startCell, grid.DescribeCellBrief(startCell),
                    end, endCell, grid.DescribeCellBrief(endCell)));
            }

            static string StateOf(SeanceDungeonGrid grid, HashSet<SeanceCell> a, HashSet<SeanceCell> b, SeanceCell cell)
            {
                if (!grid.IsOpen(cell))
                    return "INSIDE-SOLID";
                if (!grid.HasSupport(cell))
                    return "OPEN-NO-SUPPORT";

                return Membership(grid, a, b, cell);
            }

            static string Membership(SeanceDungeonGrid grid, HashSet<SeanceCell> a, HashSet<SeanceCell> b, SeanceCell cell)
            {
                if (a.Contains(cell))
                    return "in A";
                if (b.Contains(cell))
                    return "in B";
                if (grid.IsOpen(cell) && grid.HasSupport(cell))
                    return "ORPHAN";

                return grid.IsOpen(cell) ? "UNSUPPORTED" : "NOT-OPEN";
            }
        }

        // Tests whether a step edge that fails as a STRAIGHT segment clears as an L-shaped one. A straight
        // line between two stair treads passes through the riser's interior - which is what a staircase is -
        // whereas a player walks out over the drop and then descends. No new geometry code: this is
        // IsSegmentClear called twice instead of once.
        static class SeanceStepTest
        {
            public static readonly string name = "seance_steptest";
            public static readonly string description =
                "Tests one cell pair (or, bare, every adjacent orphan pair from the last seam report) for " +
                "whether a straight segment is blocked while an L-shaped path clears - the stair-traversal test.";
            public static readonly string usage = "seance_steptest [<x1> <y1> <z1> <x2> <y2> <z2>]";

            public static string Execute(params string[] args)
            {
                SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
                if (grid == null)
                    return "No active SeanceDungeonGrid - cast Seance at least once in this dungeon first.";

                if (args.Length >= 6)
                {
                    int x1, y1, z1, x2, y2, z2;
                    if (!int.TryParse(args[0], out x1) || !int.TryParse(args[1], out y1) || !int.TryParse(args[2], out z1) ||
                        !int.TryParse(args[3], out x2) || !int.TryParse(args[4], out y2) || !int.TryParse(args[5], out z2))
                        return "Usage: " + usage + "   (cell coordinates, not world positions)";

                    return DescribePair(grid, new SeanceCell(x1, y1, z1), new SeanceCell(x2, y2, z2));
                }

                HashSet<SeanceCell> orphans = SeanceSeamReport.LastOrphans;
                if (orphans == null || orphans.Count == 0)
                    return "No orphan set recorded - run seance_seamreport (both regions) first, or pass two cell coordinates.";

                int straightClear = 0;
                int straightBlockedLClear = 0;
                int blockedBothWays = 0;
                int pairs = 0;

                foreach (SeanceCell cell in orphans)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                if (dy == 0 || (dx == 0 && dz == 0))
                                    continue;   // step moves only: dy != 0 with horizontal displacement

                                SeanceCell neighbour = cell.Offset(dx, dy, dz);
                                if (!orphans.Contains(neighbour))
                                    continue;

                                // Each unordered pair once.
                                if (neighbour.X < cell.X || (neighbour.X == cell.X && neighbour.Y < cell.Y) ||
                                    (neighbour.X == cell.X && neighbour.Y == cell.Y && neighbour.Z < cell.Z))
                                    continue;

                                pairs++;

                                bool straight, descL, ascL;
                                Evaluate(grid, cell, neighbour, out straight, out descL, out ascL);

                                if (straight)
                                    straightClear++;
                                else if (descL || ascL)
                                    straightBlockedLClear++;
                                else
                                    blockedBothWays++;
                            }
                        }
                    }
                }

                return string.Format(
                    "STEP TEST over {0} adjacent orphan step-pairs (dy != 0), probe radius {1:F3}:\n" +
                    "  straight already clear      : {2}\n" +
                    "  straight BLOCKED, L CLEAR   : {3}   <-- these are the edges an L-path would recover\n" +
                    "  blocked both ways           : {4}",
                    pairs, SeanceDungeonGrid.ProbeRadius, straightClear, straightBlockedLClear, blockedBothWays);
            }

            static void Evaluate(SeanceDungeonGrid grid, SeanceCell a, SeanceCell b,
                out bool straight, out bool descendingL, out bool ascendingL)
            {
                Vector3 freeA = grid.FreePosition(a);
                Vector3 freeB = grid.FreePosition(b);

                Collider blocker;
                bool byAction;
                straight = grid.IsSegmentClear(freeA, freeB, SeanceDungeonGrid.ProbeRadius, out blocker, out byAction);

                // Out over the drop at the upper height, then descend.
                Vector3 descCorner = new Vector3(freeB.x, freeA.y, freeB.z);
                descendingL = grid.IsSegmentClear(freeA, descCorner, SeanceDungeonGrid.ProbeRadius, out blocker, out byAction)
                    && grid.IsSegmentClear(descCorner, freeB, SeanceDungeonGrid.ProbeRadius, out blocker, out byAction);

                // Rise to the upper height first, then move across.
                Vector3 ascCorner = new Vector3(freeA.x, freeB.y, freeA.z);
                ascendingL = grid.IsSegmentClear(freeA, ascCorner, SeanceDungeonGrid.ProbeRadius, out blocker, out byAction)
                    && grid.IsSegmentClear(ascCorner, freeB, SeanceDungeonGrid.ProbeRadius, out blocker, out byAction);
            }

            static string DescribePair(SeanceDungeonGrid grid, SeanceCell a, SeanceCell b)
            {
                Vector3 freeA = grid.FreePosition(a);
                Vector3 freeB = grid.FreePosition(b);
                Vector3 centreA = a.ToWorldCenter(SeanceDungeonGrid.CellSize);
                Vector3 centreB = b.ToWorldCenter(SeanceDungeonGrid.CellSize);

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendFormat("STEP TEST {0} -> {1} (probe radius {2:F3})", a, b, SeanceDungeonGrid.ProbeRadius);
                sb.AppendFormat("\n  A free {0}{1}", freeA,
                    (freeA - centreA).magnitude > 0.0001f
                        ? string.Format(" (OFF-CENTRE by {0:F2}m)", (freeA - centreA).magnitude) : "");
                sb.AppendFormat("\n  B free {0}{1}", freeB,
                    (freeB - centreB).magnitude > 0.0001f
                        ? string.Format(" (OFF-CENTRE by {0:F2}m)", (freeB - centreB).magnitude) : "");

                Collider blocker;
                bool byAction;
                bool straight = grid.IsSegmentClear(freeA, freeB, SeanceDungeonGrid.ProbeRadius, out blocker, out byAction);
                sb.AppendFormat("\n\n  STRAIGHT {0}", straight ? "CLEAR" : "BLOCKED");
                if (!straight)
                {
                    sb.AppendFormat(" by '{0}' on layer '{1}', {2:F3}m thick",
                        blocker != null ? blocker.name : "(unknown)",
                        blocker != null ? LayerMask.LayerToName(blocker.gameObject.layer) : "(unknown)",
                        grid.MeasureBlockageThickness(freeA, freeB, SeanceDungeonGrid.ProbeRadius));
                }

                Vector3 descCorner = new Vector3(freeB.x, freeA.y, freeB.z);
                bool descLeg1 = grid.IsSegmentClear(freeA, descCorner, SeanceDungeonGrid.ProbeRadius, out blocker, out byAction);
                bool descLeg2 = grid.IsSegmentClear(descCorner, freeB, SeanceDungeonGrid.ProbeRadius, out blocker, out byAction);
                sb.AppendFormat("\n  DESC-L via {0}: out {1}, down {2} -> {3}",
                    descCorner, descLeg1 ? "CLEAR" : "BLOCKED", descLeg2 ? "CLEAR" : "BLOCKED",
                    descLeg1 && descLeg2 ? "CLEAR" : "BLOCKED");

                Vector3 ascCorner = new Vector3(freeA.x, freeB.y, freeA.z);
                bool ascLeg1 = grid.IsSegmentClear(freeA, ascCorner, SeanceDungeonGrid.ProbeRadius, out blocker, out byAction);
                bool ascLeg2 = grid.IsSegmentClear(ascCorner, freeB, SeanceDungeonGrid.ProbeRadius, out blocker, out byAction);
                sb.AppendFormat("\n  ASC-L  via {0}: up {1}, across {2} -> {3}",
                    ascCorner, ascLeg1 ? "CLEAR" : "BLOCKED", ascLeg2 ? "CLEAR" : "BLOCKED",
                    ascLeg1 && ascLeg2 ? "CLEAR" : "BLOCKED");

                sb.AppendFormat("\n\n  VERDICT: STRAIGHT {0} / DESC-L {1} / ASC-L {2}",
                    straight ? "CLEAR" : "BLOCKED",
                    descLeg1 && descLeg2 ? "CLEAR" : "BLOCKED",
                    ascLeg1 && ascLeg2 ? "CLEAR" : "BLOCKED");

                return sb.ToString();
            }
        }

        // Companion to the reach report: dissects ONE cell's support probe. A hit with distance 0 and point
        // (0,0,0) is the documented corrupt-data signature of a SphereCast that began already overlapping a
        // collider - it reads as "floor at the world origin", not as a missing hit.
        static class SeanceSupportCheck
        {
            public static readonly string name = "seance_supportcheck";
            public static readonly string description =
                "Dissects the support probe for one cell: raw result, every hit with distance and point, and " +
                "whether the cell below reads blocked - for diagnosing corrupt downward-sweep data.";
            public static readonly string usage = "seance_supportcheck <x> <y> <z>";

            public static string Execute(params string[] args)
            {
                SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
                if (grid == null)
                    return "No active SeanceDungeonGrid - cast Seance at least once in this dungeon first.";

                float x, y, z;
                if (args.Length < 3 || !float.TryParse(args[0], out x) || !float.TryParse(args[1], out y) || !float.TryParse(args[2], out z))
                    return "Usage: " + usage;

                return grid.DebugDescribeSupport(new Vector3(x, y, z));
            }
        }

        // Forces the gated-obstacle escort on the active ghost. Needed because the escort is by design only
        // reachable via a FAILED search, so in any dungeon that has a walkable alternative the
        // lever-then-teleporter sequence cannot be exercised on demand at all.
        static class SeanceForceEscort
        {
            public static readonly string name = "seance_forceescort";
            public static readonly string description =
                "Forces the active Seance ghost to escort you to the nearest lever-gated obstacle, even " +
                "though a normal route exists - for testing the lever-then-continue sequence on demand.";
            public static readonly string usage = "seance_forceescort";

            public static string Execute(params string[] args)
            {
                SeanceGhostGuide guide = Object.FindObjectOfType<SeanceGhostGuide>();
                if (guide == null)
                    return "No active Seance ghost - cast Seance first.";

                return guide.DebugForceObstacleEscort();
            }
        }

        // Live tuning knob for the openness/edge probe radius, with a cache flush so the change actually
        // takes effect. Exists because this value is the single most consequential number in the grid (it
        // decides which real doorways the ghost believes exist) and bisecting it by rebuilding the mod
        // each time is painfully slow.
        static class SeanceProbe
        {
            public static readonly string name = "seance_probe";
            public static readonly string description =
                "Gets or sets the Seance grid's openness/edge probe radius and flushes the cell cache " +
                "(default 0.35, matched to the player capsule) - for tuning which doorways the ghost can fit through.";
            public static readonly string usage = "seance_probe [<radius>]";

            public static string Execute(params string[] args)
            {
                if (args.Length == 0)
                {
                    return string.Format("Seance probe radius is {0:F3} (default {1:F3}; player capsule 0.35 minus 0.06 skin width = 0.29 true minimum, so stay in [0.29, 0.32]).",
                        SeanceDungeonGrid.ProbeRadius, SeanceDungeonGrid.DefaultProbeRadius);
                }

                float radius;
                if (!float.TryParse(args[0], out radius))
                    return "Usage: " + usage;

                if (radius < 0.05f || radius > 1f)
                    return "Radius must be between 0.05 and 1.0.";

                float previous = SeanceDungeonGrid.ProbeRadius;
                SeanceDungeonGrid.ProbeRadius = radius;

                SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
                if (grid != null)
                    grid.ClearCellCache();

                return string.Format(
                    "Seance probe radius {0:F3} -> {1:F3}. Cell cache flushed{2}. Re-run seance_pathcheck/seance_reachcheck " +
                    "to see the effect. NOTE: the ghost's own search walks a clearance ladder (0.45 -> 0.38 -> {3:F2}, " +
                    "widest that finds a route wins), so casting Seance will override this value.",
                    previous, radius, grid != null ? "" : " (no active grid yet)", SeanceDungeonGrid.DefaultProbeRadius);
            }
        }

        // Prints a top-down ASCII map of cell state around a position, one grid per Y level. Cell-by-cell
        // seance_gridcheck_at probing cannot show the SHAPE of a room - and when the grid disagrees with
        // what the player can actually walk (a room the physics reports as a sealed box that is really a
        // through-route), the shape is exactly what is needed: where the walls sit relative to cell
        // centres, and whether an opening exists but falls between sample points.
        static class SeanceSliceCheck
        {
            public static readonly string name = "seance_slicecheck";
            public static readonly string description =
                "Prints a top-down ASCII map of voxel-grid cell state around a position (one map per Y " +
                "level: '.' walkable, 'o' open but unsupported, '#' blocked, 'S' centre) - for seeing the " +
                "SHAPE of a room the pathfinder disagrees with rather than probing one cell at a time.";
            public static readonly string usage = "seance_slicecheck [<x> <y> <z>] [<radius> <yLevels>]";

            public static string Execute(params string[] args)
            {
                SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
                if (grid == null)
                    return "No active SeanceDungeonGrid - cast Seance at least once in this dungeon first.";

                Vector3 centre = GameManager.Instance.PlayerObject.transform.position;
                int argOffset = 0;
                if (args.Length >= 3)
                {
                    float x, y, z;
                    if (float.TryParse(args[0], out x) && float.TryParse(args[1], out y) && float.TryParse(args[2], out z))
                    {
                        centre = new Vector3(x, y, z);
                        argOffset = 3;
                    }
                }

                int radius = 10;
                if (args.Length > argOffset)
                    int.TryParse(args[argOffset], out radius);
                radius = Mathf.Clamp(radius, 2, 24);

                int yLevels = 1;
                if (args.Length > argOffset + 1)
                    int.TryParse(args[argOffset + 1], out yLevels);
                yLevels = Mathf.Clamp(yLevels, 1, 7);

                SeanceCell centreCell = SeanceCell.FromWorld(centre, SeanceDungeonGrid.CellSize);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendFormat("Slice around {0} -> centre cell {1}. radius={2}, yLevels={3}.",
                    centre, centreCell, radius, yLevels);
                sb.Append("\nLegend: '.'=open+support  ','=open+support but only OFF-CENTRE  'o'=open,no support  '#'=blocked  'S'=centre cell");
                sb.Append("\nRows are Z (increasing downward), columns are X (increasing rightward).");

                int halfLevels = yLevels / 2;
                for (int yOff = halfLevels; yOff >= -halfLevels; yOff--)
                {
                    int y = centreCell.Y + yOff;
                    sb.AppendFormat("\n\n--- Y = {0}{1} ---", y, yOff == 0 ? "  (centre level)" : "");

                    sb.Append("\n       ");
                    for (int x = centreCell.X - radius; x <= centreCell.X + radius; x++)
                        sb.Append(Mathf.Abs(x % 10));

                    for (int z = centreCell.Z - radius; z <= centreCell.Z + radius; z++)
                    {
                        sb.AppendFormat("\n z{0,5} ", z);
                        for (int x = centreCell.X - radius; x <= centreCell.X + radius; x++)
                        {
                            SeanceCell cell = new SeanceCell(x, y, z);
                            if (x == centreCell.X && z == centreCell.Z && y == centreCell.Y)
                                sb.Append('S');
                            else if (!grid.IsOpen(cell))
                                sb.Append('#');
                            else if (!grid.HasSupport(cell))
                                sb.Append('o');
                            else if ((grid.FreePosition(cell) - cell.ToWorldCenter(SeanceDungeonGrid.CellSize)).sqrMagnitude > 0.0001f)
                                sb.Append(',');
                            else
                                sb.Append('.');
                        }
                    }
                }

                return sb.ToString();
            }
        }

        // Characterises the REGION reachable from a position under the real movement rules, and reports
        // which teleporters that region can and cannot get to. In a teleporter-maze dungeon a route
        // failure is usually not local geometry but "this pocket contains no onward teleporter trigger",
        // and that is invisible in seance_pathcheck's aggregate node count. Takes explicit coordinates so
        // a pocket the player cannot easily walk to (a teleport landing spot, in particular) can still be
        // probed - there is no in-game coordinate readout to navigate by.
        static class SeanceReachCheck
        {
            public static readonly string name = "seance_reachcheck";
            public static readonly string description =
                "Reports how large the region reachable from a position is (cell count + bounding box) and " +
                "which teleporters it can/cannot reach - for diagnosing which link in a teleporter chain is " +
                "broken. Defaults to the player's position.";
            public static readonly string usage = "seance_reachcheck [<x> <y> <z>]";

            public static string Execute(params string[] args)
            {
                SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
                if (grid == null)
                    return "No active SeanceDungeonGrid - cast Seance at least once in this dungeon first.";

                Vector3 start = GameManager.Instance.PlayerObject.transform.position;
                if (args.Length >= 3)
                {
                    float x, y, z;
                    if (!float.TryParse(args[0], out x) || !float.TryParse(args[1], out y) || !float.TryParse(args[2], out z))
                        return "Usage: " + usage;
                    start = new Vector3(x, y, z);
                }

                // Deliberately aimed at a goal this region is expected to FAIL to reach, so the search
                // exhausts the whole reachable set rather than stopping early on success.
                DaggerfallDungeon dungeon = GameManager.Instance.PlayerEnterExit != null
                    ? GameManager.Instance.PlayerEnterExit.Dungeon
                    : null;
                if (dungeon == null || dungeon.StartMarker == null)
                    return "No dungeon start marker found to search toward.";

                Vector3 goal = dungeon.StartMarker.transform.position;

                SeanceVoxelPathfinder pathfinder = new SeanceVoxelPathfinder(grid, start, goal);
                IEnumerator search = pathfinder.Search();
                int guard = 0;
                const int maxIterations = 2000000;
                while (search.MoveNext() && guard < maxIterations)
                    guard++;

                HashSet<SeanceCell> reachable = pathfinder.GetExpandedCells();
                if (reachable.Count == 0)
                    return string.Format("Nothing reachable from {0}.\n{1}", start, pathfinder.DebugSummary());

                int minX = int.MaxValue, maxX = int.MinValue;
                int minY = int.MaxValue, maxY = int.MinValue;
                int minZ = int.MaxValue, maxZ = int.MinValue;
                foreach (SeanceCell cell in reachable)
                {
                    if (cell.X < minX) minX = cell.X;
                    if (cell.X > maxX) maxX = cell.X;
                    if (cell.Y < minY) minY = cell.Y;
                    if (cell.Y > maxY) maxY = cell.Y;
                    if (cell.Z < minZ) minZ = cell.Z;
                    if (cell.Z > maxZ) maxZ = cell.Z;
                }

                return string.Format(
                    "Reachable from {0} (toward entrance {1}): {2} settled cell(s). Bounding box x[{3},{4}] y[{5},{6}] z[{7},{8}].{9}\n{10}",
                    start, goal, reachable.Count, minX, maxX, minY, maxY, minZ, maxZ,
                    grid.DescribeTeleportReachability(reachable), pathfinder.DebugSummary());
            }
        }

        // Runs the actual A* search synchronously (pumping the coroutine's MoveNext() in a tight loop
        // rather than spread across frames - fine for a one-off manual diagnostic) from the player's
        // current position to the nearest live quest resource, and reports exactly where it
        // succeeded/failed: bad start/goal snap vs. pass 1/2 node counts vs. a genuine no-path result.
        // This is what actually exercises the same search the ghost itself runs, rather than just
        // inspecting grid/elevator state in isolation (see SeanceGridCheck above).
        static class SeancePathCheck
        {
            public static readonly string name = "seance_pathcheck";
            public static readonly string description =
                "Runs the voxel A* search from the player's position to the nearest quest resource " +
                "synchronously and reports start/goal snap results plus pass 1/2 node counts - for " +
                "diagnosing Seance pathfinder failures.";
            public static readonly string usage = "seance_pathcheck";

            public static string Execute(params string[] args)
            {
                SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
                if (grid == null)
                    return "No active SeanceDungeonGrid - cast Seance at least once in this dungeon first.";

                Vector3 start = GameManager.Instance.PlayerObject.transform.position;

                // SeanceEffect's own target resolution, called directly (not duplicated) so this
                // command always diagnoses exactly what the spell would really do: nearest valid
                // quest resource, else the dungeon's start marker (guide-back-to-entrance mode).
                Transform nearest = SeanceEffect.FindNearestQuestTarget(start);
                string targetKind;
                Vector3 goal;
                if (nearest != null)
                {
                    targetKind = "nearest quest resource";
                    goal = nearest.position;
                }
                else
                {
                    DaggerfallDungeon dungeon = GameManager.Instance.PlayerEnterExit != null
                        ? GameManager.Instance.PlayerEnterExit.Dungeon
                        : null;
                    if (dungeon == null || dungeon.StartMarker == null)
                        return "No active quest resource and no dungeon start marker found.";

                    targetKind = "dungeon entrance StartMarker";
                    goal = dungeon.StartMarker.transform.position;
                }

                SeanceVoxelPathfinder pathfinder = new SeanceVoxelPathfinder(grid, start, goal);
                IEnumerator search = pathfinder.Search();

                int guard = 0;
                const int maxIterations = 2000000;
                while (search.MoveNext() && guard < maxIterations)
                    guard++;

                return string.Format("Target: {0} at {1}.\n{2}", targetKind, goal, pathfinder.DebugSummary());
            }
        }

        // Floods outward from the player's position using ONLY raw cell openness (6-connected, no
        // support/climb/jump/elevator rules at all) up to a cap - distinguishes a real geometry
        // disconnect (the flood itself gets stuck small) from the A* movement rules simply being too
        // strict to use an opening that's genuinely there (the flood reaches far, but seance_pathcheck
        // still fails). Used when seance_pathcheck itself reports a suspiciously tiny expanded-node
        // count (a handful of cells, nowhere near the node cap) right from the start position.
        static class SeanceFloodCheck
        {
            public static readonly string name = "seance_floodcheck";
            public static readonly string description =
                "Floods outward from the player's position using only raw cell openness (no movement " +
                "rules) up to a cap, and reports how many cells were reached and their bounding box - " +
                "tells us whether a 'stuck immediately' pathfinder failure is a real geometry disconnect " +
                "or just the A* movement rules being too strict.";
            public static readonly string usage = "seance_floodcheck";

            const int cap = 20000;

            public static string Execute(params string[] args)
            {
                SeanceDungeonGrid grid = SeanceDungeonGrid.Current;
                if (grid == null)
                    return "No active SeanceDungeonGrid - cast Seance at least once in this dungeon first.";

                Vector3 position = GameManager.Instance.PlayerObject.transform.position;
                SeanceCell start = SeanceCell.FromWorld(position, SeanceDungeonGrid.CellSize);

                if (!grid.IsOpen(start))
                    return string.Format("Start cell {0} is not open at all - standing inside geometry?", start);

                HashSet<SeanceCell> visited = new HashSet<SeanceCell>();
                Queue<SeanceCell> queue = new Queue<SeanceCell>();
                visited.Add(start);
                queue.Enqueue(start);

                int minX = start.X, maxX = start.X, minY = start.Y, maxY = start.Y, minZ = start.Z, maxZ = start.Z;

                int[] dx = { 1, -1, 0, 0, 0, 0 };
                int[] dy = { 0, 0, 1, -1, 0, 0 };
                int[] dz = { 0, 0, 0, 0, 1, -1 };

                while (queue.Count > 0 && visited.Count < cap)
                {
                    SeanceCell current = queue.Dequeue();

                    for (int i = 0; i < 6; i++)
                    {
                        SeanceCell neighbor = current.Offset(dx[i], dy[i], dz[i]);
                        if (visited.Contains(neighbor) || !grid.IsOpen(neighbor))
                            continue;

                        visited.Add(neighbor);
                        queue.Enqueue(neighbor);

                        if (neighbor.X < minX) minX = neighbor.X;
                        if (neighbor.X > maxX) maxX = neighbor.X;
                        if (neighbor.Y < minY) minY = neighbor.Y;
                        if (neighbor.Y > maxY) maxY = neighbor.Y;
                        if (neighbor.Z < minZ) minZ = neighbor.Z;
                        if (neighbor.Z > maxZ) maxZ = neighbor.Z;

                        if (visited.Count >= cap)
                            break;
                    }
                }

                return string.Format(
                    "Flood-filled {0} open cell(s) from {1} (cap {2}). Bounding box: x[{3},{4}] y[{5},{6}] z[{7},{8}].",
                    visited.Count, start, cap, minX, maxX, minY, maxY, minZ, maxZ);
            }
        }
    }

    // One recorded step of the player's actual route. Plain struct, no enums - see SeanceCell.cs's header
    // for why this mod has no enums anywhere.
    public struct SeanceTraceSample
    {
        public Vector3 Position;
        public bool Grounded;
        public bool InWater;

        public SeanceTraceSample(Vector3 position, bool grounded, bool inWater)
        {
            Position = position;
            Grounded = grounded;
            InWater = inWater;
        }
    }

    // Records where the player ACTUALLY walked, so the route stops being something we infer from a verbal
    // description and becomes something we replay against the grid cell by cell.
    //
    // Distance-triggered rather than time-triggered: standing still, reading a book, or sitting in a menu
    // costs nothing, and a slow careful walk is sampled at the same spatial resolution as a run. Top-level
    // MonoBehaviour in the console-commands file rather than its own .cs, since a new file means hand-editing
    // Mod Builder's own file list.
    public class SeanceTraceRecorder : MonoBehaviour
    {
        public const float SampleSpacing = 0.25f;
        public const int MaxSamples = 20000;

        public static SeanceTraceRecorder Instance;

        public readonly List<SeanceTraceSample> Samples = new List<SeanceTraceSample>();

        // The ghost gets its OWN buffer, sampled independently. The player may stand still while the ghost
        // flies off a ledge, so one shared distance trigger would miss exactly the event worth catching.
        // Ghost samples carry no grounded/water meaning (it hovers and noclips), so those stay false.
        public readonly List<SeanceTraceSample> GhostSamples = new List<SeanceTraceSample>();

        public bool Recording;
        public bool BufferFull;
        public bool LeftDungeon;

        Vector3 lastSample;
        Vector3 lastGhostSample;
        bool hasGhostSample;

        public static SeanceTraceRecorder Ensure()
        {
            if (Instance == null)
            {
                GameObject host = new GameObject("SeanceTraceRecorder");
                UnityEngine.Object.DontDestroyOnLoad(host);
                Instance = host.AddComponent<SeanceTraceRecorder>();
            }

            return Instance;
        }

        public void Begin(Vector3 position)
        {
            Samples.Clear();
            GhostSamples.Clear();
            hasGhostSample = false;
            BufferFull = false;
            LeftDungeon = false;
            Recording = true;
            lastSample = position;
            Samples.Add(Capture(position));
        }

        void Update()
        {
            if (!Recording)
                return;

            if (!GameManager.Instance.IsPlayerInsideDungeon)
            {
                // Recording on outside the dungeon would append positions in a completely different world
                // space against a grid that no longer describes them - garbage, not data.
                Recording = false;
                LeftDungeon = true;
                return;
            }

            SampleGhost();

            Vector3 position = GameManager.Instance.PlayerObject.transform.position;
            if ((position - lastSample).sqrMagnitude < SampleSpacing * SampleSpacing)
                return;

            if (Samples.Count >= MaxSamples)
            {
                Recording = false;
                BufferFull = true;
                return;
            }

            lastSample = position;
            Samples.Add(Capture(position));
        }

        void SampleGhost()
        {
            SeanceGhostGuide ghost = SeanceGhostGuide.Active;
            if (ghost == null)
            {
                hasGhostSample = false;
                return;
            }

            Vector3 position = ghost.transform.position;

            // Always take the first sample after a (re)spawn, so a ghost that falls immediately is caught.
            if (hasGhostSample && (position - lastGhostSample).sqrMagnitude < SampleSpacing * SampleSpacing)
                return;

            if (GhostSamples.Count >= MaxSamples)
                return;

            lastGhostSample = position;
            hasGhostSample = true;
            GhostSamples.Add(new SeanceTraceSample(position, false, false));
        }

        static SeanceTraceSample Capture(Vector3 position)
        {
            bool grounded = GameManager.Instance.PlayerMotor != null && GameManager.Instance.PlayerMotor.IsGrounded;
            bool inWater = GameManager.Instance.PlayerEnterExit != null && GameManager.Instance.PlayerEnterExit.IsPlayerSwimming;
            return new SeanceTraceSample(position, grounded, inWater);
        }
    }
}
