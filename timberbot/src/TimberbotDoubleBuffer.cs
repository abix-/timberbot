// DoubleBuffer.cs -- Lock-free thread safety for game state.
//
// Problem: HTTP GET requests run on a background thread, but Unity game objects
// can only be read on the main thread. We can't lock the main thread (blocks game)
// and we can't read game objects on the background thread (crashes).
//
// Solution: two lists of the same data. The main thread writes to one (Write),
// then swaps it to become the Read list. The background thread only reads from Read.
// No locks, no contention, no copying.
//
// How it works:
//   Main thread:  for each entity, update fields in Write list -> call Swap()
//   HTTP thread:  iterate Read list (safe, never structurally modified during read)
//
// Structural changes (Add/Remove) are deferred via ConcurrentQueue and applied
// during Swap(). This prevents "Collection was modified during enumeration" when
// EventBus fires entity create/delete while the background thread is iterating.
// Same pattern as Unity ECS Entity Command Buffers.
//
// The two-arg Add(writeItem, readItem) lets you put different initial values
// in each buffer (e.g. separate Dictionary instances to avoid shared references).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Timberbot
{
    public class TimberbotDoubleBuffer<T>
    {
        private List<T> _write = new List<T>();  // main thread writes here
        private List<T> _read = new List<T>();   // background thread reads here

        // structural changes queued from EventBus, applied at Swap() time
        private readonly ConcurrentQueue<(T write, T read)> _pendingAdds = new ConcurrentQueue<(T, T)>();
        private readonly ConcurrentQueue<Predicate<T>> _pendingRemoves = new ConcurrentQueue<Predicate<T>>();

        public List<T> Read => _read;     // safe to iterate from any thread
        public List<T> Write => _write;   // only touch from main thread
        public int Count => _write.Count;

        // queue add -- applied at next Swap()
        public void Add(T item) { _pendingAdds.Enqueue((item, item)); }
        public void Add(T writeItem, T readItem) { _pendingAdds.Enqueue((writeItem, readItem)); }

        // queue remove -- applied at next Swap()
        public void RemoveAll(Predicate<T> match) { _pendingRemoves.Enqueue(match); }
        public void Clear() { _write.Clear(); _read.Clear(); }

        // apply pending structural changes, then swap read/write refs.
        // called from main thread only (RefreshCachedState).
        public void Swap()
        {
            while (_pendingRemoves.TryDequeue(out var pred))
            {
                _write.RemoveAll(pred);
                _read.RemoveAll(pred);
            }
            while (_pendingAdds.TryDequeue(out var pair))
            {
                _write.Add(pair.write);
                _read.Add(pair.read);
            }
            var tmp = _read; _read = _write; _write = tmp;
        }
    }
}
