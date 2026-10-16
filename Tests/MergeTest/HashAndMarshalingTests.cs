using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp;

namespace Tests;

[TestClass]
public class HashAndMarshalingTests
{
    /// <summary>The stream-based MurmurHash3 exactly as the library implemented it before it read bytes directly.</summary>
    private static int ReferenceHash(byte[] data)
    {
        const uint c1 = 0xcc9e2d51;
        const uint c2 = 0x1b873593;
        static uint Rotl32(uint x, byte r) => (x << r) | (x >> (32 - r));

        uint h1 = 144;
        uint k1;
        uint streamLength = 0;
        using (var reader = new BinaryReader(new MemoryStream(data)))
        {
            byte[] chunk = reader.ReadBytes(4);
            while (chunk.Length > 0)
            {
                streamLength += (uint)chunk.Length;
                switch (chunk.Length)
                {
                    case 4:
                        k1 = (uint)(chunk[0] | chunk[1] << 8 | chunk[2] << 16 | chunk[3] << 24);
                        k1 *= c1; k1 = Rotl32(k1, 15); k1 *= c2;
                        h1 ^= k1; h1 = Rotl32(h1, 13); h1 = h1 * 5 + 0xe6546b64;
                        break;
                    case 3:
                        k1 = (uint)(chunk[0] | chunk[1] << 8 | chunk[2] << 16);
                        k1 *= c1; k1 = Rotl32(k1, 15); k1 *= c2; h1 ^= k1;
                        break;
                    case 2:
                        k1 = (uint)(chunk[0] | chunk[1] << 8);
                        k1 *= c1; k1 = Rotl32(k1, 15); k1 *= c2; h1 ^= k1;
                        break;
                    case 1:
                        k1 = chunk[0];
                        k1 *= c1; k1 = Rotl32(k1, 15); k1 *= c2; h1 ^= k1;
                        break;
                }
                chunk = reader.ReadBytes(4);
            }
        }

        h1 ^= streamLength;
        h1 ^= h1 >> 16; h1 *= 0x85ebca6b; h1 ^= h1 >> 13; h1 *= 0xc2b2ae35; h1 ^= h1 >> 16;
        return unchecked((int)h1);
    }

    [TestMethod]
    public void DirectHashMatchesTheStreamHashForEveryLengthAndTail()
    {
        var random = new Random(1234);
        var inputs = new List<byte[]>();
        for (int length = 0; length <= 1100; length++)
        {
            var data = new byte[length];
            random.NextBytes(data);
            inputs.Add(data);
        }

        // Every byte has the high bit set, which catches sign extension mistakes.
        inputs.Add(Enumerable.Repeat((byte)0xFF, 7).ToArray());
        inputs.Add(Enumerable.Repeat((byte)0x80, 13).ToArray());
        var big = new byte[65_537];
        random.NextBytes(big);
        inputs.Add(big);

        foreach (var data in inputs)
        {
            int expected = ReferenceHash(data);
            Assert.AreEqual(expected, MurMurHash3.Hash(data), $"byte[] hash, length {data.Length}");
            Assert.AreEqual(expected, MurMurHash3.Hash((ReadOnlySpan<byte>)data), $"span hash, length {data.Length}");
            Assert.AreEqual(expected, MurMurHash3.Hash(new MemoryStream(data)), $"stream hash, length {data.Length}");
            Assert.AreEqual(expected, BinaryComparer.Default.GetHashCode(data), $"comparer hash, length {data.Length}");
        }
    }

    [TestMethod]
    public void HashKeepsItsKnownValuesAndRejectsNull()
    {
        // Values of the original implementation; a change here changes every persisted or shared hash.
        Assert.AreEqual(ReferenceHash(Array.Empty<byte>()), MurMurHash3.Hash(Array.Empty<byte>()));
        Assert.AreEqual(ReferenceHash("hello"u8.ToArray()), MurMurHash3.Hash("hello"u8.ToArray()));
        Assert.ThrowsExactly<ArgumentNullException>(() => MurMurHash3.Hash((byte[])null));
    }

    [TestMethod]
    public void StreamHashReadsShortReadsLikeBinaryReader()
    {
        var data = new byte[37];
        new Random(5).NextBytes(data);
        Assert.AreEqual(ReferenceHash(data), MurMurHash3.Hash(new OneByteStream(data)));
    }

