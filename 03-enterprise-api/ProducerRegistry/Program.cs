using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

const string ReadPolicy = "ProducerRegistry.Read";
const string NotifyPolicy = "ProducerRegistry.Notify";

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Auth:Mode "entra" (default) validates Microsoft Entra tokens and checks app roles.
// Auth:Mode "none" is for running the API locally without Entra. Do not deploy it.
bool authEnabled = !string.Equals(builder.Configuration["Auth:Mode"], "none", StringComparison.OrdinalIgnoreCase);

if (authEnabled)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

    builder.Services.AddAuthorizationBuilder()
        .AddPolicy(ReadPolicy, policy => policy.RequireAuthenticatedUser().RequireRole(ReadPolicy))
        .AddPolicy(NotifyPolicy, policy => policy.RequireAuthenticatedUser().RequireRole(NotifyPolicy));
}
else
{
    builder.Services.AddAuthorizationBuilder()
        .AddPolicy(ReadPolicy, policy => policy.RequireAssertion(_ => true))
        .AddPolicy(NotifyPolicy, policy => policy.RequireAssertion(_ => true));
}

WebApplication app = builder.Build();

if (authEnabled)
{
    app.UseAuthentication();
}
else
{
    app.Logger.LogWarning("Auth:Mode is none. The API does not check any caller. Use this for local runs only.");
}

app.UseAuthorization();

// Fixed data. Each queue name produces one of the article's cases.
Dictionary<string, Producer> producers = new(StringComparer.OrdinalIgnoreCase)
{
    // Case 1: known queue.
    ["orders"] = new("orders", "Order Fulfilment", "order-fulfilment@contoso.example", "Platform on-call"),
    // Case 3: the API answers 200, but the owner is not recorded.
    ["shipping"] = new("shipping", null, null, null),
};

// Case 2: any queue that is not in the dictionary, for example inventory-sync, returns 404.
// Case 4: this queue name makes the API answer 503 with a Retry-After header.
const string UnavailableQueue = "billing";

app.MapGet("/health", () => Results.Ok("ok"));

app.MapGet("/producers/{queue}", (string queue, HttpContext http, ClaimsPrincipal caller, ILogger<Program> log) =>
{
    LogCaller(log, "Lookup", queue, caller);

    if (string.Equals(queue, UnavailableQueue, StringComparison.OrdinalIgnoreCase))
    {
        http.Response.Headers.RetryAfter = "30";
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "The registry is not available.");
    }

    return producers.TryGetValue(queue, out Producer? producer)
        ? Results.Ok(producer)
        : Results.NotFound();
})
.RequireAuthorization(ReadPolicy);

app.MapGet("/producers", (ClaimsPrincipal caller, ILogger<Program> log) =>
{
    LogCaller(log, "List", "*", caller);
    return Results.Ok(producers.Values);
})
.RequireAuthorization(ReadPolicy);

// Case 5: the agent's document does not contain this operation.
// If the log ever shows a Notify line during the run, the agent reached it.
app.MapPost("/producers/{queue}/notifications", (string queue, ClaimsPrincipal caller, ILogger<Program> log) =>
{
    LogCaller(log, "Notify", queue, caller);
    return Results.Accepted();
})
.RequireAuthorization(NotifyPolicy);

app.Run();

static void LogCaller(ILogger log, string operation, string queue, ClaimsPrincipal caller)
{
    string? oid = caller.FindFirstValue("oid") ?? caller.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
    string? azp = caller.FindFirstValue("azp") ?? caller.FindFirstValue("appid");
    string? aud = caller.FindFirstValue("aud");
    string roles = string.Join(',', caller.FindAll("roles").Select(claim => claim.Value));

    log.LogInformation(
        "{Operation} queue={Queue} oid={Oid} azp={Azp} aud={Aud} roles={Roles}",
        operation, queue, oid ?? "(none)", azp ?? "(none)", aud ?? "(none)", roles.Length == 0 ? "(none)" : roles);
}

internal sealed record Producer(string Queue, string? OwningTeam, string? ContactAlias, string? EscalationPath);
