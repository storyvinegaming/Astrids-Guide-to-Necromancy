// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Integer grid-cell key for SeanceDungeonGrid/SeanceVoxelPathfinder, plus the shared 26-direction
// neighbor table both of those use to enumerate a cell's full 3D neighborhood. Kept in its own file
// since both consumers need it and neither owns it more than the other.
//
// Deliberately a plain struct with a hand-rolled hash, not UnityEngine.Vector3Int - a voxel grid
// packs a huge number of adjacent integer coordinates into a fairly small, dense range (a dungeon is
// rarely more than a few hundred cells on a side), and Vector3Int's own GetHashCode combines its
// three components in a way that collides far more than the classic large-prime spatial hash below
// (Teschner et al., "Optimized Spatial Hashing for Collision Detection of Deformable Objects") does
// on exactly this kind of dense, regular lattice.
//
// No move-type enum anywhere in this file (or SeanceVoxelPathfinder.cs) - this mod's own mod loader
// does a reflection scan over every field in its assembly at load time, and a NESTED enum type
// elsewhere in this mod once made that scan fail outright ("Field 'mode' is an enum type with a bad
// underlying type"), silently breaking this mod's whole effect registration. Move types are plain
// byte constants (SeanceMoveType) instead - see SeanceVoxelPathfinder.cs for where they're used.

using UnityEngine;

namespace VoxsNecromancyMod.AI
{
    public struct SeanceCell : System.IEquatable<SeanceCell>
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        public SeanceCell(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static SeanceCell FromWorld(Vector3 worldPosition, float cellSize)
        {
            return new SeanceCell(
                Mathf.FloorToInt(worldPosition.x / cellSize),
                Mathf.FloorToInt(worldPosition.y / cellSize),
                Mathf.FloorToInt(worldPosition.z / cellSize));
        }

        public Vector3 ToWorldCenter(float cellSize)
        {
            return new Vector3((X + 0.5f) * cellSize, (Y + 0.5f) * cellSize, (Z + 0.5f) * cellSize);
        }

        public SeanceCell Offset(int dx, int dy, int dz)
        {
            return new SeanceCell(X + dx, Y + dy, Z + dz);
        }

        public bool Equals(SeanceCell other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object obj)
        {
            return obj is SeanceCell && Equals((SeanceCell)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791);
            }
        }

        public override string ToString()
        {
            return string.Format("({0},{1},{2})", X, Y, Z);
        }
    }

    // Plain byte constants, not an enum - see file header comment for why.
    public static class SeanceMoveType
    {
        public const byte Walk = 0;
        public const byte Drop = 1;
        public const byte Jump = 2;
        public const byte Elevator = 3;
        public const byte Climb = 4;
        public const byte Swim = 5;
        public const byte Teleport = 6;
    }

    // A cache class rather than a struct - it's stored by reference in SeanceDungeonGrid's dictionary
    // and mutated in place as edges/wall-adjacency get lazily discovered, which is simpler as a class
    // than repeatedly re-inserting an updated struct value back into the dictionary.
    public class SeanceCellRecord
    {
        public bool IsOpen;
        public bool HasSupport;
        public bool IsWater;

        // Open at the wider CLEARANCE radius, not merely at the search radius - i.e. there is room to spare
        // here rather than just barely enough. A* pays a cost penalty for cells that are open but not roomy,
        // so it prefers corridor centres and only hugs walls where the geometry genuinely forces it.
        public bool IsRoomy;

        // Where inside this cell the ghost actually fits - NOT necessarily the cell centre. A single
        // sample at the exact centre decides a whole cubic metre, and real Daggerfall doorways do not
        // line up with a 1m lattice: confirmed live in Klarnem's Hold, where the player walks through
        // x~27.3 of a cell whose centre sample at x=27.5 lands in the door frame beside the opening.
        // Edge sweeps, support probes and path waypoints all use this instead of the centre, otherwise a
        // cell that is only open off-centre still fails its own centre-to-centre edge validation.
        public Vector3 FreePosition;

        public bool WallAdjacencyKnown;
        public bool IsWallAdjacent;

        // One bit per direction (see SeanceDirections) - "known" tracks whether that edge has been
        // probed yet at all, "passable" is only meaningful where the matching known-bit is set.
        public uint EdgeKnownMask;
        public uint EdgePassableMask;
    }

    // The 26 integer offsets surrounding a cell (every neighbor in a 3x3x3 block except the center),
    // built once in a static constructor rather than as 26 hand-typed rows - shared by the grid (edge
    // caching) and the pathfinder (neighbor generation).
    public static class SeanceDirections
    {
        public const int Count = 26;

        public static readonly int[] DX = new int[Count];
        public static readonly int[] DY = new int[Count];
        public static readonly int[] DZ = new int[Count];

        // Index of the opposite direction for each entry - used to mirror an edge result onto the
        // neighbor's own cache entry (an edge is symmetric: if A can reach B, B can reach A) without
        // computing it twice.
        public static readonly int[] Opposite = new int[Count];

        // O(1) reverse lookup from a (dx,dy,dz) offset (each in {-1,0,1}) to its direction index,
        // indexed as [dx+1, dy+1, dz+1] - avoids a 26-entry linear scan every time an edge cache bit
        // needs to be read or written. The center entry (0,0,0) is left at -1 (never a valid direction).
        public static readonly int[,,] IndexOf = new int[3, 3, 3];

        static SeanceDirections()
        {
            for (int x = 0; x < 3; x++)
                for (int y = 0; y < 3; y++)
                    for (int z = 0; z < 3; z++)
                        IndexOf[x, y, z] = -1;

            int index = 0;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0)
                            continue;

                        DX[index] = dx;
                        DY[index] = dy;
                        DZ[index] = dz;
                        IndexOf[dx + 1, dy + 1, dz + 1] = index;
                        index++;
                    }
                }
            }

            for (int i = 0; i < Count; i++)
            {
                for (int j = 0; j < Count; j++)
                {
                    if (DX[j] == -DX[i] && DY[j] == -DY[i] && DZ[j] == -DZ[i])
                    {
                        Opposite[i] = j;
                        break;
                    }
                }
            }
        }
    }
}
