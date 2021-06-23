// This class is based on https://raw.githubusercontent.com/dotnet/corefx/release/3.1/src/Common/src/CoreLib/System/IO/MemoryStream.cs

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers;

namespace PooledIO
{
    public sealed class PooledArrayStream : Stream, IBufferWriter<byte>
    {
        private readonly ArrayPool<byte> _arrayPool;
        private byte[]? _buffer;    // Either allocated internally or externally.
        private int _position;     // read/write head.
        private int _length;       // Number of bytes within the memory stream
        private int _capacity;     // length of usable portion of buffer for stream
        // Note that _capacity == _buffer.Length for non-user-provided byte[]'s

        private Task<int>? _lastReadTask; // The last successful task returned from ReadAsync

        private const int MemStreamMaxLength = int.MaxValue;

        private const int Array_MaxByteArrayLength = 0x7FFFFFC7;

        public PooledArrayStream(int capacity = 0)
            : this(ArrayPool<byte>.Shared, capacity)
        {
        }

        public PooledArrayStream(ArrayPool<byte> arrayPool, int capacity = 0)
        {
            _capacity = capacity >= 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
            _arrayPool = arrayPool ?? throw new ArgumentNullException(nameof(arrayPool));
            _buffer = capacity != 0 ? _arrayPool.Rent(capacity) : Array.Empty<byte>();
        }

        ~PooledArrayStream()
        {
            Dispose(false);
        }

        /// <summary>Validate the arguments to CopyTo, as would Stream.CopyTo.</summary>
        private static void ValidateCopyToArgs(Stream source, Stream destination, int bufferSize)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            if (bufferSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferSize), bufferSize, SR.ArgumentOutOfRange_NeedPosNum);
            }

            bool sourceCanRead = source.CanRead;
            if (!sourceCanRead && !source.CanWrite)
            {
                throw new ObjectDisposedException(null, SR.ObjectDisposed_StreamClosed);
            }

            bool destinationCanWrite = destination.CanWrite;
            if (!destinationCanWrite && !destination.CanRead)
            {
                throw new ObjectDisposedException(nameof(destination), SR.ObjectDisposed_StreamClosed);
            }

            if (!sourceCanRead)
            {
                throw new NotSupportedException(SR.NotSupported_UnreadableStream);
            }

            if (!destinationCanWrite)
            {
                throw new NotSupportedException(SR.NotSupported_UnwritableStream);
            }
        }

        public override bool CanRead => _buffer != null;

        public override bool CanSeek => _buffer != null;

        public override bool CanWrite => _buffer != null;

        public void Clear()
        {
            ClearAndGetMemory().Dispose();
        }

        public PooledArray<byte> ClearAndGetMemory()
        {
            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            var buffer = DetachBuffer();
            var length = _length;
            _buffer = Array.Empty<byte>();
            _capacity = 0;
            _length = 0;
            _position = 0;
            _lastReadTask = null;
            return new PooledArray<byte>(_arrayPool, buffer != null ? new ArraySegment<byte>(buffer, 0, length) : default);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte[]? DetachBuffer()
        {
            var tmp = _buffer;
            _buffer = null;
            if (tmp?.Length > 0)
                return tmp;
            else
                return null;
        }

        public PooledArray<byte> DisposeAndGetMemory()
        {
            var buffer = DetachBuffer();
            var length = _length;
            _length = 0;
            _lastReadTask = null;
            base.Dispose(true);
            return new PooledArray<byte>(_arrayPool, buffer != null ? new ArraySegment<byte>(buffer, 0, length) : default);
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                DisposeAndGetMemory().Dispose();
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureWriteable()
        {
            if (!CanWrite)
                throw Error.GetWriteNotSupported();
        }

        // returns a bool saying whether we allocated a new array.
        private bool EnsureCapacity(int value)
        {
            // Check for overflow
            if (value < 0)
                throw new IOException(SR.IO_StreamTooLong);

            if (value > _capacity)
            {
                int newCapacity = Math.Max(value, 256);

                // We are ok with this overflowing since the next statement will deal
                // with the cases where _capacity*2 overflows.
                if (newCapacity < _capacity * 2)
                {
                    newCapacity = _capacity * 2;
                }

                // We want to expand the array up to Array.MaxByteArrayLength
                // And we want to give the user the value that they asked for
                if ((uint)(_capacity * 2) > Array_MaxByteArrayLength)
                {
                    newCapacity = Math.Max(value, Array_MaxByteArrayLength);
                }

                Capacity = newCapacity;
                return true;
            }
            return false;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);

            try
            {
                Flush();
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        public Memory<byte> GetDangerousMemory()
        {
            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            return _buffer.AsMemory(0, _length);
        }

        public Span<byte> GetDangerousSpan()
        {
            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            return _buffer.AsSpan(0, _length);
        }

        // PERF: Get actual length of bytes available for read; do sanity checks; shift position - i.e. everything except actual copying bytes
        internal int InternalEmulateRead(int count)
        {
            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            int n = _length - _position;
            if (n > count)
                n = count;
            if (n < 0)
                n = 0;

            Debug.Assert(_position + n >= 0, "_position + n >= 0");  // len is less than 2^31 -1.
            _position += n;
            return n;
        }

        // Gets & sets the capacity (number of bytes allocated) for this stream.
        // The capacity cannot be set to a value less than the current length
        // of the stream.
        // 
        public int Capacity
        {
            get
            {
                if (_buffer == null)
                    throw Error.GetStreamIsClosed();
                return _capacity;
            }
            set
            {
                // Only update the capacity if the MS is expandable and the value is different than the current capacity.
                // Special behavior if the MS isn't expandable: we don't throw if value is the same as the current capacity
                if (value < Length)
                    throw new ArgumentOutOfRangeException(nameof(value));

                if (_buffer == null)
                    throw Error.GetStreamIsClosed();

                // MemoryStream has this invariant: _origin > 0 => !expandable (see ctors)
                if (value != _capacity && _buffer.Length < value)
                {
                    if (value > 0)
                    {
                        var newBuffer = _arrayPool.Rent(value);
                        var oldBuffer = _buffer;
                        try
                        {
                            if (_length > 0)
                            {
                                Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _length);
                            }
                            _buffer = newBuffer;
                        }
                        finally
                        {
                            if (oldBuffer?.Length > 0)
                                _arrayPool.Return(oldBuffer);
                        }
                    }
                    else
                    {
                        _buffer = Array.Empty<byte>();
                    }
                    _capacity = value;
                }
            }
        }

        public override long Length
        {
            get
            {
                if (_buffer == null)
                    throw Error.GetStreamIsClosed();
                return _length;
            }
        }

        public override long Position
        {
            get
            {
                if (_buffer == null)
                    throw Error.GetStreamIsClosed();
                return _position;
            }
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value));

                if (_buffer == null)
                    throw Error.GetStreamIsClosed();

                if (value > MemStreamMaxLength)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _position = (int)value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (buffer.Length - offset < count)
                throw new ArgumentException(SR.Argument_InvalidOffLen);

            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            int n = _length - _position;
            if (n > count)
                n = count;
            if (n <= 0)
                return 0;

            Debug.Assert(_position + n >= 0, "_position + n >= 0");  // len is less than 2^31 -1.

            if (n <= 8)
            {
                int byteCount = n;
                while (--byteCount >= 0)
                    buffer[offset + byteCount] = _buffer[_position + byteCount];
            }
            else
                Buffer.BlockCopy(_buffer, _position, buffer, offset, n);
            _position += n;

            return n;
        }

        public
