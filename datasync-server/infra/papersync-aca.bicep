targetScope = 'resourceGroup'

@minLength(1)
@description('Primary location for Paper Sync resources.')
param location string

@minLength(2)
@maxLength(64)
@description('Globally unique Foundry/AIServices account and custom subdomain name.')
param foundryAccountName string

@minLength(1)
@description('Chat-completion deployment name used by Content Understanding.')
param completionDeploymentName string

@minLength(1)
@description('Supported chat-completion model name selected by the evaluation spike.')
param completionModelName string

@minLength(1)
@description('Pinned chat-completion model version selected by the evaluation spike.')
param completionModelVersion string

@description('Chat-completion model deployment SKU.')
param completionDeploymentSku string = 'GlobalStandard'

@minValue(1)
@description('Chat-completion deployment capacity in thousands of tokens per minute.')
param completionDeploymentCapacity int = 10

@minLength(1)
@description('Embedding deployment name used by Content Understanding.')
param embeddingDeploymentName string

@minLength(1)
@description('Supported embedding model name selected by the evaluation spike.')
param embeddingModelName string

@minLength(1)
@description('Pinned embedding model version selected by the evaluation spike.')
param embeddingModelVersion string

@description('Embedding model deployment SKU.')
param embeddingDeploymentSku string = 'GlobalStandard'

@minValue(1)
@description('Embedding deployment capacity in thousands of tokens per minute.')
param embeddingDeploymentCapacity int = 10

@minLength(1)
@maxLength(64)
@description('Versioned Content Understanding analyzer ID.')
param analyzerId string = 'paper-sync-auto-v1'

@description('Content Understanding GA API version.')
param contentUnderstandingApiVersion string = '2025-11-01'

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
@description('Resource ID of the existing Container App user-assigned managed identity.')
param containerAppIdentityId string

@minLength(1)
@description('Principal ID of the existing Container App user-assigned managed identity.')
param containerAppPrincipalId string

@description('Tags applied to Paper Sync resources.')
param tags object = {}

var analyzerDefinition = loadJsonContent('../../Ben.PaperSync.Contracts/Analyzers/paper-sync-auto-v1.json')
var modelDeploymentDefaults = {
  '${completionModelName}': completionDeploymentName
  '${embeddingModelName}': embeddingDeploymentName
  'prebuilt-analyzer-completion': completionDeploymentName
  'prebuilt-analyzer-completion-mini': completionDeploymentName
  'prebuilt-analyzer-embedding': embeddingDeploymentName
}

resource foundryAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: foundryAccountName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: foundryAccountName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource completionDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: foundryAccount
  name: completionDeploymentName
  sku: {
    name: completionDeploymentSku
    capacity: completionDeploymentCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: completionModelName
      version: completionModelVersion
    }
    raiPolicyName: 'Microsoft.Default'
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: foundryAccount
  name: embeddingDeploymentName
  sku: {
    name: embeddingDeploymentSku
    capacity: embeddingDeploymentCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: embeddingModelName
      version: embeddingModelVersion
    }
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
var cognitiveServicesUserRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'a97b65f3-24c7-4388-baec-2e87135dc908'
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

resource cognitiveServicesUserAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryAccount.id, containerAppPrincipalId, cognitiveServicesUserRoleId)
  scope: foundryAccount
  properties: {
    principalId: containerAppPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: cognitiveServicesUserRoleId
  }
}

