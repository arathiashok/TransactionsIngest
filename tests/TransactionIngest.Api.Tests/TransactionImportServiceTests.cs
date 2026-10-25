using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TransactionIngest.Api.Models;
using TransactionIngest.Api.Services;
using TransactionIngest.Api.Sources;
using Xunit;

namespace TransactionIngest.Api.Tests;

public sealed class TransactionImportServiceTests
{
    private static DateTime Now => MockTransactionSource.ReferenceTimeUtc;

    [Fact]
    public async Task BaselineInsertsTransactionsAndSingleCreatedEvents()
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        var result = await database.ImportMocksAsync();

        Assert.False(result.IsBusy);
        Assert.Equal(Now, result.ReferenceTimeUtc);
        Assert.Equal(1, database.Clock.ReadCount);
        Assert.Equal(6, result.Inserted);
        Assert.All(result.Sources, source =>
        {
            Assert.Equal(SourceImportStatus.Completed, source.Status);
            Assert.Equal(2, source.Inserted);
        });

        await using var context = database.CreateContext();
        var transactions = await context.Transactions.ToListAsync();
        Assert.Equal(6, transactions.Count);
        Assert.All(transactions, transaction =>
        {
            Assert.Equal(TransactionStatus.Active, transaction.Status);
            Assert.Equal(DateTimeKind.Utc, transaction.TransactionTime.Kind);
        });
        foreach (var source in result.Sources)
        {
            Assert.Equal(new[] { 101, 102 }, transactions.Where(t => t.SourceSystem == source.SourceSystem)
                .Select(t => t.TransactionId).OrderBy(id => id));
        }
        Assert.Equal("***1111", transactions.Single(t => t.SourceSystem == "Store Uptown" && t.TransactionId == 101).CardNumber);
        Assert.Equal("***0002", transactions.Single(t => t.SourceSystem == "Store Downtown" && t.TransactionId == 101).CardNumber);
        Assert.Equal("***4444", transactions.Single(t => t.SourceSystem == "Online" && t.TransactionId == 101).CardNumber);
        Assert.All(transactions.Where(t => t.TransactionId == 102), transaction => Assert.Empty(transaction.CardNumber));

        var audits = await context.TransactionAudits.ToListAsync();
        Assert.Equal(6, audits.Count);
        Assert.All(audits, audit =>
        {
            Assert.Equal(TransactionAuditAction.Created, audit.Action);
            Assert.Null(audit.FieldName);
            Assert.Null(audit.OldValue);
            Assert.Null(audit.NewValue);
            Assert.Equal(Now, audit.ChangedAtUtc);
            Assert.Equal(DateTimeKind.Utc, audit.ChangedAtUtc.Kind);
            Assert.Single(transactions, t => t.SourceSystem == audit.SourceSystem && t.TransactionId == audit.TransactionId);
        });

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Amount FROM Transactions WHERE SourceSystem='Store Uptown' AND TransactionId=101";
        Assert.Equal(2999L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(MockScenario.Baseline, 6, 6, 6)]
    [InlineData(MockScenario.Changes, 12, 18, 9)]
    [InlineData(MockScenario.Reactivation, 12, 21, 12)]
    public async Task RepeatingSameScenarioDoesNotDuplicateTransactionsOrAudits(
        MockScenario scenario, int expectedTransactions, int expectedAudits, int expectedUnchanged)
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        await database.ImportMocksAsync();
        if (scenario != MockScenario.Baseline)
        {
            database.ScenarioState.Scenario = MockScenario.Changes;
            await database.ImportMocksAsync();
        }
        if (scenario == MockScenario.Reactivation)
        {
            database.ScenarioState.Scenario = scenario;
            await database.ImportMocksAsync();
        }

