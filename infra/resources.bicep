// ---------------------------------------------------------------------------
// Contact Center Agent — All Azure Resources
// ---------------------------------------------------------------------------
// Called from main.bicep at subscription scope.
// Deploys AI Foundry + Container Apps Environment + ACS + Caller Agent infrastructure.
// ---------------------------------------------------------------------------

@minLength(3)
@description('Resource naming prefix (contactcenteragent)')
param resourcePrefix string

@description('Name for the AI Foundry project')
param aiProjectName string = 'cog-${resourcePrefix}-prj'

@description('Azure region')
param location string

@description('Resource tags')
param tags object = {}

@description('Data location for ACS (must be US/Europe/Asia — use United States for US phone numbers)')
param acsDataLocation string = 'United States'

@description('ACS phone number for outbound calls (provisioned by provision-phone-number.ps1). Empty on first deploy.')
param acsPhoneNumber string = ''

@description('Optional apex custom domain (e.g. "example.com"). Leave empty to skip custom domain binding on first deploy.')
param customDomain string = ''

@description('Optional www custom domain (e.g. "www.example.com"). Leave empty to skip.')
param customDomainWww string = ''

// ---------------------------------------------------------------------------
// 1. AI Foundry Resource (Cognitive Services Account)
// ---------------------------------------------------------------------------
resource aiFoundry 'Microsoft.CognitiveServices/accounts@2025-10-01-preview' = {
  name: 'cog-${resourcePrefix}'
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  sku: {
    name: 'S0'
  }
  kind: 'AIServices'
  properties: {
    allowProjectManagement: true
    customSubDomainName: 'cog-${resourcePrefix}'
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

// ---------------------------------------------------------------------------
// 2. AI Foundry Project
// ---------------------------------------------------------------------------
resource aiProject 'Microsoft.CognitiveServices/accounts/projects@2025-10-01-preview' = {
  name: aiProjectName
  parent: aiFoundry
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {}
}

// ---------------------------------------------------------------------------
// 3a. Model Deployment — GPT-5.4-nano (chat/agent)
// ---------------------------------------------------------------------------
resource modelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-10-01-preview' = {
  parent: aiFoundry
  name: 'gpt-5.4-nano'
  sku: {
    capacity: 1000
    name: 'GlobalStandard'
  }
  properties: {
    model: {
      name: 'gpt-5.4-nano'
      format: 'OpenAI'
      version: '2026-03-17'
    }
  }
}

// ---------------------------------------------------------------------------
// 3b. Model Deployment — gpt-realtime (voice AI for caller agent)
// ---------------------------------------------------------------------------
resource realtimeModelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2025-10-01-preview' = {
  parent: aiFoundry
  name: 'gpt-realtime'
  dependsOn: [modelDeployment] // Serial deployment to avoid conflicts
  sku: {
    capacity: 10 // Tier 1 quota limit for gpt-realtime-1.5 is 10 — request increase via https://aka.ms/oai/stuquotarequest
    name: 'GlobalStandard'
  }
  properties: {
    model: {
      name: 'gpt-realtime-1.5'
      format: 'OpenAI'
      version: '2026-02-23'
    }
  }
}

// ---------------------------------------------------------------------------
// 4. Log Analytics Workspace (required by Container Apps Environment)
// ---------------------------------------------------------------------------
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
  name: 'log-${resourcePrefix}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

// ---------------------------------------------------------------------------
// 5. Container Registry
// ---------------------------------------------------------------------------
var acrName = 'cr${replace(resourcePrefix, '-', '')}'

resource acr 'Microsoft.ContainerRegistry/registries@2025-11-01' = {
  name: acrName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  properties: {
    adminUserEnabled: true
  }
}

// ---------------------------------------------------------------------------
// 6. Container Apps Environment
// ---------------------------------------------------------------------------
resource containerEnv 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: 'cae-${resourcePrefix}'
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
// 7. Application Insights (persistent telemetry)
// ---------------------------------------------------------------------------
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${resourcePrefix}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
    RetentionInDays: 30
    DisableIpMasking: true
  }
}

// ---------------------------------------------------------------------------
// 8. Storage Account (Table Storage for agent data)
// ---------------------------------------------------------------------------
var storageAccountName = 'st${replace(resourcePrefix, '-', '')}'

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

