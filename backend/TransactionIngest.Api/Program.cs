using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using TransactionIngest.Api.Data;
using TransactionIngest.Api.Services;
using TransactionIngest.Api.Sources;

var builder = WebApplication.CreateBuilder(args);

var sqliteConnection = new SqliteConnectionStringBuilder(
    builder.Configuration.GetConnectionString("TransactionIngest")
        ?? throw new InvalidOperationException("Connection string 'TransactionIngest' is missing."));

// Resolve ordinary relative filenames; preserve special SQLite data sources.
if (!string.IsNullOrEmpty(sqliteConnection.DataSource)
    && sqliteConnection.Mode != SqliteOpenMode.Memory
    && sqliteConnection.DataSource != ":memory:"
    && !Path.IsPathRooted(sqliteConnection.DataSource)
    && !sqliteConnection.DataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
    && !sqliteConnection.DataSource.StartsWith("|DataDirectory|", StringComparison.OrdinalIgnoreCase))
{
    sqliteConnection.DataSource = Path.GetFullPath(
        sqliteConnection.DataSource, builder.Environment.ContentRootPath);
}

var scenarioState = new MockScenarioState();
TimeProvider timeProvider = TimeProvider.System;

// Manual mock overrides are deliberately ignored outside Development.
if (builder.Environment.IsDevelopment())
{
    if (builder.Configuration["MockSources:Scenario"] is { } scenarioName)
    {
        if (!Enum.GetNames<MockScenario>().Contains(scenarioName, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "MockSources__Scenario must be Baseline, Changes, Reactivation, Incomplete, or Failure.");
        }

        scenarioState.Scenario = Enum.Parse<MockScenario>(scenarioName, ignoreCase: true);
    }

    if (builder.Configuration["MockSources:ReferenceTimeUtc"] is { } timestamp)
    {
        if (!DateTimeOffset.TryParseExact(timestamp,
            ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var referenceTimeUtc))
        {
            throw new InvalidOperationException(
                "MockSources__ReferenceTimeUtc must be a UTC timestamp ending in Z, such as 2026-10-25T12:00:00Z.");
        }

        timeProvider = new DevelopmentFixedTimeProvider(referenceTimeUtc);
    }
}

// Add services to the container.

builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDbContext<TransactionIngestDbContext>(options =>
    options.UseSqlite(sqliteConnection.ConnectionString));
builder.Services.AddSingleton(scenarioState);
builder.Services.AddSingleton(timeProvider);
builder.Services.AddScoped<TransactionImportService>();
builder.Services.AddSingleton<ITransactionSource>(services =>
    new MockTransactionSource("Store Uptown", services.GetRequiredService<MockScenarioState>()));
builder.Services.AddSingleton<ITransactionSource>(services =>
    new MockTransactionSource("Store Downtown", services.GetRequiredService<MockScenarioState>()));
builder.Services.AddSingleton<ITransactionSource>(services =>
    new MockTransactionSource("Online", services.GetRequiredService<MockScenarioState>()));

var app = builder.Build();

// Configure the HTTP request pipeline.

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

internal sealed class DevelopmentFixedTimeProvider(DateTimeOffset referenceTimeUtc) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => referenceTimeUtc;
}
