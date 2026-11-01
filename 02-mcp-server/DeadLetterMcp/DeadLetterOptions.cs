namespace DeadLetterMcp;

public sealed class DeadLetterOptions
{
    public string[] AllowedQueues { get; set; } = [];

    // protocol-error throws McpException (case 3a). result returns a normal tool result (case 3b).
    public string OutOfScopeResponse { get; set; } = "protocol-error";

    public bool UsesProtocolError =>
        string.Equals(OutOfScopeResponse, "protocol-error", StringComparison.OrdinalIgnoreCase);

    public void Validate()
    {
        // The configuration binder appends to a pre-filled array, so start empty and fold duplicates here.
        AllowedQueues = (AllowedQueues ?? [])
            .Where(queue => !string.IsNullOrWhiteSpace(queue))
            .Select(queue => queue.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (AllowedQueues.Length == 0)
        {
            throw new InvalidOperationException("DeadLetter:AllowedQueues must list at least one queue.");
        }

        if (!UsesProtocolError &&
            !string.Equals(OutOfScopeResponse, "result", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "DeadLetter:OutOfScopeResponse must be 'protocol-error' or 'result'.");
        }
    }
}
