namespace TransactionIngest.Api.Models;

public enum TransactionAuditAction
{
    Created,
    Updated,
    Revoked,
    Reactivated
}

public class TransactionAudit
{
    public long Id { get; set; }
    public required string SourceSystem { get; set; }
    public int TransactionId { get; set; }
    public TransactionAuditAction Action { get; set; }
    public string? FieldName { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTime ChangedAtUtc { get; set; }

    public Transaction Transaction { get; set; } = null!;
}
