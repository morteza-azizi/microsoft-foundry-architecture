using System.Text.Json.Serialization;
using Azure.Messaging.ServiceBus;

namespace DeadLetterMcp;

public sealed record DeadLetterSummary(
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("deadLetterReason")] string? DeadLetterReason,
    [property: JsonPropertyName("deadLetterErrorDescription")] string? DeadLetterErrorDescription,
    [property: JsonPropertyName("deliveryCount")] int DeliveryCount,
    [property: JsonPropertyName("enqueuedTime")] DateTimeOffset EnqueuedTime,
    [property: JsonPropertyName("body")] string Body)
{
    public const int MaxBodyLength = 500;

    public static DeadLetterSummary From(ServiceBusReceivedMessage message)
    {
        // Body is untrusted input, capped at MaxBodyLength. It may contain sensitive data or instructions.
        return new(
            message.MessageId,
            message.DeadLetterReason,
            message.DeadLetterErrorDescription,
            message.DeliveryCount,
            message.EnqueuedTime,
            FirstCharacters(message.Body.ToString(), MaxBodyLength));
    }

    public static string FirstCharacters(string value, int maxCharacters)
    {
        if (value.Length <= maxCharacters)
        {
            return value;
        }

        int cut = maxCharacters;
        if (cut > 0 && char.IsLowSurrogate(value[cut]))
        {
            cut--;
        }

        return value[..cut];
    }
}

public sealed record PeekDeadLetterResult(
    [property: JsonPropertyName("exposed")] bool Exposed,
    [property: JsonPropertyName("messages")] IReadOnlyList<DeadLetterSummary> Messages,
    [property: JsonPropertyName("refusal")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Refusal);
