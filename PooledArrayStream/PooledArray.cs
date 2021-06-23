using System;
using System.Runtime.InteropServices;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace PooledIO
{
    public ref struct PooledArray<T> /*: IMemoryOwner<T>*/
    {
        private ArrayPool<T>? _ArrayPool;

        public ArraySegment<T> ArraySegment { get; private set; }

        public Memory<T> Memory => ArraySegment;

        internal PooledArray(ArrayPool<T> arrayPool, ArraySegment<T> arraySegment)
        {
            _ArrayPool = arrayPool;
            ArraySegment = arraySegment;
        }

        public static PooledArray<T> Allocate(ArrayPool<T> arrayPool, int length)
        {
            var buffer = arrayPool.Rent(length);
            return new PooledArray<T>(arrayPool, new ArraySegment<T>(buffer, 0, length));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            if (_ArrayPool != null && ArraySegment != default)
            {
                _ArrayPool.Return(ArraySegment.Array);
                _ArrayPool = null;
                ArraySegment = default;
            }
        }

        public Span<T> GetSpan() => Memory.Span;

    }
}
