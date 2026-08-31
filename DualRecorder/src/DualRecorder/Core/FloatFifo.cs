using System;

namespace DualRecorder.Core
{
    /// <summary>Simple growable circular buffer of float samples, with a hard cap.</summary>
    public sealed class FloatFifo
    {
        private float[] _buf;
        private int _head;
        private int _count;
        private readonly int _maxCapacity;

        public FloatFifo(int initialCapacity, int maxCapacity)
        {
            _buf = new float[Math.Max(1024, initialCapacity)];
            _maxCapacity = Math.Max(maxCapacity, _buf.Length);
        }

        public int Count => _count;

        public void Write(float[] src, int offset, int count)
        {
            if (count <= 0) return;
            EnsureCapacity(_count + count);

            // at the cap we drop the oldest samples rather than grow without bound
            if (count >= _buf.Length)
            {
                int keep = _buf.Length;
                Array.Copy(src, offset + count - keep, _buf, 0, keep);
                _head = 0;
                _count = keep;
                return;
            }
            if (_count + count > _buf.Length) Skip((_count + count) - _buf.Length);

            int tail = (_head + _count) % _buf.Length;
            int first = Math.Min(count, _buf.Length - tail);
            Array.Copy(src, offset, _buf, tail, first);
            if (first < count) Array.Copy(src, offset + first, _buf, 0, count - first);
            _count += count;
        }

        public int Read(float[] dst, int offset, int count)
        {
            int n = Math.Min(count, _count);
            if (n <= 0) return 0;
            int first = Math.Min(n, _buf.Length - _head);
            Array.Copy(_buf, _head, dst, offset, first);
            if (first < n) Array.Copy(_buf, 0, dst, offset + first, n - first);
            _head = (_head + n) % _buf.Length;
            _count -= n;
            return n;
        }

        public void Skip(int count)
        {
            int n = Math.Min(count, _count);
            _head = (_head + n) % _buf.Length;
            _count -= n;
        }

        public void Clear() { _head = 0; _count = 0; }

        private void EnsureCapacity(int needed)
        {
            if (needed <= _buf.Length) return;
            int newSize = _buf.Length;
            while (newSize < needed && newSize < _maxCapacity) newSize *= 2;
            newSize = Math.Min(newSize, _maxCapacity);
            if (newSize == _buf.Length) return;

            var next = new float[newSize];
            int copied = Read(next, 0, _count);
            _buf = next;
            _head = 0;
            _count = copied;
        }
    }
}
