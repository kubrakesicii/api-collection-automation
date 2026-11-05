using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SecureApiFixture.Controllers;

// Analysis fixture: protected endpoints, header/query parameters and a nested body.
// It is not part of any project and is never compiled; the discovery engine only reads it.
[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class AccountsController : ControllerBase
{
    [HttpGet]
    public IActionResult List([FromQuery] int page = 1, [FromQuery] string? search = null) => Ok();

    [HttpGet("{id:guid}")]
    public IActionResult Get(Guid id, [FromHeader(Name = "X-Correlation-Id")] string? correlationId) => Ok();

    [HttpPost]
    public IActionResult Create(CreateAccountRequest request) => Created();

    [Authorize(Roles = "Admin")]
    [HttpDelete("{id:guid}")]
    public IActionResult Delete(Guid id) => NoContent();

    [AllowAnonymous]
    [HttpGet("ping")]
    public IActionResult Ping() => Ok();
}
