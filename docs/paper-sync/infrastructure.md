# Paper Sync ACA infrastructure

Paper Sync is provisioned only through the Azure Container Apps path:

`azure-aca.yaml` → `infra/main-aca.bicep` → `infra/resources-aca.bicep` →
`infra/papersync-aca.bicep`

The existing App Service templates are not part of this deployment.

## Feature state

`paperSyncEnabled` defaults to `false`. When disabled, the deployment does not create
Foundry, Paper Sync Storage, or Paper Sync Application Insights resources, and the
backend receives `PaperSync__Enabled=false`.

Before enabling Paper Sync, supply supported chat-completion and embedding model
names, pinned versions, SKUs, and capacities selected by the evaluation spike. Azure
Content Understanding and both deployments must be available in the selected
`location`.

The Content Understanding integration uses the GA `2025-11-01` API. The Foundry
resource is a `Microsoft.CognitiveServices/accounts` resource with `kind: AIServices`.
Requests use the resource's `https://<name>.services.ai.azure.com` endpoint. Local
authentication is disabled.

## Analyzer deployment

The versioned analyzer definition is embedded in `Ben.PaperSync.Contracts` and loaded
by Bicep from `Analyzers/paper-sync-auto-v1.json`.

After Foundry and the two model deployments are available, an Azure CLI deployment
script runs under the existing Container App managed identity. It:

1. Maps the selected model names and Content Understanding aliases to the deployed
   completion and embedding model names through `PATCH /contentunderstanding/defaults`.
2. Upserts the versioned analyzer with
   `PUT /contentunderstanding/analyzers/{analyzerId}`.
3. Polls the analyzer deployment operation until it succeeds or fails.

The deployment script's `forceUpdateTag` is derived from the model mappings, analyzer
definition, and API version. Unchanged deployments do not rerun the script; changing
any of those inputs applies the new configuration idempotently.

## Backend configuration

When Paper Sync is enabled, the Container App receives:

- `AZURE_CLIENT_ID`
- `PaperSync__Enabled`
- `PaperSync__BlobEndpoint`
- `PaperSync__ContentUnderstanding__Endpoint`
- `PaperSync__ContentUnderstanding__ApiVersion`
- `PaperSync__ContentUnderstanding__AnalyzerId`
- `PaperSync__ContentUnderstanding__CompletionDeployment`
- `PaperSync__ContentUnderstanding__EmbeddingDeployment`
- `APPLICATIONINSIGHTS_CONNECTION_STRING`

The existing Container App user-assigned identity receives Storage Blob Data
Contributor on the Paper Sync storage account and Cognitive Services User on the
Foundry resource. No storage, Foundry, or model API keys are created or injected.

## Docker build context

The backend references `Ben.PaperSync.Contracts`, whose API schema and analyzer
definition are embedded resources. ACA Docker builds therefore use the repository root
as their context. The Dockerfile copies only the backend, contracts project, and schema
files needed to restore and publish the service.
