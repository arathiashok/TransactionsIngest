using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TransactionIngest.Api.Controllers;
using TransactionIngest.Api.Models;
using TransactionIngest.Api.Sources;
using Xunit;

namespace TransactionIngest.Api.Tests;

public sealed class TransactionsControllerTests
{
    [Theory]
    [InlineData("29.99")]
    [InlineData("0.00")]
    [InlineData("0.01")]
    [InlineData("-0.01")]
    [InlineData("-12.34")]
    [InlineData("9999999999999999.99")]
    [InlineData("-9999999999999999.99")]
    [InlineData("9999999999999999.98")]
    [InlineData("-9999999999999999.98")]
    [InlineData("90071992547409.91")]
    [InlineData("90071992547409.92")]
    public async Task AmountSurvivesSqliteAndJsonAsAnExactTwoDecimalString(string expected)
    {
        await using var database = await ImportTestDatabase.CreateAsync();
        await using var context = database.CreateContext();
        var amount = decimal.Parse(expected, CultureInfo.InvariantCulture);
        context.Transactions.Add(new Transaction
        {
            SourceSystem = "Online", TransactionId = 101, CardNumber = "***4444",
            LocationCode = "ONLINE", ProductName = "Wireless Mouse", Amount = amount,
            TransactionTime = MockTransactionSource.ReferenceTimeUtc, Status = TransactionStatus.Active
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Assert.Equal(amount, (await context.Transactions.AsNoTracking().SingleAsync()).Amount);

        // Formatting must not depend on the process culture or run inside SQLite.
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var result = Assert.IsType<OkObjectResult>(
                await new TransactionsController(context).Get(CancellationToken.None));
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var row = json.RootElement[0];
            var value = row.GetProperty("amount");
            Assert.Equal(JsonValueKind.String, value.ValueKind);
            Assert.Equal(expected, value.GetString());
            Assert.Equal(101, row.GetProperty("transactionId").GetInt32());
            Assert.Equal("***4444", row.GetProperty("cardNumber").GetString());
            Assert.EndsWith("Z", row.GetProperty("transactionTime").GetString());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Empty(await context.TransactionAudits.ToListAsync());
    }
}
