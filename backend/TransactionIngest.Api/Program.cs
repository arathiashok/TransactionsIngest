using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using TransactionIngest.Api.Data;
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

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddDbContext<TransactionIngestDbContext>(options =>
    options.UseSqlite(sqliteConnection.ConnectionString));
builder.Services.AddSingleton<MockScenarioState>();
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
