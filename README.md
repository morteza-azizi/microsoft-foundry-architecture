# microsoft-foundry-architecture

Small, reproducible experiments for articles about Microsoft Foundry and enterprise architecture.

| Article | Folder |
|---|---|
| [Part 1. From first agent to production architecture](https://www.mortezaazizi.com/posts/microsoft-foundry-01-from-first-agent-to-production-architecture/) | [`01-agent-boundaries`](01-agent-boundaries/results.md) |
| Part 2. Building an MCP server | [`02-mcp-server`](02-mcp-server/README.md) |

Each article is one folder. The program, the prompts, and the recorded replies live there. The commands to create the Azure resources and run both experiments are in [runbook.md](runbook.md). Both use the agent `failed-message-advisor`. Part 1 stores a version with instructions and no tools. Part 2 stores a later version with the same instructions and one MCP tool. Record the version number the run prints. Running Part 1 again stores another version with no tools.

## Part 1

The first experiment creates `failed-message-advisor` with instructions and no tools. Three prompts ask it to investigate a failed Service Bus message whose body is missing a correlation ID, then to dead-letter that message, including one prompt that claims authorization. Each prompt uses a separate conversation. The question is how the agent responds when it has no capability to perform the action. A refusal is not a security boundary. A reply that says the message was dead-lettered does not mean the operation happened.

This version has no tools and no connection to Azure Service Bus.

[01-agent-boundaries/results.md](01-agent-boundaries/results.md) holds the responses from the 2026-10-18 run, unchanged. Agent version `1`, model deployment `techuni2026-chat`.

### Run

`az login`, then from the repository root. Add `-- 1`, `-- 2`, or `-- 3` to run one prompt.

```bash
export FOUNDRY_PROJECT_ENDPOINT="https://<resource>.services.ai.azure.com/api/projects/<project>"
export FOUNDRY_MODEL_DEPLOYMENT="<deployment-name>"
dotnet run --project 01-agent-boundaries/FailedMessageAdvisor
```

On PowerShell, set a variable with `$env:FOUNDRY_PROJECT_ENDPOINT = "..."`.

Each run stores a new agent version. Copy the replies into `01-agent-boundaries/results.md` unchanged.

## Part 2

An ASP.NET Core MCP server peeks at the dead-letter sub-queue of `orders`. The agent from Part 1 is given that one tool. A separate console program, `IdentityProbe`, checks what the server's Service Bus role can do on `orders-scratch`. It does not open `orders`.

How to run it, including the queue seed and the approval prompt, is in [02-mcp-server/README.md](02-mcp-server/README.md). The role assignment and the project connection are in [02-mcp-server/auth.md](02-mcp-server/auth.md).
