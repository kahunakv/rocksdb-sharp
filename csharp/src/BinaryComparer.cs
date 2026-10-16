using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;

namespace RocksDbSharp
{
    // TODO: consider somehow reusing the actual unmanaged comparer
    public class BinaryComparer : IEqualityComparer<byte[]>, IComparer<byte[]>
    {
        public static BinaryComparer Default { get; } = new BinaryComparer();

        public int Compare(byte[] a1, byte[] a2)
        {
            int length = Math.Min(a1.Length, a2.Length);
            unsafe
            {
                fixed (byte* p1 = a1, p2 = a2)
                {
                    byte* c1 = p1, c2 = p2;
                    byte* end = c1 + length;
                    byte* end1 = c1 + a1.Length, end2 = c2 + a2.Length;
                    for (; c1 < end && *c1 == *c2; c1++, c2++) ;
                    if (c1 == end1)
                        return c2 == end2 ? 0 : -1;
                    if (c2 == end2)
                        return 1;
                    return (*c1 < *c2) ? -1 : 1;
                }
            }
        }

        public bool Equals(byte[] a1, byte[] a2)
        {
            if (ReferenceEquals(a1, a2))
                return true;
            if (a1 == null || a2 == null || a1.Length != a2.Length)
                return false;
            unsafe
            {
                fixed (byte* p1 = a1, p2 = a2)
                {
                    byte* x1 = p1, x2 = p2;
                    int l = a1.Length;
                    for (int i = 0; i < l / 8; i++, x1 += 8, x2 += 8)
                        if (*((long*)x1) != *((long*)x2)) return false;
                    if ((l & 4) != 0) { if (*((int*)x1) != *((int*)x2)) return false; x1 += 4; x2 += 4; }
                    if ((l & 2) != 0) { if (*((short*)x1) != *((short*)x2)) return false; x1 += 2; x2 += 2; }
                    if ((l & 1) != 0) if (*((byte*)x1) != *((byte*)x2)) return false;
                    return true;
                }
            }
        }

        public bool PrefixEquals(byte[] a1, byte[] a2, int prefix)
        {
            if (ReferenceEquals(a1, a2))
                return true;
            prefix = Math.Min(prefix, Math.Max(a1.Length, a2.Length));
            var a1length = Math.Min(prefix, a1.Length);
            var a2length = Math.Min(prefix, a2.Length);
            if (a1 == null || a2 == null || a1length != a2length)
                return false;
            unsafe
            {
                fixed (byte* p1 = a1, p2 = a2)
                {
                    byte* x1 = p1, x2 = p2;
                    int l = a1length;
                    for (int i = 0; i < l / 8; i++, x1 += 8, x2 += 8)
                        if (*((long*)x1) != *((long*)x2)) return false;
                    if ((l & 4) != 0) { if (*((int*)x1) != *((int*)x2)) return false; x1 += 4; x2 += 4; }
                    if ((l & 2) != 0) { if (*((short*)x1) != *((short*)x2)) return false; x1 += 2; x2 += 2; }
                    if ((l & 1) != 0) if (*((byte*)x1) != *((byte*)x2)) return false;
                    return true;
                }
            }
        }

        public int GetHashCode(byte[] obj)
        {
            return MurMurHash3.Hash(obj);
        }
    }

    /*
    This code is public domain.

    The MurmurHash3 algorithm was created by Austin Appleby and put into the public domain.  See http://code.google.com/p/smhasher/

    This C# variant was authored by
    Elliott B. Edwards and was placed into the public domain as a gist
    Status...Working on verification (Test Suite)
    Set up to run as a LinqPad (linqpad.net) script (thus the ".Dump()" call)
    */
    public static class MurMurHash3
    {
        //Change to suit your needs
        const uint seed = 144;

        /// <summary>
        /// Hashes the bytes of <paramref name="data"/>. Gives the same result as <see cref="Hash(Stream)"/> over a
        /// stream of the same bytes, without the stream.
        /// </summary>
        public static int Hash(byte[] data)
        {
            if (data is null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            unsafe
            {
                fixed (byte* p = data)
                {
                    return Hash(p, data.Length);
                }
            }
        }

#if !NETSTANDARD2_0
        /// <summary>
        /// Hashes <paramref name="data"/>. Gives the same result as <see cref="Hash(byte[])"/> over the same bytes.
        /// </summary>
        public static unsafe int Hash(ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data)
            {
                return Hash(p, data.Length);
            }
        }
#endif

        private static unsafe int Hash(byte* data, int length)
        {
            uint h1 = seed;

            // Computed once, so the loop bound cannot overflow for arrays close to int.MaxValue bytes.
            int blockEnd = length & ~3;
            int i = 0;
            for (; i < blockEnd; i += 4)
            {
                uint k1 = (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24);
                h1 ^= MixK1(k1);
                h1 = Rotl32(h1, 13);
                h1 = unchecked(h1 * 5 + 0xe6546b64);
            }

            int tail = length & 3;
            if (tail != 0)
            {
                uint k1 = 0;
                switch (tail)
                {
                    case 3:
                        k1 |= (uint)data[i + 2] << 16;
                        goto case 2;
                    case 2:
                        k1 |= (uint)data[i + 1] << 8;
                        goto case 1;
                    case 1:
                        k1 |= data[i];
                        break;
                }

                h1 ^= MixK1(k1);
            }

            h1 ^= (uint)length;
            return unchecked((int)Fmix(h1));
        }

        public static int Hash(Stream stream)
        {
            uint h1 = seed;
            uint streamLength = 0;

            // Reads four bytes at a time into one buffer. A short chunk only happens at the end of the stream,
            // the same way BinaryReader.ReadBytes(4) behaved here before.
            byte[] chunk = new byte[4];
            using (stream)
            {
                while (true)
                {
                    int length = ReadChunk(stream, chunk);
                    if (length == 0)
                    {
                        break;
                    }

                    streamLength += (uint)length;
                    if (length == 4)
                    {
                        uint k1 = (uint)(chunk[0] | chunk[1] << 8 | chunk[2] << 16 | chunk[3] << 24);
                        h1 ^= MixK1(k1);
                        h1 = Rotl32(h1, 13);
                        h1 = unchecked(h1 * 5 + 0xe6546b64);
                    }
                    else
                    {
                        uint k1 = 0;
                        for (int j = 0; j < length; j++)
                        {
                            k1 |= (uint)chunk[j] << (8 * j);
                        }

                        h1 ^= MixK1(k1);
                        break;
                    }
                }
            }

            // finalization, magic chants to wrap it all up
            h1 ^= streamLength;
            return unchecked((int)Fmix(h1));
        }

        private static int ReadChunk(Stream stream, byte[] chunk)
        {
            int total = 0;
            while (total < chunk.Length)
            {
                int read = stream.Read(chunk, total, chunk.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        private static uint MixK1(uint k1)
        {
            const uint c1 = 0xcc9e2d51;
            const uint c2 = 0x1b873593;

            unchecked
            {
                k1 *= c1;
                k1 = Rotl32(k1, 15);
                k1 *= c2;
                return k1;
            }
        }

        private static uint Rotl32(uint x, byte r)
        {
            return (x << r) | (x >> (32 - r));
        }

        private static uint Fmix(uint h)
        {
            unchecked
            {
                h ^= h >> 16;
                h *= 0x85ebca6b;
                h ^= h >> 13;
                h *= 0xc2b2ae35;
                h ^= h >> 16;
                return h;
            }
        }
    }
}

