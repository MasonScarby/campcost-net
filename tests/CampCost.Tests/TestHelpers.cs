using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace CampCost.Tests;

/// <summary>Shared helpers: in-memory DbContext, fake plan/assignment services, fake authenticated user.</summary>
public static class TestHelpers
{
    public static CampCostDbContext NewDb() =>
        new(new DbContextOptionsBuilder<CampCostDbContext>()
            .UseInMemoryDatabase($"campcost-{Guid.NewGuid()}")
            .Options);

    /// <summary>Attaches a fake Supabase-style principal (sub + optional aal claim) to a controller.</summary>
    public static void SetUser(this ControllerBase controller, Guid userId, string? aal = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (aal is not null) claims.Add(new Claim("aal", aal));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };
    }
}

/// <summary>IPlanService stub with a fixed plan — no DB read.</summary>
public class FixedPlanService : IPlanService
{
    private readonly string _plan;
    public FixedPlanService(string plan) => _plan = plan;
    public Task<string> GetPlanAsync(Guid userId) => Task.FromResult(_plan);
}

/// <summary>ITransactionAssignmentService stub for controller tests that don't exercise assignment.</summary>
public class NoopAssignmentService : ITransactionAssignmentService
{
    public Task<AutoAssignResult> AutoAssignForTripAsync(Guid userId, Guid tripId) =>
        Task.FromResult(new AutoAssignResult(0, null));
    public Task<AssignOutcome> AssignAsync(Guid userId, Guid transactionId, Guid? tripId) =>
        Task.FromResult(AssignOutcome.Success);
    public Task<IReadOnlyList<Transaction>> GetUnassignedAsync(Guid userId) =>
        Task.FromResult<IReadOnlyList<Transaction>>(new List<Transaction>());
}
