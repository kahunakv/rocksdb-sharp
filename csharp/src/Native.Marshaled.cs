/*
    The functions in this file provide some wrappers around the lowest level C API to aid in marshalling.
    This is kept separate so that the lowest level imports can be kept as close as possible to c.h from rocksdb.
    See Native.Raw.cs for more information.
*/
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Transitional;

#pragma warning disable IDE1006 // Intentionally violating naming conventions because this is meant to match the C API
namespace RocksDbSharp
{
    public abstract partial class Native
    {
        public static byte MarshalBool(bool input) => input ? (byte)1 : (byte)0;
        public static unsafe string MarshalNullTermAsciiStr(IntPtr nullTermStr)
        {
            if (nullTermStr == IntPtr.Zero) return null;

            byte* bv = (byte*)nullTermStr.ToPointer();
            byte* n = bv;
            while (*n != 0)
            {
                n++;
            }

            var vlength = n - bv;
            fixed (char* v = new char[vlength])
            {
                Encoding.ASCII.GetChars(bv, (int)vlength, v, (int)vlength);
                Native.Instance.rocksdb_free(nullTermStr);
                return new string(v, 0, (int)vlength);
            }
        }

        public string[] rocksdb_list_column_families(
            /* const rocksdb_options_t* */ IntPtr options,
            string name
            )
        {
            var result = rocksdb_list_column_families(options, name, out UIntPtr lencf, out IntPtr errptr);
            if (errptr != IntPtr.Zero)
            {
                throw new RocksDbException(errptr);
            }

            IntPtr[] ptrs = new IntPtr[(ulong)lencf];
            Marshal.Copy(result, ptrs, 0, (int)lencf);
            string[] strings = new string[(ulong)lencf];
            for (ulong i = 0; i < (ulong)lencf; i++)
            {
                strings[i] = Marshal.PtrToStringAnsi(ptrs[i]);
            }

            rocksdb_list_column_families_destroy(result, lencf);
            return strings;
        }

        public void rocksdb_put(
            /*rocksdb_t**/ IntPtr db,
            /*const rocksdb_writeoptions_t**/ IntPtr writeOptions,
            string key,
            string val,
            out IntPtr errptr,
            ColumnFamilyHandle cf = null,
            Encoding encoding = null)
        {
            unsafe
            {
                if (encoding is null)
                {
                    encoding = Encoding.UTF8;
                }

                fixed (char* k = key, v = val)
                {
                    int klength = key.Length;
                    int vlength = val.Length;
                    int bklength = encoding.GetByteCount(k, klength);
                    int bvlength = encoding.GetByteCount(v, vlength);
                    int bufferLength = checked(bklength + bvlength);
                    byte* stack = stackalloc byte[EncodeScratch.StackBytes(bufferLength)];
                    using var scratch = new EncodeScratch(stack, bufferLength);
                    byte* bk = scratch.Pointer;
                    encoding.GetBytes(k, klength, bk, bklength);
                    byte* bv = bk + bklength;
                    encoding.GetBytes(v, vlength, bv, bvlength);
                    UIntPtr sklength = (UIntPtr)bklength;
                    UIntPtr svlength = (UIntPtr)bvlength;

                    if (cf is null)
                    {
                        rocksdb_put(db, writeOptions, bk, sklength, bv, svlength, out errptr);
                    }
                    else
                    {
                        rocksdb_put_cf(db, writeOptions, cf.Handle, bk, sklength, bv, svlength, out errptr);
                    }
                }
            }
        }

        public void rocksdb_merge(
            /*rocksdb_t**/ IntPtr db,
            /*const rocksdb_writeoptions_t**/ IntPtr writeOptions,
            string key,
            string val,
            out IntPtr errptr,
            ColumnFamilyHandle cf = null,
            Encoding encoding = null)
        {
            unsafe
            {
                if (encoding is null)
                {
                    encoding = Encoding.UTF8;
                }

                fixed (char* k = key, v = val)
                {
                    int klength = key.Length;
                    int vlength = val.Length;
                    int bklength = encoding.GetByteCount(k, klength);
                    int bvlength = encoding.GetByteCount(v, vlength);
                    int bufferLength = checked(bklength + bvlength);
                    byte* stack = stackalloc byte[EncodeScratch.StackBytes(bufferLength)];
                    using var scratch = new EncodeScratch(stack, bufferLength);
                    byte* bk = scratch.Pointer;
                    encoding.GetBytes(k, klength, bk, bklength);
                    byte* bv = bk + bklength;
                    encoding.GetBytes(v, vlength, bv, bvlength);
                    UIntPtr sklength = (UIntPtr)bklength;
                    UIntPtr svlength = (UIntPtr)bvlength;

                    if (cf is null)
                    {
                        rocksdb_merge(db, writeOptions, bk, sklength, bv, svlength, out errptr);
                    }
                    else
                    {
                        rocksdb_merge_cf(db, writeOptions, cf.Handle, bk, sklength, bv, svlength, out errptr);
                    }
                }
            }
        }

        public string rocksdb_get(
            /*rocksdb_t**/ IntPtr db,
            /*const rocksdb_readoptions_t**/ IntPtr read_options,
            string key,
            out IntPtr errptr,
            ColumnFamilyHandle cf = null,
            Encoding encoding = null)
        {
            if (encoding is null)
            {
                encoding = Encoding.UTF8;
            }

            unsafe
            {
                fixed (char* k = key)
                {
                    int klength = key.Length;
                    int bklength = encoding.GetByteCount(k, klength);
                    byte* stack = stackalloc byte[EncodeScratch.StackBytes(bklength)];
                    using var scratch = new EncodeScratch(stack, bklength);
                    byte* bk = scratch.Pointer;
                    encoding.GetBytes(k, klength, bk, bklength);

                    IntPtr pinned = GetPinned(db, read_options, bk, (UIntPtr)bklength, cf, out errptr);
                    if (pinned == IntPtr.Zero)
                    {
                        return null;
                    }

                    return MarshalAndDestroyPinnedString(pinned, encoding);
                }
            }
        }

        /// <summary>
        /// Looks up a key with rocksdb_get_pinned. Unlike rocksdb_get, the value is not copied into a malloc()ed
        /// buffer: the pinnable slice can point straight into the block cache. A result that is not zero must be
        /// released with rocksdb_pinnableslice_destroy. Zero means not found, or an error in errptr.
        /// </summary>
        internal unsafe IntPtr GetPinned(IntPtr db, IntPtr readOptions, byte* key, UIntPtr keyLength, ColumnFamilyHandle cf, out IntPtr errptr)
        {
            return cf is null
                ? rocksdb_get_pinned(db, readOptions, key, keyLength, out errptr)
                : rocksdb_get_pinned_cf(db, readOptions, cf.Handle, key, keyLength, out errptr);
        }

