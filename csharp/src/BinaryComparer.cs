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
#if NETSTANDARD2_0
            int length = Math.Min(a1.Length, a2.Length);
            for (int i = 0; i < length; i++)
            {
                if (a1[i] != a2[i]) return a1[i] < a2[i] ? -1 : 1;
            }
            return a1.Length == a2.Length ? 0 : (a1.Length < a2.Length ? -1 : 1);
#else
            return Math.Sign(a1.AsSpan().SequenceCompareTo(a2));
#endif
        }

        public bool Equals(byte[] a1, byte[] a2)
        {
            if (ReferenceEquals(a1, a2))
                return true;
            if (a1 == null || a2 == null || a1.Length != a2.Length)
                return false;
            return BytesEqual(a1, a2, a1.Length);
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
            return BytesEqual(a1, a2, a1length);
        }

        static bool BytesEqual(byte[] a1, byte[] a2, int length)
        {
#if NETSTANDARD2_0
            for (int i = 0; i < length; i++)
            {
                if (a1[i] != a2[i]) return false;
            }
            return true;
#else
            return a1.AsSpan(0, length).SequenceEqual(a2.AsSpan(0, length));
#endif
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