resource configureContentUnderstanding 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: 'configure-paper-sync-content-understanding'
  location: location
  tags: tags
  kind: 'AzureCLI'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${containerAppIdentityId}': {}
    }
  }
  properties: {
    azCliVersion: '2.76.0'
    cleanupPreference: 'OnSuccess'
    environmentVariables: [
      {
        name: 'CONTENT_UNDERSTANDING_ENDPOINT'
        value: 'https://${foundryAccountName}.services.ai.azure.com'
      }
      {
        name: 'CONTENT_UNDERSTANDING_API_VERSION'
        value: contentUnderstandingApiVersion
      }
      {
        name: 'CONTENT_UNDERSTANDING_ANALYZER_ID'
        value: analyzerId
      }
      {
        name: 'MODEL_DEPLOYMENT_DEFAULTS'
        value: string({
          modelDeployments: modelDeploymentDefaults
        })
      }
      {
        name: 'ANALYZER_DEFINITION'
        value: string(analyzerDefinition)
      }
    ]
    forceUpdateTag: uniqueString(
      string(modelDeploymentDefaults),
      string(analyzerDefinition),
      contentUnderstandingApiVersion
    )
    retentionInterval: 'P1D'
    timeout: 'PT30M'
    scriptContent: '''
      #!/usr/bin/env bash
      set -euo pipefail

      RESOURCE_SCOPE="https://cognitiveservices.azure.com"

      invoke_api() {
        local method="$1"
        local url="$2"
        local body="$3"
        local response_file="$4"
        local headers_file="$5"
        local content_type="$6"

        for attempt in $(seq 1 20); do
          token=$(az account get-access-token --resource "$RESOURCE_SCOPE" --query accessToken -o tsv)
          http_code=$(curl --silent --show-error \
            --request "$method" \
            --url "$url" \
            --header "Authorization: Bearer $token" \
            --header "Content-Type: $content_type" \
            --data-binary "$body" \
            --dump-header "$headers_file" \
            --output "$response_file" \
            --write-out "%{http_code}")

          if [[ "$http_code" -ge 200 && "$http_code" -lt 300 ]]; then
            return 0
          fi

          if [[ "$http_code" == "401" || "$http_code" == "403" || "$http_code" == "429" || "$http_code" -ge 500 ]]; then
            sleep 15
            continue
          fi

          cat "$response_file"
          echo "Content Understanding request failed with HTTP $http_code." >&2
          return 1
        done

        cat "$response_file"
        echo "Content Understanding request did not succeed after 20 attempts." >&2
        return 1
      }

      defaults_url="$CONTENT_UNDERSTANDING_ENDPOINT/contentunderstanding/defaults?api-version=$CONTENT_UNDERSTANDING_API_VERSION"
      invoke_api PATCH "$defaults_url" "$MODEL_DEPLOYMENT_DEFAULTS" defaults.json defaults.headers application/merge-patch+json

      analyzer_url="$CONTENT_UNDERSTANDING_ENDPOINT/contentunderstanding/analyzers/$CONTENT_UNDERSTANDING_ANALYZER_ID?api-version=$CONTENT_UNDERSTANDING_API_VERSION"
      invoke_api PUT "$analyzer_url" "$ANALYZER_DEFINITION" analyzer.json analyzer.headers application/json

      operation_location=$(tr -d '\r' < analyzer.headers \
        | awk -F': ' 'tolower($1) == "operation-location" {print $2}' \
        | tail -n 1)

      if [[ -z "$operation_location" ]]; then
        echo "Analyzer deployment did not return Operation-Location." >&2
        cat analyzer.json
        exit 1
      fi

      for attempt in $(seq 1 90); do
        token=$(az account get-access-token --resource "$RESOURCE_SCOPE" --query accessToken -o tsv)
        if ! http_code=$(curl --silent --show-error \
            --url "$operation_location" \
            --header "Authorization: Bearer $token" \
            --output operation.json \
            --write-out "%{http_code}"); then
          sleep 10
          continue
        fi

        if [[ "$http_code" == "401" || "$http_code" == "403" || "$http_code" == "429" || "$http_code" -ge 500 ]]; then
          sleep 10
          continue
        fi

        if [[ "$http_code" -lt 200 || "$http_code" -ge 300 ]]; then
          cat operation.json
          echo "Analyzer deployment polling failed with HTTP $http_code." >&2
          exit 1
        fi

        operation_status=$(python -c "import json; print(json.load(open('operation.json')).get('status', ''))")
        normalized_status=$(echo "$operation_status" | tr '[:upper:]' '[:lower:]')

        if [[ "$normalized_status" == "succeeded" ]]; then
          exit 0
        fi

        if [[ "$normalized_status" == "failed" || "$normalized_status" == "canceled" ]]; then
          cat operation.json
          exit 1
        fi

        sleep 10
      done

      echo "Timed out waiting for analyzer deployment." >&2
      cat operation.json
      exit 1
    '''
  }
  dependsOn: [
    cognitiveServicesUserAssignment
    completionDeployment
    embeddingDeployment
  ]
}

output blobEndpoint string = storageAccount.properties.primaryEndpoints.blob
output contentUnderstandingEndpoint string = 'https://${foundryAccountName}.services.ai.azure.com/'
output contentUnderstandingApiVersion string = contentUnderstandingApiVersion
output analyzerId string = analyzerId
output completionDeploymentName string = completionDeployment.name
output embeddingDeploymentName string = embeddingDeployment.name
output applicationInsightsConnectionString string = applicationInsights.properties.ConnectionString
