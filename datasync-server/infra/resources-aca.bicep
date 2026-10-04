// New resource-group-scoped module for Azure Container Apps deployment.
// Does NOT contain any App Service resources — the existing resources.bicep is unchanged.
targetScope = 'resourceGroup'

@minLength(1)
@description('Primary location for all resources.')
param location string

@description('The name of the Container App.')
param containerAppName string

@description('The name of the Azure Container Registry.')
param containerRegistryName string

@description('The name of the Log Analytics Workspace.')
param logAnalyticsWorkspaceName string

@description('The name of the ACA Managed Environment.')
param managedEnvironmentName string

@description('SQL Server connection string passed as a secret into the container.')
@secure()
param sqlConnectionString string

@description('Azure AD / Entra ID Tenant ID used for JWT bearer validation.')
param azureAdTenantId string

@description('Azure AD / Entra ID Client ID (App Registration) used for JWT bearer validation.')
param azureAdClientId string

@description('Azure AD / Entra ID authority base URL.')
param azureAdInstance string = 'https://login.microsoftonline.com/'

@description('Whether Paper Sync infrastructure and backend configuration are enabled.')
param paperSyncEnabled bool = false

@description('The Foundry/AIServices account and custom subdomain name for Paper Sync.')
param paperSyncFoundryAccountName string = ''

@description('The chat-completion deployment name used by Content Understanding.')
param paperSyncCompletionDeploymentName string = 'paper-sync-completion'

@description('The supported chat-completion model selected by the Paper Sync evaluation spike.')
param paperSyncCompletionModelName string = ''

@description('The pinned chat-completion model version selected by the Paper Sync evaluation spike.')
param paperSyncCompletionModelVersion string = ''

@description('The chat-completion model deployment SKU.')
param paperSyncCompletionDeploymentSku string = 'GlobalStandard'

@minValue(1)
@description('The chat-completion deployment capacity in thousands of tokens per minute.')
param paperSyncCompletionDeploymentCapacity int = 10

@description('The embedding deployment name used by Content Understanding.')
param paperSyncEmbeddingDeploymentName string = 'paper-sync-embedding'

@description('The supported embedding model selected by the Paper Sync evaluation spike.')
param paperSyncEmbeddingModelName string = ''

@description('The pinned embedding model version selected by the Paper Sync evaluation spike.')
param paperSyncEmbeddingModelVersion string = ''

@description('The embedding model deployment SKU.')
param paperSyncEmbeddingDeploymentSku string = 'GlobalStandard'

@minValue(1)
@description('The embedding deployment capacity in thousands of tokens per minute.')
param paperSyncEmbeddingDeploymentCapacity int = 10

@description('The versioned Content Understanding analyzer ID.')
param paperSyncAnalyzerId string = 'paper-sync-auto-v1'

@description('The Content Understanding GA API version.')
param paperSyncContentUnderstandingApiVersion string = '2025-11-01'

@description('The Paper Sync storage account name.')
param paperSyncStorageAccountName string = ''

@description('The Paper Sync Application Insights resource name.')
param paperSyncApplicationInsightsName string = ''

@description('Tags applied to all resources.')
param tags object = {}

// ---------------------------------------------------------------------------
// Log Analytics Workspace
// ---------------------------------------------------------------------------
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

// ---------------------------------------------------------------------------
// Azure Container Registry (Basic SKU — cheapest tier)
// ---------------------------------------------------------------------------
resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-01-01-preview' = {
  name: containerRegistryName
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

// ---------------------------------------------------------------------------
// User-assigned managed identity for the Container App to pull from ACR
// ---------------------------------------------------------------------------
resource managedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${containerAppName}'
  location: location
  tags: tags
}

module paperSync './papersync-aca.bicep' = if (paperSyncEnabled) {
  name: 'paper-sync'
  params: {
    location: location
    tags: tags
    foundryAccountName: paperSyncFoundryAccountName
    completionDeploymentName: paperSyncCompletionDeploymentName
    completionModelName: paperSyncCompletionModelName
    completionModelVersion: paperSyncCompletionModelVersion
    completionDeploymentSku: paperSyncCompletionDeploymentSku
    completionDeploymentCapacity: paperSyncCompletionDeploymentCapacity
    embeddingDeploymentName: paperSyncEmbeddingDeploymentName
    embeddingModelName: paperSyncEmbeddingModelName
    embeddingModelVersion: paperSyncEmbeddingModelVersion
    embeddingDeploymentSku: paperSyncEmbeddingDeploymentSku
    embeddingDeploymentCapacity: paperSyncEmbeddingDeploymentCapacity
    analyzerId: paperSyncAnalyzerId
    contentUnderstandingApiVersion: paperSyncContentUnderstandingApiVersion
    storageAccountName: paperSyncStorageAccountName
    applicationInsightsName: paperSyncApplicationInsightsName
    logAnalyticsWorkspaceId: logAnalytics.id
    containerAppIdentityId: managedIdentity.id
    containerAppPrincipalId: managedIdentity.properties.principalId
  }
}

