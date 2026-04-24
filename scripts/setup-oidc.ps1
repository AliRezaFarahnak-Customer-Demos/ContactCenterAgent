<#
.SYNOPSIS
    Creates (or recreates) the OIDC federated credential for GitHub Actions.

.DESCRIPTION
    MngEnv* tenants have nightly cleanup policies that delete app registrations.
    Run this script to restore the OIDC setup whenever CI/CD breaks.

    What it does:
      1. Creates an Entra ID app registration ("ContactCenterAgent-GitHub-OIDC")
      2. Creates a service principal
      3. Adds a federated credential for the GitHub Actions "production" environment
      4. Assigns Contributor + User Access Administrator on the subscription
      5. Updates the GitHub environment secrets (AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID)

.PARAMETER GitHubRepo
    GitHub repo in "owner/repo" format. Default: auto-detected from git remote.

.PARAMETER SubscriptionId
    Azure subscription ID. Default: current az account.

.EXAMPLE
    .\scripts\setup-oidc.ps1
    .\scripts\setup-oidc.ps1 -GitHubRepo "AliRezaFarahnak-Customer-Demos/ContactCenterAgent"
#>

[CmdletBinding()]
param(
    [string]$GitHubRepo,
    [string]$SubscriptionId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Auto-detect values ──────────────────────────────────────────────────────
if (-not $GitHubRepo) {
    $remote = git remote get-url origin 2>$null
    if ($remote -match "github\.com[:/](.+?)(?:\.git)?$") {
        $GitHubRepo = $Matches[1]
    }
    else {
        throw "Could not detect GitHub repo from git remote. Pass -GitHubRepo explicitly."
    }
}

if (-not $SubscriptionId) {
    $SubscriptionId = az account show --query id -o tsv
}

$TenantId = az account show --query tenantId -o tsv
$AppName = "ContactCenterAgent-GitHub-OIDC"
$Environment = "production"

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  OIDC Federated Credential Setup for GitHub Actions     ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Repo:         $GitHubRepo"
Write-Host "  Environment:  $Environment"
Write-Host "  Subscription: $SubscriptionId"
Write-Host "  Tenant:       $TenantId"
Write-Host ""

# ── Step 1: Clean up any existing app registration with the same name ────
Write-Host "[1/5] Checking for existing app registration '$AppName'..." -ForegroundColor Yellow
$existingAppId = az ad app list --display-name $AppName --query "[0].appId" -o tsv 2>$null
if ($existingAppId) {
    Write-Host "  → Found existing app $existingAppId, deleting..." -ForegroundColor DarkYellow
    az ad app delete --id $existingAppId 2>$null
    Start-Sleep -Seconds 3
}

# ── Step 2: Create app registration ─────────────────────────────────────
Write-Host "[2/5] Creating app registration..." -ForegroundColor Yellow
$AppId = az ad app create --display-name $AppName --query appId -o tsv
Write-Host "  → App ID: $AppId" -ForegroundColor Green

# ── Step 3: Create service principal ─────────────────────────────────────
Write-Host "[3/5] Creating service principal..." -ForegroundColor Yellow
$SpId = az ad sp create --id $AppId --query id -o tsv
Write-Host "  → SP Object ID: $SpId" -ForegroundColor Green

# ── Step 4: Add federated credential ────────────────────────────────────
Write-Host "[4/5] Adding federated credential..." -ForegroundColor Yellow
$fedCredJson = @{
    name      = "github-actions-deploy"
    issuer    = "https://token.actions.githubusercontent.com"
    subject   = "repo:${GitHubRepo}:environment:${Environment}"
    audiences = @("api://AzureADTokenExchange")
} | ConvertTo-Json

$tempFile = [System.IO.Path]::GetTempFileName()
$fedCredJson | Out-File -FilePath $tempFile -Encoding utf8
try {
    az ad app federated-credential create --id $AppId --parameters $tempFile | Out-Null
    Write-Host "  → Federated credential created (subject: repo:${GitHubRepo}:environment:${Environment})" -ForegroundColor Green
}
finally {
    Remove-Item $tempFile -ErrorAction SilentlyContinue
}

# ── Step 5: Assign roles ────────────────────────────────────────────────
Write-Host "[5/5] Assigning roles on subscription..." -ForegroundColor Yellow
$scope = "/subscriptions/$SubscriptionId"

az role assignment create --assignee $AppId --role "Contributor" --scope $scope | Out-Null
Write-Host "  → Contributor role assigned" -ForegroundColor Green

az role assignment create --assignee $AppId --role "User Access Administrator" --scope $scope | Out-Null
Write-Host "  → User Access Administrator role assigned" -ForegroundColor Green

# ── Step 6: Update GitHub secrets ────────────────────────────────────────
Write-Host ""
Write-Host "Updating GitHub environment secrets..." -ForegroundColor Yellow
gh secret set AZURE_CLIENT_ID       --env $Environment --body $AppId
gh secret set AZURE_TENANT_ID       --env $Environment --body $TenantId
gh secret set AZURE_SUBSCRIPTION_ID --env $Environment --body $SubscriptionId

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║  ✓ OIDC setup complete! CI/CD is ready.                 ║" -ForegroundColor Green
Write-Host "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
Write-Host "  App Registration: $AppName ($AppId)"
Write-Host "  Roles:            Contributor + User Access Administrator"
Write-Host "  GitHub Secrets:   AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID"
Write-Host ""
Write-Host "  If the tenant cleanup removes this overnight, just re-run:" -ForegroundColor DarkGray
Write-Host "    .\scripts\setup-oidc.ps1" -ForegroundColor DarkGray
Write-Host ""
