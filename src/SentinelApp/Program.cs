using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using SentinelApp.Services;

var builder = WebApplication.CreateBuilder(args);

var opts = SentinelOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(opts);
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
builder.Services.AddSingleton<IGateLogClient, AzureGateLogClient>();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("protected-gate", client =>
    client.Timeout = TimeSpan.FromSeconds(75))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
    });
builder.Services.AddHttpClient("hosted-agent", client =>
    client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
    });
builder.Services.AddSingleton<GateTools>();
builder.Services.AddSingleton<GateForwarder>();
builder.Services.AddSingleton<AgentService>();
builder.Services.AddSingleton<BrokerEvidenceStore>();
builder.Services.AddSingleton<IEmbeddedGateExplainer, EmbeddedGateExplainer>();
builder.Services.AddSingleton<IHostedGateExplainer, HostedGateExplainer>();
builder.Services.AddSingleton<IGateExplainer, AgentRouter>();
builder.Services.AddSingleton<IAuthorizationHandler, HostedAgentAuthorizationHandler>();

// The UI plane (this same-origin API) uses classic in-app token validation —
// a deliberate contrast with the gateway-validated SentinelGate /enter plane.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = $"https://login.microsoftonline.com/{opts.TenantId}/v2.0";
        o.TokenValidationParameters = new()
        {
            ValidIssuer = $"https://login.microsoftonline.com/{opts.TenantId}/v2.0",
            ValidAudiences = [opts.ApiClientId, opts.ApiAudienceUri],
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(SentinelAuthorization.SpaUserPolicy, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
            SentinelAuthorization.HasDelegatedScope(
                context.User,
                SentinelAuthorization.AccessAsUserScope));
    });
    options.AddPolicy(SentinelAuthorization.HostedAgentPolicy, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new HostedAgentRequirement());
    });
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

// Runtime config for the SPA (MSAL settings), generated from environment.
app.MapGet("/config.js", () => Results.Content(
    $$"""
    window.SENTINEL = {
      tenantId: "{{opts.TenantId}}",
      spaClientId: "{{opts.SpaClientId}}",
      apiScope: "{{opts.ApiAudienceUri}}/access_as_user",
      gateApiBase: "{{opts.GateApiBase}}"
    };
    """,
    "application/javascript"));

var api = app.MapGroup("/api")
    .RequireAuthorization(SentinelAuthorization.SpaUserPolicy);

api.MapGet("/whoami", (HttpContext ctx) =>
{
    string? Claim(string type) => ctx.User.FindFirst(type)?.Value;
    return Results.Ok(new
    {
        name = Claim("name") ?? Claim("preferred_username"),
        oid = Claim("http://schemas.microsoft.com/identity/claims/objectidentifier") ?? Claim("oid"),
        tenant = Claim("http://schemas.microsoft.com/identity/claims/tenantid") ?? Claim("tid"),
        validatedBy = "in-app JwtBearer middleware (UI plane). SentinelGate /enter is validated by Application Gateway instead.",
    });
});

