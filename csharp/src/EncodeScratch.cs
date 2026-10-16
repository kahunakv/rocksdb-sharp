using System;
using System.Runtime.InteropServices;

namespace RocksDbSharp
{
    /// <summary>
    /// Memory for the encoded bytes of string arguments, held for one synchronous native call.
    /// <para>
    /// Inputs up to <see cref="MaxStackBytes"/> use a buffer that the caller allocates on its own stack, so the
    /// common case of a short key or value needs no allocation at all. Larger inputs fall back to native memory:
    /// they are rare, and encoding them costs more than the allocation does.
    /// </para>
    /// <code>
    /// byte* stack = stackalloc byte[EncodeScratch.StackBytes(length)];
    /// using (var scratch = new EncodeScratch(stack, length))
    /// {
    ///     // use scratch.Pointer for exactly length bytes, only until the using block ends
    /// }
    /// </code>
    /// </summary>
    internal unsafe struct EncodeScratch : IDisposable
    {
        /// <summary>The largest input that the caller's stack buffer holds.</summary>
        internal const int MaxStackBytes = 512;

        private IntPtr _heap;
#if DEBUG
        private readonly int _length;
#endif

        /// <summary>
        /// Gives the size of the stack buffer that the caller must allocate for <paramref name="length"/> bytes.
        /// The size is never zero, so the pointer that native code gets is never null, also for empty strings.
        /// </summary>
        internal static int StackBytes(int length) => length <= MaxStackBytes ? Math.Max(length, 1) : 1;

        internal EncodeScratch(byte* stackBuffer, int length)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

#if DEBUG
            _length = length;
#endif
            if (length <= MaxStackBytes)
            {
                _heap = IntPtr.Zero;
                Pointer = stackBuffer;
            }
            else
            {
                _heap = Marshal.AllocHGlobal(length);
                Pointer = (byte*)_heap;
            }
        }

        /// <summary>The start of the buffer. It is valid until <see cref="Dispose"/>.</summary>
        internal byte* Pointer { get; }

        public void Dispose()
        {
#if DEBUG
            // Zero the memory so that a native call that keeps the pointer after it returns shows up in tests.
            for (int i = 0; i < _length; i++)
            {
                Pointer[i] = 0;
            }
#endif
            if (_heap != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_heap);
                _heap = IntPtr.Zero;
            }
        }
    }
}
