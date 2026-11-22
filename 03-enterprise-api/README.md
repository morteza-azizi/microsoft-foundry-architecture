# Part 3. Enterprise API

Code for the article *Connecting Foundry Agents to Enterprise APIs*. Part 1 is `01-agent-boundaries`. Part 2 is `02-mcp-server`. This folder has an API the agent calls, the document the agent receives, and the gateway policy.

The agent name stays `failed-message-advisor`. This part adds one tool: an OpenAPI tool with one operation, `getProducerForQueue`.

| File or folder | What it is |
|---|---|
| `ProducerRegistry` | A small ASP.NET Core API. Fixed data. It logs `oid`, `azp`, `aud` and `roles` on every call. |
| `producer-registry.agent.openapi.yaml` | The OpenAPI document the agent receives. One operation. |
| `app-roles.json` | The two app roles on the API: `ProducerRegistry.Read` and `ProducerRegistry.Notify`. |
| `apim-policy.xml` | Path B. Token check, a per caller limit, and a correlation id. |
| `AdvisorWithRegistry` | Stores an agent version with the OpenAPI tool and sends one prompt per conversation. |

Authentication and the Entra setup are in [auth.md](auth.md). Prompts and expectations are in [prompts.md](prompts.md). Paste the replies into [results.md](results.md) unchanged.

## Cases

The queue name chooses the case. The data is fixed.

| Queue | API answer |
|---|---|
| `orders` | `200`, with a team and an alias |
| `inventory-sync` | `404` (it is not in the data) |
| `shipping` | `200`, with `owningTeam` and `contactAlias` set to `null` |
| `billing` | `503` with `Retry-After: 30` |

Case 5 has no queue of its own. The agent is asked to notify a producer. The agent's document has no such operation, and the project's identity does not have the Notify role. If a `Notify` line ever appears in the API log during the run, the agent reached an operation it should not have.

## Run the API locally

```bash
export Auth__Mode=none
dotnet run --project 03-enterprise-api/ProducerRegistry
```

On PowerShell, use `$env:Auth__Mode = "none"`.

The API listens on `http://localhost:5090`. `Auth__Mode=none` turns off every check. Use it only to look at the five answers:

```bash
curl -i http://localhost:5090/producers/orders
curl -i http://localhost:5090/producers/inventory-sync
curl -i http://localhost:5090/producers/shipping
curl -i http://localhost:5090/producers/billing
```

To check the identity question, run the API with Entra enabled (see [auth.md](auth.md)). Without a token, `/producers/orders` returns `401`. `/health` never checks a caller.

## Deploy

```bash
docker build -t producer-registry 03-enterprise-api/ProducerRegistry
```

Agent Service cannot call localhost. Deploy the image where Agent Service can reach it over HTTPS, for example Azure Container Apps. Set `AzureAd__TenantId`, `AzureAd__ClientId` and `AzureAd__Audience` on the host.

## Path A and path B

Path A: the agent's server URL is the API. Path B: it is the API Management URL. `AdvisorWithRegistry` reads `producer-registry.agent.openapi.yaml`, replaces the placeholder `https://<host>` with `FOUNDRY_REGISTRY_SERVER_URL`, and stores a new agent version. Leave the placeholder in the file. Run the same prompts on both paths. Record which URL each run used.

```powershell
$env:FOUNDRY_PROJECT_ENDPOINT = "https://<resource>.services.ai.azure.com/api/projects/<project>"
$env:FOUNDRY_MODEL_DEPLOYMENT = "<deployment-name>"
$env:FOUNDRY_REGISTRY_SERVER_URL = "https://<api-or-gateway>"
$env:AZURE_TOKEN_CREDENTIALS = "AzureCliCredential"
dotnet run --project 03-enterprise-api/AdvisorWithRegistry -- version
dotnet run --project 03-enterprise-api/AdvisorWithRegistry -- all
```

`burst` repeats prompt 1 ten times. That is the Path B limit check. The version it stores has the OpenAPI tool only. It does not attach the Part 2 MCP tool.
