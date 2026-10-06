using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp;

namespace Tests;

[TestClass]
public class MultiGetVisitorTests
{
    private struct CollectingVisitor : IMultiGetValueVisitor
    {
        public Dictionary<int, string> Found;

        public void OnValue(int index, ReadOnlySpan<byte> value)
        {
            Assert.IsFalse(Found.ContainsKey(index), $"index {index} should be visited once");
            Found[index] = Encoding.UTF8.GetString(value);
        }
    }

    private struct ThrowingVisitor : IMultiGetValueVisitor
    {
        public int Calls;

        public void OnValue(int index, ReadOnlySpan<byte> value)
        {
            Calls++;
            throw new InvalidOperationException("visitor failed");
        }
    }

    private static (byte[] Packed, int[] Lengths) Pack(params string[] keys)
    {
        var lengths = new int[keys.Length];
        var buffer = new MemoryStream();
        for (int i = 0; i < keys.Length; i++)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(keys[i]);
            lengths[i] = bytes.Length;
            buffer.Write(bytes, 0, bytes.Length);
        }

        return (buffer.ToArray(), lengths);
    }

    private static Dictionary<int, string> Visit(RocksDb db, string[] keys, ColumnFamilyHandle cf = null, bool sortedInput = false)
    {
        var (packed, lengths) = Pack(keys);
        var visitor = new CollectingVisitor { Found = new Dictionary<int, string>() };
        db.MultiGet(packed, lengths, ref visitor, cf, sortedInput: sortedInput);
        return visitor.Found;
    }

    private static void WithDb(Action<string> body)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            body(dbPath);
        }
        finally
        {
            if (Directory.Exists(dbPath))
            {
                Directory.Delete(dbPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void VisitsFoundKeysByBatchIndexFromMemtableAndTableFiles()
    {
        WithDb(dbPath =>
        {
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(), dbPath);

            db.Put("flushed-a", "value-a");
            db.Put("flushed-b", "value-b");
            db.Flush(new FlushOptions().SetWaitForFlush(true));
            db.Put("memtable-c", "value-c");

            // Unsorted, with missing keys in between: indexes must follow the batch, not key order.
            var found = Visit(db, new[] { "memtable-c", "missing-1", "flushed-b", "missing-2", "flushed-a" });

            Assert.HasCount(3, found);
            Assert.AreEqual("value-c", found[0]);
            Assert.AreEqual("value-b", found[2]);
            Assert.AreEqual("value-a", found[4]);
        });
    }

    [TestMethod]
    public void SortedInputGivesTheSameResult()
    {
        WithDb(dbPath =>
        {
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(), dbPath);
            db.Put("a", "1");
            db.Put("c", "3");

            var found = Visit(db, new[] { "a", "b", "c" }, sortedInput: true);

            Assert.HasCount(2, found);
            Assert.AreEqual("1", found[0]);
            Assert.AreEqual("3", found[2]);
        });
    }

    [TestMethod]
    public void ReadsTheNamedColumnFamilyAndDefaultsToTheDefaultFamily()
    {
        WithDb(dbPath =>
        {
            var families = new ColumnFamilies { { "other", new ColumnFamilyOptions() } };
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing().SetCreateMissingColumnFamilies(), dbPath, families);
            ColumnFamilyHandle other = db.GetColumnFamily("other");

            db.Put("key", "in-default");
            db.Put("key", "in-other", other);

            Assert.AreEqual("in-default", Visit(db, new[] { "key" })[0]);
            Assert.AreEqual("in-other", Visit(db, new[] { "key" }, other)[0]);
            Assert.AreEqual("in-default", Visit(db, new[] { "key" }, db.GetDefaultColumnFamily())[0]);
        });
    }

    [TestMethod]
    public void LargeBatchUsesPooledBuffersAndFindsEveryKey()
    {
        WithDb(dbPath =>
        {
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(), dbPath);

            const int count = 1000;
            var keys = new string[count];
            for (int i = 0; i < count; i++)
            {
                keys[i] = $"key-{i:D5}";
                if (i % 3 != 0)
                {
                    db.Put(keys[i], $"value-{i}");
                }
            }

            db.Flush(new FlushOptions().SetWaitForFlush(true));

            // Two rounds: the second rents the pooled arrays again, which may still hold the first round's pointers.
            for (int round = 0; round < 2; round++)
            {
                var found = Visit(db, keys);
                for (int i = 0; i < count; i++)
                {
                    if (i % 3 == 0)
                    {
                        Assert.IsFalse(found.ContainsKey(i), $"key {i} was never written");
                    }
                    else
                    {
                        Assert.AreEqual($"value-{i}", found[i]);
                    }
                }
            }
        });
    }

    [TestMethod]
    public void EmptyAndZeroLengthKeys()
    {
        WithDb(dbPath =>
        {
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(), dbPath);
            db.Put(Array.Empty<byte>(), Encoding.UTF8.GetBytes("empty-key-value"));

            var visitor = new CollectingVisitor { Found = new Dictionary<int, string>() };
            db.MultiGet(ReadOnlySpan<byte>.Empty, ReadOnlySpan<int>.Empty, ref visitor);
            Assert.IsEmpty(visitor.Found, "an empty batch should make no call");

            var found = Visit(db, new[] { "", "absent", "" });
            Assert.HasCount(2, found);
            Assert.AreEqual("empty-key-value", found[0]);
            Assert.AreEqual("empty-key-value", found[2]);
        });
    }

    [TestMethod]
    public void RejectsKeyLengthsThatDoNotMatchThePackedKeys()
    {
        WithDb(dbPath =>
        {
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(), dbPath);
            var visitor = new CollectingVisitor { Found = new Dictionary<int, string>() };

            byte[] packed = Encoding.UTF8.GetBytes("abcdef");
            Assert.ThrowsExactly<ArgumentException>(() => db.MultiGet(packed, new[] { 3, 2 }, ref visitor));
            Assert.ThrowsExactly<ArgumentException>(() => db.MultiGet(packed, new[] { 3, 4 }, ref visitor));
            Assert.ThrowsExactly<ArgumentException>(() => db.MultiGet(packed, new[] { 7, -1 }, ref visitor));
        });
    }

    [TestMethod]
    public void VisitorExceptionPropagatesAndTheDatabaseStaysUsable()
    {
        WithDb(dbPath =>
        {
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(), dbPath);
            db.Put("a", "1");
            db.Put("b", "2");

            var (packed, lengths) = Pack("a", "b");
            var throwing = new ThrowingVisitor();
            Assert.ThrowsExactly<InvalidOperationException>(() => db.MultiGet(packed, lengths, ref throwing));

            // The pinned values of the failed batch were released; later reads and writes still work.
            db.Put("a", "1b");
            var found = Visit(db, new[] { "a", "b" });
            Assert.AreEqual("1b", found[0]);
            Assert.AreEqual("2", found[1]);
        });
    }
}
