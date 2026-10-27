using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TransactionIngest.Api.Controllers;
using TransactionIngest.Api.Models;
using TransactionIngest.Api.Sources;
using Xunit;

namespace TransactionIngest.Api.Tests;

public sealed class AuditControllerTests
{
    [Fact]
    public async Task GlobalAuditHistoryIsEmptyBeforeImport()
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        await using var context = database.CreateContext();

        var result = await new AuditsController(context).Get(CancellationToken.None);

        Assert.Empty(Audits(result));
    }

    [Fact]
    public async Task GlobalAuditHistoryReturnsPersistedFieldsAllSourcesAndActionsNewestFirst()
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        await ImportLifecycleAsync(database);
        await using var context = database.CreateContext();

        var events = Audits(await new AuditsController(context).Get(CancellationToken.None));
        var stored = await context.TransactionAudits.AsNoTracking()
            .OrderByDescending(audit => audit.ChangedAtUtc).ThenByDescending(audit => audit.Id).ToArrayAsync();

        Assert.Equal(21, events.Count);
        Assert.Equal(new[] { "Online", "Store Downtown", "Store Uptown" },
            events.Select(audit => audit.SourceSystem).Distinct().OrderBy(source => source));
        Assert.Equal(Enum.GetValues<TransactionAuditAction>(), events.Select(audit => audit.Action).Distinct().OrderBy(action => action));
        Assert.Equal(stored.Select(audit => audit.Id), events.Select(audit => audit.Id));
        Assert.All(events.GroupBy(audit => audit.ChangedAtUtc), group =>
            Assert.Equal(group.OrderByDescending(audit => audit.Id).Select(audit => audit.Id), group.Select(audit => audit.Id)));

        foreach (var audit in stored)
        {
            var response = Assert.Single(events, item => item.Id == audit.Id);
            Assert.Equal(audit.SourceSystem, response.SourceSystem);
            Assert.Equal(audit.TransactionId, response.TransactionId);
            Assert.Equal(audit.Action, response.Action);
            Assert.Equal(audit.FieldName, response.FieldName);
            Assert.Equal(audit.OldValue, response.OldValue);
            Assert.Equal(audit.NewValue, response.NewValue);
            Assert.Equal(audit.ChangedAtUtc, response.ChangedAtUtc);
            Assert.Equal(DateTimeKind.Utc, response.ChangedAtUtc.Kind);
        }

        Assert.All(events.Where(audit => audit.Action == TransactionAuditAction.Created), audit =>
        {
            Assert.Null(audit.FieldName);
            Assert.Null(audit.OldValue);
            Assert.Null(audit.NewValue);
        });
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(21, await context.TransactionAudits.CountAsync());
    }

    [Theory]
    [InlineData("Store Uptown", 101, "29.99", "34.99")]
    [InlineData("Store Downtown", 101, "129.99", "119.99")]
    [InlineData("Online", 101, "69.99", "59.99")]
    [InlineData("Store Uptown", 102, "Revoked", "Active")]
    [InlineData("Store Downtown", 102, "Revoked", "Active")]
    [InlineData("Online", 102, "Revoked", "Active")]
    public async Task TransactionHistoryUsesBothIdentityFieldsAndPreservesEventValues(
        string sourceSystem, int transactionId, string oldValue, string newValue)
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        await ImportLifecycleAsync(database);
        await using var context = database.CreateContext();
        var controller = new AuditsController(context);

        var events = Audits(await controller.GetForTransaction(sourceSystem, transactionId, CancellationToken.None));
        var global = Audits(await controller.Get(CancellationToken.None));

        Assert.Equal(transactionId == 101 ? 2 : 3, events.Count);
        Assert.All(events, audit =>
        {
            Assert.Equal(sourceSystem, audit.SourceSystem);
            Assert.Equal(transactionId, audit.TransactionId);
        });
        Assert.Equal(global.Where(audit => audit.SourceSystem == sourceSystem && audit.TransactionId == transactionId), events);
        Assert.Equal(oldValue, events[0].OldValue);
        Assert.Equal(newValue, events[0].NewValue);
        Assert.Equal(transactionId == 101 ? "Amount" : "Status", events[0].FieldName);
        Assert.Equal(transactionId == 101 ? TransactionAuditAction.Updated : TransactionAuditAction.Reactivated, events[0].Action);
        Assert.Equal(TransactionAuditAction.Created, events[^1].Action);
        if (transactionId == 102)
        {
            Assert.Equal(TransactionAuditAction.Revoked, events[1].Action);
            Assert.Equal("Active", events[1].OldValue);
            Assert.Equal("Revoked", events[1].NewValue);
        }
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task FixedTimestampAuditEventsUseDescendingIdAsTieBreaker()
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        await database.ImportMocksAsync();
        database.ScenarioState.Scenario = MockScenario.Changes;
        await database.ImportMocksAsync();
        database.ScenarioState.Scenario = MockScenario.Reactivation;
        await database.ImportMocksAsync();
        await using var context = database.CreateContext();
        var controller = new AuditsController(context);

        var global = Audits(await controller.Get(CancellationToken.None));
        var individual = Audits(await controller.GetForTransaction("Online", 102, CancellationToken.None));

        Assert.Single(global.Select(audit => audit.ChangedAtUtc).Distinct());
        Assert.Equal(global.Select(audit => audit.Id).OrderDescending(), global.Select(audit => audit.Id));
        Assert.Equal(individual.Select(audit => audit.Id).OrderDescending(), individual.Select(audit => audit.Id));
        Assert.Equal(new[] { TransactionAuditAction.Reactivated, TransactionAuditAction.Revoked, TransactionAuditAction.Created },
            individual.Select(audit => audit.Action));
    }

    [Theory]
    [InlineData("Unknown", 101)]
    [InlineData("Store Uptown", 999)]
    [InlineData("Store Uptown", 102)]
    public async Task MissingCompositeIdentityReturnsNotFound(string sourceSystem, int transactionId)
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        context.Transactions.AddRange(Transaction("Store Uptown", 101), Transaction("Store Downtown", 102));
        await context.SaveChangesAsync();

        var result = await new AuditsController(context).GetForTransaction(sourceSystem, transactionId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ExistingTransactionWithoutAuditsReturnsEmptyArray()
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        context.Transactions.Add(Transaction("Online", 0));
        await context.SaveChangesAsync();

        var result = await new AuditsController(context).GetForTransaction("Online", 0, CancellationToken.None);

        Assert.Empty(Audits(result));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("***4444", "***4444")]
    [InlineData("4111111111111111", null)]
    [InlineData("***12AB", null)]
    [InlineData("1234567", null)]
    public async Task ReadResponsesExposeOnlyValidMaskedCardsAndSerializeEnumsAsStrings(string card, string? expected)
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var transaction = Transaction("Online", 101);
        transaction.CardNumber = card;
        transaction.Audits.Add(new TransactionAudit
        {
            SourceSystem = "Online", TransactionId = 101, Action = TransactionAuditAction.Updated,
            FieldName = "CardNumber", OldValue = "4111111111111111", NewValue = card,
            ChangedAtUtc = MockTransactionSource.ReferenceTimeUtc
        });
        context.Transactions.Add(transaction);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var rows = Assert.IsType<OkObjectResult>(await new TransactionsController(context).Get(CancellationToken.None));
        var rowJson = JsonSerializer.Serialize(rows.Value, options);
        using var document = JsonDocument.Parse(rowJson);
        var row = document.RootElement[0];
        Assert.Equal(expected, row.GetProperty("cardNumber").GetString());
        Assert.Equal("Active", row.GetProperty("status").GetString());
        Assert.False(row.TryGetProperty("audits", out _));
        Assert.DoesNotContain("4111111111111111", rowJson);

        var controller = new AuditsController(context);
        var audit = Assert.Single(Audits(await controller.Get(CancellationToken.None)));
        Assert.Null(audit.OldValue);
        Assert.Equal(expected, audit.NewValue);
        Assert.Equal(new[] { audit }, Audits(await controller.GetForTransaction("Online", 101, CancellationToken.None)));
        var json = JsonSerializer.Serialize(audit, options);
        using var auditDocument = JsonDocument.Parse(json);
        Assert.Equal("Updated", auditDocument.RootElement.GetProperty("action").GetString());
        Assert.EndsWith("Z", auditDocument.RootElement.GetProperty("changedAtUtc").GetString());
        Assert.False(auditDocument.RootElement.TryGetProperty("transaction", out _));
        Assert.DoesNotContain("4111111111111111", json);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    private static List<AuditResponse> Audits(IActionResult result) =>
        Assert.IsType<List<AuditResponse>>(Assert.IsType<OkObjectResult>(result).Value);

    private static Transaction Transaction(string source, int id) => new()
    {
        SourceSystem = source, TransactionId = id, CardNumber = "", LocationCode = "LOC",
        ProductName = "Wireless Mouse", Amount = 29.99m,
        TransactionTime = MockTransactionSource.ReferenceTimeUtc.AddHours(-1), Status = TransactionStatus.Active
    };

    private static async Task ImportLifecycleAsync(ImportTestDatabase database)
    {
        await database.ImportMocksAsync();
        database.Clock.UtcNow = database.Clock.UtcNow.AddMinutes(1);
        database.ScenarioState.Scenario = MockScenario.Changes;
        await database.ImportMocksAsync();
        database.Clock.UtcNow = database.Clock.UtcNow.AddMinutes(1);
        database.ScenarioState.Scenario = MockScenario.Reactivation;
        await database.ImportMocksAsync();
    }
}