    private sealed class OneByteStream : MemoryStream
    {
        public OneByteStream(byte[] data) : base(data) { }

        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
    }

    public static IEnumerable<object[]> StringCases()
    {
        yield return new object[] { "", "" };
        yield return new object[] { "key", "" };
        yield return new object[] { "", "value" };
        yield return new object[] { "ascii-key", "ascii-value" };
        yield return new object[] { "clé-ñ-中文-😀", "valeur-ü-日本語-🎉" };
        // Around the stack buffer limit of 512 encoded bytes, counting key and value together.
        yield return new object[] { new string('k', 256), new string('v', 256) };
        yield return new object[] { new string('k', 256), new string('v', 257) };
        yield return new object[] { new string('é', 300), new string('中', 300) };
        yield return new object[] { new string('x', 20_000), new string('y', 100_000) };
    }

    [TestMethod]
    [DynamicData(nameof(StringCases))]
    public void StringPutGetHasKeyMergeAndRemoveRoundTrip(string key, string value)
    {
        TestDb.WithPath(path =>
        {
            var families = new ColumnFamilies { { "other", new ColumnFamilyOptions() } };
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing().SetCreateMissingColumnFamilies(), path, families);
            var other = db.GetColumnFamily("other");

            foreach (var cf in new[] { null, other })
            {
                db.Put(key, value, cf);
                Assert.AreEqual(value, db.Get(key, cf));
                Assert.IsTrue(db.HasKey(key, cf));
                CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(value), db.Get(Encoding.UTF8.GetBytes(key), cf));

                db.Remove(key, cf);
                Assert.IsNull(db.Get(key, cf));
                Assert.IsFalse(db.HasKey(key, cf));
            }

            // A custom encoding is used for both the key and the value.
            db.Put(key, value, encoding: Encoding.Unicode);
            Assert.AreEqual(value, db.Get(key, encoding: Encoding.Unicode));
            CollectionAssert.AreEqual(Encoding.Unicode.GetBytes(value), db.Get(Encoding.Unicode.GetBytes(key)));
        });
    }

    [TestMethod]
    [DynamicData(nameof(StringCases))]
    public void StringBatchesSeeksAndSstFilesRoundTrip(string key, string value)
    {
        TestDb.WithPath(path =>
        {
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(), path);

            using (var batch = new WriteBatch())
            {
                batch.Put(key, value);
                db.Write(batch);
            }
            Assert.AreEqual(value, db.Get(key));

            using (var indexed = new WriteBatchWithIndex())
            {
                indexed.Put(key + "-wi", value);
                Assert.AreEqual(value, indexed.Get(key + "-wi"));
                Assert.AreEqual(value, indexed.Get(db, key));
            }

            using (var iterator = db.NewIterator())
            {
                iterator.Seek(key);
                Assert.IsTrue(iterator.Valid());
                Assert.AreEqual(key, iterator.StringKey());
                Assert.AreEqual(value, iterator.StringValue());

                iterator.SeekForPrev(key);
                Assert.IsTrue(iterator.Valid());
                Assert.AreEqual(key, iterator.StringKey());
            }

            var sstPath = Path.Combine(path, "ingest.sst");
            using (var writer = new SstFileWriter())
            {
                writer.Open(sstPath);
                writer.Add(key + "-sst", value);
                writer.Finish();
            }
            db.IngestExternalFiles(new[] { sstPath }, new IngestExternalFileOptions());
            Assert.AreEqual(value, db.Get(key + "-sst"));
        });
    }

    [TestMethod]
    public void ArrayMultiGetReturnsHitsMissesEmptyValuesAndDuplicatesInOrder()
    {
        TestDb.WithPath(path =>
        {
            var families = new ColumnFamilies { { "other", new ColumnFamilyOptions() } };
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing().SetCreateMissingColumnFamilies(), path, families);
            var other = db.GetColumnFamily("other");

            foreach (int count in new[] { 1, 16, 128, 129, 1024 })
            {
                foreach (var cf in new[] { null, other })
                {
                    var keys = new byte[count][];
                    for (int i = 0; i < count; i++)
                    {
                        // Every third key is missing, every fifth value is empty, and the last key repeats the first.
                        keys[i] = Encoding.UTF8.GetBytes($"key-{count}-{i:D5}");
                        if (i % 3 != 0)
                        {
                            db.Put(keys[i], i % 5 == 0 ? Array.Empty<byte>() : Encoding.UTF8.GetBytes($"value-{i}"), cf);
                        }
                    }
                    if (count > 1)
                    {
                        keys[count - 1] = keys[1];
                    }

                    var cfs = cf is null ? null : Enumerable.Repeat(cf, count).ToArray();
                    AssertMultiGet(db, keys, cfs, cf);
                }
            }

            db.Flush(new FlushOptions().SetWaitForFlush(true));
            var flushedKeys = new[] { Encoding.UTF8.GetBytes("key-16-00001"), Encoding.UTF8.GetBytes("missing") };
            AssertMultiGet(db, flushedKeys, null, null);
        });
    }

    private static void AssertMultiGet(RocksDb db, byte[][] keys, ColumnFamilyHandle[] cfs, ColumnFamilyHandle cf)
    {
        var result = db.MultiGet(keys, cfs);
        Assert.HasCount(keys.Length, result);
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.AreSame(keys[i], result[i].Key);
            var expected = db.Get(keys[i], cf);
            if (expected is null)
            {
                Assert.IsNull(result[i].Value, $"key {i} should be missing");
            }
            else
            {
                CollectionAssert.AreEqual(expected, result[i].Value, $"key {i}");
            }
        }

        var stringKeys = keys.Select(k => Encoding.UTF8.GetString(k)).ToArray();
        var stringResult = db.MultiGet(stringKeys, cfs);
        for (int i = 0; i < keys.Length; i++)
        {
            Assert.AreEqual(stringKeys[i], stringResult[i].Key);
            Assert.AreEqual(db.Get(stringKeys[i], cf), stringResult[i].Value, $"string key {i}");
        }
    }

    [TestMethod]
    public void ArrayMultiGetAcrossColumnFamiliesAndWithNonAsciiKeys()
    {
        TestDb.WithPath(path =>
        {
            var families = new ColumnFamilies { { "other", new ColumnFamilyOptions() } };
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing().SetCreateMissingColumnFamilies(), path, families);
            var other = db.GetColumnFamily("other");
            var defaultCf = db.GetDefaultColumnFamily();

            db.Put("ключ", "значение");
            db.Put("ключ", "другое", other);

            // Different families per key take the multi_get_cf path.
            var mixed = db.MultiGet(new[] { "ключ", "ключ", "nope" }, new[] { defaultCf, other, other });
            Assert.AreEqual("значение", mixed[0].Value);
            Assert.AreEqual("другое", mixed[1].Value);
            Assert.IsNull(mixed[2].Value);

            var mixedBytes = db.MultiGet(
                new[] { Encoding.UTF8.GetBytes("ключ"), Encoding.UTF8.GetBytes("ключ") },
                new[] { defaultCf, other });
            Assert.AreEqual("значение", Encoding.UTF8.GetString(mixedBytes[0].Value));
            Assert.AreEqual("другое", Encoding.UTF8.GetString(mixedBytes[1].Value));

            Assert.HasCount(0, db.MultiGet(Array.Empty<byte[]>()));
            Assert.HasCount(0, db.MultiGet(Array.Empty<string>()));
        });
    }

    [TestMethod]
    public void NativeArrayMultiGetHonorsKeyLengthsAndLargeKeys()
    {
        TestDb.WithPath(path =>
        {
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(), path);
            var bigKey = Encoding.UTF8.GetBytes(new string('k', 10_000));
            db.Put(bigKey, Encoding.UTF8.GetBytes("big"));
            db.Put(Encoding.UTF8.GetBytes("abc"), Encoding.UTF8.GetBytes("prefix"));

            // Only the first three bytes of the second key are used.
            var result = Native.Instance.rocksdb_multi_get(
                db.Handle,
                new ReadOptions().Handle,
                new[] { bigKey, Encoding.UTF8.GetBytes("abcdef") },
                keyLengths: new ulong[] { (ulong)bigKey.Length, 3 });

            Assert.AreEqual("big", Encoding.UTF8.GetString(result[0].Value));
            Assert.AreEqual("prefix", Encoding.UTF8.GetString(result[1].Value));
        });
    }
}
