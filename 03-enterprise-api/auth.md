# Authentication

This file lists the Entra setup for the experiment. I have not run these commands. Check each one before you rely on it, and expect small differences in your tenant.

There are two sides:

- The **API side** is the Producer Registry. It has an app registration that defines who may read it.
- The **caller side** is the Foundry project's managed identity. It gets a role on the API.

The managed identity answers "which project called". It does not name the engineer.

## 1. App registration for the API

```bash
az ad app create \
  --display-name producer-registry \
  --sign-in-audience AzureADMyOrg \
  --identifier-uris api://producer-registry \
  --app-roles @app-roles.json
```

`app-roles.json` defines two roles: `ProducerRegistry.Read` and `ProducerRegistry.Notify`. Only Read is given to the agent.

Some tenants only accept `api://<app-id>` as an identifier URI. If the command is rejected, use `api://<app-id>` here, in the `AzureAd:Audience` setting, and in the audience of the agent's connection.

Create the service principal for the API. Keep its object id:

```bash
az ad sp create --id <api-app-id>
```

## 2. Give the Foundry project's identity the Read role

Find the project's managed identity principal id in the Azure portal (the Foundry project resource, Identity). Then assign the app role:

```bash
az rest --method POST \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/<project-principal-id>/appRoleAssignments" \
  --body "{\"principalId\":\"<project-principal-id>\",\"resourceId\":\"<api-service-principal-object-id>\",\"appRoleId\":\"152ef8c3-76e7-4e74-9ac1-1994748195b6\"}"
```

`152ef8c3-76e7-4e74-9ac1-1994748195b6` is `ProducerRegistry.Read`. Do not assign `ProducerRegistry.Notify`. Case 5 depends on that.

Record the identity's role assignments before and after, and paste the output into `results.md` unchanged:

```bash
az rest --method GET \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/<project-principal-id>/appRoleAssignments"
```

## 3. API settings

Fill in `ProducerRegistry/appsettings.json`, or set these as environment variables on the host:

```bash
export AzureAd__TenantId="<tenant-id>"
export AzureAd__ClientId="<api-app-id>"
export AzureAd__Audience="api://producer-registry"
```

`Auth:Mode` must be `entra`. The value `none` is for local runs and turns off all checks.

## 4. The agent's connection

The agent's OpenAPI tool uses managed identity. The audience is the API's identifier URI, `api://producer-registry`. It is not the Foundry project endpoint.

How the connection is created depends on your tooling. Check the OpenAPI tool page in the Foundry documentation, and record which method you used in `results.md`.

## 5. Path B

Create an API in API Management that points at the Producer Registry. Apply `apim-policy.xml` to that API. The agent's server URL for path B is the API Management URL.

The policy checks the token, applies a limit of 5 calls per minute per caller, and sets a correlation id. The API code does not change between path A and path B.
