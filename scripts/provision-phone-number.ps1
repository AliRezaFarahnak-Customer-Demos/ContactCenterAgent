#!/usr/bin/env pwsh
# ---------------------------------------------------------------------------
# provision-phone-number.ps1
# ---------------------------------------------------------------------------
# Dynamically provisions a US phone number via Azure Communication Services.
# IDEMPOTENT: If a phone number is already provisioned on the ACS resource,
# it skips provisioning and outputs the existing number.
#
# Uses the ACS REST API (2024-03-01-preview) with HMAC-SHA256 authentication
# for phone number search and purchase (the Azure CLI extension only supports
# 'list' and 'show', not 'search' or 'purchase').
#
# Usage:
#   ./scripts/provision-phone-number.ps1 -ResourceGroup "rg-contactcenteragent" -AcsResourceName "acs-contactcenteragent"
#
# Called automatically by azd hooks (postprovision) and GitHub Actions.
# ---------------------------------------------------------------------------

param(
    [Parameter(Mandatory = $false)]
    [string]$ResourceGroup = "rg-contactcenteragent",

    [Parameter(Mandatory = $false)]
    [string]$AcsResourceName = "acs-contactcenteragent",

    [Parameter(Mandatory = $false)]
    [string]$CountryCode = "US",

    [Parameter(Mandatory = $false)]
    [ValidateSet("tollFree", "geographic")]
    [string]$PhoneNumberType = "tollFree",

    [Parameter(Mandatory = $false)]
    [string]$AreaCode = "",

    [Parameter(Mandatory = $false)]
    [string]$ContainerAppName = "ca-caller-agent",

    [Parameter(Mandatory = $false)]
    [string]$PreferredPhoneNumber = "",

    [Parameter(Mandatory = $false)]
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ACS_API_VERSION = "2024-03-01-preview"

# ---------------------------------------------------------------------------
# Helper: Parse ACS connection string into endpoint + access key
# ---------------------------------------------------------------------------
function Parse-AcsConnectionString {
    param([string]$ConnectionString)

    $parts = @{}
    foreach ($segment in $ConnectionString.Split(';')) {
        $kv = $segment.Split('=', 2)
        if ($kv.Count -eq 2) {
            $parts[$kv[0].Trim().ToLower()] = $kv[1].Trim()
        }
    }

    $endpoint = $parts["endpoint"].TrimEnd('/')
    $accessKey = $parts["accesskey"]

    if (-not $endpoint -or -not $accessKey) {
        throw "Could not parse ACS connection string - missing endpoint or accesskey."
    }

    return @{ Endpoint = $endpoint; AccessKey = $accessKey }
}

# ---------------------------------------------------------------------------
# Helper: Build HMAC-SHA256 auth headers for ACS REST API
# ---------------------------------------------------------------------------
function Get-AcsAuthHeaders {
    param(
        [string]$Endpoint,
        [string]$AccessKey,
        [string]$Method,
        [string]$PathAndQuery,
        [string]$Body = ""
    )

    $dateStr = [System.DateTimeOffset]::UtcNow.ToString("r")
    $hostName = ([System.Uri]$Endpoint).Host

    # Content hash (SHA-256 of body)
    $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($Body)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $hash = $sha256.ComputeHash($bodyBytes)
    $contentHash = [System.Convert]::ToBase64String($hash)

    # String to sign: VERB\npath_and_query\ndate;host;content_hash
    $stringToSign = "$Method`n$PathAndQuery`n$dateStr;$hostName;$contentHash"

    # HMAC-SHA256 signature
    $keyBytes = [System.Convert]::FromBase64String($AccessKey)
    $hmac = New-Object System.Security.Cryptography.HMACSHA256
    $hmac.Key = $keyBytes
    $signatureBytes = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($stringToSign))
    $signature = [System.Convert]::ToBase64String($signatureBytes)

    return @{
        "x-ms-date"           = $dateStr
        "x-ms-content-sha256" = $contentHash
        "Authorization"       = "HMAC-SHA256 SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature=$signature"
        "Content-Type"        = "application/json"
    }
}

