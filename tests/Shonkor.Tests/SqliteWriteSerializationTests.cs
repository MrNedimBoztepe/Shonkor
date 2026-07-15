// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;
using Shonkor.Infrastructure.Storage;

namespace Shonkor.Tests;

/// <summary>
/// Writes are serialized through the in-process write lock (#246).
///
/// <para>
/// <b>Why these tests are shaped the way they are.</b> The failure they defend against —
/// <i>"cannot start a transaction within a transaction"</i> — cannot be reproduced deterministically on a fast
/// machine: it needs a writer's <c>COMMIT</c> to fail with <c>SQLITE_BUSY</c> and the following rollback to
/// <i>also</i> fail against a still-locked database, which only happens under real contention on a slow box.
/// (It surfaced only on the 2-core CI runner, #209.) A synthetic "return a dirty connection to the pool" probe
/// does not reproduce it, because an uncontended dispose rolls back cleanly.
/// </para>
/// <para>
/// So rather than fake a reproduction, these pin the <b>fix</b>: writers take a mutual-exclusion lock, so at
/// most one is ever inside a SQLite transaction, so the concurrent-writer precondition for the bug cannot
/// occur. The first test proves the lock is real; the second proves it is actually <b>wired into</b> a write
/// method (a regression that drops the <c>using var _writeScope</c> line fails it). The end-to-end concurrency
/// guard remains <c>ParserAndStorageTests.SqliteGraphStorage_ShouldHandleConcurrentOperationsWithoutCorruption</c>,
/// exercised for real on the slow CI leg.
/// </para>
/// </summary>
public class SqliteWriteSerializationTests
{
    private static SqliteGraphStorageProvider NewStorage()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shonkor_wl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return new SqliteGraphStorageProvider(Path.Combine(dir, "g.db"));
    }

    [Fact]
    public async Task TheWriteLock_IsAMutualExclusionLock_TheSecondAcquireWaitsForTheFirstToRelease()
    {
        using var storage = NewStorage();
        await storage.InitializeAsync();

        var first = await storage.AcquireWriteLockAsync(CancellationToken.None);

        // A second acquisition must NOT complete while the first is held.
        var second = storage.AcquireWriteLockAsync(CancellationToken.None);
        var wonRace = await Task.WhenAny(second, Task.Delay(500));
        Assert.NotSame(second, wonRace); // the delay won — the second acquire is genuinely blocked

        first.Dispose();

        // Now it proceeds.
        var acquired = await Task.WhenAny(second, Task.Delay(2000));
        Assert.Same(second, acquired);
        (await second).Dispose();
    }

    /// <summary>
    /// The wiring test: while the write lock is held, a real <c>UpsertNodesAsync</c> must block — proving it
    /// takes the lock rather than merely that the lock exists. Drop the <c>using var _writeScope</c> line from
    /// the method and this fails, because the upsert would run straight through.
    /// </summary>
    [Fact]
    public async Task AWriteMethod_ActuallyWaitsOnTheLock_NotJustDeclaresIt()
    {
        using var storage = NewStorage();
        await storage.InitializeAsync();

        var held = await storage.AcquireWriteLockAsync(CancellationToken.None);

        var upsert = storage.UpsertNodesAsync(
            [new GraphNode { Id = "n", Type = "Class", Name = "N", Content = "x" }]);

        // With the lock held, the upsert cannot reach its transaction — it is parked on WaitAsync.
        var raced = await Task.WhenAny(upsert, Task.Delay(500));
        Assert.NotSame(upsert, raced);

        held.Dispose();

        // Released — the upsert completes, and the node lands.
        await upsert.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, (await storage.GetStatisticsAsync()).TotalNodes);
    }

    [Fact]
    public async Task TheScope_IsIdempotent_DisposingTwiceReleasesOnce()
    {
        using var storage = NewStorage();
        await storage.InitializeAsync();

        var scope = await storage.AcquireWriteLockAsync(CancellationToken.None);
        scope.Dispose();
        scope.Dispose(); // must not over-release (which would let two writers in at once)

        // The lock is free exactly once: one acquire succeeds immediately, a second then blocks.
        var a = await storage.AcquireWriteLockAsync(CancellationToken.None);
        var b = storage.AcquireWriteLockAsync(CancellationToken.None);
        Assert.NotSame(b, await Task.WhenAny(b, Task.Delay(300)));
        a.Dispose();
        (await b).Dispose();
    }
}
