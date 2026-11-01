# Authentication

Two identities. They are not the same permission.

The **project connection** is how Agent Service calls the MCP server. The **server identity** is how that process calls Service Bus. A caller who gets through the first one acts as the second one.

This article does not put authentication on the HTTP endpoint. The server accepts any caller who can open `/mcp`. A container, a public HTTPS URL, and `AllowedHosts` do not change that. Do not treat a deployment of this server as production-secure. Run it where the network is the boundary. Who may call the server is the gap Part 3 starts from.

## Project connection

Agent Service needs a remote HTTPS URL. Create the connection in the project, then set `FOUNDRY_MCP_CONNECTION` to its name. `AdvisorWithMcp` writes that name into `project_connection_id`.

This server does not check a credential. `auth-type none` matches that open endpoint. It is the setting for this experiment, not a production authentication choice:

```bash
azd ai project set "https://<resource>.services.ai.azure.com/api/projects/<project>"

azd ai connection create deadletter \
  --kind remote-tool \
  --target https://<server-host>/mcp \
  --auth-type none
```

`FOUNDRY_MCP_SERVER_URL` is the same URL. If the connection and the tool definition disagree, Agent Service uses the connection's target.

`allowed_tools` is `peek_dead_letter`. `require_approval` is `always`. Both are set in `AdvisorWithMcp`.

## Server identity on Service Bus

The server process uses `DefaultAzureCredential`. On a host with a managed identity, that is the identity to assign. Locally, `dotnet run` uses your `az login` user, which is a different principal and is the wrong one for case 5.

Assign `Azure Service Bus Data Receiver` on the `orders` queue, not on the namespace:

```bash
az role assignment create \
  --assignee <server-principal-id> \
  --role "Azure Service Bus Data Receiver" \
  --scope /subscriptions/<subscription>/resourceGroups/<group>/providers/Microsoft.ServiceBus/namespaces/<namespace>/queues/orders
```

`<server-principal-id>` is the managed identity's object id.

The design is one assignment. Case 5 needs a second one, on `orders-scratch`, and only while that probe runs. Record the list before you add it if the row you want is the design, then record it again for the probe. Remove the scratch assignment when you are done.

```bash
az role assignment create \
  --assignee <server-principal-id> \
  --role "Azure Service Bus Data Receiver" \
  --scope /subscriptions/<subscription>/resourceGroups/<group>/providers/Microsoft.ServiceBus/namespaces/<namespace>/queues/orders-scratch
```

No send role. No management role. Nothing on any other queue.

These two commands are the audit. Paste the output into the experiment record unchanged.

```bash
az role assignment list --assignee <server-principal-id> --all --include-inherited -o table
az role definition list --name "Azure Service Bus Data Receiver" \
  --query "[0].permissions[0].{actions:actions,dataActions:dataActions}"
```

To run `IdentityProbe` as that identity, the process credential has to be that principal. On Azure, a managed identity does that. Locally, set the environment variables `DefaultAzureCredential` already reads (`AZURE_CLIENT_ID`, and a client secret or federated token) for a service principal that is this identity, or that has only the same role assignment. The probe prints whether `AZURE_CLIENT_ID` is set. It does not check that the principal is the right one.

## Diagnostic logs

Enable the Service Bus log category `RuntimeAuditLogs` on the namespace and send it to a Log Analytics workspace. That category records data-plane operations and the identity that performed them. Management logs do not.

`QueuePrep` is not the server identity. It is the signed-in engineer, and it needs send and receive so it can seed. Do not give the server identity those extra rights in order to seed.
