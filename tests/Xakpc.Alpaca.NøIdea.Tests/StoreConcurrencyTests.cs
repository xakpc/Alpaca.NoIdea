using Xakpc.Alpaca.NøIdea.Storage;

namespace Xakpc.Alpaca.NøIdea.Tests;

/// <summary>
/// Two loops write to the audit, so the store must accept two writers.
/// </summary>
/// <remarks>
/// The store opens a connection for each operation. Without write-ahead logging the second of
/// two concurrent writers fails immediately, and a busy timeout set on one connection does not
/// apply to the next one. Both settings are therefore a correctness requirement of the
/// hard-exit loop, not a performance option. See <c>.lode/trading/hard-exit-loop.md</c>.
/// </remarks>
public sealed class StoreConcurrencyTests
{
    [Fact]
    public async Task TheDatabaseUsesWriteAheadLogging()
    {
        await WithStoreAsync(async store =>
        {
            var mode = await store.JournalModeAsync(CancellationToken.None);

            Assert.Equal("wal", mode.ToLowerInvariant());
        });
    }

    [Fact]
    public async Task ConcurrentWritersBothSucceed()
    {
        await WithStoreAsync(async store =>
        {
            // One writer for each loop, plus enough repeats that an unserialised pair of
            // connections would collide.
            var writers = Enumerable.Range(0, 2).Select(writer => Task.Run(async () =>
            {
                for (var i = 0; i < 25; i++)
                {
                    await store.RecordEquityAsync(
                        (writer * 1000) + i, "live", 100_000m + i, 99_000m,
                        CancellationToken.None);
                }
            }));

            // The assertion is that this does not throw SqliteException: database is locked.
            await Task.WhenAll(writers);

            var counts = await store.AuditRowCountsAsync(CancellationToken.None);

            Assert.Equal(50, counts["equity_snapshots"]);
        });
    }

    private static async Task WithStoreAsync(Func<TradingStore, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"store-concurrency-{Guid.NewGuid():N}.db");

        try
        {
            var store = new TradingStore(TradingStore.ConnectionStringForFile(path));
            await store.CreateSchemaAsync(CancellationToken.None);

            await test(store);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            foreach (var file in new[] { path, $"{path}-wal", $"{path}-shm" })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }
}
