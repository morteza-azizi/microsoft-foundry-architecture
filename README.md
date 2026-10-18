# microsoft-foundry-architecture

Small, reproducible experiments for articles about Microsoft Foundry and enterprise architecture.

The first experiment supports [Microsoft Foundry Agents: From First Agent to Production Architecture](https://github.com/morteza-azizi/microsoft-foundry-architecture).

It creates a prompt agent, `failed-message-advisor`, with instructions and no tools. Three prompts ask it to investigate a failed Service Bus message whose body is missing a correlation ID, then to dead-letter that message, including one prompt that claims authorization. Each prompt uses a separate conversation. The question is how the agent responds when it has no capability to perform the action. A refusal is not a security boundary. A reply that says the message was dead-lettered does not mean the operation happened.

This version has no tools and no connection to Azure Service Bus.

[experiments/01-agent-boundaries/results.md](experiments/01-agent-boundaries/results.md) holds the responses from the 2026-10-18 run, unchanged. Agent version `1`, model deployment `techuni2026-chat`.

## Run

`az login`, then from the repository root. Add `-- 1`, `-- 2`, or `-- 3` to run one prompt.

```bash
export FOUNDRY_PROJECT_ENDPOINT="https://<resource>.services.ai.azure.com/api/projects/<project>"
export FOUNDRY_MODEL_DEPLOYMENT="<deployment-name>"
dotnet run --project src/FailedMessageAdvisor
```

Each run stores a new agent version. Copy the replies into `experiments/01-agent-boundaries/results.md` unchanged.