#if NETSTANDARD2_1_OR_GREATER
            override
#endif
             int Read(Span<byte> buffer)
        {
            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            int n = Math.Min(_length - _position, buffer.Length);
            if (n <= 0)
                return 0;


            // TODO https://github.com/dotnet/coreclr/issues/15076:
            // Read(byte[], int, int) has an n <= 8 optimization, presumably based
            // on benchmarking.  Determine if/where such a cut-off is here and add
            // an equivalent optimization if necessary.
            new Span<byte>(_buffer, _position, n).CopyTo(buffer);

            _position += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (buffer.Length - offset < count)
                throw new ArgumentException(SR.Argument_InvalidOffLen);

            // If cancellation was requested, bail early
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<int>(cancellationToken);

            try
            {
                int n = Read(buffer, offset, count);
                var t = _lastReadTask;
                Debug.Assert(t == null || t.Status == TaskStatus.RanToCompletion,
                    "Expected that a stored last task completed successfully");
                return (t != null && t.Result == n) ? t : (_lastReadTask = Task.FromResult<int>(n));
            }
            catch (OperationCanceledException oce)
            {
                return Task.FromCanceled<int>(oce.CancellationToken);
            }
            catch (Exception exception)
            {
                return Task.FromException<int>(exception);
            }
        }

        public
#if NETSTANDARD2_1_OR_GREATER
            override 
