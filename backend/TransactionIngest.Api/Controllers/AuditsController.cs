using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TransactionIngest.Api.Data;
using TransactionIngest.Api.Models;

namespace TransactionIngest.Api.Controllers;

[ApiController]
[Route("api")]
public class AuditsController(TransactionIngestDbContext dbContext) : ControllerBase
{
    [HttpGet("audits")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Ok(await ReadAuditsAsync(null, null, cancellationToken));

    [HttpGet("transactions/{sourceSystem}/{transactionId:int}/audits")]
    public async Task<IActionResult> GetForTransaction(
        string sourceSystem, int transactionId, CancellationToken cancellationToken)
    {
        var exists = await dbContext.Transactions.AsNoTracking().AnyAsync(
            transaction => transaction.SourceSystem == sourceSystem
                && transaction.TransactionId == transactionId, cancellationToken);

        if (!exists)
        {
            return NotFound();
        }

        return Ok(await ReadAuditsAsync(sourceSystem, transactionId, cancellationToken));
    }

    private Task<List<AuditResponse>> ReadAuditsAsync(
        string? sourceSystem, int? transactionId, CancellationToken cancellationToken)
    {
        var query = dbContext.TransactionAudits.AsNoTracking();
        if (sourceSystem is not null)
        {
            query = query.Where(audit => audit.SourceSystem == sourceSystem
                && audit.TransactionId == transactionId);
        }

        return query.OrderByDescending(audit => audit.ChangedAtUtc)
            .ThenByDescending(audit => audit.Id)
            .Select(audit => new AuditResponse(
                audit.Id, audit.SourceSystem, audit.TransactionId, audit.Action, audit.FieldName,
                SafeAuditValue(audit.FieldName, audit.OldValue),
                SafeAuditValue(audit.FieldName, audit.NewValue), audit.ChangedAtUtc))
            .ToListAsync(cancellationToken);
    }

    private static string? SafeAuditValue(string? fieldName, string? value)
    {
        if (fieldName != nameof(Transaction.CardNumber) || value is null || value == "")
        {
            return value;
        }

        return value.Length == 7 && value.StartsWith("***", StringComparison.Ordinal)
            && value.Skip(3).All(character => character is >= '0' and <= '9') ? value : null;
    }
}

public sealed record AuditResponse(
    long Id,
    string SourceSystem,
    int TransactionId,
    TransactionAuditAction Action,
    string? FieldName,
    string? OldValue,
    string? NewValue,
    DateTime ChangedAtUtc);
