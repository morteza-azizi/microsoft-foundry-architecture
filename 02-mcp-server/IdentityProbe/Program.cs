using System.Reflection;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;

const string DefaultQueue = "orders-scratch";

string queue = args.Length == 0 ? DefaultQueue : args[0];
if (args.Length > 1 ||
    queue.Equals("orders", StringComparison.OrdinalIgnoreCase) ||
    !queue.EndsWith("-scratch", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project 02-mcp-server/IdentityProbe -- [queue]");
    Console.Error.WriteLine(
        "The queue must end with -scratch. This probe will not open 'orders'.");
    return 1;
}

IConfiguration configuration = new ConfigurationBuilder()
    .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
    .AddEnvironmentVariables()
    .Build();

string serviceBusNamespace = RequiredNamespace(configuration);

Console.WriteLine($"""
    Identity probe. This process is not the MCP server and it does not call Foundry.
    Namespace: {serviceBusNamespace}
    Queue: {queue}
    """);

string? clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
Console.WriteLine(clientId is null
    ? "AZURE_CLIENT_ID is not set. DefaultAzureCredential uses the developer sign-in unless this process has a managed identity."
    : $"AZURE_CLIENT_ID: {clientId}");
Console.WriteLine("Case 5 is valid only when this credential is the MCP server's identity. See 02-mcp-server/auth.md.");
Console.WriteLine();

await using var client = new ServiceBusClient(serviceBusNamespace, new DefaultAzureCredential());
await using ServiceBusReceiver receiver = client.CreateReceiver(queue);

try
{
    IReadOnlyList<ServiceBusReceivedMessage> peeked = await receiver.PeekMessagesAsync(10);
    string counts = peeked.Count == 0
        ? "none"
        : string.Join(", ", peeked.Select(message => $"{message.MessageId}={message.DeliveryCount}"));
    Console.WriteLine($"Peek: Allowed ({peeked.Count} message(s); delivery counts: {counts})");
}
catch (Exception ex)
{
    Report("Peek", ex);
}

ServiceBusReceivedMessage? first = await ReceiveAsync(receiver, "Receive (peek-lock)");
if (first is null)
{
    Console.WriteLine("Complete: Not attempted");
}
else
{
    try
    {
        await receiver.CompleteMessageAsync(first);
        Console.WriteLine($"Complete: Allowed ({first.MessageId})");
    }
    catch (Exception ex)
    {
        Report("Complete", ex);
    }
}

ServiceBusReceivedMessage? second = await ReceiveAsync(receiver, "Receive (peek-lock, for dead-letter)");
if (second is null)
{
    Console.WriteLine("Dead-letter: Not attempted");
}
else
{
    try
    {
        await receiver.DeadLetterMessageAsync(second, "identity-probe", "Case 5, under the server identity.");
        Console.WriteLine($"Dead-letter: Allowed ({second.MessageId})");
    }
    catch (Exception ex)
    {
        Report("Dead-letter", ex);
    }
}

try
{
    await using ServiceBusSender sender = client.CreateSender(queue);
    await sender.SendMessageAsync(new ServiceBusMessage("identity probe send")
    {
        MessageId = $"identity-probe-send-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
    });
    Console.WriteLine("Send: Allowed (a message was left on the scratch queue)");
}
catch (Exception ex)
{
    Report("Send", ex);
}

return 0;

static async Task<ServiceBusReceivedMessage?> ReceiveAsync(ServiceBusReceiver receiver, string label)
{
    try
    {
        ServiceBusReceivedMessage? message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine(message is null
            ? $"{label}: Allowed (no message)"
            : $"{label}: Allowed ({message.MessageId}, delivery count {message.DeliveryCount})");
        return message;
    }
    catch (Exception ex)
    {
        Report(label, ex);
        return null;
    }
}

static void Report(string operation, Exception exception)
{
    Console.WriteLine($"{operation}: {(IsDenied(exception) ? "Denied" : "Error")}");
    Console.WriteLine($"  {exception.GetType().Name}: {exception.Message}");
}

static bool IsDenied(Exception exception)
{
    for (Exception? current = exception; current is not null; current = current.InnerException)
    {
        if (current is UnauthorizedAccessException)
        {
            return true;
        }

        if (current is ServiceBusException serviceBus &&
            serviceBus.Reason.ToString().Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (current.Message.Contains("401", StringComparison.Ordinal) ||
            current.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            current.Message.Contains("not authorized", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}

static string RequiredNamespace(IConfiguration configuration)
{
    string? value = configuration["SERVICEBUS_NAMESPACE"] ?? configuration["ServiceBus:Namespace"];
    if (string.IsNullOrWhiteSpace(value) ||
        value.Contains("://", StringComparison.Ordinal) ||
        value.Contains("SharedAccessKey", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "Set SERVICEBUS_NAMESPACE to the fully qualified namespace, for example myns.servicebus.windows.net.");
        Environment.Exit(1);
    }

    return value.Trim();
}
