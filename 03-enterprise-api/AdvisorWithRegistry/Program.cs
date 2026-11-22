using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using OpenAI.Responses;
using YamlDotNet.Serialization;

const string AgentName = "failed-message-advisor";
const string ToolName = "producer_registry";
const string DefaultAudience = "api://producer-registry";

const string Instructions = """
    You advise an integration engineer about one failed message.
    You may explain the likely cause and recommend the next action.
    You cannot dead-letter, resubmit, complete, or notify anyone.
    If asked to perform an action, name the action and who must perform it.
    Never write as if the action has already happened.
    """;

(int Number, string Title, string Text)[] prompts =
[
    (1, "Known queue", "Who owns the producer of the orders queue?"),
    (2, "Unknown queue", "Who owns the producer of the inventory-sync queue?"),
    (3, "Missing field", "Who owns the producer of the shipping queue?"),
    (4, "Registry down", "Who owns the producer of the billing queue?"),
    (5, "Notify", "Notify the producer of the orders queue that the message is fixed."),
];

if (!TrySelect(args, prompts, out bool versionOnly, out bool specOnly, out (int Number, string Title, string Text)[] selected))
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project 03-enterprise-api/AdvisorWithRegistry -- <spec|version|1|2|3|4|5|all|burst>");
    return 1;
}

IConfiguration configuration = new ConfigurationBuilder()
    .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
    .AddEnvironmentVariables()
    .Build();

string serverUrlText = Required(configuration, "FOUNDRY_REGISTRY_SERVER_URL");
if (!Uri.TryCreate(serverUrlText, UriKind.Absolute, out Uri? serverUrl) || serverUrl.Scheme != Uri.UriSchemeHttps)
{
    Console.Error.WriteLine("FOUNDRY_REGISTRY_SERVER_URL must be an absolute https URL. Agent Service cannot call localhost.");
    return 1;
}

string? configuredAudience = configuration["FOUNDRY_OPENAPI_AUDIENCE"];
string audience = string.IsNullOrWhiteSpace(configuredAudience) ? DefaultAudience : configuredAudience.Trim();
JsonObject specification = LoadSpecification(serverUrlText);
string operationId = specification["paths"]?["/producers/{queue}"]?["get"]?["operationId"]?.GetValue<string>()
    ?? "";
if (!string.Equals(operationId, "getProducerForQueue", StringComparison.Ordinal))
{
    Console.Error.WriteLine("The agent spec must contain only getProducerForQueue. Refusing to store a version.");
    return 1;
}

var tool = new OpenAPITool(new OpenApiFunctionDefinition(
    ToolName,
    BinaryData.FromString(specification.ToJsonString()),
    new OpenAPIManagedAuthenticationDetails(new OpenAPIManagedSecurityScheme(audience)))
{
    Description = "Looks up the team that owns the producer of a Service Bus queue.",
});

JsonNode? toolJson = JsonNode.Parse(ModelReaderWriter.Write(tool).ToString());
Console.WriteLine($"""
    Tool: {ToolName}
    Auth: managed_identity
    Audience: {audience}
    Server URL: {serverUrlText}
    Operation in the agent spec: {operationId}
    MCP tool: not attached
    SDK: Azure.AI.Projects {typeof(AIProjectClient).Assembly.GetName().Version}
    """);

if (specOnly)
{
    Console.WriteLine(toolJson?.ToJsonString());
    return 0;
}

string projectEndpoint = Required(configuration, "FOUNDRY_PROJECT_ENDPOINT");
string deploymentName = Required(configuration, "FOUNDRY_MODEL_DEPLOYMENT");

var body = new JsonObject
{
    ["definition"] = new JsonObject
    {
        ["kind"] = "prompt",
        ["model"] = deploymentName,
        ["instructions"] = Instructions,
        ["tools"] = new JsonArray(toolJson),
    },
};

AIProjectClient projectClient = new(new Uri(projectEndpoint), new DefaultAzureCredential());
ClientResult created = await projectClient.AgentAdministrationClient.CreateAgentVersionAsync(
    AgentName,
    BinaryContent.Create(BinaryData.FromString(body.ToJsonString())),
    foundryFeatures: null,
    new RequestOptions());

PipelineResponse raw = created.GetRawResponse();
string createdJson = raw.Content?.ToString() ?? "";
if (raw.Status < 200 || raw.Status >= 300)
{
    Console.Error.WriteLine($"Create agent version failed with HTTP {raw.Status}.");
    Console.Error.WriteLine(Trim(createdJson));
    return 1;
}

string version = ReadVersion(createdJson);
Console.WriteLine($"""
    Agent: {AgentName}
    Version: {version}
    Model deployment: {deploymentName}
    """);

if (versionOnly)
{
    return 0;
}

foreach ((int number, string title, string text) in selected)
{
    await RunPromptAsync(projectClient, deploymentName, number.ToString(CultureInfo.InvariantCulture), title, text);
}

return 0;

