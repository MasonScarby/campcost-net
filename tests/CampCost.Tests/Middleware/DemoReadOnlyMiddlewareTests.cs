using System.Security.Claims;
using CampCost.Api.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace CampCost.Tests.Middleware;

/// <summary>
/// The demo account must be read-only: writes (POST/PUT/PATCH/DELETE) from the configured
/// demo user are rejected 403, reads pass, real users are untouched, and the whole thing is a
/// no-op when Demo:UserId isn't configured.
/// </summary>
public class DemoReadOnlyMiddlewareTests
{
    private static readonly string DemoId = Guid.NewGuid().ToString();

    private static DefaultHttpContext Ctx(string method, string? userId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Response.Body = new MemoryStream();
        if (userId is not null)
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "Test"));
        return ctx;
    }

    private static DemoReadOnlyMiddleware Sut(RequestDelegate next, string? demoId = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:UserId"] = demoId ?? DemoId })
            .Build();
        return new DemoReadOnlyMiddleware(next, config);
    }

    [Fact]
    public async Task DemoUser_Write_Is403_AndShortCircuits()
    {
        var reached = false;
        var sut = Sut(_ => { reached = true; return Task.CompletedTask; });

        var ctx = Ctx("POST", DemoId);
        await sut.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(403);
        reached.Should().BeFalse("the write must be blocked before it reaches the app");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task DemoUser_AllMutationVerbs_AreBlocked(string verb)
    {
        var reached = false;
        var sut = Sut(_ => { reached = true; return Task.CompletedTask; });

        var ctx = Ctx(verb, DemoId);
        await sut.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(403);
        reached.Should().BeFalse();
    }

    [Fact]
    public async Task DemoUser_Read_PassesThrough()
    {
        var reached = false;
        var sut = Sut(_ => { reached = true; return Task.CompletedTask; });

        await sut.InvokeAsync(Ctx("GET", DemoId));

        reached.Should().BeTrue("reads are always allowed for the demo");
    }

    [Fact]
    public async Task RealUser_Write_PassesThrough()
    {
        var reached = false;
        var sut = Sut(_ => { reached = true; return Task.CompletedTask; });

        await sut.InvokeAsync(Ctx("POST", Guid.NewGuid().ToString()));

        reached.Should().BeTrue("real users can write");
    }

    [Fact]
    public async Task UnauthenticatedWrite_PassesThrough()
    {
        // The Plaid + Stripe webhooks are unauthenticated POSTs — no user id, never the demo.
        var reached = false;
        var sut = Sut(_ => { reached = true; return Task.CompletedTask; });

        await sut.InvokeAsync(Ctx("POST", userId: null));

        reached.Should().BeTrue("webhooks have no user and must not be blocked");
    }

    [Fact]
    public async Task NotConfigured_DemoUserCanWrite()
    {
        var reached = false;
        var sut = Sut(_ => { reached = true; return Task.CompletedTask; }, demoId: "");

        await sut.InvokeAsync(Ctx("POST", DemoId));

        reached.Should().BeTrue("no-op when Demo:UserId is unset (local dev)");
    }
}