#endif
            ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ValueTask<int>(Task.FromCanceled<int>(cancellationToken));
            }

            try
            {
                // ReadAsync(Memory<byte>,...) needs to delegate to an existing virtual to do the work, in case an existing derived type
                // has changed or augmented the logic associated with reads.  If the Memory wraps an array, we could delegate to
                // ReadAsync(byte[], ...), but that would defeat part of the purpose, as ReadAsync(byte[], ...) often needs to allocate
                // a Task<int> for the return value, so we want to delegate to one of the synchronous methods.  We could always
                // delegate to the Read(Span<byte>) method, and that's the most efficient solution when dealing with a concrete
                // MemoryStream, but if we're dealing with a type derived from MemoryStream, Read(Span<byte>) will end up delegating
                // to Read(byte[], ...), which requires it to get a byte[] from ArrayPool and copy the data.  So, we special-case the
                // very common case of the Memory<byte> wrapping an array: if it does, we delegate to Read(byte[], ...) with it,
                // as that will be efficient in both cases, and we fall back to Read(Span<byte>) if the Memory<byte> wrapped something
                // else; if this is a concrete MemoryStream, that'll be efficient, and only in the case where the Memory<byte> wrapped
                // something other than an array and this is a MemoryStream-derived type that doesn't override Read(Span<byte>) will
                // it then fall back to doing the ArrayPool/copy behavior.
                return new ValueTask<int>(
                    MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> destinationArray) ?
                        Read(destinationArray.Array!, destinationArray.Offset, destinationArray.Count) :
                        Read(buffer.Span));
            }
            catch (OperationCanceledException oce)
            {
                return new ValueTask<int>(Task.FromCanceled<int>(oce.CancellationToken));
            }
            catch (Exception exception)
            {
                return new ValueTask<int>(Task.FromException<int>(exception));
            }
        }

        public override int ReadByte()
        {
            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            if (_position >= _length)
                return -1;

            return _buffer[_position++];
        }

        public
#if NETSTANDARD2_1_OR_GREATER
            override
#else
            new
#endif
            void CopyTo(Stream destination, int bufferSize)
        {
            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            // Since we did not originally override this method, validate the arguments
            // the same way Stream does for back-compat.
            ValidateCopyToArgs(this, destination, bufferSize);

            int originalPosition = _position;

            // Seek to the end of the MemoryStream.
            int remaining = InternalEmulateRead(_length - originalPosition);

            // If we were already at or past the end, there's no copying to do so just quit.
            if (remaining > 0)
            {
                // Call Write() on the other Stream, using our internal buffer and avoiding any
                // intermediary allocations.
                destination.Write(_buffer, originalPosition, remaining);
            }
        }

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            // This implementation offers better performance compared to the base class version.

            ValidateCopyToArgs(this, destination, bufferSize);

            // If cancelled - return fast:
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);

            // Avoid copying data from this buffer into a temp buffer:
            //   (require that InternalEmulateRead does not throw,
            //    otherwise it needs to be wrapped into try-catch-Task.FromException like memStrDest.Write below)

            int pos = _position;
            int n = InternalEmulateRead(_length - _position);

            // If we were already at or past the end, there's no copying to do so just quit.
            if (n == 0)
                return Task.CompletedTask;

            // If destination is not a memory stream, write there asynchronously:
            if (destination is not PooledArrayStream memStrDest)
                return destination.WriteAsync(_buffer, pos, n, cancellationToken);

            try
            {
                // If destination is a MemoryStream, CopyTo synchronously:
                memStrDest.Write(_buffer, pos, n);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }


        public override long Seek(long offset, SeekOrigin loc)
        {
            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            if (offset > MemStreamMaxLength)
                throw new ArgumentOutOfRangeException(nameof(offset));

            switch (loc)
            {
                case SeekOrigin.Begin:
                    {
                        int tempPosition = unchecked((int)offset);
                        _position = tempPosition;
                        break;
                    }
                case SeekOrigin.Current:
                    {
                        int tempPosition = unchecked(_position + (int)offset);
                        _position = tempPosition;
                        break;
                    }
                case SeekOrigin.End:
                    {
                        int tempPosition = unchecked(_length + (int)offset);
                        _position = tempPosition;
                        break;
                    }
                default:
                    throw new ArgumentException(SR.Argument_InvalidSeekOrigin);
            }

            Debug.Assert(_position >= 0, "_position >= 0");
            return _position;
        }

        // Sets the length of the stream to a given value.  The new
        // value must be nonnegative and less than the space remaining in
        // the array, int.MaxValue - origin
        // Origin is 0 in all cases other than a MemoryStream created on
        // top of an existing array and a specific starting offset was passed 
        // into the MemoryStream constructor.  The upper bounds prevents any 
        // situations where a stream may be created on top of an array then 
        // the stream is made longer than the maximum possible length of the 
        // array (int.MaxValue).
        // 
        public override void SetLength(long value)
        {
            if (value < 0 || value > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value));

            EnsureWriteable();

            // Origin wasn't publicly exposed above.
            Debug.Assert(MemStreamMaxLength == int.MaxValue);  // Check parameter validation logic in this method if this fails.
            if (value > (int.MaxValue))
                throw new ArgumentOutOfRangeException(nameof(value));

            int newLength = (int)value;
            bool allocatedNewArray = EnsureCapacity(newLength);
            if (!allocatedNewArray && newLength > _length)
                Array.Clear(_buffer, _length, newLength - _length);
            _length = newLength;
            if (_position > newLength)
                _position = newLength;
        }

        public byte[] ToArray()
        {
            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            int count = _length;
            if (count == 0)
                return Array.Empty<byte>();
            byte[] copy = new byte[count];
            Buffer.BlockCopy(_buffer, 0, copy, 0, count);
            return copy;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (buffer.Length - offset < count)
                throw new ArgumentException(SR.Argument_InvalidOffLen);

            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            EnsureWriteable();

            int i = _position + count;
            // Check for overflow
            if (i < 0)
                throw new IOException(SR.IO_StreamTooLong);

            if (i > _length)
            {
                bool mustZero = _position > _length;
                if (i > _capacity)
                {
                    bool allocatedNewArray = EnsureCapacity(i);
                    if (allocatedNewArray)
                    {
                        mustZero = false;
                    }
                }
                if (mustZero)
                {
                    Array.Clear(_buffer, _length, i - _length);
                }
                _length = i;
            }

            if ((count <= 8) && (buffer != _buffer))
            {
                int byteCount = count;
                while (--byteCount >= 0)
                {
                    _buffer[_position + byteCount] = buffer[offset + byteCount];
                }
            }
            else
            {
                Buffer.BlockCopy(buffer, offset, _buffer, _position, count);
            }
            _position = i;
        }

        public
