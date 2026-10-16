using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RocksDbSharp;

namespace Tests;

[TestClass]
public class CallbackAndReplicationTests
{
    private static MergeOperator CreateAppendWriter(bool throwOnFull = false) => MergeOperators.CreateWithValueWriter(
        "append-writer",
        (ReadOnlySpan<byte> key, MergeOperators.OperandsEnumerator operands, ref MergeOperators.MergeValueWriter result) =>
        {
            int length = 0;
            for (int i = 0; i < operands.Count; i++) length += operands.Get(i).Length;
            var target = result.Allocate(length);
            for (int i = 0; i < operands.Count; i++)
            {
                operands.Get(i).CopyTo(target);
                target = target.Slice(operands.Get(i).Length);
            }
            return true;
        },
        (ReadOnlySpan<byte> key, bool hasExisting, ReadOnlySpan<byte> existing, MergeOperators.OperandsEnumerator operands, ref MergeOperators.MergeValueWriter result) =>
        {
            if (throwOnFull) throw new InvalidOperationException("merge failed");

            // Starts with a small estimate and grows: earlier bytes must survive each resize.
            var target = result.Allocate(1);
            int written = 0;
            void Append(ReadOnlySpan<byte> bytes, ref MergeOperators.MergeValueWriter writer, ref Span<byte> span)
            {
                if (written + bytes.Length > span.Length) span = writer.Allocate(Math.Max(span.Length * 2, written + bytes.Length));
                bytes.CopyTo(span.Slice(written));
                written += bytes.Length;
            }
            if (hasExisting) Append(existing, ref result, ref target);
            for (int i = 0; i < operands.Count; i++) Append(operands.Get(i), ref result, ref target);
            result.Allocate(written);
            return true;
        });

