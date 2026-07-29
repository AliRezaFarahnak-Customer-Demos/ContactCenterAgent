// ---------------------------------------------------------------------------
// Contact Center Agent — Azure Infrastructure
// ---------------------------------------------------------------------------
// Subscription-scoped entry point for `azd up`.
// Creates resource group + all resources from scratch.
// ---------------------------------------------------------------------------
targetScope = 'subscription'

@description('Environment name — set by azd (used only for tagging, not resource names)')
param environmentName string

@description('Azure region — set by azd')
param location string

@description('ACS data location — use "United States" for US phone numbers')
param acsDataLocation string = 'United States'

@description('ACS phone number for outbound calls (set by provision-phone-number.ps1 or CI)')
param acsPhoneNumber string = ''

@description('ACS SMS-capable number. Same US toll-free number as acsPhoneNumber — toll-free carries both voice and SMS.')
param acsSmsNumber string = ''

@description('Optional apex custom domain (e.g. "example.com"). Leave empty on first deploy.')
param customDomain string = ''

@description('Optional www custom domain (e.g. "www.example.com"). Leave empty on first deploy.')
param customDomainWww string = ''

// Fixed naming prefix — all resource names derive from this, NOT from the azd env name
var resourcePrefix = 'contactcenteragent'

// azd resource tags for environment tracking
var tags = {
  'azd-env-name': environmentName
}

var resourceGroupName = 'rg-${resourcePrefix}'

// ---------------------------------------------------------------------------
// 1. Resource Group
// ---------------------------------------------------------------------------
resource rg 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

// ---------------------------------------------------------------------------
// 2. All Resources (module)
// ---------------------------------------------------------------------------
module resources 'resources.bicep' = {
  name: 'contactcenteragent-resources'
  scope: rg
  params: {
    resourcePrefix: resourcePrefix
    location: location
    tags: tags
    acsDataLocation: acsDataLocation
    acsPhoneNumber: acsPhoneNumber
    acsSmsNumber: acsSmsNumber
    customDomain: customDomain
    customDomainWww: customDomainWww
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_OPENAI_ENDPOINT string = resources.outputs.aiFoundryEndpoint
output AI_FOUNDRY_RESOURCE_ID string = resources.outputs.aiFoundryResourceId
output AI_PROJECT_NAME string = resources.outputs.aiProjectName
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = resources.outputs.containerRegistryEndpoint
output AZURE_CONTAINER_REGISTRY_NAME string = resources.outputs.containerRegistryName
output STORAGE_ACCOUNT_NAME string = resources.outputs.storageAccountName
output ADMIN_CHAT_APP_URL string = resources.outputs.adminChatAppUrl
output APPLICATIONINSIGHTS_NAME string = resources.outputs.appInsightsName
output APPLICATIONINSIGHTS_CONNECTION_STRING string = resources.outputs.appInsightsConnectionString

// Caller Agent outputs
output ACS_RESOURCE_NAME string = resources.outputs.acsResourceName
output CALLER_AGENT_APP_NAME string = resources.outputs.callerAgentAppName
output CALLER_AGENT_APP_URL string = resources.outputs.callerAgentAppUrl
