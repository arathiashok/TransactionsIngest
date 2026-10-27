using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TransactionIngest.Api.Data;

namespace TransactionIngest.Api.Controllers;

[ApiController]
[Route("api/transactions")]
public class TransactionsController(TransactionIngestDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var transactions = await dbContext.Transactions
            .AsNoTracking()
            .OrderBy(transaction => transaction.SourceSystem)
            .ThenBy(transaction => transaction.TransactionId)
            .Select(transaction => new
            {
                transaction.SourceSystem,
                transaction.TransactionId,
                CardNumber = MaskedCardNumber(transaction.CardNumber),
                transaction.LocationCode,
                transaction.ProductName,
                transaction.Amount,
                transaction.TransactionTime,
                transaction.Status
            })
            .ToListAsync(cancellationToken);

        return Ok(transactions);
    }

    private static string? MaskedCardNumber(string? value) => value == ""
        ? ""
        : value is { Length: 7 } && value.StartsWith("***", StringComparison.Ordinal)
            && value.Skip(3).All(character => character is >= '0' and <= '9')
                ? value : null;
}
