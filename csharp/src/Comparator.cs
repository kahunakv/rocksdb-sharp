using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Transitional;

namespace RocksDbSharp
{
    public interface Comparator
    {
        string Name { get; }
        int Compare(IntPtr a, UIntPtr alen, IntPtr b, UIntPtr blen);
    }

    public abstract class StringComparatorBase : Comparator
    {
        public Encoding Encoding { get; }

        public string Name { get; }

        public StringComparatorBase(Encoding encoding = null, string name = null, IntPtr state = default(IntPtr))
        {
            Name = name ?? typeof(StringComparatorBase).Name;
            Encoding = encoding ?? Encoding.UTF8;
        }

        public abstract int Compare(string a, string b);

        public unsafe int Compare(IntPtr a, UIntPtr alen, IntPtr b, UIntPtr blen)
        {
            var astr = Encoding.GetString((byte*)a, (int)alen);
            var bstr = Encoding.GetString((byte*)b, (int)blen);
            return Compare(astr, bstr);
        }
    }

#if !NETSTANDARD2_0
    /// <summary>
    /// Base for a comparator that orders keys by their bytes. Unlike <see cref="StringComparatorBase"/>, it does not
    /// decode both keys into new strings on every comparison, and RocksDB calls a comparator very often: on reads,
    /// on writes, and during flushes and compactions.
    /// <para>
    /// A database must always be opened with a comparator that has the same <see cref="Name"/> and gives exactly the
    /// same order as the one it was created with. Changing the order of an existing database needs a rebuild.
    /// Where the plain byte order is enough, do not set a comparator at all: the native bytewise comparator needs no
    /// managed callback.
    /// </para>
    /// </summary>
    public abstract class SpanComparatorBase : Comparator
    {
        /// <param name="name">The name that RocksDB stores with the database; null gives the full name of the type.</param>
        protected SpanComparatorBase(string name = null)
        {
            Name = name ?? GetType().FullName;
        }

        public string Name { get; }

        /// <summary>
        /// Compares two keys. The spans point into memory that RocksDB owns and are valid only during the call.
        /// </summary>
        public abstract int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b);

        public unsafe int Compare(IntPtr a, UIntPtr alen, IntPtr b, UIntPtr blen)
        {
            return Compare(
                new ReadOnlySpan<byte>((void*)a, checked((int)(ulong)alen)),
                new ReadOnlySpan<byte>((void*)b, checked((int)(ulong)blen)));
        }
    }
#endif

    public class StringComparator : StringComparatorBase
    {
        public Comparison<string> CompareFunc { get; }

        public StringComparator(IComparer<string> comparer = null, Encoding encoding = null, string name = null)
            : base(encoding, name)
        {
            if (comparer == null)
                comparer = StringComparer.CurrentCulture;
            CompareFunc = comparer.Compare;
        }

        public StringComparator(bool ignoreCase, Encoding encoding = null, string name = null)
            : this(ignoreCase ? StringComparer.CurrentCultureIgnoreCase : StringComparer.CurrentCulture, encoding, name)
        {
        }

        public override int Compare(string a, string b) => CompareFunc(a, b);
    }
}