api.MapPost("/gate/enter", async (HttpContext context, GateForwarder forwarder, CancellationToken ct) =>
{
    if (!BearerTokenReader.TryRead(context.Request, out var callerToken))
    {
        return Results.Json(
            GateForwardResult.Failure(
                StatusCodes.Status401Unauthorized,
                "missing_caller_token",
                "The authenticated request did not contain a reusable bearer token."),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    var result = await forwarder.EnterAsync(callerToken, ct);
    return Results.Json(result, statusCode: result.HttpStatus);
});

api.MapPost("/tools/decode", async (GateTools tools, DecodeRequest body, CancellationToken ct) =>
    Results.Ok(await tools.DecodeTokenAsync(body.Token, ct)));

api.MapGet("/tools/config", async (GateTools tools, CancellationToken ct) =>
    Results.Ok(await tools.GetGatewayConfigAsync(ct)));

api.MapGet("/tools/logs", async (GateTools tools, int? minutes, CancellationToken ct) =>
    Results.Ok(await tools.QueryGateLogsAsync(
        minutes ?? 60,
        cancellationToken: ct)));

api.MapPost("/tools/simulate", async (GateTools tools, HttpContext ctx, SimulateRequest body, CancellationToken ct) =>
{
    if (body.Scenario?.Trim() is not { Length: > 0 } scenario)
    {
        return Results.BadRequest(new { error = "A simulation scenario is required." });
    }
    string? userToken = null;
    if (string.Equals(scenario, "user_replay", StringComparison.OrdinalIgnoreCase) &&
        !BearerTokenReader.TryRead(ctx.Request, out userToken!))
    {
        return Results.BadRequest(new { error = "The caller token was unavailable for user replay." });
    }
    return Results.Ok(await tools.SimulateAsync(scenario, userToken, ct));
});

api.MapPost("/agent/chat", async (HttpContext ctx, IGateExplainer agent, ChatRequest body, CancellationToken ct) =>
{
    var owner = SentinelAuthorization.GetSessionOwner(ctx.User);
    if (owner is null)
    {
        return Results.Forbid();
    }
    if (!Guid.TryParseExact(body.SessionId, "D", out _) ||
        string.IsNullOrWhiteSpace(body.Message) ||
        body.Message.Length > 16_000)
    {
        return Results.BadRequest(new { error = "A valid session ID and bounded message are required." });
    }

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";

    await foreach (var chunk in agent.StreamAsync(owner, body.SessionId, body.Message, ct))
    {
        var payload = JsonSerializer.Serialize(new { text = chunk });
        await ctx.Response.WriteAsync($"data: {payload}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    await ctx.Response.WriteAsync("data: [DONE]\n\n", ct);
    return Results.Empty;
});

api.MapPost("/agent/reset", async (HttpContext ctx, IGateExplainer agent, ChatRequest body, CancellationToken ct) =>
{
    var owner = SentinelAuthorization.GetSessionOwner(ctx.User);
    if (owner is null)
    {
        return Results.Forbid();
    }
    if (!Guid.TryParseExact(body.SessionId, "D", out _))
    {
        return Results.BadRequest(new { error = "A valid session ID is required." });
    }
    await agent.ResetSessionAsync(owner, body.SessionId, ct);
    return Results.Ok();
});

api.MapPost("/agent/evidence/decode", async (
    HttpContext ctx,
    GateTools tools,
    BrokerEvidenceStore store,
    DecodeEvidenceRequest body,
    CancellationToken ct) =>
{
    var owner = SentinelAuthorization.GetSessionOwner(ctx.User);
    if (owner is null)
    {
        return Results.Forbid();
    }
    if (!Guid.TryParseExact(body.SessionId, "D", out var session) ||
        session == Guid.Empty ||
        string.IsNullOrWhiteSpace(body.Token) ||
        body.Token.Length > 128 * 1024)
    {
        return Results.BadRequest(new { error = "A canonical session ID and bounded token are required." });
    }

    var evidence = await tools.DecodeTokenAsync(body.Token, ct);
    var receipt = store.Store(owner, body.SessionId, evidence);
    return Results.Ok(new
    {
        evidence = receipt.Evidence,
        queuedForAgent = true,
        expiresAt = receipt.ExpiresAt,
    });
});

var broker = app.MapGroup("/api/agent/broker")
    .RequireAuthorization(SentinelAuthorization.HostedAgentPolicy);

broker.MapGet("/decode/{handle}", (string handle, BrokerEvidenceStore store) =>
{
    if (!Guid.TryParseExact(handle, "D", out var parsed) || parsed == Guid.Empty ||
        !store.TryConsume(parsed, out var evidence))
    {
        return Results.NotFound(new { error = "The evidence handle is invalid, expired, or already consumed." });
    }
    return Results.Ok(new
    {
        evidenceType = "sanitized_local_token_evidence",
        evidence,
        limitation = "The raw token is not stored or returned. Decoding is not cryptographic validation.",
    });
});

broker.MapPost("/simulate", async (
    SentinelOptions options,
    GateTools tools,
    BrokerSimulationRequest body,
    CancellationToken ct) =>
{
    if (options.AgentMode == AgentMode.HostedShadow)
    {
        return Results.Conflict(new
        {
            error = "Deterministic scenarios are disabled for hosted shadow comparisons.",
        });
    }
    var scenario = body.Scenario?.Trim().ToLowerInvariant().Replace('-', '_');
    if (scenario is not ("missing" or "valid" or "wrong_audience" or "tampered"))
    {
        return Results.BadRequest(new
        {
            error = "Scenario must be missing, valid, wrong_audience, or tampered.",
        });
    }
    return Results.Ok(await tools.SimulateForAgentAsync(scenario, ct));
});

app.Run();

internal sealed record DecodeRequest(string Token);
internal sealed record DecodeEvidenceRequest(string SessionId, string Token);
internal sealed record SimulateRequest(string? Scenario);
internal sealed record BrokerSimulationRequest(string? Scenario);
internal sealed record ChatRequest(string? SessionId, string? Message);

public partial class Program;
