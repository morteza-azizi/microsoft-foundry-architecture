# Run the experiments

PowerShell, from the repository root. Fill the names in [Values](#values) once per session. Part 1 needs a Foundry project and a model deployment. Part 2 also needs Service Bus and a public HTTPS host for the MCP server.

Agent Service cannot call `localhost`. `ToolDiscovery` and a local `DeadLetterMcp` are enough to see the tool list. Cases that go through Foundry need the public URL.

This server does not authenticate `/mcp`. A container and an HTTPS URL are how Agent Service reaches the experiment. They do not make the endpoint production-secure. Run it where the network is the boundary. Details are in [02-mcp-server/auth.md](02-mcp-server/auth.md).

Paste replies into the results file unchanged. Expected lines in the prompts are what the experiment is looking for. They are not observations.

## Values

```powershell
$env:SUBSCRIPTION = "<subscription-id>"
$env:RESOURCE_GROUP = "<resource-group>"
$env:LOCATION = "<region>"
$env:SB_NAMESPACE = "<namespace-name>"
$env:SERVICEBUS_NAMESPACE = "$($env:SB_NAMESPACE).servicebus.windows.net"
$env:FOUNDRY_PROJECT_ENDPOINT = "https://<resource>.services.ai.azure.com/api/projects/<project>"
$env:FOUNDRY_MODEL_DEPLOYMENT = "<deployment-name>"
$env:FOUNDRY_MCP_SERVER_URL = "https://<server-host>/mcp"
$env:FOUNDRY_MCP_CONNECTION = "deadletter"
$env:SERVER_PRINCIPAL_ID = "<managed-identity-object-id>"
```

`SERVICEBUS_NAMESPACE` is the host name, for example `myns.servicebus.windows.net`. It is not a connection string.

The signed-in user must be allowed to create agent versions in that Foundry project. This repository does not create the project or the model deployment. Part 1 of the recorded run used the deployment name `techuni2026-chat`. Use the deployment that exists in your project.

## 1. Sign in and create Service Bus

```powershell
az login
az account set --subscription $env:SUBSCRIPTION
az group create --name $env:RESOURCE_GROUP --location $env:LOCATION
az servicebus namespace create --resource-group $env:RESOURCE_GROUP --name $env:SB_NAMESPACE --location $env:LOCATION --sku Standard
az servicebus queue create --resource-group $env:RESOURCE_GROUP --namespace-name $env:SB_NAMESPACE --name orders
az servicebus queue create --resource-group $env:RESOURCE_GROUP --namespace-name $env:SB_NAMESPACE --name orders-scratch
```

`payments-prod` is not created. The server refuses that name before it opens a receiver.

## 2. Assign roles

Two identities. Your user seeds and snapshots. The server identity only receives.

Your user needs send, receive, and the management claim `GetQueueRuntimeProperties` uses. Assign `Azure Service Bus Data Owner` on `orders` and `orders-scratch`. Do not assign that role to the server identity.

```powershell
$env:USER_OBJECT_ID = az ad signed-in-user show --query id -o tsv
$orders = "/subscriptions/$($env:SUBSCRIPTION)/resourceGroups/$($env:RESOURCE_GROUP)/providers/Microsoft.ServiceBus/namespaces/$($env:SB_NAMESPACE)/queues/orders"
$scratch = "/subscriptions/$($env:SUBSCRIPTION)/resourceGroups/$($env:RESOURCE_GROUP)/providers/Microsoft.ServiceBus/namespaces/$($env:SB_NAMESPACE)/queues/orders-scratch"

az role assignment create --assignee $env:USER_OBJECT_ID --role "Azure Service Bus Data Owner" --scope $orders
az role assignment create --assignee $env:USER_OBJECT_ID --role "Azure Service Bus Data Owner" --scope $scratch
```

The design assignment for the server is one role, on `orders` only. `$env:SERVER_PRINCIPAL_ID` is the managed identity object id of the process that will run `DeadLetterMcp`. A local `az login` is a different principal.

```powershell
az role assignment create --assignee $env:SERVER_PRINCIPAL_ID --role "Azure Service Bus Data Receiver" --scope $orders
```

Role changes can take a few minutes to apply. No send role and no management role go on the server identity.

## 3. Run Part 1

Part 1 has no Service Bus connection. Each run stores a new version of `failed-message-advisor` with instructions and no tools. Record the version number it prints.

```powershell
dotnet run --project 01-agent-boundaries/FailedMessageAdvisor
```

One prompt: add `-- 1`, `-- 2`, or `-- 3`. Copy the replies into [01-agent-boundaries/results.md](01-agent-boundaries/results.md). The 2026-10-18 replies already in that file stay as they are. A new run is a new record.

Running this again after Part 2 stores another version with no tools.

## 4. Host the MCP server

Build the image from this repository:

```powershell
docker build -t dead-letter-mcp 02-mcp-server/DeadLetterMcp
```

This repository does not choose the Azure host. The running process needs:

- `SERVICEBUS_NAMESPACE` set to the host name above
- `AllowedHosts` set to the public host name (the checked-in value is `localhost;127.0.0.1`)
- the managed identity whose object id is `$env:SERVER_PRINCIPAL_ID`
- HTTPS on `/mcp`, which is what you put in `FOUNDRY_MCP_SERVER_URL`

`/health` does not touch Service Bus.

Locally, in a separate window, the same process listens on `http://localhost:5080` and uses your `az login` user. That local process is the wrong identity for case 5.

```powershell
dotnet run --project 02-mcp-server/DeadLetterMcp
```

Startup logs the namespace, the allow list `orders`, the out-of-scope mode, and a warning that `/mcp` does not authenticate callers. The log records the MCP method and, for a call, the tool name. It does not record arguments or message bodies.

## 5. Project connection

`auth-type none` matches this open endpoint. It is the setting for this experiment.

```powershell
azd ai project set $env:FOUNDRY_PROJECT_ENDPOINT
azd ai connection create deadletter --kind remote-tool --target $env:FOUNDRY_MCP_SERVER_URL --auth-type none
```

If the connection target and `FOUNDRY_MCP_SERVER_URL` disagree, Agent Service uses the connection.

## 6. Part 2, case 1

With the local server running:

```powershell
dotnet run --project 02-mcp-server/ToolDiscovery -- http://localhost:5080/mcp
dotnet run --project 02-mcp-server/ToolDiscovery -- http://localhost:5080/mcp call payments-prod
```

The first command is `tools/list` outside Foundry. The second invokes `peek_dead_letter` directly. The agent run is still the experiment.

Creating the agent version does not call the server. Watch the deployed server log, not the local window, for `MCP tools/list` when Agent Service connects:

```powershell
dotnet run --project 02-mcp-server/AdvisorWithMcp -- version
```

Record that version number. Tool arguments for the results table come from the approval card this program prints, not from the server log.

## 7. Part 2, cases 2, 3, and 4

Run one prompt at a time, in a real console. Redirected input denies the approval. At the prompt `Approve this MCP tool call? (y/N):`, type `y` to allow the call.

`seed` receives and completes existing messages before it sends. `seed normal` and `seed injection` do that on the `orders` dead-letter sub-queue. `seed scratch` does that on the active queue of `orders-scratch`. Only those two queues are accepted. If the drain stops at 50, messages may remain.

Record a snapshot only when the command prints `Snapshot complete` and `$LASTEXITCODE` is 0. Any other snapshot output is incomplete.

The tool body is at most 500 characters. It is untrusted and may contain sensitive data.

### Case 2

```powershell
dotnet run --project 02-mcp-server/QueuePrep -- seed normal
dotnet run --project 02-mcp-server/QueuePrep -- snapshot orders
dotnet run --project 02-mcp-server/AdvisorWithMcp -- 2
dotnet run --project 02-mcp-server/QueuePrep -- snapshot orders
```

### Case 3

The default server mode is `protocol-error` (3a). Leave the deployed server on that mode, snapshot `orders`, run the prompt, and snapshot again.

```powershell
dotnet run --project 02-mcp-server/QueuePrep -- snapshot orders
dotnet run --project 02-mcp-server/AdvisorWithMcp -- 3
dotnet run --project 02-mcp-server/QueuePrep -- snapshot orders
```

Stop the server and start it again in that window with the result mode. The startup log states the mode. `DeadLetter__OutOfScopeResponse` is read by `DeadLetterMcp`, so set it in the server window:

```powershell
$env:DeadLetter__OutOfScopeResponse = "result"
dotnet run --project 02-mcp-server/DeadLetterMcp
```

Then snapshot, run the same prompt, and snapshot again:

```powershell
dotnet run --project 02-mcp-server/QueuePrep -- snapshot orders
dotnet run --project 02-mcp-server/AdvisorWithMcp -- 3
dotnet run --project 02-mcp-server/QueuePrep -- snapshot orders
```

The server does not open `payments-prod`. Snapshot `orders` anyway. Record the state of `payments-prod` separately if that queue exists.

Do not use `-- all`. That runs cases 2, 3, and 4 in one process and skips the seed and the server restart between them.

### Case 4

Seed after case 2. The injection seed removes the case 2 dead-letter message, then dead-letters one message whose body is the notice text.

```powershell
dotnet run --project 02-mcp-server/QueuePrep -- seed injection
dotnet run --project 02-mcp-server/QueuePrep -- snapshot orders
dotnet run --project 02-mcp-server/AdvisorWithMcp -- 4
dotnet run --project 02-mcp-server/QueuePrep -- snapshot orders
```

## 8. Part 2, case 5

Add the scratch assignment only for this probe. Record the role list before you add it if the row you want for the design is the single assignment on `orders`.

```powershell
az role assignment create --assignee $env:SERVER_PRINCIPAL_ID --role "Azure Service Bus Data Receiver" --scope $scratch
```

Seed two active messages with your user, then run the probe as the server identity. On the host, the managed identity does that. Locally, `DefaultAzureCredential` uses your user unless `AZURE_CLIENT_ID` and a client secret or federated token select the server principal. The probe prints whether `AZURE_CLIENT_ID` is set. It refuses `orders`.

```powershell
dotnet run --project 02-mcp-server/QueuePrep -- seed scratch
dotnet run --project 02-mcp-server/IdentityProbe
```

Each line is `Allowed`, `Denied`, or `Inconclusive`. `Inconclusive` is not a denial. `Not attempted` means that operation was not run.

Remove the scratch assignment when the probe is done:

```powershell
az role assignment delete --assignee $env:SERVER_PRINCIPAL_ID --role "Azure Service Bus Data Receiver" --scope $scratch
```

## 9. Record the run

Paste into [02-mcp-server/results.md](02-mcp-server/results.md):

- the agent version, the server URL, the connection name, and the mode used for 3a and 3b
- each reply, unchanged
- queue snapshots that printed `Snapshot complete`
- case 5 lines as printed
- the audit commands below, unchanged

```powershell
az role assignment list --assignee $env:SERVER_PRINCIPAL_ID --all --include-inherited -o table
az role definition list --name "Azure Service Bus Data Receiver" --query "[0].permissions[0].{actions:actions,dataActions:dataActions}"
```

For the data-plane audit, enable the Service Bus log category `RuntimeAuditLogs` on the namespace and send it to a Log Analytics workspace. Management logs do not show these operations.

```powershell
$namespaceId = az servicebus namespace show --resource-group $env:RESOURCE_GROUP --name $env:SB_NAMESPACE --query id -o tsv
az monitor diagnostic-settings create --name runtime-audit --resource $namespaceId --workspace "<log-analytics-workspace-id>" --logs '[{"category":"RuntimeAuditLogs","enabled":true}]'
```

`QueuePrep` stays on your user. Do not add send or management rights to the server identity so that seeding can run.
