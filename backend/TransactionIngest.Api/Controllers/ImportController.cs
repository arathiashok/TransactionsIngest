using Microsoft.AspNetCore.Mvc;
using TransactionIngest.Api.Services;

namespace TransactionIngest.Api.Controllers;

[ApiController]
[Route("api/import")]
public class ImportController(TransactionImportService importService) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<ImportResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ImportResult>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ImportResult>(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ImportResult>> Import(CancellationToken cancellationToken)
    {
        var result = await importService.ImportAsync(cancellationToken);
        if (result.IsBusy)
        {
            return Conflict(result);
        }

        if (result.StoppedOnDatabaseFailure)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, result);
        }

        return Ok(result);
    }
}