    [TestMethod]
    public void ValueWriterMergeOperatorProducesFullAndPartialMerges()
    {
        TestDb.WithPath(path =>
        {
            var options = new ColumnFamilyOptions().SetMergeOperator(CreateAppendWriter());
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(), path, new ColumnFamilies(options));

            db.Put("k", "start-");
            for (int i = 0; i < 50; i++) db.Merge("k", $"{i},");
            db.Merge("new", "only");
            db.Merge("empty", "");

            var expected = "start-" + string.Concat(Enumerable.Range(0, 50).Select(i => $"{i},"));
            Assert.AreEqual(expected, db.Get("k"));
            Assert.AreEqual("only", db.Get("new"));
            Assert.AreEqual("", db.Get("empty"));

            // Compaction runs the merges in the background as well.
            db.Flush(new FlushOptions().SetWaitForFlush(true));
            db.CompactRange((byte[])null, null);
            Assert.AreEqual(expected, db.Get("k"));
        });
    }

    [TestMethod]
    public void ValueWriterMergeExceptionBecomesAFailedReadInsteadOfACrash()
    {
        TestDb.WithPath(path =>
        {
            var options = new ColumnFamilyOptions().SetMergeOperator(CreateAppendWriter(throwOnFull: true));
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(), path, new ColumnFamilies(options));

            db.Put("k", "a");
            db.Merge("k", "b");
            Assert.Throws<RocksDbException>(() => db.Get("k"));
        });
    }

    private sealed class ReverseComparator : SpanComparatorBase
    {
        public ReverseComparator() : base("reverse-bytewise") { }

        public override int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => b.SequenceCompareTo(a);
    }

    [TestMethod]
    public void SpanComparatorOrdersKeys()
    {
        TestDb.WithPath(path =>
        {
            using var db = RocksDb.Open(new DbOptions().SetCreateIfMissing().SetComparator(new ReverseComparator()), path);
            foreach (var key in new[] { "b", "a", "c", "", "ab" }) db.Put(key, key);
            db.Flush(new FlushOptions().SetWaitForFlush(true));

            var keys = new List<string>();
            using (var iterator = db.NewIterator())
            {
                for (iterator.SeekToFirst(); iterator.Valid(); iterator.Next()) keys.Add(iterator.StringKey());
            }
            CollectionAssert.AreEqual(new[] { "c", "b", "ab", "a", "" }, keys);
            Assert.AreEqual("ab", db.Get("ab"));
        });
    }

    [TestMethod]
    public void WalInspectorFindsTheFirstSequenceOfFullAndFragmentedRecords()
    {
        foreach (var compression in new[] { Compression.No, Compression.Zstd })
        foreach (int firstValueLength in new[] { 10, 100_000 })
        {
            TestDb.WithPath(path =>
            {
                ulong firstSeq;
                using (var db = RocksDb.Open(new DbOptions().SetCreateIfMissing().SetWalCompression(compression), path))
                {
                    // A record longer than one 32 KiB block is split into first, middle and last fragments.
                    db.Put("first"u8.ToArray(), new byte[firstValueLength]);
                    firstSeq = db.GetLatestSequenceNumber();
                    db.Put("second"u8, "x"u8);
                }

                var copy = Path.Combine(path, "wal-copy");
                Directory.CreateDirectory(copy);
                foreach (var log in Directory.GetFiles(path, "*.log"))
                {
                    File.Copy(log, Path.Combine(copy, Path.GetFileName(log)));
                }

                var result = RocksDbWalInspector.GetFirstSequenceNumbers(copy);
                Assert.IsTrue(result.Values.Contains(firstSeq), $"{compression}, first value of {firstValueLength} bytes: got {string.Join(",", result.Values)}, expected {firstSeq}");
            });
        }
    }

    [TestMethod]
    public async Task FileSignatureIsUnchangedForEmptyShortAndLargeFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            foreach (int length in new[] { 0, 10, 200_000 })
            {
                var data = new byte[length];
                new Random(length).NextBytes(data);
                var file = Path.Combine(dir, $"{length}.sst");
                File.WriteAllBytes(file, data);

                foreach (int tail in new[] { 4096, 1 << 20 })
                {
                    var tailBytes = data.Skip(Math.Max(0, length - tail)).ToArray();
                    var expected = Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes((long)length).Concat(tailBytes).ToArray()));
                    Assert.AreEqual(expected, await ReplicationDelta.ComputeFileSignatureAsync(file, tail));
                }
            }

            // Concurrent hashing rents a separate buffer per call.
            var files = Directory.GetFiles(dir);
            var single = await Task.WhenAll(files.Select(f => ReplicationDelta.ComputeFileSignatureAsync(f)));
            var parallel = await Task.WhenAll(Enumerable.Range(0, 8).SelectMany(_ => files).Select(f => ReplicationDelta.ComputeFileSignatureAsync(f)));
            CollectionAssert.AreEqual(Enumerable.Range(0, 8).SelectMany(_ => single).ToArray(), parallel);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A transport whose heartbeats to some peers never complete, and ignore the cancellation token.</summary>
    private sealed class FakeTransport : IPeerTransport
    {
        public readonly ConcurrentDictionary<string, int> Heartbeats = new();
        public readonly HashSet<string> Hanging = new();

        public Task<RequestVoteResult?> SendRequestVoteAsync(ClusterMember peer, long term, string candidateId, ulong lastLogSeq, long lastLogTerm, CancellationToken ct)
            => Task.FromResult<RequestVoteResult?>(null);

        public Task<AppendEntriesResult?> SendHeartbeatAsync(ClusterMember peer, HeartbeatPayload payload, CancellationToken ct)
        {
            Heartbeats.AddOrUpdate(peer.NodeId, 1, (_, n) => n + 1);
            return Hanging.Contains(peer.NodeId)
                ? new TaskCompletionSource<AppendEntriesResult?>().Task
                : Task.FromResult<AppendEntriesResult?>(new AppendEntriesResult(payload.Term, true, payload.PrevLogSeq, null));
        }
    }

    private static (RaftClusterNode Node, RocksDb Db) CreateBootstrapLeader(string path, IPeerTransport transport, int heartbeatMs, int electionMinMs)
    {
        Directory.CreateDirectory(path);
        var db = RocksDb.Open(new DbOptions().SetCreateIfMissing(), Path.Combine(path, "db"));
        var config = new RaftConfig(
            "leader",
            "http://leader",
            new[] { new ClusterMember("slow", "http://slow"), new ClusterMember("fast", "http://fast") },
            bootstrapPrimary: true,
            electionTimeoutMinMs: electionMinMs,
            electionTimeoutMaxMs: electionMinMs * 2,
            heartbeatIntervalMs: heartbeatMs);
        var state = new RaftPersistentState(Path.Combine(path, "raft"));
        return (new RaftClusterNode(config, state, db, transport), db);
    }

    [TestMethod]
    public async Task SlowPeerHasAtMostOneHeartbeatInFlightAndDoesNotDelayHealthyPeers()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var transport = new FakeTransport();
        transport.Hanging.Add("slow");
        var (node, db) = CreateBootstrapLeader(dir, transport, heartbeatMs: 20, electionMinMs: 400);
        try
        {
            node.Start();
            await Task.Delay(1500);
            node.Dispose();
            // Let the cancelled leader loop and its last heartbeats finish before the database closes.
            await Task.Delay(300);

            int slow = transport.Heartbeats.GetValueOrDefault("slow");
            int fast = transport.Heartbeats.GetValueOrDefault("fast");

            // Without the bound the slow peer would get one request per 20 ms interval, about 75. With it, a new
            // request goes out only after the 400 ms deadline of the previous one.
            Assert.IsInRange(2, 6, slow, $"slow peer got {slow} heartbeats");
            Assert.IsGreaterThan(30, fast, $"fast peer got only {fast} heartbeats");
            Assert.IsFalse(node.Peers.Single(p => p.Member.NodeId == "slow").IsReachable);
            Assert.IsTrue(node.Peers.Single(p => p.Member.NodeId == "fast").IsReachable);
        }
        finally
        {
            db.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void CommitIndexFollowsTheQuorumOfFollowerProgress()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var (node, db) = CreateBootstrapLeader(dir, new FakeTransport(), heartbeatMs: 50, electionMinMs: 500);
        try
        {
            for (int i = 0; i < 10; i++) db.Put($"k{i}", "v");
            ulong latest = db.GetLatestSequenceNumber();

            // Three members, quorum two: the leader plus the furthest follower decide the commit index.
            node.RecordFollowerProgress("slow", 3);
            Assert.AreEqual(3UL, node.CommitSeq);
            node.RecordFollowerProgress("fast", latest - 2);
            Assert.AreEqual(latest - 2, node.CommitSeq);
            // A lower or repeated report never moves the commit index back.
            node.RecordFollowerProgress("fast", 1);
            node.RecordFollowerProgress("slow", latest - 2);
            Assert.AreEqual(latest - 2, node.CommitSeq);
            node.RecordFollowerProgress("slow", latest);
            Assert.AreEqual(latest, node.CommitSeq);

            Assert.AreSame(node.Peers, node.Peers);
            Assert.HasCount(2, node.Peers);
        }
        finally
        {
            node.Dispose();
            db.Dispose();
            Directory.Delete(dir, recursive: true);
        }
    }
}