// ---------------------------------------------------------------------------
// 8a. Diagnostic Settings — AI Foundry → Log Analytics + Application Insights
//     Captures: AI model request/response logs, token usage metrics, audit logs
// ---------------------------------------------------------------------------
resource aiFoundryDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'diag-${resourcePrefix}-ai'
  scope: aiFoundry
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
      {
        categoryGroup: 'audit'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

// ---------------------------------------------------------------------------
// 8b. Diagnostic Settings — Container Apps Environment → Log Analytics
//     Captures: container console logs, system logs, HTTP request logs
// ---------------------------------------------------------------------------
resource containerEnvDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'diag-${resourcePrefix}-cae'
  scope: containerEnv
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

// ---------------------------------------------------------------------------
// 8c. Optional Managed Certificates — bound only if customDomain params provided
//     The cert resources must already exist in the environment (created via
//     `az containerapp env certificate create` after DNS validation).
// ---------------------------------------------------------------------------
resource adminChatApexCert 'Microsoft.App/managedEnvironments/managedCertificates@2025-07-01' existing = if (!empty(customDomain)) {
  parent: containerEnv
  name: 'mc-${resourcePrefix}-apex'
}

resource adminChatWwwCert 'Microsoft.App/managedEnvironments/managedCertificates@2025-07-01' existing = if (!empty(customDomainWww)) {
  parent: containerEnv
  name: 'mc-${resourcePrefix}-www'
}

var apexDomainEntry = empty(customDomain)
  ? []
  : [
      {
        name: customDomain
        certificateId: adminChatApexCert.id
        bindingType: 'SniEnabled'
      }
    ]

var wwwDomainEntry = empty(customDomainWww)
  ? []
  : [
      {
        name: customDomainWww
        certificateId: adminChatWwwCert.id
        bindingType: 'SniEnabled'
      }
    ]

var customDomainsConfig = concat(apexDomainEntry, wwwDomainEntry)

// ---------------------------------------------------------------------------
// 9. Container App — Admin Chat Agent
// ---------------------------------------------------------------------------
resource adminChatApp 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'ca-admin-chat'
  location: location
  tags: union(tags, { 'azd-service-name': 'admin-chat' })
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: containerEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 3000
        transport: 'http'
        allowInsecure: false
        customDomains: customDomainsConfig
      }
      registries: [
        {
          server: acr.properties.loginServer
          username: acr.listCredentials().username
          passwordSecretRef: 'acr-password'
        }
      ]
      secrets: [
        {
          name: 'acr-password'
          value: acr.listCredentials().passwords[0].value
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'admin-chat'
          // mcr placeholder — azd deploy will replace with the real image from ACR
          image: 'mcr.microsoft.com/k8se/quickstart:latest'
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
          env: [
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsights.properties.ConnectionString
            }
            {
              name: 'NUXT_CALLER_AGENT_URL'
              value: 'https://${callerAgentApp.properties.configuration.ingress.fqdn}'
            }
            {
              name: 'NUXT_PUBLIC_APPINSIGHTS_CONNECTION_STRING'
              value: appInsights.properties.ConnectionString
            }
            {
              name: 'LOG_ANALYTICS_WORKSPACE_ID'
              value: logAnalytics.properties.customerId
            }
            {
              name: 'PORT'
              value: '3000'
            }
            {
              name: 'NITRO_PORT'
              value: '3000'
            }
            {
              name: 'NITRO_HOST'
              value: '0.0.0.0'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

// ---------------------------------------------------------------------------
// 10. (removed) Cognitive Services OpenAI User role for Admin Chat App.
// The .NET sidecar that called Azure OpenAI was removed when the chat UI
// was deleted. See .github/copilot-instructions "Removed: AdminChat .NET
// sidecar" if this needs restoring.
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// 11. Role Assignment — Monitoring Reader for Admin Chat App (App Insights query)
// ---------------------------------------------------------------------------
// Role: Monitoring Reader (43d0d8ad-25c7-4714-9337-8ba259a9fe05)
resource adminChatMonitoringRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsights.id, adminChatApp.id, '43d0d8ad-25c7-4714-9337-8ba259a9fe05')
  scope: appInsights
  properties: {
    principalId: adminChatApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '43d0d8ad-25c7-4714-9337-8ba259a9fe05'
    )
  }
}

// ---------------------------------------------------------------------------
// 11b. Role Assignment — Log Analytics Reader for Admin Chat App (call-stats API)
// ---------------------------------------------------------------------------
// Role: Log Analytics Reader (73c42c96-874c-492b-b04d-ab87d138a893)
resource adminChatLogAnalyticsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(logAnalytics.id, adminChatApp.id, '73c42c96-874c-492b-b04d-ab87d138a893')
  scope: logAnalytics
  properties: {
    principalId: adminChatApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '73c42c96-874c-492b-b04d-ab87d138a893'
    )
  }
}

// ---------------------------------------------------------------------------
// 12. Azure Communication Services (ACS) — Phone calling infrastructure
// ---------------------------------------------------------------------------
resource acs 'Microsoft.Communication/communicationServices@2023-04-01' = {
  name: 'acs-${resourcePrefix}'
  location: 'global'
  tags: tags
  properties: {
    dataLocation: acsDataLocation
  }
}

// ---------------------------------------------------------------------------
// 12a. EventGrid System Topic — ACS events (calls)
// ---------------------------------------------------------------------------
resource acsEventGridTopic 'Microsoft.EventGrid/systemTopics@2024-06-01-preview' = {
  name: 'evgt-${resourcePrefix}-acs'
  location: 'global'
  tags: tags
  properties: {
    source: acs.id
    topicType: 'Microsoft.Communication.CommunicationServices'
  }
}

