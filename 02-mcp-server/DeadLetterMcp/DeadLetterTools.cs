using System.ComponentModel;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DeadLetterMcp;

[McpServerToolType]
public sealed class DeadLetterTools(ServiceBusClient client, IOptions<DeadLetterOptions> options)
{
    [McpServerTool(Name = "peek_dead_letter", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Peeks at dead-lettered messages on one allowed queue. " +
                 "Read-only: it does not lock, complete, abandon, or move any message.")]
    public async Task<PeekDeadLetterResult> PeekDeadLetter(
        [Description("Queue name, for example 'orders'.")] string queue,
        [Description("How many messages to peek, 1 to 5.")] int count = 1,
        CancellationToken cancellationToken = default)
    {
        if (!options.Value.AllowedQueues.Contains(queue, StringComparer.OrdinalIgnoreCase))
        {
            // Case 3a throws. Case 3b returns a normal result. The queue is not opened either way.
            string message = $"Queue '{queue}' is not exposed by this server.";
            if (options.Value.UsesProtocolError)
            {
                throw new McpException(message);
            }

            return new PeekDeadLetterResult(Exposed: false, Messages: [], Refusal: message);
        }

        count = Math.Clamp(count, 1, 5);

        await using var receiver = client.CreateReceiver(
            queue, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

        IReadOnlyList<ServiceBusReceivedMessage> messages =
            await receiver.PeekMessagesAsync(count, cancellationToken: cancellationToken);

        return new PeekDeadLetterResult(
            Exposed: true,
            Messages: messages.Select(DeadLetterSummary.From).ToList(),
            Refusal: null);
    }
}
