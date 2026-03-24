using System;
using System.Collections.Generic;

namespace Timberbot
{
    // thread-safe double buffer: writer updates Write list, swaps to Read.
    // reader only accesses Read list. zero contention.
    // add/remove updates BOTH lists so they always have the same items.
    class DoubleBuffer<T>
    {
        private List<T> _write = new List<T>();
        private List<T> _read = new List<T>();

        public List<T> Read => _read;
        public List<T> Write => _write;
        public int Count => _write.Count;

        public void Add(T item) { _write.Add(item); _read.Add(item); }
        public void Add(T writeItem, T readItem) { _write.Add(writeItem); _read.Add(readItem); }
        public void RemoveAll(Predicate<T> match) { _write.RemoveAll(match); _read.RemoveAll(match); }
        public void Clear() { _write.Clear(); _read.Clear(); }
        public void Swap() { var tmp = _read; _read = _write; _write = tmp; }
    }
}
