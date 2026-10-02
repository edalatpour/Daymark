// Subscription-scoped entry point for the ACA deployment.
// Mirrors the structure of main.bicep but targets Container Apps instead of App Service.
// The existing main.bicep and resources.bicep are NOT modified.
targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Environment name used to generate a unique hash for resource names.')
param environmentName string

@minLength(1)
@description('Primary location for all resources.')
param location string

@description('Optional — Resource Group name. Defaults to rg-aca-{environmentName}.')
param resourceGroupName string = ''

@description('Optional — Container App name. Defaults to a generated unique name.')
param containerAppName string = ''

@description('Optional — Container Registry name. Defaults to a generated unique name.')
param containerRegistryName string = ''

@description('Optional — Log Analytics Workspace name. Defaults to a generated unique name.')
param logAnalyticsWorkspaceName string = ''

@description('Optional — ACA Managed Environment name. Defaults to a generated unique name.')
param managedEnvironmentName string = ''

@description('SQL Server connection string for the existing database.')
@secure()
param sqlConnectionString string

@description('Azure AD / Entra ID Tenant ID for JWT bearer validation.')
param azureAdTenantId string

@description('Azure AD / Entra ID Client ID (App Registration) for JWT bearer validation.')
param azureAdClientId string

@description('Azure AD / Entra ID authority base URL.')
param azureAdInstance string = 'https://login.microsoftonline.com/'

@description('Whether to provision and configure Paper Sync. Defaults to disabled.')
param paperSyncEnabled bool = false

@description('Optional Azure OpenAI account name. Defaults to a generated unique name.')
param paperSyncOpenAIAccountName string = ''

@description('Primary Azure OpenAI deployment name.')
param paperSyncPrimaryDeploymentName string = 'paper-sync-primary'

@description('Primary model name selected by the Paper Sync evaluation spike. Required when Paper Sync is enabled.')
param paperSyncPrimaryModelName string = ''

@description('Pinned primary model version selected by the Paper Sync evaluation spike. Required when Paper Sync is enabled.')
param paperSyncPrimaryModelVersion string = ''

@description('Primary model deployment SKU.')
param paperSyncPrimaryDeploymentSku string = 'GlobalStandard'

@minValue(1)
@description('Primary model capacity in thousands of tokens per minute.')
param paperSyncPrimaryDeploymentCapacity int = 10

@description('Whether to provision an optional second model deployment for A/B evaluation.')
param paperSyncSecondaryDeploymentEnabled bool = false

@description('Optional secondary Azure OpenAI deployment name.')
param paperSyncSecondaryDeploymentName string = ''

@description('Optional secondary model name.')
param paperSyncSecondaryModelName string = ''

@description('Optional pinned secondary model version.')
param paperSyncSecondaryModelVersion string = ''

@description('Secondary model deployment SKU.')
param paperSyncSecondaryDeploymentSku string = 'GlobalStandard'

@minValue(1)
@description('Secondary model capacity in thousands of tokens per minute.')
param paperSyncSecondaryDeploymentCapacity int = 10

@description('Optional Paper Sync storage account name. Defaults to a generated unique name.')
param paperSyncStorageAccountName string = ''

@description('Optional Paper Sync Application Insights name. Defaults to a generated unique name.')
param paperSyncApplicationInsightsName string = ''

var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))
var tags = { 'azd-env-name': environmentName }

resource resourceGroup 'Microsoft.Resources/resourceGroups@2022-09-01' = {
  name: !empty(resourceGroupName) ? resourceGroupName : 'rg-aca-${environmentName}'
  location: location
  tags: tags
}

module resources './resources-aca.bicep' = {
  name: 'resources-aca'
  scope: resourceGroup
  params: {
    location: location
    tags: tags
    containerAppName: !empty(containerAppName) ? containerAppName : 'ca-${resourceToken}'
    // ACR names must be globally unique, alphanumeric only, 5-50 chars
    containerRegistryName: !empty(containerRegistryName) ? containerRegistryName : 'cr${resourceToken}'
    logAnalyticsWorkspaceName: !empty(logAnalyticsWorkspaceName) ? logAnalyticsWorkspaceName : 'log-${resourceToken}'
    managedEnvironmentName: !empty(managedEnvironmentName) ? managedEnvironmentName : 'cae-${resourceToken}'
    sqlConnectionString: sqlConnectionString
    azureAdTenantId: azureAdTenantId
    azureAdClientId: azureAdClientId
    azureAdInstance: azureAdInstance
    paperSyncEnabled: paperSyncEnabled
    paperSyncOpenAIAccountName: !empty(paperSyncOpenAIAccountName) ? paperSyncOpenAIAccountName : 'oai-${resourceToken}'
    paperSyncPrimaryDeploymentName: paperSyncPrimaryDeploymentName
    paperSyncPrimaryModelName: paperSyncPrimaryModelName
    paperSyncPrimaryModelVersion: paperSyncPrimaryModelVersion
    paperSyncPrimaryDeploymentSku: paperSyncPrimaryDeploymentSku
    paperSyncPrimaryDeploymentCapacity: paperSyncPrimaryDeploymentCapacity
    paperSyncSecondaryDeploymentEnabled: paperSyncSecondaryDeploymentEnabled
    paperSyncSecondaryDeploymentName: paperSyncSecondaryDeploymentName
    paperSyncSecondaryModelName: paperSyncSecondaryModelName
    paperSyncSecondaryModelVersion: paperSyncSecondaryModelVersion
    paperSyncSecondaryDeploymentSku: paperSyncSecondaryDeploymentSku
    paperSyncSecondaryDeploymentCapacity: paperSyncSecondaryDeploymentCapacity
    paperSyncStorageAccountName: !empty(paperSyncStorageAccountName)
      ? paperSyncStorageAccountName
      : 'stps${resourceToken}'
    paperSyncApplicationInsightsName: !empty(paperSyncApplicationInsightsName)
      ? paperSyncApplicationInsightsName
      : 'appi-ps-${resourceToken}'
  }
}

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = resources.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT
output AZURE_CONTAINER_REGISTRY_NAME string = resources.outputs.AZURE_CONTAINER_REGISTRY_NAME
output AZURE_CONTAINER_APP_NAME string = resources.outputs.AZURE_CONTAINER_APP_NAME
output SERVICE_ENDPOINT string = resources.outputs.SERVICE_ENDPOINT