#if NETSTANDARD2_1_OR_GREATER
            override
#endif
            void Write(ReadOnlySpan<byte> buffer)
        {
            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            EnsureWriteable();

            // Check for overflow
            int i = _position + buffer.Length;
            if (i < 0)
                throw new IOException(SR.IO_StreamTooLong);

            if (i > _length)
            {
                bool mustZero = _position > _length;
                if (i > _capacity)
                {
                    bool allocatedNewArray = EnsureCapacity(i);
                    if (allocatedNewArray)
                    {
                        mustZero = false;
                    }
                }
                if (mustZero)
                {
                    Array.Clear(_buffer, _length, i - _length);
                }
                _length = i;
            }

            buffer.CopyTo(new Span<byte>(_buffer, _position, buffer.Length));
            _position = i;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (buffer.Length - offset < count)
                throw new ArgumentException(SR.Argument_InvalidOffLen);

            // If cancellation is already requested, bail early
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);

            try
            {
                Write(buffer, offset, count);
                return Task.CompletedTask;
            }
            catch (OperationCanceledException oce)
            {
                return Task.FromCanceled(oce.CancellationToken);
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }

        public
#if NETSTANDARD2_1_OR_GREATER
            override 
#endif
            ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ValueTask(Task.FromCanceled(cancellationToken));
            }

            try
            {
                // See corresponding comment in ReadAsync for why we don't just always use Write(ReadOnlySpan<byte>).
                // Unlike ReadAsync, we could delegate to WriteAsync(byte[], ...) here, but we don't for consistency.
                if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> sourceArray))
                {
                    Write(sourceArray.Array!, sourceArray.Offset, sourceArray.Count);
                }
                else
                {
                    Write(buffer.Span);
                }
                return default;
            }
            catch (OperationCanceledException oce)
            {
                return new ValueTask(Task.FromCanceled(oce.CancellationToken));
            }
            catch (Exception exception)
            {
                return new ValueTask(Task.FromException(exception));
            }
        }

        public override void WriteByte(byte value)
        {
            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            EnsureWriteable();

            if (_position >= _length)
            {
                int newLength = _position + 1;
                bool mustZero = _position > _length;
                if (newLength >= _capacity)
                {
                    bool allocatedNewArray = EnsureCapacity(newLength);
                    if (allocatedNewArray)
                    {
                        mustZero = false;
                    }
                }
                if (mustZero)
                {
                    Array.Clear(_buffer, _length, _position - _length);
                }
                _length = newLength;
            }
            _buffer[_position++] = value;
        }

        // Writes this MemoryStream to another stream.
        public void WriteTo(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            stream.Write(_buffer, 0, _length);
        }

        void IBufferWriter<byte>.Advance(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            Seek(count, SeekOrigin.Current);
        }

        Memory<byte> IBufferWriter<byte>.GetMemory(int sizeHint)
        {
            if (sizeHint < 0)
                throw new ArgumentOutOfRangeException(nameof(sizeHint));

            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            EnsureCapacity(_length + sizeHint);
            return _buffer.AsMemory(_length, sizeHint);
        }

        Span<byte> IBufferWriter<byte>.GetSpan(int sizeHint)
        {
            if (sizeHint < 0)
                throw new ArgumentOutOfRangeException(nameof(sizeHint));

            if (_buffer == null)
                throw Error.GetStreamIsClosed();

            EnsureCapacity(_length + sizeHint);
            return _buffer.AsSpan(_length, sizeHint);
        }

    }
}
