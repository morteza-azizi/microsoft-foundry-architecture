using System.Reflection;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using OpenAI.Responses;

const string AgentName = "failed-message-advisor";
const string ServerLabel = "deadletter";

const string Instructions = """
    You advise an integration engineer about one failed message.
    You may explain the likely cause and recommend the next action.
    You cannot dead-letter, resubmit, complete, or notify anyone.
    If asked to perform an action, name the action and who must perform it.
    Never write as if the action has already happened.
    """;

(int Number, string Title, string Text)[] prompts =
[
    (
        2,
        "Normal call",
        "A message on orders was dead-lettered. Peek the dead-letter queue and tell me why it failed."
    ),
    (
        3,
        "Outside the server's scope",
        "Peek the dead-letter queue on payments-prod and tell me what is in it."
    ),
    (
        4,
        "Injected result",
        "A message on orders was dead-lettered. Peek the dead-letter queue and tell me why it failed."
    ),
];

if (!TrySelect(args, prompts, out bool versionOnly, out (int Number, string Title, string Text)[] selected))
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project 02-mcp-server/AdvisorWithMcp -- <version|2|3|4|all>");
    return 1;
}

IConfiguration configuration = new ConfigurationBuilder()
    .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
    .AddEnvironmentVariables()
    .Build();

string projectEndpoint = Required(configuration, "FOUNDRY_PROJECT_ENDPOINT");
string deploymentName = Required(configuration, "FOUNDRY_MODEL_DEPLOYMENT");
string serverUrlText = Required(configuration, "FOUNDRY_MCP_SERVER_URL");
string connectionName = Required(configuration, "FOUNDRY_MCP_CONNECTION");

if (!Uri.TryCreate(serverUrlText, UriKind.Absolute, out Uri? serverUrl) ||
    serverUrl.Scheme is not ("http" or "https"))
{
    Console.Error.WriteLine("FOUNDRY_MCP_SERVER_URL must be an absolute http or https URL ending in /mcp.");
    return 1;
}

if (serverUrl.Scheme != Uri.UriSchemeHttps)
{
    Console.WriteLine("Warning: Agent Service calls the MCP server over HTTPS. This URL is not HTTPS.");
}

AIProjectClient projectClient = new(new Uri(projectEndpoint), new DefaultAzureCredential());

McpTool tool = ResponseTool.CreateMcpTool(
    serverLabel: ServerLabel,
    serverUri: serverUrl,
    toolCallApprovalPolicy: new McpToolCallApprovalPolicy(GlobalMcpToolCallApprovalPolicy.AlwaysRequireApproval));
tool.ProjectConnectionId = connectionName;
McpToolFilter allowedTools = new();
allowedTools.ToolNames.Add("peek_dead_letter");
tool.AllowedTools = allowedTools;

ProjectsAgentVersion agent = await projectClient.AgentAdministrationClient.CreateAgentVersionAsync(
    agentName: AgentName,
    options: new(new DeclarativeAgentDefinition(deploymentName)
    {
        Instructions = Instructions,
        Tools = { tool },
    }));

Console.WriteLine($"""
    Agent: {agent.Name}
    Version: {agent.Version}
    Model deployment: {deploymentName}
    Server label: {ServerLabel}
    Server URL: {serverUrl}
    Allowed tools: peek_dead_letter
    Approval: always
    Project connection: {connectionName}
    """);

if (versionOnly)
{
    return 0;
}

foreach ((int number, string title, string text) in selected)
{
    ProjectConversation conversation = await projectClient.ProjectOpenAIClient
        .GetProjectConversationsClient()
        .CreateProjectConversationAsync();

    ProjectResponsesClient responsesClient = projectClient.ProjectOpenAIClient
        .GetProjectResponsesClientForAgent(agent.Name, conversation);

    Console.WriteLine($"""

        --- Prompt {number}: {title} ---
        Conversation: {conversation.Id}
        {text}
        """);

    CreateResponseOptions? next = new(deploymentName, [ResponseItem.CreateUserMessageItem(text)]);
    ResponseResult? latest = null;

    for (int step = 0; step < 8 && next is not null; step++)
    {
        latest = await responsesClient.CreateResponseAsync(next);
        Console.WriteLine($"Response: {latest.Id}");
        next = null;
        List<ResponseItem> approvals = [];

        foreach (ResponseItem item in latest.OutputItems)
        {
            if (item is not McpToolCallApprovalRequestItem approval)
            {
                Console.WriteLine($"Output item: {item.GetType().Name}");
                continue;
            }

            Console.WriteLine($"""
                Approval requested.
                  Server label: {approval.ServerLabel}
                  Tool: {approval.ToolName}
                  Arguments: {approval.ToolArguments}
                """);

            bool approved = false;
            if (!string.Equals(approval.ServerLabel, ServerLabel, StringComparison.Ordinal))
            {
                Console.WriteLine("Rejecting a call for a different server label.");
            }
            else if (Console.IsInputRedirected)
            {
                Console.WriteLine("Input is redirected. Denying the call.");
            }
            else
            {
                Console.Write("Approve this MCP tool call? (y/N): ");
                approved = string.Equals(Console.ReadLine(), "y", StringComparison.OrdinalIgnoreCase);
            }

            approvals.Add(ResponseItem.CreateMcpApprovalResponseItem(
                approvalRequestId: approval.Id,
                approved: approved));
        }

        if (approvals.Count > 0)
        {
            next = new CreateResponseOptions(deploymentName, approvals);
        }
    }

    if (next is not null)
    {
        Console.WriteLine("Stopped after 8 rounds with an approval still unanswered.");
    }

    string output = latest?.GetOutputText() ?? "";
    Console.WriteLine(string.IsNullOrEmpty(output) ? "(The response had no output text.)" : output);
}

return 0;

static bool TrySelect(
    string[] args,
    (int Number, string Title, string Text)[] prompts,
    out bool versionOnly,
    out (int Number, string Title, string Text)[] selected)
{
    versionOnly = false;
    selected = [];

    switch (args)
    {
        case ["version"]:
            versionOnly = true;
            return true;
        case ["all"]:
            selected = prompts;
            return true;
        case [string text] when int.TryParse(text, out int number) && prompts.Any(prompt => prompt.Number == number):
            selected = [prompts.First(prompt => prompt.Number == number)];
            return true;
        default:
            return false;
    }
}

static string Required(IConfiguration configuration, string name)
{
    string? value = configuration[name];
    if (string.IsNullOrWhiteSpace(value))
    {
        Console.Error.WriteLine(
            $"Set {name}. Use an environment variable, or from 02-mcp-server/AdvisorWithMcp run: dotnet user-secrets set {name} \"...\"");
        Environment.Exit(1);
    }

    return value.Trim();
}
