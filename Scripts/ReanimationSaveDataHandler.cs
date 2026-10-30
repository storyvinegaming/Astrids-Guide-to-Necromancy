// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Persists active reanimations through DFU's own per-mod save data extension point
// (Mod.SaveDataInterface / IHasModSaveData), which writes/reads a small JSON file alongside the
// main save file. This is what makes reanimated allies survive a save made, then the game fully
// closed and relaunched later - the in-memory snapshot VoxsNecromancy.cs used previously
// (SnapshotForLoad, keyed off currently-live ReanimatedLifetime components) only worked for a
// "hot" save+load within the same running session, since there's nothing live in memory to
// snapshot from immediately after a cold relaunch. GetSaveData()/RestoreSaveData() here run at
// the same point in SaveLoadManager's save/load sequence as the rest of a mod's own state:
// GetSaveData() while the world is still fully intact (ordinary save), RestoreSaveData() after
// DFU's own enemy data has already been restored but before OnLoad fires, so
// VoxsNecromancy.ReconcileAfterLoad() always has this data ready by the time it runs.
//
// Also carries Seance's own kill-count progress (VoxsNecromancy.seanceKillCount/
// seanceLastDungeonID) - a plain in-memory counter with the exact same "can't survive a cold
// relaunch on its own" problem reanimated allies had, so it rides along in this same save data
// file rather than needing a second Mod.SaveDataInterface registration (a mod can only have one).

using System;
using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using FullSerializer;
using VoxsNecromancyMod.AI;

namespace VoxsNecromancyMod
{
    [fsObject("v1")]
    public class ReanimationSaveData_v1
    {
        public ReanimationRecord_v1[] records = new ReanimationRecord_v1[0];

        // Added after v1 first shipped. FullSerializer leaves a field at its own C# default when
        // deserializing older save data that predates it - explicit int.MinValue here (rather than
        // the implicit 0 an int field would otherwise get) matches VoxsNecromancy's own "no dungeon
        // visited yet" sentinel, so an old save missing this field restores to the same fresh-session
        // state it would have had anyway, not a bogus "dungeon ID 0."
        public int seanceKillCount;
        public int seanceLastDungeonID = int.MinValue;
    }

    [fsObject("v1")]
    public struct ReanimationRecord_v1
    {
        public ulong loadID;
        public MobileTypes foeType;
        public bool allied;
        public int magnitude;
        public int mysticismLevel;
        public int intelligence;
        public int willpower;
        public int roundsRemaining;

        // Named "magickaReservation" rather than the current "MagickaUpkeep" terminology used
        // everywhere else in this mod - deliberately NOT renamed. FullSerializer matches fields by
        // name, so renaming this would silently drop the stored value for every existing save made
        // under the old reservation system (it would just deserialize to 0, not throw). The field
        // still holds the same number under either name; only the concept/vocabulary changed.
        public int magickaReservation;
    }

    public class ReanimationSaveDataHandler : IHasModSaveData
    {
        public Type SaveDataType { get { return typeof(ReanimationSaveData_v1); } }

        public object NewSaveData()
        {
            return new ReanimationSaveData_v1();
        }

        public object GetSaveData()
        {
            ReanimatedLifetime[] active = GameObject.FindObjectsOfType<ReanimatedLifetime>();
            List<ReanimationRecord_v1> records = new List<ReanimationRecord_v1>();

            for (int i = 0; i < active.Length; i++)
            {
                ReanimatedLifetime lifetime = active[i];
                DaggerfallEnemy enemy = lifetime.GetComponent<DaggerfallEnemy>();
                if (enemy == null || enemy.LoadID == 0)
                    continue;

                records.Add(new ReanimationRecord_v1
                {
                    loadID = enemy.LoadID,
                    foeType = lifetime.FoeType,
                    allied = lifetime.Allied,
                    magnitude = lifetime.Magnitude,
                    mysticismLevel = lifetime.MysticismLevel,
                    intelligence = lifetime.Intelligence,
                    willpower = lifetime.Willpower,
                    roundsRemaining = lifetime.RoundsRemaining,
                    magickaReservation = lifetime.MagickaUpkeep,
                });
            }

            // Per IHasModSaveData.GetSaveData()'s contract, null means "nothing to save" - the
            // framework deletes this mod's save-data file in that case rather than writing an empty
            // one. Only truly nothing when there's neither an active reanimation NOR any Seance
            // kill-count progress worth remembering - a nonzero kill count still needs to be saved
            // even with zero active allies.
            int seanceKillCount = VoxsNecromancy.SeanceKillCount;
            if (records.Count == 0 && seanceKillCount == 0)
                return null;

            ReanimationSaveData_v1 data = new ReanimationSaveData_v1();
            data.records = records.ToArray();
            data.seanceKillCount = seanceKillCount;
            data.seanceLastDungeonID = VoxsNecromancy.SeanceLastDungeonID;
            return data;
        }

        public void RestoreSaveData(object saveData)
        {
            ReanimationSaveData_v1 data = saveData as ReanimationSaveData_v1;
            VoxsNecromancy.OnModSaveDataRestored(data);

            if (data != null)
                VoxsNecromancy.RestoreSeanceProgress(data.seanceKillCount, data.seanceLastDungeonID);
        }
    }
}
