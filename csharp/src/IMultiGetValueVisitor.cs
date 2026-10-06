using System;

namespace RocksDbSharp
{
#if !NETSTANDARD2_0
    /// <summary>
    /// Receives the values that <see cref="RocksDb.MultiGet{TVisitor}"/> finds. Implement it on a struct so the
    /// calls are not virtual and the visitor is not boxed; keep any results the visitor collects in its fields.
    /// </summary>
    public interface IMultiGetValueVisitor
    {
        /// <summary>
        /// Called once for each key that exists, in the order of the batch. A key that does not exist gets no call.
        /// </summary>
        /// <param name="index">The position of the key in the batch.</param>
        /// <param name="value">
        /// The value, in memory that RocksDB owns (usually the block cache). It is valid only until this call
        /// returns: copy what must live longer.
        /// </param>
        void OnValue(int index, ReadOnlySpan<byte> value);
    }
#endif
}
