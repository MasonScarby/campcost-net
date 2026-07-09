using CampCost.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace CampCost.Api.Controllers;

[ApiController]
[Route("api/account")]
[Authorize]
public class AccountController : ControllerBase
{
    private readonly IPlanService _plans;

    public AccountController(IPlanService plans) => _plans = plans;

    private Guid UserId => Guid.Parse(
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedAccessException("No user ID in token"));

    // GET /api/account/plan
    // Server-side source of truth for the user's plan ("free" | "pro").
    // The client caches this for display only — all enforcement happens server-side.
    [HttpGet("plan")]
    public async Task<IActionResult> GetPlan()
    {
        var plan = await _plans.GetPlanAsync(UserId);
        return Ok(new { plan });
    }
}
