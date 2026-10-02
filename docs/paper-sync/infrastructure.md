# Paper Sync ACA infrastructure

Paper Sync is provisioned only through the Azure Container Apps path:

`azure-aca.yaml` → `infra/main-aca.bicep` → `infra/resources-aca.bicep` →
`infra/papersync-aca.bicep`

The existing App Service templates are not part of this deployment.

## Feature state

`paperSyncEnabled` defaults to `false`. When disabled, the deployment does not create
Azure OpenAI, Paper Sync Storage, or Paper Sync Application Insights resources, and
the backend receives `PaperSync__Enabled=false`.

Before enabling Paper Sync, supply the primary model name and pinned model version
selected by the model evaluation spike. The deployment SKU and capacity are also
parameters and default to `GlobalStandard` and 10 capacity units. The model must be
available with that SKU in the selected `location`.

An optional second deployment can be configured for controlled A/B evaluation. Its
deployment name, model name, and pinned version must all be supplied when it is enabled.

## Backend configuration

When Paper Sync is enabled, the Container App receives:

- `AZURE_CLIENT_ID`
- `PaperSync__Enabled`
- `PaperSync__BlobEndpoint`
- `PaperSync__OpenAI__Endpoint`
- `PaperSync__OpenAI__Deployment`
- `APPLICATIONINSIGHTS_CONNECTION_STRING`

The existing Container App user-assigned identity receives Storage Blob Data
Contributor on the Paper Sync storage account and Cognitive Services OpenAI User on
the Azure OpenAI account. No storage or model API keys are created or injected.

## Docker build context

The backend references `Ben.PaperSync.Contracts`, whose embedded schemas are stored
under `docs/schemas`. ACA Docker builds therefore use the repository root as their
context. The Dockerfile copies only the backend, contracts project, and schema files
needed to restore and publish the service.
