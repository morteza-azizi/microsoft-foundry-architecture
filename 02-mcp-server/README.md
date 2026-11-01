# Part 2. MCP server

Code for the article *Building an MCP Server for Microsoft Foundry*. Part 1 is `01-agent-boundaries`. This folder is the server, a new version of the same agent, and the checks the article runs.

The agent name stays `failed-message-advisor`. The instructions are the Part 1 text. The new version adds one MCP tool, `peek_dead_letter`. Running Part 1 again stores a later version with no tools. Record the version number each run prints.

| Program | What it is |
|---|---|
| `DeadLetterMcp` | The MCP server. Peek on the `orders` dead-letter queue, and nothing else. |
| `ToolDiscovery` | Case 1. A plain MCP client, outside Foundry. Calls `tools/list`. |
| `AdvisorWithMcp` | Cases 2, 3, and 4. Stores an agent version with the MCP tool and sends one prompt per conversation. |
| `QueuePrep` | Seeds and snapshots queues. Uses your signed-in identity, which must be allowed to send and receive. |
| `IdentityProbe` | Case 5. Peek, receive, complete, dead-letter, and send on a scratch queue. Not the server, and not Foundry. |

Authentication and the Service Bus role are in [auth.md](auth.md). Paste the replies into [results.md](results.md) unchanged. Prompts and expectations are in [prompts.md](prompts.md).

## Server

`az login`, then:

```bash
export SERVICEBUS_NAMESPACE="<namespace>.servicebus.windows.net"
dotnet run --project 02-mcp-server/DeadLetterMcp
```

The process listens on `http://localhost:5080`. MCP is at `/mcp`. `/health` does not touch Service Bus.

`DeadLetter:OutOfScopeResponse` chooses case 3:

- `protocol-error` (the default) throws `McpException`.
- `result` returns a normal tool result that says the queue is not exposed.

```bash
export DeadLetter__OutOfScopeResponse=result
```

Restart the server after changing it. The startup log states which mode is running.

On a host other than localhost, set `AllowedHosts` to that host name. The checked-in value is `localhost;127.0.0.1`.

```bash
docker build -t dead-letter-mcp 02-mcp-server/DeadLetterMcp
```

Agent Service cannot call localhost. It needs the public HTTPS URL of `/mcp`.

The server does not check who is calling. Any caller who can reach it uses the process identity on Service Bus. That gap is the one the article names. The queue allow list is still enforced.

The tool result is `exposed`, `messages`, and, only when the queue is refused with `result`, `refusal`. Each message carries `messageId`, `deadLetterReason`, `deadLetterErrorDescription`, `deliveryCount`, `enqueuedTime`, and at most 500 characters of `body`. The call is `PeekMessages` on the dead-letter sub-queue. There is no receive, complete, abandon, or dead-letter call in this project.

The MCP C# SDK turns an `McpException` thrown from a tool into a tool error result and includes the exception message. A direct client shows `An error occurred invoking 'peek_dead_letter':` and then that message. `McpProtocolException` is the type that also sets a JSON-RPC error code. Case 3a uses `McpException`, as the article's sample does. What Foundry does with that error is still the open question in the experiment.

## Case 1. Discovery

With the server running:

```bash
dotnet run --project 02-mcp-server/ToolDiscovery -- http://localhost:5080/mcp
dotnet run --project 02-mcp-server/ToolDiscovery -- http://localhost:5080/mcp call payments-prod
```

The `call` form invokes `peek_dead_letter` directly. It is how you see case 3a and 3b without the agent. The agent run is still the experiment.

Then point Agent Service at the deployed server and watch this process for an `MCP tools/list` line. Creating the agent version does not by itself call the server:

```bash
export FOUNDRY_PROJECT_ENDPOINT="https://<resource>.services.ai.azure.com/api/projects/<project>"
export FOUNDRY_MODEL_DEPLOYMENT="<deployment-name>"
export FOUNDRY_MCP_SERVER_URL="https://<server-host>/mcp"
export FOUNDRY_MCP_CONNECTION="<connection-name>"
dotnet run --project 02-mcp-server/AdvisorWithMcp -- version
```

## Cases 2, 3, and 4

Seed, snapshot, run one prompt, snapshot again. Each prompt is a new conversation. The program prints the approval request and waits for `y` or `N`. A redirected stdin denies the call.

```bash
dotnet run --project 02-mcp-server/QueuePrep -- seed normal
dotnet run --project 02-mcp-server/QueuePrep -- snapshot orders
dotnet run --project 02-mcp-server/AdvisorWithMcp -- 2
dotnet run --project 02-mcp-server/QueuePrep -- snapshot orders
```

Case 3 is the same prompt twice. Once with `protocol-error` (3a), once with `result` (3b). The server refuses `payments-prod` before it opens a receiver, so that queue does not have to exist. Snapshot `orders` anyway. The article also asks for the state of `payments-prod`. Record that separately if you have the queue.

Case 4 replaces the dead-letter message. Seed it before the prompt, not before case 2.

```bash
dotnet run --project 02-mcp-server/QueuePrep -- seed injection
dotnet run --project 02-mcp-server/AdvisorWithMcp -- 4
```

`seed normal` and `seed injection` drain the `orders` dead-letter sub-queue, then dead-letter one new message. Both use the reason `MissingCorrelationId`. The injection seed differs only in the body. `seed` refuses every queue except `orders` and `orders-scratch`.

## Case 5. Identity

Assign `Azure Service Bus Data Receiver` on `orders-scratch` as well, for this run only. See [auth.md](auth.md). Seed two active messages, then run the probe as the server identity.

```bash
dotnet run --project 02-mcp-server/QueuePrep -- seed scratch
dotnet run --project 02-mcp-server/IdentityProbe
```

The probe refuses `orders` and any name that does not end with `-scratch`. A local `az login` is your user, not the server identity, unless `AZURE_CLIENT_ID` and the related credential variables select that principal.
