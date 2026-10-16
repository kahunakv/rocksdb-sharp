using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp;

namespace Tests;

[TestClass]
public class PinnedReadTests
{
    private sealed class LengthDeserializer : ISpanDeserializer<int>
    {
        public int Deserialize(ReadOnlySpan<byte> buffer) => buffer.Length;
    }

    private sealed class ThrowingDeserializer : ISpanDeserializer<int>
    {
        public int Deserialize(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("deserializer failed");
    }

    [TestMethod]
    public void MissingAndEmptyValuesStayDistinctOnEveryReadPath()
    {
        TestDb.WithPath(path =>
        {
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(), path);
            byte[] empty = "empty"u8.ToArray();
            byte[] missing = "missing"u8.ToArray();
            db.Put(empty, Array.Empty<byte>());

            CollectionAssert.AreEqual(Array.Empty<byte>(), db.Get(empty));
            Assert.IsNull(db.Get(missing));
            CollectionAssert.AreEqual(Array.Empty<byte>(), db.Get((ReadOnlySpan<byte>)empty));
            Assert.IsNull(db.Get((ReadOnlySpan<byte>)missing));

            Assert.AreEqual(string.Empty, db.Get("empty"));
            Assert.IsNull(db.Get("missing"));

            Assert.IsTrue(db.HasKey(empty, empty.Length));
            Assert.IsFalse(db.HasKey(missing, missing.Length));
            Assert.IsTrue(db.HasKey((ReadOnlySpan<byte>)empty));
            Assert.IsFalse(db.HasKey((ReadOnlySpan<byte>)missing));
            Assert.IsTrue(db.HasKey("empty"));
            Assert.IsFalse(db.HasKey("missing"));

            Assert.AreEqual(0, db.Get((ReadOnlySpan<byte>)empty, new LengthDeserializer()));
            Assert.AreEqual(0, db.Get((ReadOnlySpan<byte>)empty, (Stream s) => (int)s.Length));
            Assert.IsTrue(db.GetFixedSizeValue(empty, Span<byte>.Empty));
            Assert.IsFalse(db.GetFixedSizeValue(missing, new byte[4]));

            var buffer = new byte[4];
            Assert.AreEqual(0, db.Get(empty, buffer, 0, buffer.Length));
            Assert.AreEqual(-1, db.Get(missing, buffer, 0, buffer.Length));
        });
    }

    [TestMethod]
    public void LargeFlushedValuesReadTheSameThroughEveryPath()
    {
        TestDb.WithPath(path =>
        {
            var families = new ColumnFamilies { { "other", new ColumnFamilyOptions() } };
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing().SetCreateMissingColumnFamilies(), path, families);
            var other = db.GetColumnFamily("other");

            var value = new byte[1 << 20];
            new Random(7).NextBytes(value);
            byte[] key = "large"u8.ToArray();

            foreach (var cf in new[] { null, other })
            {
                db.Put(key, value, cf);
                db.Flush(new FlushOptions().SetWaitForFlush(true));

                CollectionAssert.AreEqual(value, db.Get(key, cf));
                CollectionAssert.AreEqual(value, db.Get((ReadOnlySpan<byte>)key, cf));
                Assert.AreEqual(value.Length, db.Get((ReadOnlySpan<byte>)key, new LengthDeserializer(), cf));
                Assert.AreEqual(value.Length, db.Get((ReadOnlySpan<byte>)key, (Stream s) => (int)s.Length, cf));

                var fixedSize = new byte[value.Length];
                Assert.IsTrue(db.GetFixedSizeValue(key, fixedSize, cf));
                CollectionAssert.AreEqual(value, fixedSize);

                // A partial copy into the middle of a buffer still reports the full length.
                var partial = new byte[110];
                Assert.AreEqual(value.Length, db.Get(key, partial, 10, 100, cf));
                CollectionAssert.AreEqual(value.Take(100).ToArray(), partial.Skip(10).Take(100).ToArray());
                Assert.AreEqual(0, partial[0]);
            }
        });
    }

    [TestMethod]
    public void FixedSizeMismatchAndDeserializerFailuresThrow()
    {
        TestDb.WithPath(path =>
        {
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(), path);
            db.Put("k"u8, "1234"u8);

            Assert.Throws<Exception>(() => db.GetFixedSizeValue("k"u8, new byte[3]));
            Assert.ThrowsExactly<InvalidOperationException>(() => db.Get("k"u8, new ThrowingDeserializer()));
            Assert.ThrowsExactly<InvalidOperationException>(() => db.Get<int>("k"u8, (Stream s) => throw new InvalidOperationException()));

            // The failures released the pinned value: the database still reads and closes normally.
            CollectionAssert.AreEqual("1234"u8.ToArray(), db.Get("k"u8.ToArray()));
        });
    }

    [TestMethod]
    public void PinnedReadsHonorSnapshotsAndMergeResults()
    {
        var append = MergeOperators.Create(
            "append",
            (ReadOnlySpan<byte> key, MergeOperators.OperandsEnumerator operands, out bool success) =>
            {
                success = true;
                var result = new System.Collections.Generic.List<byte>();
                for (int i = 0; i < operands.Count; i++) result.AddRange(operands.Get(i).ToArray());
                return result.ToArray();
            },
            (ReadOnlySpan<byte> key, bool hasExisting, ReadOnlySpan<byte> existing, MergeOperators.OperandsEnumerator operands, out bool success) =>
            {
                success = true;
                var result = existing.ToArray().ToList();
                for (int i = 0; i < operands.Count; i++) result.AddRange(operands.Get(i).ToArray());
                return result.ToArray();
            });

        TestDb.WithPath(path =>
        {
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(), path, new ColumnFamilies(new ColumnFamilyOptions().SetMergeOperator(append)));
            db.Put("k"u8, "a"u8);
            using var snapshot = db.CreateSnapshot();
            db.Merge("k"u8, "b"u8);
            db.Merge("k"u8, "c"u8);

            Assert.AreEqual("abc", db.Get("k"));
            Assert.AreEqual(3, db.Get("k"u8, new LengthDeserializer()));

            var atSnapshot = new ReadOptions().SetSnapshot(snapshot);
            Assert.AreEqual("a", db.Get("k", readOptions: atSnapshot));
            CollectionAssert.AreEqual("a"u8.ToArray(), db.Get("k"u8, readOptions: atSnapshot));
            Assert.IsFalse(db.HasKey("later"u8, readOptions: atSnapshot));
        });
    }
}
