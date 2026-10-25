using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TransactionIngest.Api.Data;
using TransactionIngest.Api.Services;
using TransactionIngest.Api.Sources;

namespace TransactionIngest.Api.Tests;

internal sealed class ImportTestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:;Foreign Keys=True");
    private DbContextOptions<TransactionIngestDbContext> _options = null!;

    public MockScenarioState ScenarioState { get; } = new();
    public ControlledTimeProvider Clock { get; } = new();

    public static async Task<ImportTestDatabase> CreateAsync()
    {
        var database = new ImportTestDatabase();
        try
        {
            await database._connection.OpenAsync();
            database._options = new DbContextOptionsBuilder<TransactionIngestDbContext>()
                .UseSqlite(database._connection).Options;
            await using var context = database.CreateContext();
            await context.Database.MigrateAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public TransactionIngestDbContext CreateContext() => new(_options);

    public async Task<ImportResult> ImportAsync(
        IEnumerable<ITransactionSource> sources, CancellationToken cancellationToken = default)
    {
        // Each invocation gets its own context, matching separate application scopes.
        await using var context = CreateContext();
        var service = new TransactionImportService(context, sources, Clock, ScenarioState,
            NullLogger<TransactionImportService>.Instance);
        return await service.ImportAsync(cancellationToken);
    }

    public Task<ImportResult> ImportMocksAsync() => ImportAsync(
    [
        new MockTransactionSource("Store Uptown", ScenarioState),
        new MockTransactionSource("Store Downtown", ScenarioState),
        new MockTransactionSource("Online", ScenarioState)
    ]);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

internal sealed class ControlledTimeProvider : TimeProvider
{
    private int _readCount;

    public DateTimeOffset UtcNow { get; set; } = new(MockTransactionSource.ReferenceTimeUtc);
    public int ReadCount => Volatile.Read(ref _readCount);

    public override DateTimeOffset GetUtcNow()
    {
        Interlocked.Increment(ref _readCount);
        return UtcNow;
    }
}
