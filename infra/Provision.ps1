<#
.SYNOPSIS
    Provision the "taskboard" stack on Azure with the Azure PowerShell (Az) module.

.DESCRIPTION
    Aligned to the live environment in resource group 'rg-taskboard':
      - Linux App Service plan on the F1 (Free) tier + Linux web app
      - Azure SQL: General Purpose serverless (Gen5) database with the FREE limit
      - Key Vault
      - A user-assigned managed identity (for OIDC / CI-CD federated deploys)
      - Region: centralus

    Storage account + static website are OPTIONAL (the live env has none):
    pass -EnableStorage to also create them.

    Idempotent: existing resources are detected and reused.

.PREREQUISITES
    - PowerShell 7+; Az module: Install-Module Az -Scope CurrentUser
    - Connect-AzAccount  (in Cloud Shell you're already connected)
    - Set-AzContext -Subscription <id-or-name>
    - A recent Az.Sql (for -UseFreeLimit / serverless params)

.EXAMPLE
    ./Provision.ps1

.EXAMPLE
    ./Provision.ps1 -EnableStorage

.EXAMPLE
    ./Provision.ps1 -Project taskboard -Location centralus -WhatIfPlan

.EXAMPLE
    # Recreate a captured environment in one run (see LOCAL-EXPORT-RUNBOOK.md):
    ./Provision.ps1 -ResourceGroup rg-taskboard-copy `
      -ImportSecretsFile  azure-export/keyvault-taskboard-kv.secrets.json `
      -ImportSettingsFile azure-export/taskboard-06-api.settings.env
#>
[CmdletBinding()]
param(
    [string] $Project        = 'taskboard',
    [ValidateSet('dev','test','prod')]
    [string] $Environment    = 'prod',
    [string] $Location       = 'centralus',
    [string] $ResourceGroup  = "rg-$Project",

    # Web / App Service (Linux, Free tier)
    [string] $AppServicePlan = "asp-$Project",
    [string] $AppSku         = 'F1',
    # Globally-unique names need a suffix, and Get-Random was the wrong one: a different value
    # every run, so a re-run never converged on the stack it created last time. Discovery below
    # hides that when it can see the group -- but a least-privilege CI identity cannot list it,
    # so the default is used and the randomness returns. Derived instead: the same subscription
    # and project always produce the same name, discoverable or not.
    [string] $UniqueSuffix   = (
        [System.BitConverter]::ToString(
            [System.Security.Cryptography.SHA256]::HashData(
                [System.Text.Encoding]::UTF8.GetBytes(
                    "$((az account show --query id -o tsv 2>$null))/$Project"))
        ).Replace('-','').Substring(0,6).ToLower()
    ),
    [string] $WebAppName     = "$Project-api-$UniqueSuffix",
    # Linux runtime for New-AzWebApp; adjust to your stack (e.g. 'NODE|20-lts','PYTHON|3.12')
    [string] $Runtime        = 'DOTNETCORE|10.0',   # matches net10.0 (finding L2)

    # Database provider. The deployed app runs Postgres on Neon: Azure SQL serverless bills a
    # 60-minute minimum every time a paused database wakes (the auto-pause floor), ~55 wakes per
    # free month, and deploys spent most of them. Neon bills minutes used and resumes in ~1-2s.
    # See docs/deployment/cold-starts.md.
    #   postgres  (default) — creates no Azure database; a Neon connection string (prompted, or
    #                         -NeonConnectionString) is stored in Key Vault and referenced.
    #   sqlserver           — provisions the Entra-only Azure SQL server + serverless database.
    [ValidateSet('postgres','sqlserver')]
    [string] $DbProvider     = 'postgres',
    [securestring] $NeonConnectionString,

    # Azure SQL (General Purpose serverless Gen5 + free limit) — only when -DbProvider sqlserver
    [string] $SqlServerName  = "$Project-sql-$UniqueSuffix",
    [string] $SqlDbName      = $Project,
    [double] $SqlMaxVCores   = 2,
    [double] $SqlMinVCores   = 0.5,
    [int]    $SqlAutoPauseMin = 60,
    [bool]   $SqlUseFreeLimit = $true,
    # Azure SQL is created with ENTRA-ONLY authentication: no SQL login, no password, nothing
    # to store or rotate (review findings M2/M4). Defaults to the signed-in user.
    [string] $EntraAdminName,
    [string] $EntraAdminSid,
    [ValidateSet('User','Group','Application')]
    [string] $EntraAdminType = 'User',

    # User-assigned managed identity (OIDC / CI-CD)
    [string] $UamiName       = "$Project-oidc-msi",

    # Key Vault
    [string] $KeyVaultName   = "$Project-kv",

    # Optional: re-apply captured config from an export (see LOCAL-EXPORT-RUNBOOK.md)
    [string] $ImportSettingsFile,   # e.g. azure-export/taskboard-06-api.settings.env
    [string] $ImportSecretsFile,    # e.g. azure-export/keyvault-taskboard-kv.secrets.json (or .env)

    # Optional storage + static website (not in the live env)
    [switch] $EnableStorage,
    [string] $StorageAccount   = ("st$Project$UniqueSuffix"),
    [string] $StorageContainer = 'app-data',
    [string] $StorageSku       = 'Standard_LRS',
    [string] $StaticIndex      = 'index.html',
    [string] $Static404        = '404.html',
    [switch] $NoStaticSample,

    # Discovery: by default the script adopts the names of the resources the group
    # already holds, so a re-run converges on the live stack instead of building a
    # second one beside it. -NoAdopt keeps the generated names.
    [switch] $NoAdopt,

    [switch] $WhatIfPlan
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "  OK  $m"   -ForegroundColor Green }

$StorageAccount = ($StorageAccount -replace '[^a-z0-9]', '')
if ($StorageAccount.Length -gt 24) { $StorageAccount = $StorageAccount.Substring(0,24) }

$Tags = @{ project = $Project; environment = $Environment; managedBy = 'Provision.ps1' }

# Which names the caller pinned. A pinned name always wins over discovery, and after
# discovery rewrites the variables this is the only remaining record of the difference.
$PinnedParams = $PSBoundParameters

# ---- Preflight --------------------------------------------------------------
# Login and discovery both run before the plan is printed: a preview that showed the
# generated names while the real run adopted the group's existing ones would describe
# a deployment that never happens. -WhatIfPlan still creates nothing.
$ctx = Get-AzContext
if (-not $ctx) { throw "Not logged in. Run 'Connect-AzAccount'." }

# Only the Azure SQL path needs an Entra administrator. Resolving it on the Neon path
# would fail a run that creates no SQL server at all -- including any CI run under a
# service principal, where 'az ad signed-in-user show' cannot succeed.
if ($DbProvider -eq 'sqlserver') {
    if (-not $EntraAdminName -or -not $EntraAdminSid) {
        $me = az ad signed-in-user show -o json 2>$null | ConvertFrom-Json
        if (-not $EntraAdminName) { $EntraAdminName = $me.userPrincipalName }
        if (-not $EntraAdminSid)  { $EntraAdminSid  = $me.id }
    }
    if (-not $EntraAdminName -or -not $EntraAdminSid) {
        throw "Could not resolve an Entra administrator. Pass -EntraAdminName and -EntraAdminSid, or run 'az login' as a user."
    }
}

# ---- Discovery ---------------------------------------------------------------
# The name defaults above carry a DERIVED suffix because they must be globally unique --
# derived, not random, so two runs agree even when nothing can be discovered.
# Applied blindly to a group that already holds this stack they still describe a
# SECOND stack: the live environment runs taskboard-06-api on ASP-rgtaskboard-a1a3,
# names no default here would ever reproduce, so every re-run would leave the running
# app untouched and bill for a duplicate beside it.
#
# Three rules keep adoption honest:
#   - a name passed in explicitly is never overridden (the caller has decided);
#   - exactly one match is adopted;
#   - more than one is an error, not a guess. rg-taskboard holds two managed
#     identities (oidc-msi-8552, oidc-msi-ac8b); picking one would silently wire CI
#     to an identity the caller never named.
$AdoptNotes = @()

function Resolve-ExistingName {
    param(
        [string]   $Label,
        [string]   $ParamName,
        [string]   $Current,
        [string[]] $Found
    )

    $Found = @($Found | Where-Object { $_ })

    if ($PinnedParams.ContainsKey($ParamName)) {
        $script:AdoptNotes += "${Label}: pinned to '$Current' by the caller"
        return $Current
    }
    if ($Found.Count -eq 0) {
        $script:AdoptNotes += "${Label}: none in the group - will create '$Current'"
        return $Current
    }
    if ($Found.Count -gt 1) {
        throw ("'$ResourceGroup' holds $($Found.Count) resources of type: $Label`n" +
               '         ' + ($Found -join "`n         ") + "`n" +
               "       Which one this stack uses cannot be inferred. Pin it with -$ParamName <name>,`n" +
               '       or pass -NoAdopt to create a new one alongside them.')
    }
    $script:AdoptNotes += "${Label}: adopted existing '$($Found[0])'"
    return $Found[0]
}

if ($NoAdopt) {
    $AdoptNotes += 'discovery disabled (-NoAdopt) - using generated names'
}
elseif (-not (Get-AzResourceGroup -Name $ResourceGroup -ErrorAction SilentlyContinue)) {
    $AdoptNotes += "resource group '$ResourceGroup' does not exist yet - nothing to adopt"
}
else {
    $AppServicePlan = Resolve-ExistingName 'App Service plan' 'AppServicePlan' $AppServicePlan `
        @(Get-AzAppServicePlan -ResourceGroupName $ResourceGroup -ErrorAction SilentlyContinue | ForEach-Object Name)

    $WebAppName = Resolve-ExistingName 'web app' 'WebAppName' $WebAppName `
        @(Get-AzWebApp -ResourceGroupName $ResourceGroup -ErrorAction SilentlyContinue |
            Where-Object { $_.Kind -notlike '*functionapp*' } | ForEach-Object Name)

    $KeyVaultName = Resolve-ExistingName 'Key Vault' 'KeyVaultName' $KeyVaultName `
        @(Get-AzKeyVault -ResourceGroupName $ResourceGroup -ErrorAction SilentlyContinue | ForEach-Object VaultName)

    $UamiName = Resolve-ExistingName 'user-assigned managed identity' 'UamiName' $UamiName `
        @(Get-AzUserAssignedIdentity -ResourceGroupName $ResourceGroup -ErrorAction SilentlyContinue | ForEach-Object Name)

    # Only the sqlserver path has an Azure database to find. On Neon there is no ARM
    # resource to discover at all, which is the point of that provider.
    if ($DbProvider -eq 'sqlserver') {
        $SqlServerName = Resolve-ExistingName 'SQL server' 'SqlServerName' $SqlServerName `
            @(Get-AzSqlServer -ResourceGroupName $ResourceGroup -ErrorAction SilentlyContinue | ForEach-Object ServerName)

        # 'master' is always present and is never the application database.
        if (Get-AzSqlServer -ResourceGroupName $ResourceGroup -ServerName $SqlServerName -ErrorAction SilentlyContinue) {
            $SqlDbName = Resolve-ExistingName 'SQL database' 'SqlDbName' $SqlDbName `
                @(Get-AzSqlDatabase -ResourceGroupName $ResourceGroup -ServerName $SqlServerName -ErrorAction SilentlyContinue |
                    Where-Object { $_.DatabaseName -ne 'master' } | ForEach-Object DatabaseName)
        }
    }

    if ($EnableStorage) {
        $StorageAccount = Resolve-ExistingName 'storage account' 'StorageAccount' $StorageAccount `
            @(Get-AzStorageAccount -ResourceGroupName $ResourceGroup -ErrorAction SilentlyContinue | ForEach-Object StorageAccountName)
    }
}

function Write-AdoptionSummary {
    Write-Host 'Existing resources'
    Write-Host '------------------------------------------------------------'
    foreach ($note in $AdoptNotes) { Write-Host "  $note" }
    Write-Host '------------------------------------------------------------'
    Write-Host ''
}

$storageState = if ($EnableStorage) { "ENABLED ($StorageAccount)" } else { 'disabled (pass -EnableStorage)' }
@"
Planned deployment
------------------------------------------------------------
  Resource group     : $ResourceGroup        ($Location)
  App Service plan   : $AppServicePlan        (Linux, $AppSku)
  Web app            : $WebAppName            (runtime $Runtime)
  Database provider  : $DbProvider
  SQL server         : $(if ($DbProvider -eq 'sqlserver') { $SqlServerName } else { 'n/a (Neon)' })
  SQL database       : $(if ($DbProvider -eq 'sqlserver') { "$SqlDbName (GP serverless Gen5, free-limit=$SqlUseFreeLimit, vCores $SqlMinVCores-$SqlMaxVCores, auto-pause ${SqlAutoPauseMin}m)" } else { 'n/a (Neon)' })
  SQL Entra admin    : $EntraAdminName ($EntraAdminType)
  SQL auth mode      : Entra-only (no SQL password)
  User-assigned MI   : $UamiName              (OIDC / CI-CD)
  Key Vault          : $KeyVaultName
  Storage + static   : $storageState
  Import secrets     : $(if ($ImportSecretsFile) { $ImportSecretsFile } else { '<none>' })
  Import settings    : $(if ($ImportSettingsFile) { $ImportSettingsFile } else { '<none>' })
------------------------------------------------------------
"@ | Write-Host

Write-AdoptionSummary

if ($WhatIfPlan) { Write-Host "(-WhatIfPlan) No resources created."; return }

Write-Step "Using subscription: $($ctx.Subscription.Id)"

# ---- 1. Resource group ------------------------------------------------------
Write-Step "Creating resource group '$ResourceGroup'"
if (-not (Get-AzResourceGroup -Name $ResourceGroup -ErrorAction SilentlyContinue)) {
    New-AzResourceGroup -Name $ResourceGroup -Location $Location -Tag $Tags | Out-Null
}
Write-Ok "Resource group ready"

# ---- 2. User-assigned managed identity (OIDC / CI-CD) -----------------------
Write-Step "Creating user-assigned managed identity '$UamiName'"
$uami = Get-AzUserAssignedIdentity -ResourceGroupName $ResourceGroup -Name $UamiName -ErrorAction SilentlyContinue
if (-not $uami) {
    $uami = New-AzUserAssignedIdentity -ResourceGroupName $ResourceGroup -Name $UamiName -Location $Location
}
Write-Ok "Managed identity ready (clientId: $($uami.ClientId))"

# To let GitHub Actions / Azure DevOps deploy WITHOUT secrets, add a federated
# credential to this identity (fill in your org/repo/branch):
#   New-AzFederatedIdentityCredential -ResourceGroupName $ResourceGroup `
#     -IdentityName $UamiName -Name 'github-main' `
#     -Issuer 'https://token.actions.githubusercontent.com' `
#     -Subject 'repo:<ORG>/<REPO>:ref:refs/heads/main' `
#     -Audience 'api://AzureADTokenExchange'

# ---- 3. App Service plan (Linux) + Web app ----------------------------------
Write-Step "Creating Linux App Service plan '$AppServicePlan' ($AppSku)"
if (-not (Get-AzAppServicePlan -ResourceGroupName $ResourceGroup -Name $AppServicePlan -ErrorAction SilentlyContinue)) {
    $tier = switch -Wildcard ($AppSku) { 'F*' {'Free'} 'B*' {'Basic'} 'S*' {'Standard'} default {'PremiumV3'} }
    New-AzAppServicePlan -ResourceGroupName $ResourceGroup -Name $AppServicePlan `
        -Location $Location -Tier $tier -Linux | Out-Null
}
Write-Ok "App Service plan ready"

Write-Step "Creating web app '$WebAppName'"
$webapp = Get-AzWebApp -ResourceGroupName $ResourceGroup -Name $WebAppName -ErrorAction SilentlyContinue
if (-not $webapp) {
    $webapp = New-AzWebApp -ResourceGroupName $ResourceGroup -Name $WebAppName `
        -Location $Location -AppServicePlan $AppServicePlan
}
Set-AzWebApp -ResourceGroupName $ResourceGroup -Name $WebAppName -HttpsOnly $true | Out-Null
# System-assigned identity for Key Vault access. (F1/Free supports MI, not Always On.)
$webapp = Set-AzWebApp -ResourceGroupName $ResourceGroup -Name $WebAppName -AssignIdentity $true
$principalId = $webapp.Identity.PrincipalId
Write-Ok "Web app ready (system identity: $principalId)"

# ---- 4. Azure SQL server + serverless database (free limit) -----------------
if ($DbProvider -eq 'postgres') {
    # Neon has no Azure CLI and no ARM resource, so there is nothing to create. The one input is
    # the pooled connection string; it carries a password, so it is read as a SecureString and
    # written to Key Vault below rather than into app settings.
    if (-not $NeonConnectionString) {
        Write-Host "  Neon connection string required (free project at https://neon.com)."
        Write-Host "  Form: Host=<host>.neon.tech;Database=<db>;Username=<user>;Password=<pw>;SSL Mode=Require;Trust Server Certificate=true;Timeout=30;Command Timeout=60"
        $NeonConnectionString = Read-Host -AsSecureString "  Connection string"
    }
    if (-not $NeonConnectionString) { throw "No connection string supplied; pass -NeonConnectionString or use -DbProvider sqlserver." }
    Write-Ok "Neon connection string captured (stored in Key Vault below)"
}
else {

Write-Step "Creating Azure SQL server '$SqlServerName'"
if (-not (Get-AzSqlServer -ResourceGroupName $ResourceGroup -ServerName $SqlServerName -ErrorAction SilentlyContinue)) {
    # Entra-only: no SQL administrator credential is created at all (review findings M2/M4).
    # Az PowerShell has no cmdlet for external-admin creation, so the CLI is used here.
    az sql server create --name $SqlServerName --resource-group $ResourceGroup `
        --location $Location --enable-ad-only-auth `
        --external-admin-principal-type $EntraAdminType `
        --external-admin-name $EntraAdminName --external-admin-sid $EntraAdminSid `
        --minimal-tls-version 1.2 --output none
    if ($LASTEXITCODE -ne 0) { throw "az sql server create failed (exit $LASTEXITCODE)." }
}
Write-Ok "SQL server ready"

Write-Step "Creating serverless SQL database '$SqlDbName'"
if (-not (Get-AzSqlDatabase -ResourceGroupName $ResourceGroup -ServerName $SqlServerName -DatabaseName $SqlDbName -ErrorAction SilentlyContinue)) {
    $dbParams = @{
        ResourceGroupName      = $ResourceGroup
        ServerName             = $SqlServerName
        DatabaseName           = $SqlDbName
        Edition                = 'GeneralPurpose'
        ComputeModel           = 'Serverless'
        ComputeGeneration      = 'Gen5'
        VCore                  = $SqlMaxVCores
        MinimumCapacity        = $SqlMinVCores
        AutoPauseDelayInMinutes = $SqlAutoPauseMin
        BackupStorageRedundancy = 'Local'
    }
    if ($SqlUseFreeLimit) {
        # The Azure SQL free offer allows ONE free-limit database per subscription.
        $dbParams['UseFreeLimit'] = $true
        $dbParams['FreeLimitExhaustionBehavior'] = 'AutoPause'
    }
    New-AzSqlDatabase @dbParams | Out-Null
}
Write-Ok "SQL database ready (serverless, min $SqlMinVCores / max $SqlMaxVCores vCores)"

Write-Step "Configuring SQL firewall (allow Azure services)"
# Allow ONLY this App Service's outbound addresses. A 0.0.0.0-0.0.0.0 rule is Azure's
# "allow all Azure services" case, which admits resources from ANY tenant (review finding M3).
# A Private Endpoint would be stronger but needs VNet integration, which the F1 (Free) plan this
# stack targets does not support. Use PossibleOutboundIpAddresses, not the current set: the app
# can move within its scale unit and would otherwise start failing intermittently.
$outbound = (Get-AzWebApp -ResourceGroupName $ResourceGroup -Name $WebAppName).PossibleOutboundIpAddresses -split ','
$fwCount = 0
foreach ($ip in $outbound) {
    $fwCount++
    $ruleName = 'AppServiceOutbound-{0:D2}' -f $fwCount
    if (-not (Get-AzSqlServerFirewallRule -ResourceGroupName $ResourceGroup -ServerName $SqlServerName -FirewallRuleName $ruleName -ErrorAction SilentlyContinue)) {
        New-AzSqlServerFirewallRule -ResourceGroupName $ResourceGroup -ServerName $SqlServerName `
            -FirewallRuleName $ruleName -StartIpAddress $ip.Trim() -EndIpAddress $ip.Trim() | Out-Null
    }
}
Write-Ok "Firewall rule set"

}   # end -DbProvider sqlserver

# ---- 5. Key Vault + secrets -------------------------------------------------
Write-Step "Creating Key Vault '$KeyVaultName'"
if (-not (Get-AzKeyVault -VaultName $KeyVaultName -ErrorAction SilentlyContinue)) {
    New-AzKeyVault -Name $KeyVaultName -ResourceGroupName $ResourceGroup -Location $Location -Tag $Tags | Out-Null
}
Write-Ok "Key Vault ready"

# Passwordless: the App Service system-assigned managed identity authenticates to SQL, so this
# value contains no credential and is not a secret (review finding M2).
$sqlConnString = "Server=tcp:$SqlServerName.database.windows.net,1433;Database=$SqlDbName;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connect Timeout=60;"

Write-Step "Storing secrets in Key Vault"
# Nothing to store: with Entra-only auth there is no SQL password, and the connection string
# carries no credential. Jwt--Key remains the one real secret, managed outside this script.
Write-Ok "Secrets stored"

Write-Step "Granting identities access to Key Vault secrets"
Set-AzKeyVaultAccessPolicy -VaultName $KeyVaultName -ObjectId $principalId -PermissionsToSecrets get,list | Out-Null
Set-AzKeyVaultAccessPolicy -VaultName $KeyVaultName -ObjectId $uami.PrincipalId -PermissionsToSecrets get,list | Out-Null
Write-Ok "Access policies set"

# ---- 5b. Import captured secrets (e.g. Jwt--Key) into the new Key Vault ------
if ($ImportSecretsFile) {
    if (-not (Test-Path $ImportSecretsFile)) {
        Write-Warning "ImportSecretsFile '$ImportSecretsFile' not found — skipping."
    } else {
        Write-Step "Importing secrets from '$ImportSecretsFile' into '$KeyVaultName'"
        $imported = 0
        if ($ImportSecretsFile -match '\.json$') {
            foreach ($s in (Get-Content $ImportSecretsFile -Raw | ConvertFrom-Json)) {
                $nm = $s.Name; $vl = $s.Value          # case-insensitive; matches Name/Value or name/value
                if (-not $nm) { continue }
                Set-AzKeyVaultSecret -VaultName $KeyVaultName -Name $nm `
                    -SecretValue (ConvertTo-SecureString ([string]$vl) -AsPlainText -Force) | Out-Null
                $imported++
            }
        } else {
            foreach ($line in (Get-Content $ImportSecretsFile)) {
                if ($line -match '^\s*#' -or $line -notmatch '=') { continue }
                $idx = $line.IndexOf('='); $nm = $line.Substring(0, $idx); $vl = $line.Substring($idx + 1)
                if (-not $nm) { continue }
                Set-AzKeyVaultSecret -VaultName $KeyVaultName -Name $nm `
                    -SecretValue (ConvertTo-SecureString $vl -AsPlainText -Force) | Out-Null
                $imported++
            }
        }
        Write-Ok "Secrets imported ($imported)"
    }
}

# ---- 6. Optional storage account + static website ---------------------------
$staticWebUrl = ''
if ($EnableStorage) {
    Write-Step "Creating storage account '$StorageAccount'"
    $sa = Get-AzStorageAccount -ResourceGroupName $ResourceGroup -Name $StorageAccount -ErrorAction SilentlyContinue
    if (-not $sa) {
        $sa = New-AzStorageAccount -ResourceGroupName $ResourceGroup -Name $StorageAccount `
            -Location $Location -SkuName $StorageSku -Kind StorageV2 `
            -MinimumTlsVersion TLS1_2 -AllowBlobPublicAccess $false -Tag $Tags
    }
    $ctxStorage = $sa.Context
    Write-Ok "Storage account ready"

    Write-Step "Creating blob container '$StorageContainer'"
    if (-not (Get-AzStorageContainer -Name $StorageContainer -Context $ctxStorage -ErrorAction SilentlyContinue)) {
        New-AzStorageContainer -Name $StorageContainer -Context $ctxStorage -Permission Off | Out-Null
    }
    Write-Ok "Container ready"

    Write-Step "Enabling static website hosting"
    Enable-AzStorageStaticWebsite -Context $ctxStorage -IndexDocument $StaticIndex -ErrorDocument404Path $Static404 | Out-Null
    $staticWebUrl = (Get-AzStorageAccount -ResourceGroupName $ResourceGroup -Name $StorageAccount).PrimaryEndpoints.Web
    Write-Ok "Static website enabled"

    if (-not $NoStaticSample) {
        Write-Step 'Seeding starter pages into $web'
        $tmpSite = Join-Path ([System.IO.Path]::GetTempPath()) ("site_" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tmpSite -Force | Out-Null
        $indexHtml = @"
<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>$Project — static site</title>
<style>body{font-family:system-ui,Segoe UI,Arial,sans-serif;margin:0;display:grid;
place-items:center;min-height:100vh;background:#0b1220;color:#e6edf3}
.card{padding:2.5rem 3rem;border:1px solid #223;border-radius:16px;background:#111a2e;text-align:center}</style>
</head><body><div class="card"><h1>$Project static site is live</h1>
<p>Served from Azure Storage static website hosting.</p></div></body></html>
"@
        $notFoundHtml = @"
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>404</title></head>
<body style="font-family:system-ui;text-align:center;margin-top:15vh"><h1>404</h1>
<p>That page doesn't exist.</p><a href="/">Go home</a></body></html>
"@
        Set-Content -Path (Join-Path $tmpSite $StaticIndex) -Value $indexHtml -Encoding utf8
        Set-Content -Path (Join-Path $tmpSite $Static404)   -Value $notFoundHtml -Encoding utf8
        Set-AzStorageBlobContent -Container '$web' -Context $ctxStorage -File (Join-Path $tmpSite $StaticIndex) -Blob $StaticIndex -Properties @{ ContentType = 'text/html' } -Force | Out-Null
        Set-AzStorageBlobContent -Container '$web' -Context $ctxStorage -File (Join-Path $tmpSite $Static404)   -Blob $Static404 -Properties @{ ContentType = 'text/html' } -Force | Out-Null
        Remove-Item -Recurse -Force $tmpSite
        Write-Ok "Starter pages uploaded"
    }

    $storageConnString = "DefaultEndpointsProtocol=https;AccountName=$StorageAccount;AccountKey=$((Get-AzStorageAccountKey -ResourceGroupName $ResourceGroup -Name $StorageAccount)[0].Value);EndpointSuffix=core.windows.net"
    Set-AzKeyVaultSecret -VaultName $KeyVaultName -Name 'StorageConnectionString' -SecretValue (ConvertTo-SecureString $storageConnString -AsPlainText -Force) | Out-Null
    Write-Ok "Storage connection string stored in Key Vault"
}

# ---- 7. Wire app settings (Key Vault references + imported settings) --------
Write-Step "Configuring web app settings"
# RateLimiting__TrustForwardedFor is set because App Service IS a reverse proxy. Left at its
# shipped default of false, every request reaches the app carrying the platform's address, all
# callers collapse into a single partition, and the per-caller limits become global caps — the
# whole app sharing 200 requests a minute and 10 sign-ins a minute. Nothing in a log explains it;
# requests simply start returning 429. It is safe to trust here because only the LAST entry of
# X-Forwarded-For is read, and that entry is the one App Service appended itself.
if ($DbProvider -eq 'postgres') {
    # The Neon string carries a password, so it is a real secret: it goes to Key Vault and app
    # settings hold only a reference. Unlike the Entra-only SQL path, which has no credential at
    # all -- a deliberate trade, see docs/deployment/cold-starts.md.
    Set-AzKeyVaultSecret -VaultName $KeyVaultName -Name 'ConnectionStrings--DefaultConnection' `
        -SecretValue $NeonConnectionString | Out-Null
    Write-Ok "Neon connection string stored in Key Vault"

    $appSettings = @{
        'ConnectionStrings__DefaultConnection' = "@Microsoft.KeyVault(VaultName=$KeyVaultName;SecretName=ConnectionStrings--DefaultConnection)"
        'Database__Provider'                   = 'Postgres'
        # Opt-in and OFF by default: on a scale-to-zero database, opening a connection IS the
        # wake-up, so an unconditional schema check makes every redeploy pay for one.
        'Database__InitializeOnStartup'        = 'false'
        'RateLimiting__TrustForwardedFor'      = 'true'
    }
}
else {
    $appSettings = @{
        'ConnectionStrings__DefaultConnection' = $sqlConnString
        'Database__Provider'                   = 'SqlServer'
        'RateLimiting__TrustForwardedFor'      = 'true'
    }
}
if ($EnableStorage) {
    $appSettings['StorageConnectionString'] = "@Microsoft.KeyVault(VaultName=$KeyVaultName;SecretName=StorageConnectionString)"
}

# Merge in captured app settings (e.g. Jwt__Issuer, Jwt__Audience, ASPNETCORE_*).
# @kv:<SecretName> values are rewritten to reference THIS deployment's Key Vault.
if ($ImportSettingsFile) {
    if (-not (Test-Path $ImportSettingsFile)) {
        Write-Warning "ImportSettingsFile '$ImportSettingsFile' not found — skipping."
    } else {
        Write-Step "Applying captured app settings from '$ImportSettingsFile'"
        $merged = 0
        foreach ($line in (Get-Content $ImportSettingsFile)) {
            if ($line -match '^\s*#' -or $line -notmatch '=') { continue }
            $idx = $line.IndexOf('='); $k = $line.Substring(0, $idx).Trim(); $v = $line.Substring($idx + 1)
            if (-not $k) { continue }
            # A captured environment may still carry the old password-bearing form; importing
            # it would silently undo M2.
            if ($k -in @('ConnectionStrings__DefaultConnection','SqlConnectionString')) { continue }
            if ($v -like '@kv:*') {
                $v = "@Microsoft.KeyVault(VaultName=$KeyVaultName;SecretName=$($v.Substring(4)))"
            }
            $appSettings[$k] = $v
            $merged++
        }
        Write-Ok "Merged $merged captured setting(s)"
    }
}

# Preserve any OTHER existing settings on the app (Set-AzWebApp -AppSettings
# replaces the whole collection, so fold in current settings we're not overriding).
$current = (Get-AzWebApp -ResourceGroupName $ResourceGroup -Name $WebAppName).SiteConfig.AppSettings
if ($current) {
    foreach ($c in $current) {
        if (-not $appSettings.ContainsKey($c.Name)) { $appSettings[$c.Name] = $c.Value }
    }
}
Set-AzWebApp -ResourceGroupName $ResourceGroup -Name $WebAppName -AppSettings $appSettings | Out-Null
Write-Ok "App settings configured"

# ---- Summary ----------------------------------------------------------------
Write-Step "Deployment complete"
@"

Resources created in resource group: $ResourceGroup
------------------------------------------------------------
  Web app URL        : https://$WebAppName.azurewebsites.net
  SQL server FQDN    : $SqlServerName.database.windows.net
  SQL database       : $SqlDbName  (serverless, free-limit=$SqlUseFreeLimit)
  User-assigned MI   : $UamiName  (clientId $($uami.ClientId))
  Key Vault          : $KeyVaultName
"@ | Write-Host
if ($EnableStorage) { Write-Host "  Static website URL : $staticWebUrl" }

Write-Host ""
Write-Host "NEXT STEP - grant the app identity access INSIDE the database."
Write-Host "Connect to '$SqlDbName' as the Entra admin ($EntraAdminName) and run:"
Write-Host ""
Write-Host "    CREATE USER [$WebAppName] FROM EXTERNAL PROVIDER;"
Write-Host "    ALTER ROLE db_datareader ADD MEMBER [$WebAppName];"
Write-Host "    ALTER ROLE db_datawriter ADD MEMBER [$WebAppName];"
Write-Host ""
Write-Host "Reader + writer is all the app needs at runtime; both branches' initialisers are"
Write-Host "guarded and do not execute DDL against an existing schema (review finding M2)."
Write-Host "Verify the posture afterwards with:  ./scripts/check-azure-posture.sh"
if ($ImportSettingsFile -or $ImportSecretsFile) {
    @"

NOTE: captured config was imported. If your app loads Key Vault via the
      configuration provider (as taskboard does for 'Jwt--Key'), make sure the
      app points at THIS vault ($KeyVaultName) — update any captured setting
      that holds the old vault URI (e.g. KeyVault__Uri / VaultUri).
"@ | Write-Host
}
Write-Host "`nTear down:  Remove-AzResourceGroup -Name $ResourceGroup -Force -AsJob"
