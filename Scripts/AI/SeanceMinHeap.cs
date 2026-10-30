// Project:         Vox's Necromancy (Daggerfall Unity mod)
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Author:          Vox
//
// Plain binary min-heap over (int nodeIndex, float key), used by SeanceVoxelPathfinder as its A* open
// set. Not System.Collections.Generic.PriorityQueue<T> - that type is .NET 6+ only, and this project
// targets an older C# 6 baseline (confirmed via existing mod code avoiding tuples/pattern
// matching/local functions throughout), so it isn't available here regardless of API surface.
//
// Uses lazy deletion rather than a real decrease-key: when a node's cost improves, the pathfinder just
// pushes it again with the better key instead of mutating an existing heap entry. A stale, worse-key
// copy of an already-finalized node may still be sitting in the heap when it's popped later - the
// pathfinder is expected to check the node's current best-known cost against the popped key and skip
// the pop outright if they no longer match. This trades a few extra (cheap) heap entries for a much
// simpler heap implementation with no index-lookup table to keep in sync.

using System;

namespace VoxsNecromancyMod.AI
{
    public class SeanceMinHeap
    {
        int[] nodeIndices;
        float[] keys;
        int count;

        public SeanceMinHeap(int initialCapacity)
        {
            int capacity = initialCapacity > 16 ? initialCapacity : 16;
            nodeIndices = new int[capacity];
            keys = new float[capacity];
            count = 0;
        }

        public int Count
        {
            get { return count; }
        }

        public void Clear()
        {
            count = 0;
        }

        public void Push(int nodeIndex, float key)
        {
            if (count == nodeIndices.Length)
                Grow();

            int i = count;
            count++;
            nodeIndices[i] = nodeIndex;
            keys[i] = key;
            SiftUp(i);
        }

        public int Pop(out float key)
        {
            int result = nodeIndices[0];
            key = keys[0];

            count--;
            nodeIndices[0] = nodeIndices[count];
            keys[0] = keys[count];
            SiftDown(0);

            return result;
        }

        void Grow()
        {
            int newCapacity = nodeIndices.Length * 2;
            Array.Resize(ref nodeIndices, newCapacity);
            Array.Resize(ref keys, newCapacity);
        }

        void SiftUp(int i)
        {
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (keys[parent] <= keys[i])
                    break;

                Swap(i, parent);
                i = parent;
            }
        }

        void SiftDown(int i)
        {
            while (true)
            {
                int left = i * 2 + 1;
                int right = i * 2 + 2;
                int smallest = i;

                if (left < count && keys[left] < keys[smallest])
                    smallest = left;
                if (right < count && keys[right] < keys[smallest])
                    smallest = right;

                if (smallest == i)
                    break;

                Swap(i, smallest);
                i = smallest;
            }
        }

        void Swap(int a, int b)
        {
            int tempIndex = nodeIndices[a];
            nodeIndices[a] = nodeIndices[b];
            nodeIndices[b] = tempIndex;

            float tempKey = keys[a];
            keys[a] = keys[b];
            keys[b] = tempKey;
        }
    }
}