        /// <inheritdoc cref="GetPinned(IntPtr, IntPtr, byte*, UIntPtr, ColumnFamilyHandle, out IntPtr)"/>
        internal IntPtr GetPinned(IntPtr db, IntPtr readOptions, byte[] key, UIntPtr keyLength, ColumnFamilyHandle cf, out IntPtr errptr)
        {
            return cf is null
                ? rocksdb_get_pinned(db, readOptions, key, keyLength, out errptr)
                : rocksdb_get_pinned_cf(db, readOptions, cf.Handle, key, keyLength, out errptr);
        }

        private unsafe string MarshalAndDestroyPinnedString(IntPtr pinned, Encoding encoding)
        {
            try
            {
                IntPtr data = rocksdb_pinnableslice_value(pinned, out UIntPtr length);
                int valueLength = checked((int)(ulong)length);
                return valueLength == 0
                    ? string.Empty
                    : CurrentFramework.CreateString((sbyte*)data, 0, valueLength, encoding);
            }
            finally
            {
                rocksdb_pinnableslice_destroy(pinned);
            }
        }

        private byte[] MarshalAndDestroyPinnedBytes(IntPtr pinned)
        {
            try
            {
                IntPtr data = rocksdb_pinnableslice_value(pinned, out UIntPtr length);
                var result = new byte[checked((int)(ulong)length)];
                if (result.Length > 0)
                {
                    Marshal.Copy(data, result, 0, result.Length);
                }

                return result;
            }
            finally
            {
                rocksdb_pinnableslice_destroy(pinned);
            }
        }

        /// <summary>
        /// Copies the value of <paramref name="key"/> into <paramref name="buffer"/>, up to <paramref name="length"/>
        /// bytes. Returns the full length of the value, or -1 when the key does not exist.
        /// </summary>
        internal long rocksdb_get_into(
            IntPtr db,
            IntPtr read_options,
            byte[] key,
            long keyLength,
            byte[] buffer,
            long offset,
            long length,
            ColumnFamilyHandle cf)
        {
            IntPtr pinned = GetPinned(db, read_options, key, (UIntPtr)keyLength, cf, out IntPtr errptr);
            if (errptr != IntPtr.Zero)
            {
                ThrowRocksDbException(errptr);
            }

            if (pinned == IntPtr.Zero)
            {
                return -1;
            }

            try
            {
                IntPtr data = rocksdb_pinnableslice_value(pinned, out UIntPtr valueLength);
                long fullLength = (long)(ulong)valueLength;
                long copyLength = Math.Min(length, fullLength);
                if (copyLength > 0)
                {
                    Marshal.Copy(data, buffer, checked((int)offset), checked((int)copyLength));
                }

                return fullLength;
            }
            finally
            {
                rocksdb_pinnableslice_destroy(pinned);
            }
        }

        private unsafe string MarshalAndFreeRocksDbString(IntPtr resultPtr, long resultLength, Encoding encoding)
        {
            var result = CurrentFramework.CreateString((sbyte*)resultPtr.ToPointer(), 0, (int)resultLength, encoding);
            rocksdb_free(resultPtr);
            return result;
        }
        private unsafe string MarshalString(IntPtr resultPtr, long resultLength, Encoding encoding)
        {
            return CurrentFramework.CreateString((sbyte*)resultPtr.ToPointer(), 0, (int)resultLength, encoding);
        }

        public bool rocksdb_has_key(
            IntPtr db,
            IntPtr read_options,
            byte[] key,
            long keyLength,
            out IntPtr errptr,
            ColumnFamilyHandle cf = null)
        {
            // The pinned read finds the value without copying it; only its existence is needed.
            IntPtr pinned = GetPinned(db, read_options, key, (UIntPtr)keyLength, cf, out errptr);
            if (pinned == IntPtr.Zero)
            {
                return false;
            }

            rocksdb_pinnableslice_destroy(pinned);
            return true;
        }

        public byte[] rocksdb_get(
            IntPtr db,
            IntPtr read_options,
            byte[] key,
            long keyLength,
            out IntPtr errptr,
            ColumnFamilyHandle cf = null)
        {
            IntPtr pinned = GetPinned(db, read_options, key, (UIntPtr)keyLength, cf, out errptr);
            if (pinned == IntPtr.Zero)
            {
                return null;
            }

            return MarshalAndDestroyPinnedBytes(pinned);
        }

#if !NETSTANDARD2_0
        public unsafe byte[] rocksdb_get(
            IntPtr db,
            IntPtr read_options,
            ReadOnlySpan<byte> key,
            out IntPtr errptr,
            ColumnFamilyHandle cf = null)
        {
            IntPtr pinned;
            fixed (byte* ptr = &MemoryMarshal.GetReference(key))
            {
                pinned = GetPinned(db, read_options, ptr, (UIntPtr)key.Length, cf, out errptr);
            }

            if (pinned == IntPtr.Zero)
            {
                return null;
            }

            return MarshalAndDestroyPinnedBytes(pinned);
        }

