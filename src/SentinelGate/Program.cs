var builder = WebApplication.CreateBuilder(args);

var protectedHost = RequiredConfiguration(builder.Configuration, "PROTECTED_HOST");
if (Uri.CheckHostName(protectedHost) != UriHostNameType.Dns ||
    protectedHost.EndsWith(".", StringComparison.Ordinal))
{
    throw new InvalidOperationException("PROTECTED_HOST must be a DNS hostname without a scheme, port, or trailing dot.");
}
var tenantText = RequiredConfiguration(builder.Configuration, "EXPECTED_TENANT_ID");
if (!Guid.TryParseExact(tenantText, "D", out var expectedTenantId) || expectedTenantId == Guid.Empty)
{
    throw new InvalidOperationException("EXPECTED_TENANT_ID must be a non-empty canonical GUID.");
}

builder.Services.AddSingleton(new SentinelGateOptions(protectedHost, expectedTenantId));

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new
{
    service = "SentinelGate",
    status = "healthy",
}));

app.MapPost("/enter", (HttpRequest request, SentinelGateOptions options) =>
{
    // Trust in this injected identity comes from the protected listener/rule,
    // isolated backend pool, and ingress boundary. Parse it before considering
    // client-originated routing context.
    var identityValues = request.Headers["x-msft-entra-identity"];
    if (identityValues.Count != 1 || string.IsNullOrWhiteSpace(identityValues[0]))
    {
        return GateError(
            StatusCodes.Status403Forbidden,
            "missing_identity",
            "The gateway identity header is missing or ambiguous.");
    }

    var parts = identityValues[0]!.Split(':', StringSplitOptions.None);
    if (parts.Length != 2 ||
        !Guid.TryParseExact(parts[0], "D", out var tenantId) ||
        !Guid.TryParseExact(parts[1], "D", out var objectId) ||
        tenantId == Guid.Empty ||
        objectId == Guid.Empty)
    {
        return GateError(
            StatusCodes.Status403Forbidden,
            "malformed_identity",
            "The gateway identity header is not in canonical tenantId:objectId form.");
    }

    if (tenantId != options.ExpectedTenantId)
    {
        return GateError(
            StatusCodes.Status403Forbidden,
            "unexpected_tenant",
            "The injected identity belongs to an unexpected tenant.");
    }

    // App Gateway deliberately keeps the ACA FQDN as backend Host for routing,
    // TLS, and SNI. x-original-host is only client-originated routing context.
    // An exact match is a consistency condition, never proof of JWT validation.
    var originalHosts = request.Headers["x-original-host"];
    if (originalHosts.Count != 1 ||
        !TryReadOriginalHost(originalHosts[0], out var originalHost) ||
        !string.Equals(originalHost, options.ProtectedHost, StringComparison.OrdinalIgnoreCase))
    {
        return GateError(
            StatusCodes.Status403Forbidden,
            "unexpected_routing_context",
            "The original-host routing context does not match the protected public hostname.");
    }

    return Results.Ok(new GateEntryResponse(
        Service: "SentinelGate",
        Allowed: true,
        Message: "You are in",
        GatewayValidated: true,
        RoutingContextConsistent: true,
        TenantId: tenantId,
        ObjectId: objectId,
        ErrorCode: null));
});

app.Run();

static string RequiredConfiguration(IConfiguration configuration, string name) =>
    configuration[name]?.Trim() is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing configuration value {name}.");

static IResult GateError(int status, string code, string message) =>
    Results.Json(
        new GateEntryResponse(
            Service: "SentinelGate",
            Allowed: false,
            Message: message,
            GatewayValidated: false,
            RoutingContextConsistent: false,
            TenantId: null,
            ObjectId: null,
            ErrorCode: code),
        statusCode: status);

static bool TryReadOriginalHost(string? value, out string host)
{
    host = string.Empty;
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var candidate = value.Trim();
    if (Uri.CheckHostName(candidate) != UriHostNameType.Dns ||
        candidate.EndsWith(".", StringComparison.Ordinal))
    {
        return false;
    }

    host = candidate;
    return true;
}

public sealed record SentinelGateOptions(string ProtectedHost, Guid ExpectedTenantId);

public sealed record GateEntryResponse(
    string Service,
    bool Allowed,
    string Message,
    bool GatewayValidated,
    bool RoutingContextConsistent,
    Guid? TenantId,
    Guid? ObjectId,
    string? ErrorCode);

public partial class Program;