# ---------------------------------------------------------------------------
# Helper: Invoke ACS REST API with HMAC-SHA256 auth
# ---------------------------------------------------------------------------
function Invoke-AcsApi {
    param(
        [string]$Endpoint,
        [string]$AccessKey,
        [string]$Method,
        [string]$Path,
        [string]$Body = "",
        [switch]$RawResponse
    )

    $pathAndQuery = $Path
    $url = "$Endpoint$Path"

    $headers = Get-AcsAuthHeaders -Endpoint $Endpoint -AccessKey $AccessKey `
        -Method $Method -PathAndQuery $pathAndQuery -Body $Body

    $params = @{
        Uri         = $url
        Method      = $Method
        Headers     = $headers
        ContentType = "application/json"
    }

    if ($Body) {
        $params["Body"] = $Body
    }

    # Retry on 429 (Too Many Requests) with exponential backoff
    $maxRetries = 10
    $retryDelay = 60  # start with 60 seconds

    for ($retry = 0; $retry -le $maxRetries; $retry++) {
        try {
            if ($RawResponse) {
                return Invoke-WebRequest @params -UseBasicParsing
            }
            else {
                return Invoke-RestMethod @params
            }
        }
        catch {
            $statusCode = 0
            if ($_.Exception.Response) {
                $statusCode = [int]$_.Exception.Response.StatusCode
            }
            if ($statusCode -eq 429 -and $retry -lt $maxRetries) {
                # Check for Retry-After header
                $retryAfter = $retryDelay
                if ($_.Exception.Response.Headers["Retry-After"]) {
                    $retryAfter = [int]$_.Exception.Response.Headers["Retry-After"]
                    if ($retryAfter -lt 30) { $retryAfter = 30 }
                }
                Write-Host "    Rate limited (429). Waiting ${retryAfter}s before retry ($($retry+1)/$maxRetries)..." -ForegroundColor Yellow
                # Re-generate auth headers since they contain a timestamp
                Start-Sleep -Seconds $retryAfter
                $headers = Get-AcsAuthHeaders -Endpoint $Endpoint -AccessKey $AccessKey `
                    -Method $Method -PathAndQuery $pathAndQuery -Body $Body
                $params["Headers"] = $headers
                $retryDelay = [Math]::Min($retryDelay * 2, 300)  # max 5 min
            }
            else {
                throw
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Helper: Poll an ACS long-running operation until complete
# ---------------------------------------------------------------------------
function Wait-AcsOperation {
    param(
        [string]$Endpoint,
        [string]$AccessKey,
        [string]$OperationPath,
        [int]$TimeoutSeconds = 120,
        [int]$PollIntervalSeconds = 3
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $PollIntervalSeconds

        $op = Invoke-AcsApi -Endpoint $Endpoint -AccessKey $AccessKey `
            -Method "GET" -Path $OperationPath

        $status = $op.status
        Write-Host "    Operation status: $status" -ForegroundColor Gray

        if ($status -eq "succeeded" -or $status -eq "Succeeded") {
            return $op
        }
        elseif ($status -eq "failed" -or $status -eq "Failed") {
            throw "ACS operation failed: $($op | ConvertTo-Json -Depth 5)"
        }
    }

    throw "ACS operation timed out after ${TimeoutSeconds}s"
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  ACS Phone Number Provisioning" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# ---------------------------------------------------------------------------
# Step 1: Check if az communication extension is installed
# ---------------------------------------------------------------------------
Write-Host "[1/5] Checking Azure CLI extensions..." -ForegroundColor Yellow

$extensions = az extension list --query "[].name" -o tsv 2>$null
if ($extensions -notcontains "communication") {
    Write-Host "  Installing 'communication' extension..." -ForegroundColor Gray
    az extension add --name communication --yes 2>$null
    Write-Host "  Extension installed." -ForegroundColor Green
}
else {
    Write-Host "  Extension 'communication' already installed." -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# Step 2: Get ACS connection string and parse it
# ---------------------------------------------------------------------------
Write-Host "[2/5] Retrieving ACS connection string..." -ForegroundColor Yellow

$acsConnectionString = az communication list-key `
    --name $AcsResourceName `
    --resource-group $ResourceGroup `
    --query "primaryConnectionString" -o tsv 2>$null

if (-not $acsConnectionString) {
    Write-Host "  ERROR: Could not retrieve ACS connection string." -ForegroundColor Red
    Write-Host "  Make sure the ACS resource '$AcsResourceName' exists in '$ResourceGroup'." -ForegroundColor Red
    exit 1
}

$acs = Parse-AcsConnectionString -ConnectionString $acsConnectionString
Write-Host "  Connection string retrieved. Endpoint: $($acs.Endpoint)" -ForegroundColor Green

# ---------------------------------------------------------------------------
# Step 3: Check for existing phone numbers (via REST API)
# ---------------------------------------------------------------------------
Write-Host "[3/5] Checking for existing phone numbers..." -ForegroundColor Yellow

try {
    $listResult = Invoke-AcsApi -Endpoint $acs.Endpoint -AccessKey $acs.AccessKey `
        -Method "GET" -Path "/phoneNumbers?api-version=$ACS_API_VERSION"

    $existingNumbers = $listResult.phoneNumbers
}
catch {
    Write-Host "  WARNING: Could not list phone numbers via REST API. Falling back to CLI..." -ForegroundColor Yellow
    $existingNumbersCli = az communication phonenumber list `
        --connection-string $acsConnectionString `
        --query "[].phoneNumber" -o tsv 2>$null
    if ($existingNumbersCli) {
        $existingNumbers = @($existingNumbersCli -split "`n" | ForEach-Object { @{ phoneNumber = $_.Trim() } })
    }
}

if ($existingNumbers -and $existingNumbers.Count -gt 0 -and -not $Force) {
    # Selection priority when multiple numbers are provisioned on the ACS resource:
    #   1. -PreferredPhoneNumber (exact match) if supplied
    #   2. Newest by purchaseDate (descending)
    #   3. First in the unordered API response (legacy fallback)
    $selected = $null
    if ($PreferredPhoneNumber) {
        $selected = $existingNumbers | Where-Object { $_.phoneNumber -eq $PreferredPhoneNumber } | Select-Object -First 1
        if (-not $selected) {
            Write-Host "  WARNING: -PreferredPhoneNumber '$PreferredPhoneNumber' not found on ACS resource. Falling back to newest-purchased." -ForegroundColor Yellow
        }
    }
    if (-not $selected -and ($existingNumbers | Where-Object { $_.purchaseDate })) {
        $selected = $existingNumbers | Where-Object { $_.purchaseDate } | Sort-Object { [datetime]$_.purchaseDate } -Descending | Select-Object -First 1
    }
    if (-not $selected) {
        $selected = $existingNumbers[0]
    }
    $phoneNumber = $selected.phoneNumber
    Write-Host ""
    Write-Host "  SKIPPING PROVISIONING - Phone number already exists!" -ForegroundColor Green
    Write-Host "  Selected number: $phoneNumber" -ForegroundColor Cyan
    if ($existingNumbers.Count -gt 1) {
        $allNumbers = ($existingNumbers | ForEach-Object { $_.phoneNumber }) -join ", "
        Write-Host "  (chose from $($existingNumbers.Count) provisioned: $allNumbers)" -ForegroundColor Gray
    }
    Write-Host "  (pass -Force to purchase an additional number, or -PreferredPhoneNumber to pin a specific one)" -ForegroundColor Gray
    Write-Host ""
}
else {
    # ---------------------------------------------------------------------------
    # Step 3b: Search for available phone numbers (via REST API)
    # ---------------------------------------------------------------------------
    Write-Host "  No existing phone numbers found. Searching for available numbers..." -ForegroundColor Gray

    $searchBody = @{
        phoneNumberType = $PhoneNumberType
        assignmentType  = "application"
        capabilities    = @{
            calling = "inbound+outbound"
            sms     = "none"
        }
        quantity        = 1
    }

    if ($AreaCode) {
        $searchBody["areaCode"] = $AreaCode
    }

    $searchJson = $searchBody | ConvertTo-Json -Depth 3
    $searchPath = "/availablePhoneNumbers/countries/$CountryCode/:search?api-version=$ACS_API_VERSION"

    Write-Host "  Searching for $PhoneNumberType numbers in $CountryCode..." -ForegroundColor Gray

    $searchResponse = Invoke-AcsApi -Endpoint $acs.Endpoint -AccessKey $acs.AccessKey `
        -Method "POST" -Path $searchPath -Body $searchJson -RawResponse

    # Extract search ID from response headers or body
    # Note: PowerShell may return header values as arrays; force scalar with [0]
    $searchIdRaw = $searchResponse.Headers["search-id"]
    if ($searchIdRaw -is [array]) { $searchId = $searchIdRaw[0] } else { $searchId = $searchIdRaw }
    if (-not $searchId) {
        # Try to get it from the response body
        $searchResultBody = $searchResponse.Content | ConvertFrom-Json
        $searchId = $searchResultBody.searchId
    }

    if (-not $searchId) {
        Write-Host "  ERROR: Search request did not return a search ID." -ForegroundColor Red
        Write-Host "  Response: $($searchResponse.Content)" -ForegroundColor Red
        exit 1
    }

    Write-Host "  Search initiated (searchId: $searchId). Waiting for results..." -ForegroundColor Gray

    # Poll the operation until complete
    $operationPathRaw = $searchResponse.Headers["Operation-Location"]
    if ($operationPathRaw -is [array]) { $operationPath = $operationPathRaw[0] } else { $operationPath = $operationPathRaw }
    if (-not $operationPath) {
        # Construct it from search-id
        $operationPath = "/phoneNumbers/operations/search_$searchId`?api-version=$ACS_API_VERSION"
    }
    elseif ($operationPath -notmatch "api-version") {
        $operationPath = "$operationPath`?api-version=$ACS_API_VERSION"
    }

    Wait-AcsOperation -Endpoint $acs.Endpoint -AccessKey $acs.AccessKey `
        -OperationPath $operationPath -TimeoutSeconds 120

    # Get the search result
    $searchResultPath = "/availablePhoneNumbers/searchResults/$searchId`?api-version=$ACS_API_VERSION"
    $searchResult = Invoke-AcsApi -Endpoint $acs.Endpoint -AccessKey $acs.AccessKey `
        -Method "GET" -Path $searchResultPath

    if (-not $searchResult.phoneNumbers -or $searchResult.phoneNumbers.Count -eq 0) {
        Write-Host ""
        Write-Host "  ERROR: No phone numbers available for $CountryCode ($PhoneNumberType)." -ForegroundColor Red
        Write-Host "  Try a different country code, phone number type, or area code." -ForegroundColor Red
        Write-Host ""
        Write-Host "  Tip: For US toll-free numbers, no area code is needed." -ForegroundColor Yellow
        Write-Host "  Tip: For US geographic numbers, try -AreaCode '212' (New York) or '206' (Seattle)." -ForegroundColor Yellow
        exit 1
    }

    $phoneNumber = $searchResult.phoneNumbers[0]
    $monthlyCost = $searchResult.cost.amount
    $costCurrency = $searchResult.cost.currencyCode

    Write-Host "  Found: $phoneNumber (monthly cost: $monthlyCost $costCurrency)" -ForegroundColor Green

    # ---------------------------------------------------------------------------
    # Step 4: Purchase the phone number (via REST API)
    # ---------------------------------------------------------------------------
    Write-Host "[4/5] Purchasing phone number $phoneNumber..." -ForegroundColor Yellow

    $purchaseBody = @{ searchId = $searchId } | ConvertTo-Json
    $purchasePath = "/availablePhoneNumbers/:purchase?api-version=$ACS_API_VERSION"

    $purchaseResponse = Invoke-AcsApi -Endpoint $acs.Endpoint -AccessKey $acs.AccessKey `
        -Method "POST" -Path $purchasePath -Body $purchaseBody -RawResponse

    $purchaseOpPathRaw = $purchaseResponse.Headers["Operation-Location"]
    if ($purchaseOpPathRaw -is [array]) { $purchaseOpPath = $purchaseOpPathRaw[0] } else { $purchaseOpPath = $purchaseOpPathRaw }
    if (-not $purchaseOpPath) {
        $purchaseIdRaw = $purchaseResponse.Headers["purchase-id"]
        if ($purchaseIdRaw -is [array]) { $purchaseId = $purchaseIdRaw[0] } else { $purchaseId = $purchaseIdRaw }
        $purchaseOpPath = "/phoneNumbers/operations/purchase_$purchaseId`?api-version=$ACS_API_VERSION"
    }
    elseif ($purchaseOpPath -notmatch "api-version") {
        $purchaseOpPath = "$purchaseOpPath`?api-version=$ACS_API_VERSION"
    }

    Write-Host "  Purchase initiated. Waiting for completion..." -ForegroundColor Gray

    Wait-AcsOperation -Endpoint $acs.Endpoint -AccessKey $acs.AccessKey `
        -OperationPath $purchaseOpPath -TimeoutSeconds 180

    Write-Host "  Phone number purchased successfully!" -ForegroundColor Green

    # Verify the purchase
    Start-Sleep -Seconds 5
    $verifyResult = Invoke-AcsApi -Endpoint $acs.Endpoint -AccessKey $acs.AccessKey `
        -Method "GET" -Path "/phoneNumbers?api-version=$ACS_API_VERSION"

    $verified = $verifyResult.phoneNumbers | Where-Object { $_.phoneNumber -eq $phoneNumber }
    if ($verified) {
        Write-Host "  Verified: $phoneNumber is now provisioned." -ForegroundColor Green
    }
    else {
        Write-Host "  WARNING: Purchase completed but number not yet visible. It may take a few more seconds." -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------------------
# Step 5: Update the Caller Agent Container App with the phone number
# ---------------------------------------------------------------------------
Write-Host "[5/5] Configuring Caller Agent Container App..." -ForegroundColor Yellow

# Check if the Container App exists
$caExists = az containerapp show --name $ContainerAppName --resource-group $ResourceGroup --query "name" -o tsv 2>$null

if ($caExists) {
    $ErrorActionPreference = "Continue"
    az containerapp update `
        --name $ContainerAppName `
        --resource-group $ResourceGroup `
        --set-env-vars "AcsPhoneNumber=$phoneNumber" `
        --output none 2>&1 | Out-Null
    $ErrorActionPreference = "Stop"

    Write-Host "  Container App '$ContainerAppName' configured with AcsPhoneNumber=$phoneNumber" -ForegroundColor Green
}
else {
    Write-Host "  Container App '$ContainerAppName' not found - skipping config update." -ForegroundColor Yellow
    Write-Host "  You can manually set AcsPhoneNumber=$phoneNumber later." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# Set azd environment variable (if running inside azd)
# ---------------------------------------------------------------------------
if (Get-Command azd -ErrorAction SilentlyContinue) {
    # Only set if an azd environment already exists (avoid interactive prompt)
    $azdEnvJson = azd env list -o json 2>$null | ConvertFrom-Json -ErrorAction SilentlyContinue
    if ($azdEnvJson -and $azdEnvJson.Count -gt 0) {
        try {
            azd env set ACS_PHONE_NUMBER $phoneNumber 2>$null
            Write-Host "  azd env variable ACS_PHONE_NUMBER set to $phoneNumber" -ForegroundColor Green
        }
        catch {
            # Not running inside azd context - no action needed
        }
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Provisioning Complete" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Phone Number : $phoneNumber" -ForegroundColor White
Write-Host "  ACS Resource : $AcsResourceName" -ForegroundColor White
Write-Host "  Resource Group: $ResourceGroup" -ForegroundColor White
Write-Host "  Container App: $ContainerAppName" -ForegroundColor White
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "NOTE: US toll-free numbers may require toll-free verification for" -ForegroundColor Yellow
Write-Host "      sustained outbound traffic. Demo calls (a few per day) typically" -ForegroundColor Yellow
Write-Host "      work immediately. For production volume, submit verification at:" -ForegroundColor Yellow
Write-Host "      https://learn.microsoft.com/azure/communication-services/concepts/numbers/sub-eligibility-number-capability" -ForegroundColor Yellow
Write-Host ""

# Output for pipeline consumption
Write-Output $phoneNumber
