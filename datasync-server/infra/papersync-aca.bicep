targetScope = 'resourceGroup'

@minLength(1)
@description('Primary location for Paper Sync resources.')
param location string

@minLength(2)
@maxLength(64)
@description('Globally unique Azure OpenAI account and custom subdomain name.')
param openAIAccountName string

@minLength(1)
@description('Primary Azure OpenAI deployment name exposed to the backend.')
param primaryDeploymentName string

@minLength(1)
@description('Primary Azure OpenAI model name selected by the Paper Sync evaluation spike.')
param primaryModelName string

@minLength(1)
@description('Pinned primary Azure OpenAI model version selected by the Paper Sync evaluation spike.')
param primaryModelVersion string

@description('Primary model deployment SKU.')
param primaryDeploymentSku string = 'GlobalStandard'

@minValue(1)
@description('Primary model deployment capacity in thousands of tokens per minute.')
param primaryDeploymentCapacity int = 10

@description('Whether to provision a second deployment for controlled A/B evaluations.')
param secondaryDeploymentEnabled bool = false

@description('Optional secondary Azure OpenAI deployment name.')
param secondaryDeploymentName string = ''

@description('Optional secondary Azure OpenAI model name.')
param secondaryModelName string = ''

@description('Optional pinned secondary Azure OpenAI model version.')
param secondaryModelVersion string = ''

@description('Secondary model deployment SKU.')
param secondaryDeploymentSku string = 'GlobalStandard'

@minValue(1)
@description('Secondary model deployment capacity in thousands of tokens per minute.')
param secondaryDeploymentCapacity int = 10

@minLength(3)
@maxLength(24)
@description('Globally unique storage account name for Paper Sync images and opted-in evaluation samples.')
param storageAccountName string

@minLength(1)
@description('Workspace-based Application Insights resource name.')
param applicationInsightsName string

@minLength(1)
@description('Existing Log Analytics workspace resource ID.')
param logAnalyticsWorkspaceId string

@minLength(1)
@description('Principal ID of the existing Container App user-assigned managed identity.')
param containerAppPrincipalId string

@description('Tags applied to Paper Sync resources.')
param tags object = {}

resource openAIAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: openAIAccountName
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: openAIAccountName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource primaryDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: openAIAccount
  name: primaryDeploymentName
  sku: {
    name: primaryDeploymentSku
    capacity: primaryDeploymentCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: primaryModelName
      version: primaryModelVersion
    }
    raiPolicyName: 'Microsoft.Default'
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

resource secondaryDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = if (secondaryDeploymentEnabled) {
  parent: openAIAccount
  name: secondaryDeploymentName
  sku: {
    name: secondaryDeploymentSku
    capacity: secondaryDeploymentCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: secondaryModelName
      version: secondaryModelVersion
    }
    raiPolicyName: 'Microsoft.Default'
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource imagesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'paper-sync-images'
  properties: {
    publicAccess: 'None'
  }
}

resource trainingContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'paper-sync-training'
  properties: {
    publicAccess: 'None'
  }
}

resource imageLifecyclePolicy 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'delete-paper-sync-images-after-30-days'
          enabled: true
          type: 'Lifecycle'
          definition: {
            actions: {
              baseBlob: {
                delete: {
                  daysAfterModificationGreaterThan: 30
                }
              }
              snapshot: {
                delete: {
                  daysAfterCreationGreaterThan: 30
                }
              }
              version: {
                delete: {
                  daysAfterCreationGreaterThan: 30
                }
              }
            }
            filters: {
              blobTypes: [
                'blockBlob'
              ]
              prefixMatch: [
                'paper-sync-images/'
              ]
            }
          }
        }
      ]
    }
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: applicationInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspaceId
  }
}

var storageBlobDataContributorRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
)
var cognitiveServicesOpenAIUserRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'a98b61a6-410b-4e92-9987-8662971c2a41'
)

resource storageBlobDataContributorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, containerAppPrincipalId, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    principalId: containerAppPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataContributorRoleId
  }
}

resource cognitiveServicesOpenAIUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAIAccount.id, containerAppPrincipalId, cognitiveServicesOpenAIUserRoleId)
  scope: openAIAccount
  properties: {
    principalId: containerAppPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: cognitiveServicesOpenAIUserRoleId
  }
}

output blobEndpoint string = storageAccount.properties.primaryEndpoints.blob
output openAIEndpoint string = 'https://${openAIAccountName}.openai.azure.com/'
output primaryDeploymentName string = primaryDeployment.name
output applicationInsightsConnectionString string = applicationInsights.properties.ConnectionString