        await using var beforeContext = database.CreateContext();
        var auditsBefore = await beforeContext.TransactionAudits.AsNoTracking().OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.Action, a.FieldName, a.OldValue, a.NewValue, a.ChangedAtUtc }).ToArrayAsync();
        var result = await database.ImportMocksAsync();

        Assert.Equal(0, result.Inserted + result.Updated + result.Revoked + result.Reactivated);
        Assert.Equal(expectedUnchanged, result.Unchanged);
        await using var afterContext = database.CreateContext();
        Assert.Equal(expectedTransactions, await afterContext.Transactions.CountAsync());
        Assert.Equal(expectedAudits, await afterContext.TransactionAudits.CountAsync());
        var auditsAfter = await afterContext.TransactionAudits.AsNoTracking().OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.Action, a.FieldName, a.OldValue, a.NewValue, a.ChangedAtUtc }).ToArrayAsync();
        Assert.Equal(auditsBefore, auditsAfter);
    }

    [Fact]
    public async Task ChangesUpdatesOnlyAmountsRevokesMissingAndAcceptsDelayedTransactions()
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        await database.ImportMocksAsync();
        database.ScenarioState.Scenario = MockScenario.Changes;
        var result = await database.ImportMocksAsync();

        Assert.Equal(6, result.Inserted);
        Assert.Equal(3, result.Updated);
        Assert.Equal(3, result.Revoked);
        Assert.Equal(0, result.Reactivated);
        await using var context = database.CreateContext();
        var transactions = await context.Transactions.ToListAsync();
        Assert.Equal(12, transactions.Count);
        Assert.All(transactions.Where(t => t.TransactionId == 102), t => Assert.Equal(TransactionStatus.Revoked, t.Status));
        Assert.All(transactions.Where(t => t.TransactionId == 104), t =>
        {
            Assert.Equal(Now.AddHours(-30), t.TransactionTime);
            Assert.Equal(TransactionStatus.Active, t.Status);
        });
        var audits = await context.TransactionAudits.ToListAsync();
        Assert.Equal(18, audits.Count);
        Assert.Equal(12, audits.Count(a => a.Action == TransactionAuditAction.Created));
        var updates = audits.Where(a => a.Action == TransactionAuditAction.Updated).ToArray();
        Assert.Equal(3, updates.Length);
        Assert.All(updates, audit =>
        {
            Assert.Equal(101, audit.TransactionId);
            Assert.Equal(nameof(Transaction.Amount), audit.FieldName);
        });
        Assert.Contains(updates, a => a.SourceSystem == "Store Uptown" && a.OldValue == "29.99" && a.NewValue == "24.99");
        Assert.Contains(updates, a => a.SourceSystem == "Store Downtown" && a.OldValue == "129.99" && a.NewValue == "119.99");
        Assert.Contains(updates, a => a.SourceSystem == "Online" && a.OldValue == "69.99" && a.NewValue == "59.99");
        Assert.All(audits.Where(a => a.Action == TransactionAuditAction.Revoked), audit => AssertStatusAudit(audit, "Active", "Revoked"));
    }

    [Fact]
    public async Task ReactivationRestoresStatusAndPreservesEarlierAudits()
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        await database.ImportMocksAsync();
        database.ScenarioState.Scenario = MockScenario.Changes;
        await database.ImportMocksAsync();
        database.ScenarioState.Scenario = MockScenario.Reactivation;
        var result = await database.ImportMocksAsync();

        Assert.Equal(3, result.Reactivated);
        Assert.Equal(9, result.Unchanged);
        Assert.Equal(0, result.Inserted + result.Updated + result.Revoked);
        await using var context = database.CreateContext();
        Assert.All(await context.Transactions.ToListAsync(), transaction => Assert.Equal(TransactionStatus.Active, transaction.Status));
        var audits = await context.TransactionAudits.ToListAsync();
        Assert.Equal(21, audits.Count);
        Assert.Equal(12, audits.Count(a => a.Action == TransactionAuditAction.Created));
        Assert.Equal(3, audits.Count(a => a.Action == TransactionAuditAction.Updated));
        Assert.Equal(3, audits.Count(a => a.Action == TransactionAuditAction.Revoked));
        var reactivations = audits.Where(a => a.Action == TransactionAuditAction.Reactivated).ToArray();
        Assert.Equal(3, reactivations.Length);
        Assert.All(reactivations, audit =>
        {
            Assert.Equal(102, audit.TransactionId);
            AssertStatusAudit(audit, "Revoked", "Active");
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedBusinessFieldsAreAuditedAndCountedOnce(bool reactivate)
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        var original = Item(1);
        await database.ImportAsync([Source("A", Snapshot(original))]);
        if (reactivate)
        {
            await database.ImportAsync([Source("A", Snapshot())]);
        }
        var replacement = original with
        {
            CardNumber = "***2222", LocationCode = "OTHER", ProductName = "Mechanical Keyboard",
            Amount = -12.34m, TransactionTime = Now.AddHours(-1)
        };
        var previousCulture = CultureInfo.CurrentCulture;
        ImportResult result;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            result = await database.ImportAsync([Source("A", Snapshot(replacement))]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }

        Assert.Equal(reactivate ? 1 : 0, result.Reactivated);
        Assert.Equal(reactivate ? 0 : 1, result.Updated);
        await using var context = database.CreateContext();
        var stored = Assert.Single(await context.Transactions.ToListAsync());
        Assert.Equal(replacement, IncomingValues(stored));
        Assert.Equal(TransactionStatus.Active, stored.Status);
        var action = reactivate ? TransactionAuditAction.Reactivated : TransactionAuditAction.Updated;
        var audits = await context.TransactionAudits.Where(a => a.Action == action).ToListAsync();
        Assert.Equal(reactivate ? 6 : 5, audits.Count);
        var expectedFields = new[] { "CardNumber", "LocationCode", "ProductName", "Amount", "TransactionTime" };
        Assert.Equal(expectedFields.OrderBy(f => f), audits.Where(a => a.FieldName != "Status").Select(a => a.FieldName).OrderBy(f => f));
        Assert.Contains(audits, a => a.FieldName == "Amount" && a.OldValue == "29.99" && a.NewValue == "-12.34");
        Assert.Contains(audits, a => a.FieldName == "TransactionTime"
            && a.OldValue == original.TransactionTime.ToString("O", CultureInfo.InvariantCulture)
            && a.NewValue == replacement.TransactionTime.ToString("O", CultureInfo.InvariantCulture));
        Assert.All(audits, a => Assert.Equal(Now, a.ChangedAtUtc));
        if (reactivate)
        {
            AssertStatusAudit(Assert.Single(audits, a => a.FieldName == "Status"), "Revoked", "Active");
        }
    }

    [Theory]
    [InlineData("Exactly24Hours", -TimeSpan.TicksPerDay, true)]
    [InlineData("OlderThan24Hours", -TimeSpan.TicksPerDay - 1, false)]
    [InlineData("JustBeforeReference", -1L, true)]
    [InlineData("ExactlyReference", 0L, false)]
    [InlineData("FutureDated", 1L, false)]
    public async Task MissingTransactionsRespectHalfOpenRevocationWindow(string boundary, long offsetTicks, bool shouldRevoke)
    {
        Assert.False(string.IsNullOrEmpty(boundary));
        await using var database = await ImportTestDatabase.CreateAsync();
        await database.ImportAsync([Source("A", Snapshot(Item(1, Now.AddTicks(offsetTicks))))]);
        var result = await database.ImportAsync([Source("A", Snapshot())]);

        Assert.Equal(shouldRevoke ? 1 : 0, result.Revoked);
        await using var context = database.CreateContext();
        var stored = await context.Transactions.SingleAsync();
        Assert.Equal(shouldRevoke ? TransactionStatus.Revoked : TransactionStatus.Active, stored.Status);
        Assert.Equal(shouldRevoke ? 2 : 1, await context.TransactionAudits.CountAsync());
        var repeated = await database.ImportAsync([Source("A", Snapshot())]);
        Assert.Equal(0, repeated.Revoked);
        Assert.Equal(shouldRevoke ? 2 : 1, await context.TransactionAudits.CountAsync());
    }

    [Theory]
    [InlineData("NullSnapshot")]
    [InlineData("NullCollection")]
    [InlineData("NullRecord")]
    [InlineData("DuplicateIds")]
    [InlineData("NullCard")]
    [InlineData("FullCard")]
    [InlineData("InvalidMask")]
    [InlineData("NonAsciiCardDigits")]
    [InlineData("MissingLocation")]
    [InlineData("LongLocation")]
    [InlineData("MissingProduct")]
    [InlineData("LongProduct")]
    [InlineData("UnspecifiedTime")]
    [InlineData("LocalTime")]
    [InlineData("DefaultTime")]
    [InlineData("FractionalCents")]
    [InlineData("AmountTooLarge")]
    [InlineData("AmountTooSmall")]
    public async Task InvalidSnapshotsAreRejectedBeforeAnyChanges(string invalidCase)
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        var original = Item(1);
        await database.ImportAsync([Source("A", Snapshot(original))]);
        var invalid = InvalidSnapshot(invalidCase);
        var result = await database.ImportAsync([Source("A", invalid)]);

        Assert.Equal(SourceImportStatus.SkippedInvalid, Assert.Single(result.Sources).Status);
        Assert.NotNull(result.Sources[0].Error);
        Assert.Equal(0, result.Inserted + result.Updated + result.Revoked + result.Reactivated);
        await using var context = database.CreateContext();
        var stored = Assert.Single(await context.Transactions.ToListAsync());
        Assert.Equal(original, IncomingValues(stored));
        Assert.Equal(TransactionStatus.Active, stored.Status);
        Assert.Equal(TransactionAuditAction.Created, (await context.TransactionAudits.SingleAsync()).Action);
    }

    [Theory]
    [InlineData("Incomplete")]
    [InlineData("EmptyIncomplete")]
    [InlineData("Invalid")]
    [InlineData("Failed")]
    public async Task UnusableSourceDoesNotChangeItsRecordsOrBlockAnotherSource(string failureMode)
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        var original = Item(1);
        await database.ImportAsync([Source("A", Snapshot(original))]);
        var badSource = failureMode == "Failed"
            ? new StubSource("A", _ => Task.FromException<SourceSnapshot>(new InvalidOperationException("Simulated outage")))
            : Source("A", failureMode switch
            {
                "Incomplete" => new SourceSnapshot([Item(2)], false),
                "EmptyIncomplete" => new SourceSnapshot([], false),
                _ => InvalidSnapshot("FullCard")
            });
        var other = Item(1) with { CardNumber = "***9999", ProductName = "USB-C Docking Station", Amount = 129.99m };
        var result = await database.ImportAsync([badSource, Source("B", Snapshot(other))]);

        Assert.Equal(failureMode switch
        {
            "Failed" => SourceImportStatus.RetrievalFailed,
            "Invalid" => SourceImportStatus.SkippedInvalid,
            _ => SourceImportStatus.SkippedIncomplete
        }, result.Sources[0].Status);
        Assert.Equal(SourceImportStatus.Completed, result.Sources[1].Status);
        Assert.Equal(1, result.Inserted);
        // Only B has an authoritative empty snapshot; the same ID in A stays active.
        var next = await database.ImportAsync([badSource, Source("B", Snapshot())]);
        Assert.Equal(1, next.Revoked);
        await using var context = database.CreateContext();
        var transactions = await context.Transactions.OrderBy(t => t.SourceSystem).ToArrayAsync();
        Assert.Equal(2, transactions.Length);
        Assert.Equal(original, IncomingValues(transactions[0]));
        Assert.Equal(TransactionStatus.Active, transactions[0].Status);
        Assert.Equal(other, IncomingValues(transactions[1]));
        Assert.Equal(TransactionStatus.Revoked, transactions[1].Status);
        Assert.Equal(1, await context.TransactionAudits.CountAsync(a => a.SourceSystem == "A"));
        Assert.Equal(2, await context.TransactionAudits.CountAsync(a => a.SourceSystem == "B"));
    }

    [Fact]
    public async Task DatabaseWriteFailureRollsBackSourceAndStopsRemainingSources()
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        await database.ImportAsync([Source("Bad", Snapshot(Item(1), Item(3)))]);
        await using (var setup = database.CreateContext())
        {
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER RejectBadAudit BEFORE INSERT ON TransactionAudits
                WHEN NEW.SourceSystem = 'Bad'
                BEGIN SELECT RAISE(ABORT, 'Simulated audit failure'); END;
                """);
        }
        var remainingCalled = false;
        var remaining = new StubSource("Remaining", _ =>
        {
            remainingCalled = true;
            return Task.FromResult(Snapshot(Item(1)));
        });
        var result = await database.ImportAsync([
            Source("Good", Snapshot(Item(1))),
            Source("Bad", Snapshot(Item(1) with { Amount = 10m }, Item(2))),
            remaining
        ]);

        Assert.True(result.StoppedOnDatabaseFailure);
        Assert.Equal(new[] { SourceImportStatus.Completed, SourceImportStatus.DatabaseFailed, SourceImportStatus.NotProcessed },
            result.Sources.Select(s => s.Status));
        Assert.False(remainingCalled);
        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Updated + result.Revoked);
        await using var context = database.CreateContext();
        var badRows = await context.Transactions.Where(t => t.SourceSystem == "Bad").OrderBy(t => t.TransactionId).ToArrayAsync();
        Assert.Equal(new[] { 1, 3 }, badRows.Select(t => t.TransactionId));
        Assert.All(badRows, t =>
        {
            Assert.Equal(29.99m, t.Amount);
            Assert.Equal(TransactionStatus.Active, t.Status);
        });
        Assert.Equal(2, await context.TransactionAudits.CountAsync(a => a.SourceSystem == "Bad"));
        Assert.Equal(1, await context.TransactionAudits.CountAsync(a => a.SourceSystem == "Good"));
    }

    [Fact]
    public async Task ConcurrentImportIsRejectedAndScenarioIsFrozenUntilImportCompletes()
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSource = new StubSource("A", async cancellationToken =>
        {
            entered.SetResult(true);
            await release.Task.WaitAsync(cancellationToken);
            return Snapshot(Item(1));
        });
        var secondSource = new StubSource("B", _ =>
        {
            Assert.Equal(MockScenario.Baseline, database.ScenarioState.Scenario);
            return Task.FromResult(Snapshot(Item(1)));
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstImport = database.ImportAsync([firstSource, secondSource], timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            var overlappingSourceCalled = false;
            var overlapping = new StubSource("C", _ =>
            {
                overlappingSourceCalled = true;
                return Task.FromResult(Snapshot(Item(1)));
            });
            var rejected = await database.ImportAsync([overlapping], timeout.Token);
            Assert.True(rejected.IsBusy);
            Assert.Empty(rejected.Sources);
            Assert.False(overlappingSourceCalled);
            Assert.Throws<InvalidOperationException>(() => { database.ScenarioState.Scenario = MockScenario.Changes; });
            Assert.Equal(MockScenario.Baseline, database.ScenarioState.Scenario);
        }
        finally
        {
            release.TrySetResult(true);
            await firstImport;
        }

        Assert.Equal(2, (await firstImport).Inserted);
        database.ScenarioState.Scenario = MockScenario.Changes;
        Assert.Equal(MockScenario.Changes, database.ScenarioState.Scenario);
        var next = await database.ImportAsync([Source("A", Snapshot(Item(1)))]);
        Assert.False(next.IsBusy);
        Assert.Equal(1, next.Unchanged);
    }

    [Fact]
    public async Task CancellationPropagatesWithoutChangesAndReleasesGate()
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        await database.ImportAsync([Source("A", Snapshot(Item(1)))]);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new StubSource("A", async cancellationToken =>
        {
            entered.SetResult(true);
            await wait.Task.WaitAsync(cancellationToken);
            return Snapshot();
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var import = database.ImportAsync([source], cancellation.Token);
        await entered.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);

        database.ScenarioState.Scenario = MockScenario.Changes;
        var next = await database.ImportAsync([Source("A", Snapshot(Item(1)))]);
        Assert.False(next.IsBusy);
        Assert.Equal(1, next.Unchanged);
        await using var context = database.CreateContext();
        Assert.Equal(TransactionStatus.Active, (await context.Transactions.SingleAsync()).Status);
        Assert.Equal(1, await context.TransactionAudits.CountAsync());
    }

    private static SourceTransaction Item(int id, DateTime? time = null) =>
        new(id, "***1111", "LOC", "Wireless Mouse", 29.99m, time ?? Now.AddHours(-2));

    private static SourceSnapshot Snapshot(params SourceTransaction[] transactions) => new(Array.AsReadOnly(transactions), true);

    private static StubSource Source(string name, SourceSnapshot snapshot) => new(name, _ => Task.FromResult(snapshot));

    private static SourceTransaction IncomingValues(Transaction transaction) => new(transaction.TransactionId,
        transaction.CardNumber, transaction.LocationCode, transaction.ProductName, transaction.Amount, transaction.TransactionTime);

    private static void AssertStatusAudit(TransactionAudit audit, string oldValue, string newValue)
    {
        Assert.Equal(nameof(Transaction.Status), audit.FieldName);
        Assert.Equal(oldValue, audit.OldValue);
        Assert.Equal(newValue, audit.NewValue);
    }

    private static SourceSnapshot InvalidSnapshot(string invalidCase)
    {
        var invalid = Item(3);
        if (invalidCase == "NullSnapshot") return null!;
        if (invalidCase == "NullCollection") return new SourceSnapshot(null!, true);
        if (invalidCase == "NullRecord") return Snapshot(Item(2), null!);
        if (invalidCase == "DuplicateIds") return Snapshot(Item(2), Item(2) with { Amount = 1m });
        invalid = invalidCase switch
        {
            "NullCard" => invalid with { CardNumber = null! },
            "FullCard" => invalid with { CardNumber = "4111111111111111" },
            "InvalidMask" => invalid with { CardNumber = "***ABCD" },
            "NonAsciiCardDigits" => invalid with { CardNumber = "***１２３４" },
            "MissingLocation" => invalid with { LocationCode = " " },
            "LongLocation" => invalid with { LocationCode = new string('L', 20) },
            "MissingProduct" => invalid with { ProductName = "" },
            "LongProduct" => invalid with { ProductName = new string('P', 101) },
            "UnspecifiedTime" => invalid with { TransactionTime = DateTime.SpecifyKind(Now, DateTimeKind.Unspecified) },
            "LocalTime" => invalid with { TransactionTime = DateTime.SpecifyKind(Now, DateTimeKind.Local) },
            "DefaultTime" => invalid with { TransactionTime = DateTime.SpecifyKind(default, DateTimeKind.Utc) },
            "FractionalCents" => invalid with { Amount = 1.234m },
            "AmountTooLarge" => invalid with { Amount = 10_000_000_000_000_000m },
            "AmountTooSmall" => invalid with { Amount = -10_000_000_000_000_000m },
            _ => throw new ArgumentException("Unknown invalid fixture.", nameof(invalidCase))
        };
        return Snapshot(Item(2), invalid);
    }

    private sealed class StubSource(string sourceSystem, Func<CancellationToken, Task<SourceSnapshot>> retrieve) : ITransactionSource
    {
        public string SourceSystem { get; } = sourceSystem;
        public Task<SourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => retrieve(cancellationToken);
    }
}
