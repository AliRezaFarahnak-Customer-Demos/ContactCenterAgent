#!/usr/bin/env pwsh
# ---------------------------------------------------------------------------
# grant-graph-mail-permissions.ps1
# ---------------------------------------------------------------------------
# Grants the caller-agent's system-assigned managed identity the Microsoft Graph
# APPLICATION permissions it needs to send and read mail:
#
#   Mail.Send       — send email from the mailbox (Graph sendMail)
#   Mail.ReadWrite  — read replies from the inbox and mark them read (GraphInboxPoller)
#
# This is what makes email genuinely TWO-WAY without attaching a custom domain to
# ACS: the service sends from a real mailbox and polls that mailbox for replies.
#
# App-only Graph permissions cannot be granted from Bicep, so this runs as a
# separate step. IDEMPOTENT — re-running skips roles that are already assigned.
#
# Requires: a tenant admin (Privileged Role Administrator / Global Administrator),
# because app-role assignment is admin consent.
#
# Usage:
#   ./scripts/grant-graph-mail-permissions.ps1
#   ./scripts/grant-graph-mail-permissions.ps1 -ContainerAppName ca-caller-agent -ResourceGroup rg-contactcenteragent
#
# SECURITY NOTE: Mail.Send / Mail.ReadWrite are tenant-wide application permissions.
# For production, scope them to a single mailbox with an Exchange Application Access
# Policy — see the link printed at the end of this script.
# ---------------------------------------------------------------------------

param(
    [Parameter(Mandatory = $false)]
    [string]$ResourceGroup = "rg-contactcenteragent",

    [Parameter(Mandatory = $false)]
    [string]$ContainerAppName = "ca-caller-agent",

    [Parameter(Mandatory = $false)]
    [string[]]$Permissions = @("Mail.Send", "Mail.ReadWrite")
)

$ErrorActionPreference = "Stop"
$GRAPH_APP_ID = "00000003-0000-0000-c000-000000000000"   # Microsoft Graph, well-known

Write-Host "=== Grant Graph mail permissions to $ContainerAppName ===" -ForegroundColor Cyan

# 1. Managed identity principal of the container app
$principalId = az containerapp show -n $ContainerAppName -g $ResourceGroup `
    --query "identity.principalId" -o tsv 2>$null

if (-not $principalId) {
    throw "Could not read the system-assigned identity of '$ContainerAppName' in '$ResourceGroup'. Deploy the app first (azd up)."
}
Write-Host "Managed identity principal: $principalId"

# 2. Microsoft Graph service principal in this tenant
$graphSpId = az ad sp show --id $GRAPH_APP_ID --query id -o tsv
$appRoles = az ad sp show --id $GRAPH_APP_ID --query "appRoles" -o json | ConvertFrom-Json

# 3. What's already assigned (idempotency)
$existing = az rest --method get `
    --url "https://graph.microsoft.com/v1.0/servicePrincipals/$principalId/appRoleAssignments" `
    --query "value[].appRoleId" -o json 2>$null | ConvertFrom-Json
if (-not $existing) { $existing = @() }

foreach ($perm in $Permissions) {
    $role = $appRoles | Where-Object { $_.value -eq $perm -and $_.allowedMemberTypes -contains "Application" }
    if (-not $role) {
        Write-Warning "Graph application role '$perm' not found - skipping."
        continue
    }

    if ($existing -contains $role.id) {
        Write-Host "  [skip] $perm already granted" -ForegroundColor DarkGray
        continue
    }

    $body = @{ principalId = $principalId; resourceId = $graphSpId; appRoleId = $role.id } | ConvertTo-Json -Compress
    az rest --method post `
        --url "https://graph.microsoft.com/v1.0/servicePrincipals/$principalId/appRoleAssignments" `
        --headers "Content-Type=application/json" `
        --body $body | Out-Null

    Write-Host "  [ok]   $perm granted" -ForegroundColor Green
}

Write-Host ""
Write-Host "Done. Role assignments can take a minute to propagate." -ForegroundColor Cyan
Write-Host "Next: point the service at the mailbox and redeploy, e.g."
Write-Host "  azd env set GRAPH_SENDER_ADDRESS noreply@yourdomain.com"
Write-Host "  azd provision; azd deploy caller-agent"
Write-Host ""
Write-Host "Recommended for production - restrict these tenant-wide permissions to ONE mailbox:" -ForegroundColor Yellow
Write-Host "  https://learn.microsoft.com/graph/auth-limit-mailbox-access"