// 12b. EventGrid Subscriptions — created post-deploy via CLI in deploy.yml
//      (must exist AFTER the caller-agent Container App has the endpoints,
//       because EventGrid validates the webhook URL on creation)
//      - incoming-call-to-caller-agent: IncomingCall → /api/incomingCall

// ---------------------------------------------------------------------------
// 13. Container App — Caller Agent (.NET 8, WebSockets enabled)
// ---------------------------------------------------------------------------
resource callerAgentApp 'Microsoft.App/containerApps@2025-07-01' = {
  name: 'ca-caller-agent'
  location: location
  tags: union(tags, { 'azd-service-name': 'caller-agent' })
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: containerEnv.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      registries: [
        {
          server: acr.properties.loginServer
          username: acr.listCredentials().username
          passwordSecretRef: 'acr-password'
        }
      ]
      secrets: [
        {
          name: 'acr-password'
          value: acr.listCredentials().passwords[0].value
        }
        {
          name: 'acs-connection-string'
          value: acs.listKeys().primaryConnectionString
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'caller-agent'
          // mcr placeholder — azd deploy will replace with the real image from ACR
          image: 'mcr.microsoft.com/k8se/quickstart:latest'
          resources: {
            cpu: json('2.0')
            memory: '4Gi'
          }
          env: [
            {
              name: 'AcsConnectionString'
              secretRef: 'acs-connection-string'
            }
            {
              name: 'AzureOpenAI__Endpoint'
              value: aiFoundry.properties.endpoint
            }
            {
              name: 'AzureOpenAI__DeploymentName'
              value: realtimeModelDeployment.name
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: appInsights.properties.ConnectionString
            }
            {
              name: 'OTEL_SERVICE_NAME'
              value: 'CallerAgent'
            }
            {
              name: 'AcsPhoneNumber'
              value: acsPhoneNumber
            }
            // ─── Voice (TTS) ──────────────────────────────────────────────
            // Pin the TTS locale at the deployment layer so the production
            // container always sends `locale: "da-DK"` on the wire regardless
            // of any appsettings.json drift. Without this enforced, da-DK
            // voices have been observed drifting toward a generic Scandinavian
            // / Swedish accent on English loanwords, brand names, and digit
            // sequences common in Norlys calls.
            //
            // The voice NAME is left to appsettings.json (currently
            // `da-DK-ChristelNeural`, will inherit HD Omni from
            // VoiceLiveDefaults once that override is removed). To override
            // name/type/temperature here too, add Voice__Name / Voice__Type /
            // Voice__Temperature env vars — but env vars override appsettings,
            // so be deliberate.
            {
              name: 'Voice__Locale'
              value: 'da-DK'
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 10
      }
    }
  }
}

// ---------------------------------------------------------------------------
// 15. Role Assignment — Cognitive Services OpenAI User for Caller Agent
// ---------------------------------------------------------------------------
resource callerAgentOpenAIRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiFoundry.id, callerAgentApp.id, '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  scope: aiFoundry
  properties: {
    principalId: callerAgentApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
    )
  }
}

// ---------------------------------------------------------------------------
// 15b. Role Assignment — Cognitive Services User for Caller Agent (Voice Live)
// ---------------------------------------------------------------------------
// Voice Live token auth requires Cognitive Services User in addition to OpenAI User.
// Role: Cognitive Services User (a97b65f3-24c7-4388-baec-2e87135dc908)
resource callerAgentCognitiveServicesUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiFoundry.id, callerAgentApp.id, 'a97b65f3-24c7-4388-baec-2e87135dc908')
  scope: aiFoundry
  properties: {
    principalId: callerAgentApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'a97b65f3-24c7-4388-baec-2e87135dc908'
    )
  }
}

// ---------------------------------------------------------------------------
// 16. Diagnostic Settings — ACS → Log Analytics
// ---------------------------------------------------------------------------
resource acsDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'diag-${resourcePrefix}-acs'
  scope: acs
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------
output aiFoundryEndpoint string = aiFoundry.properties.endpoint
output aiFoundryResourceId string = aiFoundry.id
output aiProjectName string = aiProject.name
output containerRegistryEndpoint string = acr.properties.loginServer
output containerRegistryName string = acr.name
output containerEnvId string = containerEnv.id
output storageAccountName string = storageAccount.name
output adminChatAppUrl string = 'https://${adminChatApp.properties.configuration.ingress.fqdn}'
output appInsightsName string = appInsights.name
output appInsightsConnectionString string = appInsights.properties.ConnectionString

// Caller Agent outputs
output acsResourceName string = acs.name
output callerAgentAppName string = callerAgentApp.name
output callerAgentAppUrl string = 'https://${callerAgentApp.properties.configuration.ingress.fqdn}'
