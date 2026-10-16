using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace RocksDbSharp
{
    public interface MergeOperator
    {
        string Name { get; }
        IntPtr PartialMerge(IntPtr key, UIntPtr keyLength, IntPtr operandsList, IntPtr operandsListLength, int numOperands, out byte success, out IntPtr newValueLength);
        IntPtr FullMerge(IntPtr key, UIntPtr keyLength, IntPtr existingValue, UIntPtr existingValueLength, IntPtr operandsList, IntPtr operandsListLength, int numOperands, out byte success, out IntPtr newValueLength);
        void DeleteValue(IntPtr value, UIntPtr valueLength);
    }

#if !NETSTANDARD2_0
    public static class MergeOperators
    {
        /// <summary>
        /// This function performs merge(left_op, right_op)
        /// when both the operands are themselves merge operation types.
        /// Save the result in *new_value and return true. If it is impossible
        /// or infeasible to combine the two operations, return false instead.
        /// This is called to combine two-merge operands (if possible)
        /// </summary>
        /// <param name="key">The key that's associated with this merge operation</param>
        /// <param name="operands">the sequence of merge operations to apply, front() first</param>
        /// <param name="success">Client is responsible for filling the merge result here</param>
        /// <returns></returns>
        public delegate byte[] PartialMergeFunc(ReadOnlySpan<byte> key, OperandsEnumerator operands, out bool success);

        /// <summary>
        /// Gives the client a way to express the read -> modify -> write semantics.
        /// Called when a Put/Delete is the *existing_value (or nullptr)
        /// </summary>
        /// <param name="key">The key that's associated with this merge operation.</param>
        /// <param name="existingValue">null indicates that the key does not exist before this op</param>
        /// <param name="operands">the sequence of merge operations to apply, front() first.</param>
        /// <param name="success">Client is responsible for filling the merge result here</param>
        /// <returns></returns>
        public delegate byte[] FullMergeFunc(ReadOnlySpan<byte> key, bool hasExistingValue, ReadOnlySpan<byte> existingValue, OperandsEnumerator operands, out bool success);


        public static MergeOperator Create(
            string name,
            PartialMergeFunc partialMerge,
            FullMergeFunc fullMerge)
        {
            return new MergeOperatorImpl(name, partialMerge, fullMerge);
        }

        /// <summary>
        /// Like <see cref="PartialMergeFunc"/>, but the callback writes the result into <paramref name="result"/>
        /// instead of returning a new array.
        /// </summary>
        /// <returns>True when the merge succeeded; false when the two operands cannot be combined.</returns>
        public delegate bool PartialMergeWriterFunc(ReadOnlySpan<byte> key, OperandsEnumerator operands, ref MergeValueWriter result);

        /// <summary>
        /// Like <see cref="FullMergeFunc"/>, but the callback writes the result into <paramref name="result"/>
        /// instead of returning a new array.
        /// </summary>
        /// <returns>True when the merge succeeded; false makes the read or compaction that needs it fail.</returns>
        public delegate bool FullMergeWriterFunc(ReadOnlySpan<byte> key, bool hasExistingValue, ReadOnlySpan<byte> existingValue, OperandsEnumerator operands, ref MergeValueWriter result);

        /// <summary>
        /// Creates a merge operator whose callbacks write their result straight into the memory that is handed to
        /// RocksDB. This removes the managed result array of <see cref="Create"/> and the copy of it into native
        /// memory, which matters when merges are frequent or their results are large.
        /// <para>
        /// An exception from a callback counts as a failed merge, because an exception cannot cross back into native
        /// code. RocksDB then reports the failure to the operation that needed the merge.
        /// </para>
        /// </summary>
        public static MergeOperator CreateWithValueWriter(
            string name,
            PartialMergeWriterFunc partialMerge,
            FullMergeWriterFunc fullMerge)
        {
            if (partialMerge is null) throw new ArgumentNullException(nameof(partialMerge));
            if (fullMerge is null) throw new ArgumentNullException(nameof(fullMerge));
            return new WriterMergeOperatorImpl(name, partialMerge, fullMerge);
        }

        /// <summary>
        /// Receives the result of a merge in native memory that the merge operator then gives to RocksDB.
        /// </summary>
        public unsafe ref struct MergeValueWriter
        {
            private IntPtr _value;
            private int _length;

            /// <summary>The length of the result so far.</summary>
            public int Length => _length;

            /// <summary>
            /// Sets the length of the result and returns the memory to write it into. A second call changes the
            /// length: it keeps the bytes already written, up to the smaller of the two lengths, and makes the spans
            /// from earlier calls invalid. So a callback that does not know the final length can start with an
            /// estimate, then grow or shrink the result.
            /// </summary>
            public Span<byte> Allocate(int length)
            {
                if (length < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(length));
                }

                // Never zero bytes: RocksDB gets a valid pointer also for an empty result.
                var size = (IntPtr)Math.Max(length, 1);
                _value = _value == IntPtr.Zero ? Marshal.AllocHGlobal(size) : Marshal.ReAllocHGlobal(_value, size);
                _length = length;
                return new Span<byte>((void*)_value, length);
            }

            /// <summary>Hands the result over to the caller, who frees it with <see cref="Marshal.FreeHGlobal"/>.</summary>
            internal IntPtr Detach(out int length)
            {
                if (_value == IntPtr.Zero)
                {
                    Allocate(0);
                }

                IntPtr value = _value;
                length = _length;
                _value = IntPtr.Zero;
                _length = 0;
                return value;
            }

            internal void Free()
            {
                if (_value != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_value);
                }

                _value = IntPtr.Zero;
                _length = 0;
            }
        }

        public ref struct OperandsEnumerator
        {
            private ReadOnlySpan<IntPtr> _operandsList;
            private ReadOnlySpan<long> _operandsListLength;

            public OperandsEnumerator(ReadOnlySpan<IntPtr> operandsList, ReadOnlySpan<long> operandsListLength)
            {
                _operandsList = operandsList;
                _operandsListLength = operandsListLength;
            }

            public int Count => _operandsList.Length;
            public unsafe ReadOnlySpan<byte> Get(int index)
            {
                return new Span<byte>((void*)_operandsList[index], (int)_operandsListLength[index]);
            }
        }


        private class MergeOperatorImpl : MergeOperator
        {
            public string Name { get; }
            private PartialMergeFunc PartialMerge { get; }
            private FullMergeFunc FullMerge { get; }

            public MergeOperatorImpl(string name, PartialMergeFunc partialMerge, FullMergeFunc fullMerge)
            {
                Name = name;
                PartialMerge = partialMerge;
                FullMerge = fullMerge;
            }

            unsafe IntPtr MergeOperator.PartialMerge(IntPtr key, UIntPtr keyLength, IntPtr operandsList, IntPtr operandsListLength, int numOperands, out byte success, out IntPtr newValueLength)
            {
                var keySpan                = new ReadOnlySpan<byte>((void*)key, (int)keyLength);
                var operandsListSpan       = new ReadOnlySpan<IntPtr>((void*)operandsList, numOperands);
                var operandsListLengthSpan = new ReadOnlySpan<long>((void*)operandsListLength, numOperands);
                var operands               = new OperandsEnumerator(operandsListSpan, operandsListLengthSpan);

                var value = PartialMerge(keySpan, operands, out var _success) ?? Array.Empty<byte>();

                var ret = Marshal.AllocHGlobal(Math.Max(value.Length, 1));
                Marshal.Copy(value, 0, ret, value.Length);
                newValueLength = (IntPtr)value.Length;

                success =  Native.MarshalBool(_success);

                return ret;
            }

            unsafe IntPtr MergeOperator.FullMerge(IntPtr key, UIntPtr keyLength, IntPtr existingValue, UIntPtr existingValueLength, IntPtr operandsList, IntPtr operandsListLength, int numOperands, out byte success, out IntPtr newValueLength)
            {
                var keySpan                = new ReadOnlySpan<byte>((void*)key, (int)keyLength);
                var operandsListSpan       = new ReadOnlySpan<IntPtr>((void*)operandsList, numOperands);
                var operandsListLengthSpan = new ReadOnlySpan<long>((void*)operandsListLength, numOperands);
                var operands               = new OperandsEnumerator(operandsListSpan, operandsListLengthSpan);
                bool hasExistingValue      = existingValue != IntPtr.Zero;
                var existingValueSpan      = hasExistingValue ? new ReadOnlySpan<byte>((void*)existingValue, (int)existingValueLength) : ReadOnlySpan<byte>.Empty;

                var value = FullMerge(keySpan, hasExistingValue, existingValueSpan, operands, out var _success) ?? Array.Empty<byte>();

                var ret = Marshal.AllocHGlobal(Math.Max(value.Length, 1));
                Marshal.Copy(value, 0, ret, value.Length);
                newValueLength = (IntPtr)value.Length;

                success =  Native.MarshalBool(_success);

                return ret;
            }

            void MergeOperator.DeleteValue(IntPtr value, UIntPtr valueLength) => Marshal.FreeHGlobal(value);
        }

        private sealed class WriterMergeOperatorImpl : MergeOperator
        {
            public string Name { get; }
            private PartialMergeWriterFunc PartialMerge { get; }
            private FullMergeWriterFunc FullMerge { get; }

            public WriterMergeOperatorImpl(string name, PartialMergeWriterFunc partialMerge, FullMergeWriterFunc fullMerge)
            {
                Name = name;
                PartialMerge = partialMerge;
                FullMerge = fullMerge;
            }

            unsafe IntPtr MergeOperator.PartialMerge(IntPtr key, UIntPtr keyLength, IntPtr operandsList, IntPtr operandsListLength, int numOperands, out byte success, out IntPtr newValueLength)
            {
                var keySpan                = new ReadOnlySpan<byte>((void*)key, (int)keyLength);
                var operandsListSpan       = new ReadOnlySpan<IntPtr>((void*)operandsList, numOperands);
                var operandsListLengthSpan = new ReadOnlySpan<long>((void*)operandsListLength, numOperands);
                var operands               = new OperandsEnumerator(operandsListSpan, operandsListLengthSpan);

                var writer = new MergeValueWriter();
                bool merged;
                try
                {
                    merged = PartialMerge(keySpan, operands, ref writer);
                }
                catch
                {
                    writer.Free();
                    merged = false;
                }

                return Complete(ref writer, merged, out success, out newValueLength);
            }

            unsafe IntPtr MergeOperator.FullMerge(IntPtr key, UIntPtr keyLength, IntPtr existingValue, UIntPtr existingValueLength, IntPtr operandsList, IntPtr operandsListLength, int numOperands, out byte success, out IntPtr newValueLength)
            {
                var keySpan                = new ReadOnlySpan<byte>((void*)key, (int)keyLength);
                var operandsListSpan       = new ReadOnlySpan<IntPtr>((void*)operandsList, numOperands);
                var operandsListLengthSpan = new ReadOnlySpan<long>((void*)operandsListLength, numOperands);
                var operands               = new OperandsEnumerator(operandsListSpan, operandsListLengthSpan);
                bool hasExistingValue      = existingValue != IntPtr.Zero;
                var existingValueSpan      = hasExistingValue ? new ReadOnlySpan<byte>((void*)existingValue, (int)existingValueLength) : ReadOnlySpan<byte>.Empty;

                var writer = new MergeValueWriter();
                bool merged;
                try
                {
                    merged = FullMerge(keySpan, hasExistingValue, existingValueSpan, operands, ref writer);
                }
                catch
                {
                    writer.Free();
                    merged = false;
                }

                return Complete(ref writer, merged, out success, out newValueLength);
            }

            private static IntPtr Complete(ref MergeValueWriter writer, bool merged, out byte success, out IntPtr newValueLength)
            {
                if (!merged)
                {
                    // RocksDB ignores the value of a failed merge, but still frees what it gets back.
                    writer.Free();
                }

                IntPtr value = writer.Detach(out int length);
                newValueLength = (IntPtr)length;
                success = Native.MarshalBool(merged);
                return value;
            }

            void MergeOperator.DeleteValue(IntPtr value, UIntPtr valueLength) => Marshal.FreeHGlobal(value);
        }
    }
#endif
}