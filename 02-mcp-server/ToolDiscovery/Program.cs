using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

if (args.Length is < 1 or > 4 || (args.Length > 1 && (args[1] != "call" || args.Length < 3)))
{
    Console.Error.WriteLine("Usage: dotnet run --project 02-mcp-server/ToolDiscovery -- <mcp-url> [call <queue> [count]]");
    Console.Error.WriteLine("Example: dotnet run --project 02-mcp-server/ToolDiscovery -- http://localhost:5080/mcp");
    return 1;
}

string endpointText = args[0];

if (!Uri.TryCreate(endpointText, UriKind.Absolute, out Uri? endpoint) ||
    endpoint.Scheme is not ("http" or "https"))
{
    Console.Error.WriteLine("The MCP URL must be an absolute http or https URL.");
    return 1;
}

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = endpoint,
    TransportMode = HttpTransportMode.StreamableHttp,
});

await using var client = await McpClient.CreateAsync(transport);

if (args is [_, "call", string queueName, ..])
{
    int count = 1;
    if (args.Length > 3 && !int.TryParse(args[3], out count))
    {
        Console.Error.WriteLine("count must be an integer.");
        return 1;
    }

    CallToolResult result = await client.CallToolAsync(
        "peek_dead_letter",
        new Dictionary<string, object?> { ["queue"] = queueName, ["count"] = count });

    Console.WriteLine($"IsError: {result.IsError}");
    foreach (ContentBlock block in result.Content)
    {
        Console.WriteLine(block is TextContentBlock text ? text.Text : block.ToString());
    }

    return result.IsError == true ? 2 : 0;
}

IList<McpClientTool> tools = await client.ListToolsAsync();

Console.WriteLine($"Server: {endpoint}");
Console.WriteLine($"Tools: {tools.Count}");

foreach (McpClientTool tool in tools)
{
    Console.WriteLine($"""

        Name: {tool.Name}
        Description: {tool.Description}
        Schema: {tool.JsonSchema}
        """);
}

return 0;