static async Task RunPromptAsync(
    AIProjectClient projectClient,
    string deploymentName,
    string number,
    string title,
    string text)
{
    ProjectConversation conversation = await projectClient.ProjectOpenAIClient
        .GetProjectConversationsClient()
        .CreateProjectConversationAsync();

    ProjectResponsesClient responsesClient = projectClient.ProjectOpenAIClient
        .GetProjectResponsesClientForAgent(AgentName, conversation);

    Console.WriteLine($"""

        --- Prompt {number}: {title} ---
        Conversation: {conversation.Id}
        {text}
        """);

    CreateResponseOptions? next = new(deploymentName, [ResponseItem.CreateUserMessageItem(text)]);
    ResponseResult? latest = null;
    bool approvalSeen = false;

    for (int step = 0; step < 8 && next is not null; step++)
    {
        latest = await responsesClient.CreateResponseAsync(next);
        Console.WriteLine($"Response: {latest.Id}");
        next = null;

        foreach (ResponseItem item in latest.OutputItems)
        {
            string itemType = item.GetType().Name;
            Console.WriteLine($"Output item: {itemType}");
            if (itemType.Contains("Approval", StringComparison.OrdinalIgnoreCase)
                || itemType.Contains("Consent", StringComparison.OrdinalIgnoreCase))
            {
                approvalSeen = true;
                Console.WriteLine("Approval or consent item appeared. This program does not answer it.");
            }
        }

        string responseJson = ModelReaderWriter.Write(latest).ToString();
        Console.WriteLine(responseJson);
        if (responseJson.Contains("approval", StringComparison.OrdinalIgnoreCase)
            || responseJson.Contains("consent", StringComparison.OrdinalIgnoreCase))
        {
            approvalSeen = true;
        }
    }

    string outputText = latest?.GetOutputText() ?? "";
    Console.WriteLine(string.IsNullOrEmpty(outputText) ? "(The response had no output text.)" : outputText);
    Console.WriteLine($"Approval prompt seen: {(approvalSeen ? "yes" : "no")}");
}

static JsonObject LoadSpecification(string serverUrl)
{
    string path = Path.Combine(AppContext.BaseDirectory, "producer-registry.agent.openapi.yaml");
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"Could not find {path}.");
        Environment.Exit(1);
    }

    object? yaml = new DeserializerBuilder()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build()
        .Deserialize(new StringReader(File.ReadAllText(path)));
    if (ToNode(yaml) is not JsonObject specification)
    {
        throw new InvalidOperationException("The OpenAPI document did not parse as an object.");
    }

    JsonNode? urlNode = specification["servers"]?[0]?["url"];
    if (!string.Equals(urlNode?.GetValue<string>(), "https://<host>", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("producer-registry.agent.openapi.yaml must keep the placeholder server URL https://<host>.");
        Environment.Exit(1);
    }

    specification["servers"]![0]!["url"] = serverUrl;
    JsonObject? paths = specification["paths"] as JsonObject;
    if (paths is null || paths.Count != 1 || !paths.ContainsKey("/producers/{queue}"))
    {
        Console.Error.WriteLine("The agent spec must contain exactly one path, /producers/{queue}.");
        Environment.Exit(1);
    }

    return specification;
}

static JsonNode? ToNode(object? value)
{
    switch (value)
    {
        case null:
            return null;
        case string text:
            return JsonValue.Create(text);
        case bool flag:
            return JsonValue.Create(flag);
        case byte or sbyte or short or ushort or int or uint or long or ulong:
            return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
        case float or double or decimal:
            return JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture));
        case System.Collections.IDictionary dictionary:
            JsonObject map = [];
            foreach (System.Collections.DictionaryEntry entry in dictionary)
            {
                map[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)!] = ToNode(entry.Value);
            }

            return map;
        case System.Collections.IEnumerable enumerable:
            JsonArray array = [];
            foreach (object? item in enumerable)
            {
                array.Add(ToNode(item));
            }

            return array;
        default:
            return JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture));
    }
}

static string ReadVersion(string json)
{
    using JsonDocument document = JsonDocument.Parse(json);
    JsonElement root = document.RootElement;
    if (root.TryGetProperty("version", out JsonElement version))
    {
        return version.ToString();
    }

    if (root.TryGetProperty("name", out JsonElement name))
    {
        string? nameText = name.GetString();
        int slash = nameText?.LastIndexOf('/') ?? -1;
        if (slash >= 0 && slash < nameText!.Length - 1)
        {
            return nameText[(slash + 1)..];
        }
    }

    throw new InvalidOperationException("The create response had no version. " + Trim(json));
}

static string Trim(string text)
{
    const int limit = 4000;
    return text.Length <= limit ? text : text[..limit] + "...";
}

static bool TrySelect(
    string[] args,
    (int Number, string Title, string Text)[] prompts,
    out bool versionOnly,
    out bool specOnly,
    out (int Number, string Title, string Text)[] selected)
{
    versionOnly = false;
    specOnly = false;
    selected = [];

    switch (args)
    {
        case ["spec"]:
            specOnly = true;
            return true;
        case ["version"]:
            versionOnly = true;
            return true;
        case ["all"]:
            selected = prompts;
            return true;
        case ["burst"]:
            selected = Enumerable.Range(1, 10)
                .Select(index => (index, "Burst", prompts[0].Text))
                .ToArray();
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
            $"Set {name}. Use an environment variable, or from 03-enterprise-api/AdvisorWithRegistry run: dotnet user-secrets set {name} \"...\"");
        Environment.Exit(1);
    }

    return value.Trim();
}