        public unsafe bool rocksdb_get(
            IntPtr db,
            IntPtr read_options,
            ReadOnlySpan<byte> key,
            Span<byte> knownFixedSizeValue,
            out IntPtr errptr,
            ColumnFamilyHandle cf = null)
        {
            IntPtr pinned;
            fixed (byte* ptr = &MemoryMarshal.GetReference(key))
            {
                pinned = GetPinned(db, read_options, ptr, (UIntPtr)key.Length, cf, out errptr);
            }

            if (pinned == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                IntPtr data = rocksdb_pinnableslice_value(pinned, out UIntPtr valueLength);
                if ((ulong)valueLength != (ulong)knownFixedSizeValue.Length)
                {
                    ThrowLengthMismatch(checked((int)(ulong)valueLength), knownFixedSizeValue.Length);
                }

                new ReadOnlySpan<byte>((void*)data, knownFixedSizeValue.Length).CopyTo(knownFixedSizeValue);
                return true;
            }
            finally
            {
                rocksdb_pinnableslice_destroy(pinned);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowLengthMismatch(int readLength, int expectedLength)
        {
            throw new Exception($"Value length mismatch, expected {expectedLength}, found {readLength}");
        }

        public unsafe bool rocksdb_has_key(
            IntPtr db,
            IntPtr read_options,
            ReadOnlySpan<byte> key,
            out IntPtr errptr,
            ColumnFamilyHandle cf = null)
        {
            // The pinned read finds the value without copying it; only its existence is needed.
            IntPtr pinned;
            fixed (byte* ptr = &MemoryMarshal.GetReference(key))
            {
                pinned = GetPinned(db, read_options, ptr, (UIntPtr)key.Length, cf, out errptr);
            }

            if (pinned == IntPtr.Zero)
            {
                return false;
            }

            rocksdb_pinnableslice_destroy(pinned);
            return true;
        }

        public unsafe T rocksdb_get<T>(
            IntPtr db,
            IntPtr read_options,
            ReadOnlySpan<byte> key,
            ISpanDeserializer<T> deserializer,
            out IntPtr errptr,
            ColumnFamilyHandle cf = null)
        {
            IntPtr pinned;
            fixed (byte* ptr = &MemoryMarshal.GetReference(key))
            {
                pinned = GetPinned(db, read_options, ptr, (UIntPtr)key.Length, cf, out errptr);
            }

            if (pinned == IntPtr.Zero)
            {
                return default(T);
            }

            try
            {
                // The span points into memory that the pinnable slice owns, so it must not escape this call.
                IntPtr data = rocksdb_pinnableslice_value(pinned, out UIntPtr valueLength);
                return deserializer.Deserialize(new ReadOnlySpan<byte>((void*)data, checked((int)(ulong)valueLength)));
            }
            finally
            {
                rocksdb_pinnableslice_destroy(pinned);
            }
        }

        public unsafe T rocksdb_get<T>(
            IntPtr db,
            IntPtr read_options,
            ReadOnlySpan<byte> key,
            Func<Stream, T> deserializer,
            out IntPtr errptr,
            ColumnFamilyHandle cf = null)
        {
            IntPtr pinned;
            fixed (byte* ptr = &MemoryMarshal.GetReference(key))
            {
                pinned = GetPinned(db, read_options, ptr, (UIntPtr)key.Length, cf, out errptr);
            }

            if (pinned == IntPtr.Zero)
            {
                return default(T);
            }

            try
            {
                // The stream reads memory that the pinnable slice owns, so it must not escape this call.
                IntPtr data = rocksdb_pinnableslice_value(pinned, out UIntPtr valueLength);
                using var stream = new UnmanagedMemoryStream((byte*)data, (long)(ulong)valueLength);
                return deserializer(stream);
            }
            finally
            {
                rocksdb_pinnableslice_destroy(pinned);
            }
        }
#endif

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ThrowRocksDbException(IntPtr intPtr)
        {
            throw new RocksDbException(intPtr);
        }


        /// <summary>
        /// Executes a multi_get with automatic marshalling
        /// </summary>
        /// <param name="db"></param>
        /// <param name="read_options"></param>
        /// <param name="keys"></param>
        /// <param name="numKeys">when non-zero, specifies the number of keys in the array to fetch</param>
        /// <param name="keyLengths">when non-null specifies the lengths of each key to fetch</param>
        /// <param name="errptrs">when non-null, must be an array that will be populated with error codes</param>
        /// <param name="values">when non-null is a pre-allocated array to put the resulting values in</param>
        /// <param name="cf"></param>
        /// <returns></returns>
        public unsafe KeyValuePair<byte[], byte[]>[] rocksdb_multi_get(
            IntPtr db,
            IntPtr read_options,
            byte[][] keys,
            IntPtr[] errptrs,
            ulong numKeys = 0,
            ulong[] keyLengths = null,
            KeyValuePair<byte[], byte[]>[] values = null,
            ColumnFamilyHandle[] cf = null)
        {
            int count = numKeys == 0 ? keys.Length : checked((int)numKeys);

            if (values is null)
            {
                values = new KeyValuePair<byte[], byte[]>[count];
            }

            if (errptrs is null)
            {
                errptrs = new IntPtr[count];
            }

            var bytePool = ArrayPool<byte>.Shared;
            var intPtrPool = ArrayPool<IntPtr>.Shared;
            var uintPtrPool = ArrayPool<UIntPtr>.Shared;

            // The keys are copied into one buffer, so that one pin covers all of them. Pinning each key array on
            // its own costs a GC handle per key, which is more than the copy of a typical key.
            UIntPtr[] keyLengthsConverted = uintPtrPool.Rent(count);
            byte[] packedKeys = null;
            IntPtr[] keyPtrs = intPtrPool.Rent(count);
            IntPtr[] valuePtrs = intPtrPool.Rent(count);
            UIntPtr[] valueLengths = uintPtrPool.Rent(count);
            IntPtr[] cfhs = null;

            // A rented array can hold stale pointers from an earlier caller; clear it so that the finally block
            // frees only what this call gets back.
            Array.Clear(valuePtrs, 0, count);

            try
            {
                int totalLength = 0;
                for (int i = 0; i < count; i++)
                {
                    int keyLength = keyLengths is null ? keys[i].Length : checked((int)keyLengths[i]);
                    keyLengthsConverted[i] = (UIntPtr)(uint)keyLength;
                    totalLength = checked(totalLength + keyLength);
                }

                packedKeys = bytePool.Rent(Math.Max(totalLength, 1));
                int offset = 0;
                for (int i = 0; i < count; i++)
                {
                    int keyLength = (int)(uint)keyLengthsConverted[i];
                    Buffer.BlockCopy(keys[i], 0, packedKeys, offset, keyLength);
                    offset += keyLength;
                }

                if (cf is object)
                {
                    cfhs = intPtrPool.Rent(count);
                    for (int i = 0; i < count; i++)
                    {
                        cfhs[i] = cf[i].Handle;
                    }
                }

                fixed (byte* keyBase = packedKeys)
                {
                    offset = 0;
                    for (int i = 0; i < count; i++)
                    {
                        keyPtrs[i] = (IntPtr)(keyBase + offset);
                        offset += (int)(uint)keyLengthsConverted[i];
                    }

                    if (cfhs is null)
                    {
                        rocksdb_multi_get(db, read_options, (UIntPtr)(uint)count, keyPtrs, keyLengthsConverted, valuePtrs, valueLengths, errptrs);
                    }
                    else
                    {
                        rocksdb_multi_get_cf(db, read_options, cfhs, (UIntPtr)(uint)count, keyPtrs, keyLengthsConverted, valuePtrs, valueLengths, errptrs);
                    }
                }

                // now marshal all of the values
                for (int i = 0; i < count; i++)
                {
                    var valuePtr = valuePtrs[i];
                    if (valuePtr != IntPtr.Zero)
                    {
                        var valueLength = (ulong)valueLengths[i];
                        byte[] value = new byte[valueLength];
                        Marshal.Copy(valuePtr, value, 0, (int)valueLength);
                        values[i] = new KeyValuePair<byte[], byte[]>(keys[i], value);
                        valuePtrs[i] = IntPtr.Zero;
                        rocksdb_free(valuePtr);
                    }
                    else
                    {
                        values[i] = new KeyValuePair<byte[], byte[]>(keys[i], null);
                    }
                }

                return values;
            }
            finally
            {
                FreeNativeValues(valuePtrs, count);

                uintPtrPool.Return(keyLengthsConverted);
                intPtrPool.Return(keyPtrs);
                intPtrPool.Return(valuePtrs);
                uintPtrPool.Return(valueLengths);
                if (packedKeys is object)
                {
                    bytePool.Return(packedKeys);
                }

                if (cfhs is object)
                {
                    intPtrPool.Return(cfhs);
                }
            }
        }

        /// <summary>
        /// Executes a multi_get with automatic marshalling
        /// </summary>
        /// <param name="db"></param>
        /// <param name="read_options"></param>
        /// <param name="keys"></param>
        /// <param name="numKeys">when non-zero, specifies the number of keys in the array to fetch</param>
        /// <param name="keyLengths">when non-null specifies the lengths of each key to fetch</param>
        /// <param name="errptrs">when non-null, must be an array that will be populated with error codes</param>
        /// <param name="values">when non-null is a pre-allocated array to put the resulting values in</param>
        /// <param name="cf"></param>
        /// <returns></returns>
        public unsafe KeyValuePair<string, string>[] rocksdb_multi_get(
            IntPtr db,
            IntPtr read_options,
            string[] keys,
            IntPtr[] errptrs,
            ulong numKeys = 0,
            KeyValuePair<string, string>[] values = null,
            ColumnFamilyHandle[] cf = null,
            Encoding encoding = null)
        {
            if (encoding is null)
            {
                encoding = Encoding.UTF8;
            }

            int count = numKeys == 0 ? keys.Length : checked((int)numKeys);

            if (values is null)
            {
                values = new KeyValuePair<string, string>[count];
            }

            if (errptrs is null)
            {
                errptrs = new IntPtr[count];
            }

            var intPtrPool = ArrayPool<IntPtr>.Shared;
            var uintPtrPool = ArrayPool<UIntPtr>.Shared;

            UIntPtr[] keyLengths = uintPtrPool.Rent(count);
            IntPtr[] keyPtrs = intPtrPool.Rent(count);
            IntPtr[] valuePtrs = intPtrPool.Rent(count);
            UIntPtr[] valueLengths = uintPtrPool.Rent(count);
            IntPtr[] cfhs = null;

            // A rented array can hold stale pointers from an earlier caller; clear it so that the finally block
            // frees only what this call gets back.
            Array.Clear(valuePtrs, 0, count);

            try
            {
                // Every key is encoded into one buffer, instead of one native allocation per key.
                int totalLength = 0;
                for (int i = 0; i < count; i++)
                {
                    var key = keys[i];
                    int bklength;
                    fixed (char* k = key)
                    {
                        bklength = encoding.GetByteCount(k, key.Length);
                    }

                    keyLengths[i] = (UIntPtr)(uint)bklength;
                    totalLength = checked(totalLength + bklength);
                }

                if (cf is object)
                {
                    cfhs = intPtrPool.Rent(count);
                    for (int i = 0; i < count; i++)
                    {
                        cfhs[i] = cf[i].Handle;
                    }
                }

                byte* stack = stackalloc byte[EncodeScratch.StackBytes(totalLength)];
                using (var scratch = new EncodeScratch(stack, totalLength))
                {
                    byte* bk = scratch.Pointer;
                    for (int i = 0; i < count; i++)
                    {
                        var key = keys[i];
                        int bklength = (int)(uint)keyLengths[i];
                        fixed (char* k = key)
                        {
                            encoding.GetBytes(k, key.Length, bk, bklength);
                        }

                        keyPtrs[i] = (IntPtr)bk;
                        bk += bklength;
                    }

                    if (cfhs is null)
                    {
                        rocksdb_multi_get(db, read_options, (UIntPtr)(uint)count, keyPtrs, keyLengths, valuePtrs, valueLengths, errptrs);
                    }
                    else
                    {
                        rocksdb_multi_get_cf(db, read_options, cfhs, (UIntPtr)(uint)count, keyPtrs, keyLengths, valuePtrs, valueLengths, errptrs);
                    }
                }

                // now marshal all of the values
                for (int i = 0; i < count; i++)
                {
                    var resultPtr = valuePtrs[i];
                    if (resultPtr != IntPtr.Zero)
                    {
                        var bv = (sbyte*)resultPtr.ToPointer();
                        var bvLength = valueLengths[i];
                        values[i] = new KeyValuePair<string, string>(keys[i], CurrentFramework.CreateString(bv, 0, (int)bvLength, encoding));
                        valuePtrs[i] = IntPtr.Zero;
                        rocksdb_free(resultPtr);
                    }
                    else
                    {
                        values[i] = new KeyValuePair<string, string>(keys[i], null);
                    }
                }

                return values;
            }
            finally
            {
                FreeNativeValues(valuePtrs, count);

                uintPtrPool.Return(keyLengths);
                intPtrPool.Return(keyPtrs);
                intPtrPool.Return(valuePtrs);
                uintPtrPool.Return(valueLengths);
                if (cfhs is object)
                {
                    intPtrPool.Return(cfhs);
                }
            }
        }

        /// <summary>Frees the values of a multi_get that were not marshalled yet, for example after an exception.</summary>
        private void FreeNativeValues(IntPtr[] valuePtrs, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (valuePtrs[i] != IntPtr.Zero)
                {
                    rocksdb_free(valuePtrs[i]);
                    valuePtrs[i] = IntPtr.Zero;
                }
            }
        }

        public void rocksdb_delete(
            /*rocksdb_t**/ IntPtr db,
            /*const rocksdb_writeoptions_t**/ IntPtr writeOptions,
            /*const*/ string key,
            out IntPtr errptr,
            ColumnFamilyHandle cf,
            Encoding encoding = null)
        {
            if (encoding is null)
            {
                encoding = Encoding.UTF8;
            }

            unsafe
            {
                fixed (char* k = key)
                {
                    int klength = key.Length;
                    int bklength = encoding.GetByteCount(k, klength);
                    byte* stack = stackalloc byte[EncodeScratch.StackBytes(bklength)];
                    using var scratch = new EncodeScratch(stack, bklength);
                    byte* bk = scratch.Pointer;
                    encoding.GetBytes(k, klength, bk, bklength);
                    UIntPtr kLength = (UIntPtr)bklength;
                    if (cf is null)
                    {
                        rocksdb_delete(db, writeOptions, bk, kLength, out errptr);
                    }
                    else
                    {
                        rocksdb_delete_cf(db, writeOptions, cf.Handle, bk, kLength, out errptr);
                    }
                }
            }
        }

        public string rocksdb_options_statistics_get_string_marshaled(IntPtr opts)
        {
            return MarshalNullTermAsciiStr(rocksdb_options_statistics_get_string(opts));
        }


        public void rocksdb_writebatch_put(IntPtr writeOptions, string key, string val, Encoding encoding)
        {
            unsafe
            {
                if (encoding is null)
                {
                    encoding = Encoding.UTF8;
                }

                fixed (char* k = key, v = val)
                {
                    int klength = key.Length;
                    int vlength = val.Length;
                    int bklength = encoding.GetByteCount(k, klength);
                    int bvlength = encoding.GetByteCount(v, vlength);
                    int bufferLength = checked(bklength + bvlength);
                    byte* stack = stackalloc byte[EncodeScratch.StackBytes(bufferLength)];
                    using var scratch = new EncodeScratch(stack, bufferLength);
                    byte* bk = scratch.Pointer;
                    encoding.GetBytes(k, klength, bk, bklength);
                    byte* bv = bk + bklength;
                    encoding.GetBytes(v, vlength, bv, bvlength);
                    UIntPtr sklength = (UIntPtr)bklength;
                    UIntPtr svlength = (UIntPtr)bvlength;

                    rocksdb_writebatch_put(writeOptions, bk, sklength, bv, svlength);
                }
            }
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_wi_put(IntPtr writeBatch,
                                   byte[] key, ulong klen,
                                   byte[] val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_wi_put(writeBatch, key, sklength, val, svlength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_wi_put(IntPtr writeBatch,
                                                  byte* key, ulong klen,
                                                  byte* val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_wi_put(writeBatch, key, sklength, val, svlength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_wi_put_cf(IntPtr writeBatch, IntPtr column_family,
                                              byte[] key, ulong klen,
                                              byte[] val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_wi_put_cf(writeBatch, column_family, key, sklength, val, svlength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_wi_put_cf(IntPtr writeBatch, IntPtr column_family,
                                                     byte* key, ulong klen,
                                                     byte* val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_wi_put_cf(writeBatch, column_family, key, sklength, val, svlength);
        }

        public void rocksdb_writebatch_wi_put(IntPtr writeOptions, string key, string val, Encoding encoding)
        {
            unsafe
            {
                if (encoding is null)
                {
                    encoding = Encoding.UTF8;
                }

                fixed (char* k = key, v = val)
                {
                    int klength = key.Length;
                    int vlength = val.Length;
                    int bklength = encoding.GetByteCount(k, klength);
                    int bvlength = encoding.GetByteCount(v, vlength);
                    int bufferLength = checked(bklength + bvlength);
                    byte* stack = stackalloc byte[EncodeScratch.StackBytes(bufferLength)];
                    using var scratch = new EncodeScratch(stack, bufferLength);
                    byte* bk = scratch.Pointer;
                    encoding.GetBytes(k, klength, bk, bklength);
                    byte* bv = bk + bklength;
                    encoding.GetBytes(v, vlength, bv, bvlength);
                    UIntPtr sklength = (UIntPtr)bklength;
                    UIntPtr svlength = (UIntPtr)bvlength;

                    rocksdb_writebatch_wi_put(writeOptions, bk, sklength, bv, svlength);
                }
            }
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_iter_seek(IntPtr iter, byte* key, ulong klen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            rocksdb_iter_seek(iter, key, sklength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_iter_seek(IntPtr iter, byte[] key, ulong klen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            rocksdb_iter_seek(iter, key, sklength);
        }

        public void rocksdb_iter_seek(
            IntPtr iter,
            string key,
            Encoding encoding = null)
        {
            unsafe
            {
                if (encoding is null)
                {
                    encoding = Encoding.UTF8;
                }

                fixed (char* k = key)
                {
                    int klength = key.Length;
                    int bklength = encoding.GetByteCount(k, klength);
                    int bufferLength = bklength;
                    byte* stack = stackalloc byte[EncodeScratch.StackBytes(bufferLength)];
                    using var scratch = new EncodeScratch(stack, bufferLength);
                    byte* bk = scratch.Pointer;
                    encoding.GetBytes(k, klength, bk, bklength);
                    UIntPtr sklength = (UIntPtr)bklength;

                    rocksdb_iter_seek(iter, bk, sklength);
                }
            }
        }

        public void rocksdb_iter_seek_for_prev(
            IntPtr iter,
            string key,
            Encoding encoding = null)
        {
            unsafe
            {
                if (encoding is null)
                {
                    encoding = Encoding.UTF8;
                }

                fixed (char* k = key)
                {
                    int klength = key.Length;
                    int bklength = encoding.GetByteCount(k, klength);
                    int bufferLength = bklength;
                    byte* stack = stackalloc byte[EncodeScratch.StackBytes(bufferLength)];
                    using var scratch = new EncodeScratch(stack, bufferLength);
                    byte* bk = scratch.Pointer;
                    encoding.GetBytes(k, klength, bk, bklength);
                    UIntPtr sklength = (UIntPtr)bklength;

                    rocksdb_iter_seek_for_prev(iter, bk, sklength);
                }
            }
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void  rocksdb_iter_seek_for_prev(IntPtr iter, byte* key, ulong klen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            rocksdb_iter_seek_for_prev(iter, key, sklength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_iter_seek_for_prev(IntPtr iter, byte[] key, ulong klen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            rocksdb_iter_seek_for_prev(iter, key, sklength);
        }

        public string rocksdb_iter_key_string(
            /*rocksdb_t**/ IntPtr iter,
            Encoding encoding = null)
        {
            if (encoding is null)
            {
                encoding = Encoding.UTF8;
            }

            unsafe
            {
                var resultPtr = rocksdb_iter_key(iter, out UIntPtr bklength);

                return MarshalString(resultPtr, (long)bklength, encoding);
            }
        }

        public string rocksdb_iter_value_string(
            /*rocksdb_t**/ IntPtr iter,
            Encoding encoding = null)
        {
            if (encoding is null)
            {
                encoding = Encoding.UTF8;
            }

            unsafe
            {
                var resultPtr = rocksdb_iter_value(iter, out UIntPtr bvlength);

                return MarshalString(resultPtr, (long)bvlength, encoding);
            }
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_compact_range(IntPtr db,
            byte* start_key, long start_key_len,
            byte* limit_key, long limit_key_len)
        {
            UIntPtr sklength = (UIntPtr)start_key_len;
            UIntPtr lklength = (UIntPtr)limit_key_len;
            rocksdb_compact_range(db,
                                  start_key, sklength,
                                  limit_key, lklength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_compact_range(IntPtr db,
            byte[] start_key, long start_key_len,
            byte[] limit_key, long limit_key_len)
        {
            UIntPtr sklength = (UIntPtr)start_key_len;
            UIntPtr lklength = (UIntPtr)limit_key_len;
            rocksdb_compact_range(db,
                                  start_key, sklength,
                                  limit_key, lklength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_compact_range_cf(IntPtr db, IntPtr column_family,
            byte* start_key, long start_key_len,
            byte* limit_key, long limit_key_len)
        {
            UIntPtr sklength = (UIntPtr)start_key_len;
            UIntPtr lklength = (UIntPtr)limit_key_len;
            Native.Instance.rocksdb_compact_range_cf(db, column_family,
                                                     start_key, sklength, 
                                                     limit_key, lklength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_compact_range_cf(IntPtr db, IntPtr column_family,
            byte[] start_key, long start_key_len,
            byte[] limit_key, long limit_key_len)
        {
            UIntPtr sklength = (UIntPtr)start_key_len;
            UIntPtr lklength = (UIntPtr)limit_key_len;
            Native.Instance.rocksdb_compact_range_cf(db, column_family,
                                                     start_key, sklength, 
                                                     limit_key, lklength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public IntPtr rocksdb_writebatch_create_from(byte[] rep, long size)
        {
            UIntPtr fromSize = (UIntPtr)size;
            return rocksdb_writebatch_create_from(rep, fromSize);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_put(IntPtr writeBatch,
                                           byte[] key, ulong klen,
                                           byte[] val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_put(writeBatch, key, sklength, val, svlength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_put(IntPtr writeBatch,
                                                  byte* key, ulong klen,
                                                  byte* val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_put(writeBatch, key, sklength, val, svlength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_put_cf(IntPtr writeBatch, IntPtr column_family,
                                              byte[] key, ulong klen, 
                                              byte[] val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_put_cf(writeBatch, column_family, key, sklength, val, svlength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_put_cf(IntPtr writeBatch, IntPtr column_family,
                                                     byte* key, ulong klen,
                                                     byte* val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_put_cf(writeBatch, column_family, key, sklength, val, svlength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_merge(IntPtr writeBatch,
                                             byte[] key, ulong klen,
                                             byte[] val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_merge(writeBatch, key, sklength, val, svlength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_merge(IntPtr writeBatch,
                                                             byte* key, ulong klen,
                                                             byte* val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_merge(writeBatch, key, sklength, val, svlength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_merge_cf(IntPtr writeBatch, IntPtr column_family,
                                                         byte[] key, ulong klen,
                                                         byte[] val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_merge_cf(writeBatch, column_family, key, sklength, val, svlength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_merge_cf(IntPtr writeBatch, IntPtr column_family,
                                                                byte* key, ulong klen,
                                                                byte* val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_merge_cf(writeBatch, column_family, key, sklength, val, svlength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_delete(IntPtr writeBatch,
                                              byte[] key, ulong klen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            rocksdb_writebatch_delete(writeBatch, key, sklength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_delete(IntPtr writeBatch,
                                                     byte* key, ulong klen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            rocksdb_writebatch_delete(writeBatch, key, sklength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_delete_cf(IntPtr writeBatch, IntPtr column_family,
                                                 byte[] key, ulong klen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            rocksdb_writebatch_delete_cf(writeBatch, column_family, key, sklength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_delete_cf(IntPtr writeBatch, IntPtr column_family,
                                                        byte* key, ulong klen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            rocksdb_writebatch_delete_cf(writeBatch, column_family, key, sklength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_delete_range(IntPtr b,
                                                             byte[] start_key, ulong start_key_len,
                                                             byte[] end_key, ulong end_key_len)
        {
            UIntPtr sklength = (UIntPtr)start_key_len;
            UIntPtr eklength = (UIntPtr)end_key_len;
            rocksdb_writebatch_delete_range(b, start_key, sklength, end_key, eklength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_delete_range(IntPtr b,
                                                                    byte* start_key, ulong start_key_len,
                                                                    byte* end_key, ulong end_key_len)
        {
            UIntPtr sklength = (UIntPtr)start_key_len;
            UIntPtr eklength = (UIntPtr)end_key_len;
            rocksdb_writebatch_delete_range(b, start_key, sklength, end_key, eklength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_delete_range_cf(IntPtr b, IntPtr column_family,
                                                                byte[] start_key, ulong start_key_len,
                                                                byte[] end_key, ulong end_key_len)
        {
            UIntPtr sklength = (UIntPtr)start_key_len;
            UIntPtr eklength = (UIntPtr)end_key_len;
            rocksdb_writebatch_delete_range_cf(b, column_family, start_key, sklength, end_key, eklength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_delete_range_cf(IntPtr b, IntPtr column_family,
                                                                       byte* start_key, ulong start_key_len,
                                                                       byte* end_key, ulong end_key_len)
        {
            UIntPtr sklength = (UIntPtr)start_key_len;
            UIntPtr eklength = (UIntPtr)end_key_len;
            rocksdb_writebatch_delete_range_cf(b, column_family, start_key, sklength, end_key, eklength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_put_log_data(IntPtr writeBatch, byte[] blob, ulong len)
        {
            UIntPtr bloblength = (UIntPtr)len;
            rocksdb_writebatch_put_log_data(writeBatch, blob, bloblength);
        }

        public byte[] rocksdb_writebatch_data(IntPtr wbHandle)
        {
            var resultPtr = rocksdb_writebatch_data(wbHandle, out UIntPtr size);
            var data = new byte[(long)size];
            Marshal.Copy(resultPtr, data, 0, (int)size);
            // Do not free this memory because it is owned by the write batch and will be freed when it is disposed
            // rocksdb_free(resultPtr);
            return data;
        }

        [Obsolete("Use the UIntPtr version instead")]
        public IntPtr rocksdb_writebatch_wi_create(ulong reserved_bytes,
                                                   bool overwrite_keys)
        {
            UIntPtr res_bytes = (UIntPtr)reserved_bytes;
            return rocksdb_writebatch_wi_create(res_bytes, overwrite_keys ? (byte)1 : (byte)0);
        }

        public byte[] rocksdb_writebatch_wi_data(IntPtr wbHandle)
        {
            var resultPtr = rocksdb_writebatch_wi_data(wbHandle, out UIntPtr size);
            var data = new byte[(long)size];
            Marshal.Copy(resultPtr, data, 0, (int)size);
            // Do not free this memory because it is owned by the write batch and will be freed when it is disposed
            // rocksdb_free(resultPtr);
            return data;
        }

        public int rocksdb_writebatch_data(IntPtr wbHandle, byte[] buffer, int offset, int length)
        {
            var resultPtr = rocksdb_writebatch_data(wbHandle, out UIntPtr size);
            bool fits = (int)size <= length;
            if (!fits)
            {
                // Do not free this memory because it is owned by the write batch and will be freed when it is disposed
                // rocksdb_free(resultPtr);
                return -1;
            }
            Marshal.Copy(resultPtr, buffer, offset, (int)size);
            // Do not free this memory because it is owned by the write batch and will be freed when it is disposed
            // rocksdb_free(resultPtr);
            return (int)size;
        }

        public int rocksdb_writebatch_wi_data(IntPtr wbHandle, byte[] buffer, int offset, int length)
        {
            var resultPtr = rocksdb_writebatch_wi_data(wbHandle, out UIntPtr size);
            bool fits = (int)size <= length;
            if (!fits)
            {
                // Do not free this memory because it is owned by the write batch and will be freed when it is disposed
                // rocksdb_free(resultPtr);
                return -1;
            }
            Marshal.Copy(resultPtr, buffer, offset, (int)size);
            // Do not free this memory because it is owned by the write batch and will be freed when it is disposed
            // rocksdb_free(resultPtr);
            return (int)size;
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_wi_merge(IntPtr writeBatch,
                                     byte[] key, ulong klen,
                                     byte[] val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_wi_merge(writeBatch, key, sklength, val, svlength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_wi_merge(IntPtr writeBatch,
                                                             byte* key, ulong klen,
                                                             byte* val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_wi_merge(writeBatch, key, sklength, val, svlength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_wi_merge_cf(IntPtr writeBatch, IntPtr column_family,
                                                         byte[] key, ulong klen,
                                                         byte[] val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_wi_merge_cf(writeBatch, column_family, key, sklength, val, svlength);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_wi_merge_cf(IntPtr writeBatch, IntPtr column_family,
                                                                byte* key, ulong klen,
                                                                byte* val, ulong vlen)
        {
            UIntPtr sklength = (UIntPtr)klen;
            UIntPtr svlength = (UIntPtr)vlen;
            rocksdb_writebatch_wi_merge_cf(writeBatch, column_family, key, sklength, val, svlength);
        }

        public string rocksdb_property_value_string(IntPtr db, string propname)
        {
            return MarshalNullTermAsciiStr(rocksdb_property_value(db, propname));
        }

        public string rocksdb_property_value_cf_string(IntPtr db, IntPtr column_family, string propname)
        {
            return MarshalNullTermAsciiStr(rocksdb_property_value_cf(db, column_family, propname));
        }

        public unsafe void rocksdb_sstfilewriter_add(
            IntPtr writer,
            string key,
            string val,
            out IntPtr errptr,
            Encoding encoding = null)
        {
            unsafe
            {
                if (encoding is null)
                {
                    encoding = Encoding.UTF8;
                }

                fixed (char* k = key, v = val)
                {
                    int klength = key.Length;
                    int vlength = val.Length;
                    int bklength = encoding.GetByteCount(k, klength);
                    int bvlength = encoding.GetByteCount(v, vlength);
                    int bufferLength = checked(bklength + bvlength);
                    byte* stack = stackalloc byte[EncodeScratch.StackBytes(bufferLength)];
                    using var scratch = new EncodeScratch(stack, bufferLength);
                    byte* bk = scratch.Pointer;
                    encoding.GetBytes(k, klength, bk, bklength);
                    byte* bv = bk + bklength;
                    encoding.GetBytes(v, vlength, bv, bvlength);

                    rocksdb_sstfilewriter_add(writer, bk, new UIntPtr((uint)bklength), bv, new UIntPtr((uint)bvlength), out errptr);
                }
            }
        }

        public string rocksdb_writebatch_wi_get_from_batch(
            IntPtr wb,
            IntPtr options,
            string key,
            out IntPtr errptr,
            ColumnFamilyHandle cf = null,
            Encoding encoding = null)
        {
            if (encoding is null)
            {
                encoding = Encoding.UTF8;
            }

            unsafe
            {
                fixed (char* k = key)
                {
                    int klength = key.Length;
                    int bklength = encoding.GetByteCount(k, klength);
                    int bufferLength = bklength;
                    byte* stack = stackalloc byte[EncodeScratch.StackBytes(bufferLength)];
                    using var scratch = new EncodeScratch(stack, bufferLength);
                    byte* bk = scratch.Pointer;
                    encoding.GetBytes(k, klength, bk, bklength);

                    var resultPtr = cf is null
                        ? rocksdb_writebatch_wi_get_from_batch(wb, options, bk, new UIntPtr((uint)bklength), out UIntPtr bvlength, out errptr)
                        : rocksdb_writebatch_wi_get_from_batch_cf(wb, options, cf.Handle, bk, new UIntPtr((uint)bklength), out bvlength, out errptr);

                    if (errptr != IntPtr.Zero)
                    {
                        return null;
                    }

                    if (resultPtr == IntPtr.Zero)
                    {
                        return null;
                    }

                    return MarshalAndFreeRocksDbString(resultPtr, (long)bvlength, encoding);
                }
            }
        }

        public byte[] rocksdb_writebatch_wi_get_from_batch(
            IntPtr wb,
            IntPtr options,
            byte[] key,
            ulong keyLength,
            out IntPtr errptr,
            ColumnFamilyHandle cf = null)
        {
            var resultPtr = cf is null
                ? rocksdb_writebatch_wi_get_from_batch(wb, options, key, new UIntPtr(keyLength), out UIntPtr valueLength, out errptr)
                : rocksdb_writebatch_wi_get_from_batch_cf(wb, options, cf.Handle, key, new UIntPtr(keyLength), out valueLength, out errptr);
            if (errptr != IntPtr.Zero)
            {
                return null;
            }

            if (resultPtr == IntPtr.Zero)
            {
                return null;
            }

            var result = new byte[(long)valueLength];
            Marshal.Copy(resultPtr, result, 0, (int)valueLength);
            rocksdb_free(resultPtr);
            return result;
        }

        public string rocksdb_writebatch_wi_get_from_batch_and_db(
            IntPtr wb,
            IntPtr db,
            IntPtr read_options,
            string key,
            out IntPtr errptr,
            ColumnFamilyHandle cf = null,
            Encoding encoding = null)
        {
            if (encoding is null)
            {
                encoding = Encoding.UTF8;
            }

            unsafe
            {
                fixed (char* k = key)
                {
                    int klength = key.Length;
                    int bklength = encoding.GetByteCount(k, klength);
                    int bufferLength = bklength;
                    byte* stack = stackalloc byte[EncodeScratch.StackBytes(bufferLength)];
                    using var scratch = new EncodeScratch(stack, bufferLength);
                    byte* bk = scratch.Pointer;
                    encoding.GetBytes(k, klength, bk, bklength);

                    var resultPtr = cf is null
                        ? rocksdb_writebatch_wi_get_from_batch_and_db(wb, db, read_options, bk, new UIntPtr((uint)bklength), out UIntPtr bvlength, out errptr)
                        : rocksdb_writebatch_wi_get_from_batch_and_db_cf(wb, db, read_options, cf.Handle, bk, new UIntPtr((uint)bklength), out bvlength, out errptr);

                    if (errptr != IntPtr.Zero)
                    {
                        return null;
                    }

                    if (resultPtr == IntPtr.Zero)
                    {
                        return null;
                    }

                    return MarshalAndFreeRocksDbString(resultPtr, (long)bvlength, encoding);
                }
            }
        }

        public byte[] rocksdb_writebatch_wi_get_from_batch_and_db(
            IntPtr wb,
            IntPtr db,
            IntPtr read_options,
            byte[] key,
            ulong keyLength,
            out IntPtr errptr,
            ColumnFamilyHandle cf = null)
        {
            var resultPtr = cf is null
                ? rocksdb_writebatch_wi_get_from_batch_and_db(wb, db, read_options, key, new UIntPtr(keyLength), out UIntPtr valueLength, out errptr)
                : rocksdb_writebatch_wi_get_from_batch_and_db_cf(wb, db, read_options, cf.Handle, key, new UIntPtr(keyLength), out valueLength, out errptr);
            if (errptr != IntPtr.Zero)
            {
                return null;
            }

            if (resultPtr == IntPtr.Zero)
            {
                return null;
            }

            var result = new byte[(ulong)valueLength];
            Marshal.Copy(resultPtr, result, 0, (int)valueLength);
            rocksdb_free(resultPtr);
            return result;
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_wi_delete(IntPtr writeBatch,
                                      byte[] key, ulong klen)
        {
            rocksdb_writebatch_wi_delete(writeBatch, key, new UIntPtr(klen));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_wi_delete(IntPtr writeBatch,
                                                     byte* key, ulong klen)
        {
            rocksdb_writebatch_wi_delete(writeBatch, key, new UIntPtr(klen));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_wi_delete_cf(IntPtr writeBatch, IntPtr column_family,
                                                 byte[] key, ulong klen)
        {
            rocksdb_writebatch_wi_delete_cf(writeBatch, column_family, key, new UIntPtr(klen));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_wi_delete_cf(IntPtr writeBatch, IntPtr column_family,
                                                        byte* key, ulong klen)
        {
            rocksdb_writebatch_wi_delete_cf(writeBatch, column_family, key, new UIntPtr(klen));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_wi_delete_range(IntPtr b,
                                                             byte[] start_key, ulong start_key_len,
                                                             byte[] end_key, ulong end_key_len)
        {
            rocksdb_writebatch_wi_delete_range(b, start_key, new UIntPtr(start_key_len), end_key, new UIntPtr(end_key_len));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_wi_delete_range(IntPtr b,
                                                                    byte* start_key, ulong start_key_len,
                                                                    byte* end_key, ulong end_key_len)
        {
            rocksdb_writebatch_wi_delete_range(b, start_key, new UIntPtr(start_key_len), end_key, new UIntPtr(end_key_len));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_wi_delete_range_cf(IntPtr b, IntPtr column_family,
                                                                byte[] start_key, ulong start_key_len,
                                                                byte[] end_key, ulong end_key_len)
        {
            rocksdb_writebatch_wi_delete_range_cf(b, column_family, start_key, new UIntPtr(start_key_len), end_key, new UIntPtr(end_key_len));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public unsafe void rocksdb_writebatch_wi_delete_range_cf(IntPtr b, IntPtr column_family,
                                                                       byte* start_key, ulong start_key_len,
                                                                       byte* end_key, ulong end_key_len)
        {
            rocksdb_writebatch_wi_delete_range_cf(b, column_family, start_key, new UIntPtr(start_key_len), end_key, new UIntPtr(end_key_len));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_compaction_readahead_size(IntPtr options, ulong size)
        {
            rocksdb_options_compaction_readahead_size(options, new UIntPtr(size));
        }

        public void rocksdb_options_set_compression_per_level(IntPtr opt, Compression[] level_values, ulong num_levels)
        {
            rocksdb_options_set_compression_per_level(opt, level_values, new UIntPtr(num_levels));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_write_buffer_size(IntPtr options, ulong value)
        {
            rocksdb_options_set_write_buffer_size(options, new UIntPtr(value));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_max_bytes_for_level_multiplier_additional(IntPtr options, int[] level_values, ulong num_levels)
        {
            rocksdb_options_set_max_bytes_for_level_multiplier_additional(options, level_values, new UIntPtr(num_levels));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_soft_pending_compaction_bytes_limit(IntPtr opt, ulong v)
        {
            rocksdb_options_set_soft_pending_compaction_bytes_limit(opt, new UIntPtr(v));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_hash_skip_list_rep(IntPtr options, ulong p1, int p2, int p3)
        {
            rocksdb_options_set_hash_skip_list_rep(options, new UIntPtr(p1), p2, p3);
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_hash_link_list_rep(IntPtr options, ulong value)
        {
            rocksdb_options_set_hash_link_list_rep(options, new UIntPtr(value));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_arena_block_size(IntPtr options, ulong value)
        {
            rocksdb_options_set_arena_block_size(options, new UIntPtr(value));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_hard_pending_compaction_bytes_limit(IntPtr opt, ulong v)
        {
            rocksdb_options_set_hard_pending_compaction_bytes_limit(opt, new UIntPtr(v));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_max_successive_merges(IntPtr options, ulong value)
        {
            rocksdb_options_set_max_successive_merges(options, new UIntPtr(value));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_inplace_update_num_locks(IntPtr options, ulong value)
        {
            rocksdb_options_set_inplace_update_num_locks(options, new UIntPtr(value));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_memtable_huge_page_size(IntPtr options, ulong size)
        {
            rocksdb_options_set_memtable_huge_page_size(options, new UIntPtr(size));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_block_based_options_set_block_size(IntPtr options, ulong blockSize)
        {
            rocksdb_block_based_options_set_block_size(options, new UIntPtr(blockSize));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_max_log_file_size(IntPtr options, ulong value)
        {
            rocksdb_options_set_max_log_file_size(options, new UIntPtr(value));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_log_file_time_to_roll(IntPtr options, ulong value)
        {
            rocksdb_options_set_log_file_time_to_roll(options, new UIntPtr(value));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_keep_log_file_num(IntPtr options, ulong value)
        {
            rocksdb_options_set_keep_log_file_num(options, new UIntPtr(value));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_recycle_log_file_num(IntPtr options, ulong value)
        {
            rocksdb_options_set_recycle_log_file_num(options, new UIntPtr(value));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_max_manifest_file_size(IntPtr options, ulong value)
        {
            rocksdb_options_set_max_manifest_file_size(options, new UIntPtr(value));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_manifest_preallocation_size(IntPtr options, ulong value)
        {
            rocksdb_options_set_manifest_preallocation_size(options, new UIntPtr(value));
        }

        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_options_set_db_write_buffer_size(IntPtr options,ulong size)
        {
            rocksdb_options_set_db_write_buffer_size(options, new UIntPtr(size));
        }
        [Obsolete("Use the UIntPtr version instead")]
        public void rocksdb_writebatch_wi_put_log_data(IntPtr writeBatch, byte[] blob, ulong len)
        {
            rocksdb_writebatch_wi_put_log_data(writeBatch, blob, new UIntPtr(len));
        }

        public byte[] rocksdb_iter_key(IntPtr iterator)
        {
            IntPtr buffer = rocksdb_iter_key(iterator, out UIntPtr length);
            byte[] result = new byte[(int)length];
            Marshal.Copy(buffer, result, 0, (int)length);
            // Do not free, this is owned by the iterator and will be freed there
            //rocksdb_free(buffer);
            return result;
        }

        public byte[] rocksdb_iter_value(IntPtr iterator)
        {
            IntPtr buffer = rocksdb_iter_value(iterator, out UIntPtr length);
            byte[] result = new byte[(int)length];
            Marshal.Copy(buffer, result, 0, (int)length);
            // Do not free, this is owned by the iterator and will be freed there
            //rocksdb_free(buffer);
            return result;
        }

#if !NETSTANDARD2_0
        public unsafe T rocksdb_iter_key<T>(IntPtr iterator, ISpanDeserializer<T> deserializer)
        {
            IntPtr buffer = rocksdb_iter_key(iterator, out UIntPtr length);
            var span = new ReadOnlySpan<byte>((void*)buffer, (int)length);
            return deserializer.Deserialize(span);
        }

        public unsafe T rocksdb_iter_value<T>(IntPtr iterator, ISpanDeserializer<T> deserializer)
        {
            IntPtr buffer = rocksdb_iter_value(iterator, out UIntPtr length);
            var span = new ReadOnlySpan<byte>((void*)buffer, (int)length);
            return deserializer.Deserialize(span);
        }
#endif

        public unsafe T rocksdb_iter_key<T>(IntPtr iterator, Func<Stream, T> deserializer)
        {
            IntPtr buffer = rocksdb_iter_key(iterator, out UIntPtr length);
            using var stream = new UnmanagedMemoryStream((byte*)buffer, (long)length);
            return deserializer(stream);
        }

        public unsafe T rocksdb_iter_value<T>(IntPtr iterator, Func<Stream, T> deserializer)
        {
            IntPtr buffer = rocksdb_iter_value(iterator, out UIntPtr length);
            using var stream = new UnmanagedMemoryStream((byte*)buffer, (long)length);
            return deserializer(stream);
        }
    }
}
