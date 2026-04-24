#!/usr/bin/env pwsh
# ---------------------------------------------------------------------------
# setup-eventgrid.ps1
# ---------------------------------------------------------------------------
# Creates the EventGrid subscription that routes ACS IncomingCall events to
# the caller-agent's /api/incomingCall webhook.
#
# IDEMPOTENT: re-running is safe — `az ... create` on an existing subscription
# returns success.
#
# This MUST run AFTER `azd deploy` has rolled out the real caller-agent image,
# because EventGrid validates the webhook URL when the subscription is created.
# The placeholder image (mcr.microsoft.com/k8se/quickstart) doesn't expose
# /api/incomingCall, so creating the subscription during `azd provision` would
# fail validation.
#
# Wired up via azd hooks (postdeploy) in azure.yaml.
# ---------------------------------------------------------------------------

param(
    [Parameter(Mandatory = $false)]
    [string]$ResourceGroup = "rg-contactcenteragent",

    [Parameter(Mandatory = $false)]
    [string]$SystemTopicName = "evgt-contactcenteragent-acs",

    [Parameter(Mandatory = $false)]
    [string]$ContainerAppName = "ca-caller-agent",

    [Parameter(Mandatory = $false)]
    [string]$SubscriptionName = "incoming-call-to-caller-agent"
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  EventGrid IncomingCall Subscription" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Ensure the eventgrid CLI extension is available
$extensions = az extension list --query "[].name" -o tsv 2>$null
if ($extensions -notcontains "eventgrid") {
    Write-Host "Installing 'eventgrid' Azure CLI extension..." -ForegroundColor Gray
    az extension add --name eventgrid --yes 2>$null | Out-Null
}

# Get the caller-agent FQDN
$callerFqdn = az containerapp show `
    --name $ContainerAppName `
    --resource-group $ResourceGroup `
    --query "properties.configuration.ingress.fqdn" -o tsv 2>$null

if (-not $callerFqdn) {
    Write-Host "ERROR: Could not find caller-agent FQDN." -ForegroundColor Red
    Write-Host "  Container App '$ContainerAppName' in '$ResourceGroup' was not found." -ForegroundColor Red
    exit 1
}

$endpoint = "https://${callerFqdn}/api/incomingCall"
Write-Host "Caller-agent webhook: $endpoint" -ForegroundColor Gray

# Check if the subscription already exists
$existing = az eventgrid system-topic event-subscription show `
    --name $SubscriptionName `
    --system-topic-name $SystemTopicName `
    --resource-group $ResourceGroup `
    --query "name" -o tsv 2>$null

if ($existing) {
    Write-Host "EventGrid subscription '$SubscriptionName' already exists. Skipping." -ForegroundColor Green
    Write-Host ""
    exit 0
}

# Wait for the caller-agent to be serving the real image. EventGrid validates
# the webhook URL synchronously when the subscription is created — if the new
# revision isn't yet receiving traffic, validation fails with a 404.
$rootUrl = "https://${callerFqdn}/"
$expectedMarker = "Caller Agent"   # served by CallerAgent.cs root route
$readyTimeoutSec = 180
$deadline = (Get-Date).AddSeconds($readyTimeoutSec)
$ready = $false

Write-Host "Waiting for caller-agent revision to be live (probing $rootUrl)..." -ForegroundColor Gray
while ((Get-Date) -lt $deadline) {
    try {
        $resp = Invoke-WebRequest -Uri $rootUrl -Method GET -TimeoutSec 10 -UseBasicParsing -ErrorAction Stop
        if ($resp.StatusCode -eq 200 -and $resp.Content -like "*${expectedMarker}*") {
            $ready = $true
            break
        }
    }
    catch {
        # 404 / 502 / connection refused while the new revision rolls out
    }
    Start-Sleep -Seconds 5
}

if (-not $ready) {
    Write-Host "ERROR: caller-agent did not become ready within ${readyTimeoutSec}s." -ForegroundColor Red
    Write-Host "  EventGrid validation will fail. Re-run 'azd deploy' once the app is healthy," -ForegroundColor Red
    Write-Host "  or manually create the subscription with:" -ForegroundColor Red
    Write-Host "    az eventgrid system-topic event-subscription create --name $SubscriptionName ..." -ForegroundColor Red
    exit 1
}
Write-Host "Caller-agent is responding. Proceeding." -ForegroundColor Green

# Create the subscription
Write-Host "Creating EventGrid subscription '$SubscriptionName'..." -ForegroundColor Yellow
az eventgrid system-topic event-subscription create `
    --name $SubscriptionName `
    --system-topic-name $SystemTopicName `
    --resource-group $ResourceGroup `
    --endpoint $endpoint `
    --included-event-types "Microsoft.Communication.IncomingCall" `
    --event-delivery-schema eventgridschema `
    --output none

if ($LASTEXITCODE -eq 0) {
    Write-Host "EventGrid subscription created. Inbound calls will route to the caller agent." -ForegroundColor Green
}
else {
    Write-Host "ERROR: Failed to create EventGrid subscription." -ForegroundColor Red
    exit 1
}

Write-Host ""