// AcrPull built-in role
var acrPullRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d'
)

resource acrPullAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, managedIdentity.id, acrPullRoleId)
  scope: containerRegistry
  properties: {
    principalId: managedIdentity.properties.principalId
    roleDefinitionId: acrPullRoleId
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------------------
// ACA Managed Environment
// ---------------------------------------------------------------------------
resource managedEnvironment 'Microsoft.App/managedEnvironments@2023-05-01' = {
  name: managedEnvironmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

// ---------------------------------------------------------------------------
// Container App
// ---------------------------------------------------------------------------
resource containerApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: containerAppName
  location: location
  // azd uses 'azd-service-name' tag to locate the container app to deploy to
  tags: union(tags, { 'azd-service-name': 'backend' })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${managedIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: managedEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080 // .NET 10 runtime image default non-root port
        transport: 'http'
        allowInsecure: false
      }
      registries: [
        {
          server: containerRegistry.properties.loginServer
          identity: managedIdentity.id
        }
      ]
      secrets: [
        {
          name: 'sql-connection-string'
          value: sqlConnectionString
        }
      ]
    }
    template: {
      containers: [
        {
          // Placeholder image — azd replaces this on first `azd deploy`
          name: 'backend'
          image: 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 30
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              initialDelaySeconds: 15
              periodSeconds: 10
            }
          ]
          env: concat(
            [
              {
                name: 'ASPNETCORE_ENVIRONMENT'
                value: 'Production'
              }
              {
                name: 'ASPNETCORE_URLS'
                value: 'http://+:8080'
              }
              {
                // Injected from the ACA secret — never appears in plain text in config
                name: 'ConnectionStrings__DefaultConnection'
                secretRef: 'sql-connection-string'
              }
              {
                name: 'AzureAd__Instance'
                value: azureAdInstance
              }
              {
                name: 'AzureAd__TenantId'
                value: azureAdTenantId
              }
              {
                name: 'AzureAd__ClientId'
                value: azureAdClientId
              }
              {
                name: 'AzureAd__Authority'
                value: '${azureAdInstance}${azureAdTenantId}/v2.0'
              }
              {
                name: 'AzureAd__Audience'
                value: azureAdClientId
              }
            ],
            paperSyncEnabled
              ? [
                  {
                    name: 'AZURE_CLIENT_ID'
                    value: managedIdentity.properties.clientId
                  }
                  {
                    name: 'PaperSync__Enabled'
                    value: 'true'
                  }
                  {
                    name: 'PaperSync__BlobEndpoint'
                    value: paperSync!.outputs.blobEndpoint
                  }
                  {
                    name: 'PaperSync__ContentUnderstanding__Endpoint'
                    value: paperSync!.outputs.contentUnderstandingEndpoint
                  }
                  {
                    name: 'PaperSync__ContentUnderstanding__ApiVersion'
                    value: paperSync!.outputs.contentUnderstandingApiVersion
                  }
                  {
                    name: 'PaperSync__ContentUnderstanding__AnalyzerId'
                    value: paperSync!.outputs.analyzerId
                  }
                  {
                    name: 'PaperSync__ContentUnderstanding__CompletionDeployment'
                    value: paperSync!.outputs.completionDeploymentName
                  }
                  {
                    name: 'PaperSync__ContentUnderstanding__EmbeddingDeployment'
                    value: paperSync!.outputs.embeddingDeploymentName
                  }
                  {
                    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
                    value: paperSync!.outputs.applicationInsightsConnectionString
                  }
                ]
              : [
                  {
                    name: 'PaperSync__Enabled'
                    value: 'false'
                  }
                ]
          )
        }
      ]
      scale: {
        minReplicas: 0 // Scale to zero when idle
        maxReplicas: 3
      }
    }
  }
  dependsOn: [acrPullAssignment]
}

// ---------------------------------------------------------------------------
// Outputs consumed by azd
// ---------------------------------------------------------------------------
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = containerRegistry.properties.loginServer
output AZURE_CONTAINER_REGISTRY_NAME string = containerRegistry.name
output AZURE_CONTAINER_APP_NAME string = containerApp.name
output SERVICE_ENDPOINT string = 'https://${containerApp.properties.configuration.ingress.fqdn}'
