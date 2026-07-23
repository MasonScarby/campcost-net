using CampCost.Api.Contracts;
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
    private readonly IAccountService _account;
    private readonly IWebHostEnvironment _env;

    public AccountController(IPlanService plans, IAccountService account, IWebHostEnvironment env)
    {
        _plans = plans;
        _account = account;
        _env = env;
    }

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

    // POST /api/account/plan  { "plan": "pro" | "free" }
    // DEV-ONLY manual plan switch so Pro features can be exercised before Stripe exists.
    // Returns 404 outside Development, so it CANNOT be used to self-upgrade in production —
    // there, the plan write belongs to the Stripe webhook (service role). See FIXES.md #9.
    [HttpPost("plan")]
    public async Task<IActionResult> SetPlan([FromBody] SetPlanRequest req)
    {
        if (!_env.IsDevelopment())
            return NotFound();

        if (req.Plan is not ("free" or "pro"))
            return BadRequest(new { error = "plan must be 'free' or 'pro'" });

        await _plans.SetPlanAsync(UserId, req.Plan);
        return Ok(new { plan = req.Plan });
    }

    // DELETE /api/account
    // Permanently deletes the account and ALL its data (Plaid items removed, every DB row for
    // the user purged, Supabase auth user deleted). Irreversible. Privacy-compliance + Plaid
    // production requirement. Keyed off the JWT — a user can only delete their own account.
    [HttpDelete]
    public async Task<IActionResult> DeleteAccount()
    {
        await _account.DeleteAccountAsync(UserId);
        return Ok(new { success = true, deleted = true });
    }

    // POST /api/account/clear-transactions
    // Hard-deletes all of the user's synced bank transactions and resets sync cursors so the
    // next sync re-imports a clean set. Bank connections stay linked; manual expenses are kept.
    // User-initiated escape hatch for duplicate/stale data (e.g. after a disconnect/reconnect).
    // Keyed off the JWT — a user can only clear their own transactions.
    [HttpPost("clear-transactions")]
    public async Task<IActionResult> ClearTransactions()
    {
        var cleared = await _account.ClearTransactionsAsync(UserId);
        return Ok(new { success = true, cleared });
    }
}
