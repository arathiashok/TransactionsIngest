using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TransactionIngest.Api.Models;

namespace TransactionIngest.Api.Data;

public class TransactionIngestDbContext(DbContextOptions<TransactionIngestDbContext> options)
    : DbContext(options)
{
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<TransactionAudit> TransactionAudits => Set<TransactionAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var utcConverter = new ValueConverter<DateTime, DateTime>(
            value => RequireUtc(value),
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

        var transaction = modelBuilder.Entity<Transaction>();
        transaction.HasKey(entity => new { entity.SourceSystem, entity.TransactionId });
        transaction.Property(entity => entity.TransactionId).ValueGeneratedNever();
        transaction.Property(entity => entity.SourceSystem).IsRequired().HasMaxLength(19);
        transaction.Property(entity => entity.CardNumber).IsRequired().HasMaxLength(19);
        transaction.Property(entity => entity.LocationCode).IsRequired().HasMaxLength(19);

        // The exercise's sample exceeds its stated limit of fewer than 20 characters.
        transaction.Property(entity => entity.ProductName).IsRequired().HasMaxLength(100);

        transaction.Property(entity => entity.Amount)
            .HasConversion(value => ToCents(value), value => value / 100m)
            .HasColumnType("INTEGER");
        transaction.Property(entity => entity.TransactionTime).HasConversion(utcConverter);
        transaction.Property(entity => entity.Status).HasConversion<string>().IsRequired();

        // SQLite does not enforce HasMaxLength or decimal precision/scale metadata.
        transaction.ToTable("Transactions", table =>
        {
            table.HasCheckConstraint("CK_Transactions_SourceSystem_Length",
                "length(\"SourceSystem\") BETWEEN 1 AND 19");
            table.HasCheckConstraint("CK_Transactions_CardNumber_Length",
                "length(\"CardNumber\") <= 19");
            table.HasCheckConstraint("CK_Transactions_LocationCode_Length",
                "length(\"LocationCode\") BETWEEN 1 AND 19");
            table.HasCheckConstraint("CK_Transactions_ProductName_Length",
                "length(\"ProductName\") BETWEEN 1 AND 100");
            table.HasCheckConstraint("CK_Transactions_Amount_Cents",
                "typeof(\"Amount\") = 'integer' AND \"Amount\" BETWEEN -999999999999999999 AND 999999999999999999");
            table.HasCheckConstraint("CK_Transactions_Status",
                "\"Status\" IN ('Active', 'Revoked')");
        });

        var audit = modelBuilder.Entity<TransactionAudit>();
        audit.HasKey(entity => entity.Id);
        audit.Property(entity => entity.Id).ValueGeneratedOnAdd();
        audit.Property(entity => entity.SourceSystem).IsRequired().HasMaxLength(19);
        audit.Property(entity => entity.TransactionId).ValueGeneratedNever();
        audit.Property(entity => entity.Action).HasConversion<string>().IsRequired();
        audit.Property(entity => entity.ChangedAtUtc).HasConversion(utcConverter);
        audit.HasOne(entity => entity.Transaction)
            .WithMany(entity => entity.Audits)
            .HasForeignKey(entity => new { entity.SourceSystem, entity.TransactionId })
            .OnDelete(DeleteBehavior.Restrict);
        audit.HasIndex(entity => new
        {
            entity.SourceSystem,
            entity.TransactionId,
            entity.ChangedAtUtc
        });
        audit.ToTable("TransactionAudits", table =>
            table.HasCheckConstraint("CK_TransactionAudits_Action",
                "\"Action\" IN ('Created', 'Updated', 'Revoked', 'Reactivated')"));
    }

    private static long ToCents(decimal amount)
    {
        const decimal maxAmount = 9_999_999_999_999_999.99m;

        if (amount < -maxAmount || amount > maxAmount)
        {
            throw new ArgumentOutOfRangeException(nameof(amount),
                "Amount must fit within decimal(18,2).");
        }

        var cents = amount * 100m;
        if (cents != decimal.Truncate(cents))
        {
            throw new ArgumentException("Amount must be an exact number of cents.", nameof(amount));
        }

        return checked((long)cents);
    }

    private static DateTime RequireUtc(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamps must have DateTimeKind.Utc.", nameof(value));
        }

        return value;
    }
}
