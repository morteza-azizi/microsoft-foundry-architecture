using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using DeadLetterMcp;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

string? configuredNamespace = builder.Configuration["ServiceBus:Namespace"];
if (string.IsNullOrWhiteSpace(configuredNamespace))
{
    configuredNamespace = builder.Configuration["SERVICEBUS_NAMESPACE"];
}

string serviceBusNamespace = RequiredNamespace(configuredNamespace);

builder.Services.AddSingleton(new ServiceBusClient(serviceBusNamespace, new DefaultAzureCredential()));
builder.Services.Configure<DeadLetterOptions>(builder.Configuration.GetSection("DeadLetter"));

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<DeadLetterTools>();

var app = builder.Build();

DeadLetterOptions deadLetter = app.Services.GetRequiredService<IOptions<DeadLetterOptions>>().Value;
deadLetter.Validate();

app.Logger.LogInformation(
    "Dead-letter MCP server. Namespace: {Namespace}. Allowed queues: {Queues}. Out-of-scope response: {Mode}.",
    serviceBusNamespace,
    string.Join(", ", deadLetter.AllowedQueues),
    deadLetter.OutOfScopeResponse);

app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) &&
        context.Request.Path.StartsWithSegments("/mcp"))
    {
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        string body = await reader.ReadToEndAsync(context.RequestAborted);
        context.Request.Body.Position = 0;
        LogMcpMethod(app.Logger, body);
    }

    await next(context);
});

app.MapGet("/health", () => Results.Text("healthy"));
app.MapMcp("/mcp");
app.Run();

static string RequiredNamespace(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException(
            "Set ServiceBus:Namespace to the fully qualified Service Bus namespace, " +
            "for example myns.servicebus.windows.net. " +
            "ServiceBus__Namespace or SERVICEBUS_NAMESPACE overrides appsettings.json.");
    }

    if (value.Contains("://", StringComparison.Ordinal) ||
        value.Contains("SharedAccessKey", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("ServiceBus:Namespace must be the host name, not a connection string.");
    }

    return value.Trim();
}

static void LogMcpMethod(ILogger logger, string body)
{
    try
    {
        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("method", out JsonElement methodElement))
        {
            return;
        }

        string method = methodElement.GetString() ?? "";
        if (method == "tools/call" &&
            document.RootElement.TryGetProperty("params", out JsonElement parameters))
        {
            string tool = parameters.TryGetProperty("name", out JsonElement name)
                ? name.GetString() ?? ""
                : "";
            string arguments = parameters.TryGetProperty("arguments", out JsonElement args)
                ? args.GetRawText()
                : "";
            logger.LogInformation("MCP tools/call {Tool} {Arguments}", tool, arguments);
            return;
        }

        logger.LogInformation("MCP {Method}", method);
    }
    catch (JsonException)
    {
        logger.LogInformation("MCP request was not a JSON-RPC object.");
    }
}
