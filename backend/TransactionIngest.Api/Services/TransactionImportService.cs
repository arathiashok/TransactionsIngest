using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TransactionIngest.Api.Data;
using TransactionIngest.Api.Models;
using TransactionIngest.Api.Sources;

namespace TransactionIngest.Api.Services;

public sealed class TransactionImportService(
    TransactionIngestDbContext dbContext,
    IEnumerable<ITransactionSource> sources,
    TimeProvider timeProvider,
    MockScenarioState scenarioState,
    ILogger<TransactionImportService> logger)
{
    private readonly ITransactionSource[] _sources = sources.ToArray();

    public async Task<ImportResult> ImportAsync(CancellationToken cancellationToken = default)
    {
        var referenceTimeUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (!await scenarioState.TryBeginImportAsync(cancellationToken))
        {
            return new ImportResult(referenceTimeUtc, true, Array.Empty<SourceImportResult>());
        }

        try
        {
            // This scoped context is the import's unit of work; never save a caller's pending edits.
            if (dbContext.ChangeTracker.HasChanges())
            {
                throw new InvalidOperationException("Import requires a DbContext without pending changes.");
            }

            dbContext.ChangeTracker.Clear();
            var results = new List<SourceImportResult>();
            for (var index = 0; index < _sources.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ImportSourceAsync(_sources[index], referenceTimeUtc, cancellationToken);
                results.Add(result);

                if (result.Status == SourceImportStatus.DatabaseFailed)
                {
                    foreach (var remaining in _sources.Skip(index + 1))
                    {
                        results.Add(new SourceImportResult(remaining.SourceSystem, SourceImportStatus.NotProcessed,
                            Error: "Import stopped after a database failure."));
                    }

                    break;
                }
            }

            return new ImportResult(referenceTimeUtc, false, results.AsReadOnly());
        }
        finally
        {
            scenarioState.EndImport();
        }
    }

    private async Task<SourceImportResult> ImportSourceAsync(
        ITransactionSource source, DateTime referenceTimeUtc, CancellationToken cancellationToken)
    {
        SourceSnapshot? snapshot;
        try
        {
            snapshot = await source.GetSnapshotAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogWarning(exception, "Snapshot retrieval failed for {SourceSystem}.", source.SourceSystem);
            return new SourceImportResult(source.SourceSystem, SourceImportStatus.RetrievalFailed,
                Error: "Source retrieval failed; no reconciliation was performed.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is { IsComplete: false })
        {
            return new SourceImportResult(source.SourceSystem, SourceImportStatus.SkippedIncomplete,
                Error: "Snapshot is incomplete; no reconciliation was performed.");
        }

        var validationError = ValidateSnapshot(source.SourceSystem, snapshot, out var incoming);
        if (validationError is not null)
        {
            return new SourceImportResult(source.SourceSystem, SourceImportStatus.SkippedInvalid,
                Error: validationError);
        }

        try
        {
            var existing = await dbContext.Transactions
                .Where(transaction => transaction.SourceSystem == source.SourceSystem)
                .ToDictionaryAsync(transaction => transaction.TransactionId, cancellationToken);
            var incomingIds = incoming.Select(transaction => transaction.TransactionId).ToHashSet();
            var inserted = 0;
            var updated = 0;
            var revoked = 0;
            var reactivated = 0;
            var unchanged = 0;

            foreach (var item in incoming.OrderBy(transaction => transaction.TransactionId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!existing.TryGetValue(item.TransactionId, out var transaction))
                {
                    transaction = new Transaction
                    {
                        SourceSystem = source.SourceSystem,
                        TransactionId = item.TransactionId,
                        CardNumber = item.CardNumber,
                        LocationCode = item.LocationCode,
                        ProductName = item.ProductName,
                        Amount = item.Amount,
                        TransactionTime = item.TransactionTime,
                        Status = TransactionStatus.Active
                    };
                    dbContext.Transactions.Add(transaction);
                    AddAudit(transaction, TransactionAuditAction.Created, referenceTimeUtc);
                    inserted++;
                    continue;
                }

                var isReactivation = transaction.Status == TransactionStatus.Revoked;
                var action = isReactivation ? TransactionAuditAction.Reactivated : TransactionAuditAction.Updated;
                var hasChanges = ApplyBusinessChanges(transaction, item, action, referenceTimeUtc);
                if (isReactivation)
                {
                    AddAudit(transaction, TransactionAuditAction.Reactivated, referenceTimeUtc,
                        nameof(Transaction.Status), TransactionStatus.Revoked.ToString(), TransactionStatus.Active.ToString());
                    transaction.Status = TransactionStatus.Active;
                    reactivated++;
                }
                else if (hasChanges)
                {
                    updated++;
                }
                else
                {
                    unchanged++;
                }
            }

            var cutoffUtc = referenceTimeUtc.AddHours(-24);
            foreach (var transaction in existing.Values.OrderBy(transaction => transaction.TransactionId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (transaction.Status == TransactionStatus.Active
                    && !incomingIds.Contains(transaction.TransactionId)
                    && transaction.TransactionTime >= cutoffUtc
                    && transaction.TransactionTime < referenceTimeUtc)
                {
                    AddAudit(transaction, TransactionAuditAction.Revoked, referenceTimeUtc,
                        nameof(Transaction.Status), TransactionStatus.Active.ToString(), TransactionStatus.Revoked.ToString());
                    transaction.Status = TransactionStatus.Revoked;
                    revoked++;
                }
            }

            // One save atomically commits this source's transactions and all associated audits.
            if (inserted + updated + revoked + reactivated > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return new SourceImportResult(source.SourceSystem, SourceImportStatus.Completed,
                inserted, updated, revoked, reactivated, unchanged);
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogError(exception, "Database reconciliation failed for {SourceSystem}.", source.SourceSystem);
            return new SourceImportResult(source.SourceSystem, SourceImportStatus.DatabaseFailed,
                Error: "Database operation failed; the import has stopped.");
        }
        finally
        {
            // Failed pending edits must never leak into another source or a later import.
            dbContext.ChangeTracker.Clear();
        }
    }

    private static string? ValidateSnapshot(
        string sourceSystem, SourceSnapshot? snapshot, out SourceTransaction[] transactions)
    {
        transactions = [];
        if (string.IsNullOrWhiteSpace(sourceSystem) || sourceSystem.Length > 19)
        {
            return "SourceSystem must contain 1 to 19 characters and cannot be whitespace-only.";
        }

        if (snapshot?.Transactions is null)
        {
            return "Snapshot and transaction collection are required.";
        }

        // Retain a stable copy of the exact records that were validated.
        transactions = snapshot.Transactions.ToArray();
        var ids = new HashSet<int>();
        foreach (var transaction in transactions)
        {
            if (transaction is null)
            {
                return "Snapshot contains a null transaction.";
            }

            if (!ids.Add(transaction.TransactionId))
            {
                return $"Snapshot contains duplicate TransactionId {transaction.TransactionId}.";
            }

            var card = transaction.CardNumber;
            if (card is null || (card.Length != 0
                && (card.Length != 7 || !card.StartsWith("***", StringComparison.Ordinal)
                    || !card.Skip(3).All(character => character is >= '0' and <= '9'))))
            {
                return $"Transaction {transaction.TransactionId}: CardNumber must be empty or '***' followed by four digits.";
            }

            if (string.IsNullOrWhiteSpace(transaction.LocationCode) || transaction.LocationCode.Length > 19)
            {
                return $"Transaction {transaction.TransactionId}: LocationCode must contain 1 to 19 characters and cannot be whitespace-only.";
            }

            if (string.IsNullOrWhiteSpace(transaction.ProductName) || transaction.ProductName.Length > 100)
            {
                return $"Transaction {transaction.TransactionId}: ProductName must contain 1 to 100 characters and cannot be whitespace-only.";
            }

            if (transaction.TransactionTime.Kind != DateTimeKind.Utc || transaction.TransactionTime == default)
            {
                return $"Transaction {transaction.TransactionId}: TransactionTime must be a non-default UTC timestamp.";
            }

            const decimal maxAmount = 9_999_999_999_999_999.99m;
            var amount = transaction.Amount;
            if (amount < -maxAmount || amount > maxAmount)
            {
                return $"Transaction {transaction.TransactionId}: Amount must fit decimal(18,2).";
            }

            var cents = amount * 100m;
            if (cents != decimal.Truncate(cents))
            {
                return $"Transaction {transaction.TransactionId}: Amount must be an exact number of cents.";
            }
        }

        return null;
    }

    private bool ApplyBusinessChanges(Transaction transaction, SourceTransaction incoming,
        TransactionAuditAction action, DateTime referenceTimeUtc)
    {
        var changed = false;

        void Change<T>(string fieldName, T oldValue, T newValue, Action<T> assign)
        {
            if (EqualityComparer<T>.Default.Equals(oldValue, newValue))
            {
                return;
            }

            AddAudit(transaction, action, referenceTimeUtc, fieldName,
                FormatAuditValue(oldValue), FormatAuditValue(newValue));
            assign(newValue);
            changed = true;
        }

        Change(nameof(Transaction.CardNumber), transaction.CardNumber, incoming.CardNumber,
            value => transaction.CardNumber = value);
        Change(nameof(Transaction.LocationCode), transaction.LocationCode, incoming.LocationCode,
            value => transaction.LocationCode = value);
        Change(nameof(Transaction.ProductName), transaction.ProductName, incoming.ProductName,
            value => transaction.ProductName = value);
        Change(nameof(Transaction.Amount), transaction.Amount, incoming.Amount,
            value => transaction.Amount = value);
        Change(nameof(Transaction.TransactionTime), transaction.TransactionTime, incoming.TransactionTime,
            value => transaction.TransactionTime = value);

        return changed;
    }

    private void AddAudit(Transaction transaction, TransactionAuditAction action,
        DateTime referenceTimeUtc, string? fieldName = null, string? oldValue = null, string? newValue = null)
    {
        dbContext.TransactionAudits.Add(new TransactionAudit
        {
            SourceSystem = transaction.SourceSystem,
            TransactionId = transaction.TransactionId,
            Transaction = transaction,
            Action = action,
            FieldName = fieldName,
            OldValue = oldValue,
            NewValue = newValue,
            ChangedAtUtc = referenceTimeUtc
        });
    }

    private static string? FormatAuditValue<T>(T value) => value switch
    {
        null => null,
        decimal amount => amount.ToString("F2", CultureInfo.InvariantCulture),
        DateTime timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };
}
