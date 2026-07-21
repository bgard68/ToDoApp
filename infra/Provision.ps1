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
    [string] $WebAppName     = "$Project-api-$(Get-Random -Maximum 99999)",
    # Linux runtime for New-AzWebApp; adjust to your stack (e.g. 'NODE|20-lts','PYTHON|3.12')
    [string] $Runtime        = 'DOTNETCORE|8.0',

    # Azure SQL (General Purpose serverless Gen5 + free limit)
    [string] $SqlServerName  = "$Project-sql-$(Get-Random -Maximum 99999)",
    [string] $SqlDbName      = $Project,
    [double] $SqlMaxVCores   = 2,
    [double] $SqlMinVCores   = 0.5,
    [int]    $SqlAutoPauseMin = 60,
    [bool]   $SqlUseFreeLimit = $true,
    [string] $SqlAdminUser   = 'sqladmin',
    [string] $SqlAdminPassword,

    # User-assigned managed identity (OIDC / CI-CD)
    [string] $UamiName       = "$Project-oidc-msi",

    # Key Vault
    [string] $KeyVaultName   = "$Project-kv",

    # Optional: re-apply captured config from an export (see LOCAL-EXPORT-RUNBOOK.md)
    [string] $ImportSettingsFile,   # e.g. azure-export/taskboard-06-api.settings.env
    [string] $ImportSecretsFile,    # e.g. azure-export/keyvault-taskboard-kv.secrets.json (or .env)

    # Optional storage + static website (not in the live env)
    [switch] $EnableStorage,
    [string] $StorageAccount   = ("st$Project$(Get-Random -Maximum 99999)"),
    [string] $StorageContainer = 'app-data',
    [string] $StorageSku       = 'Standard_LRS',
    [string] $StaticIndex      = 'index.html',
    [string] $Static404        = '404.html',
    [switch] $NoStaticSample,

    [switch] $WhatIfPlan
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "  OK  $m"   -ForegroundColor Green }

$StorageAccount = ($StorageAccount -replace '[^a-z0-9]', '')
if ($StorageAccount.Length -gt 24) { $StorageAccount = $StorageAccount.Substring(0,24) }

$Tags = @{ project = $Project; environment = $Environment; managedBy = 'Provision.ps1' }

$storageState = if ($EnableStorage) { "ENABLED ($StorageAccount)" } else { 'disabled (pass -EnableStorage)' }
@"
Planned deployment
------------------------------------------------------------
  Resource group     : $ResourceGroup        ($Location)
  App Service plan   : $AppServicePlan        (Linux, $AppSku)
  Web app            : $WebAppName            (runtime $Runtime)
  SQL server         : $SqlServerName
  SQL database       : $SqlDbName             (GP serverless Gen5, free-limit=$SqlUseFreeLimit)
                       vCores $SqlMinVCores-$SqlMaxVCores, auto-pause ${SqlAutoPauseMin}m
  SQL admin user     : $SqlAdminUser
  User-assigned MI   : $UamiName              (OIDC / CI-CD)
  Key Vault          : $KeyVaultName
  Storage + static   : $storageState
  Import secrets     : $(if ($ImportSecretsFile) { $ImportSecretsFile } else { '<none>' })
  Import settings    : $(if ($ImportSettingsFile) { $ImportSettingsFile } else { '<none>' })
------------------------------------------------------------
"@ | Write-Host

if ($WhatIfPlan) { Write-Host "(-WhatIfPlan) No resources created."; return }

# ---- Preflight --------------------------------------------------------------
$ctx = Get-AzContext
if (-not $ctx) { throw "Not logged in. Run 'Connect-AzAccount'." }
Write-Step "Using subscription: $($ctx.Subscription.Id)"

$generatedPw = $false
if ([string]::IsNullOrEmpty($SqlAdminPassword)) {
    $bytes = New-Object 'byte[]' 18
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $SqlAdminPassword = [Convert]::ToBase64String($bytes) + 'Aa1!'
    $generatedPw = $true
}
$securePw = ConvertTo-SecureString $SqlAdminPassword -AsPlainText -Force
$sqlCred  = New-Object System.Management.Automation.PSCredential ($SqlAdminUser, $securePw)

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
Write-Step "Creating Azure SQL server '$SqlServerName'"
if (-not (Get-AzSqlServer -ResourceGroupName $ResourceGroup -ServerName $SqlServerName -ErrorAction SilentlyContinue)) {
    New-AzSqlServer -ResourceGroupName $ResourceGroup -ServerName $SqlServerName `
        -Location $Location -SqlAdministratorCredentials $sqlCred | Out-Null
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
if (-not (Get-AzSqlServerFirewallRule -ResourceGroupName $ResourceGroup -ServerName $SqlServerName -FirewallRuleName 'AllowAzureServices' -ErrorAction SilentlyContinue)) {
    New-AzSqlServerFirewallRule -ResourceGroupName $ResourceGroup -ServerName $SqlServerName `
        -FirewallRuleName 'AllowAzureServices' -StartIpAddress '0.0.0.0' -EndIpAddress '0.0.0.0' | Out-Null
}
Write-Ok "Firewall rule set"

# ---- 5. Key Vault + secrets -------------------------------------------------
Write-Step "Creating Key Vault '$KeyVaultName'"
if (-not (Get-AzKeyVault -VaultName $KeyVaultName -ErrorAction SilentlyContinue)) {
    New-AzKeyVault -Name $KeyVaultName -ResourceGroupName $ResourceGroup -Location $Location -Tag $Tags | Out-Null
}
Write-Ok "Key Vault ready"

$sqlConnString = "Server=tcp:$SqlServerName.database.windows.net,1433;Initial Catalog=$SqlDbName;Persist Security Info=False;User ID=$SqlAdminUser;Password=$SqlAdminPassword;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

Write-Step "Storing secrets in Key Vault"
Set-AzKeyVaultSecret -VaultName $KeyVaultName -Name 'SqlConnectionString' -SecretValue (ConvertTo-SecureString $sqlConnString -AsPlainText -Force) | Out-Null
Set-AzKeyVaultSecret -VaultName $KeyVaultName -Name 'SqlAdminPassword'    -SecretValue $securePw | Out-Null
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
$appSettings = @{
    'SqlConnectionString' = "@Microsoft.KeyVault(VaultName=$KeyVaultName;SecretName=SqlConnectionString)"
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
            if ($k -eq 'SqlConnectionString') { continue }   # keep our freshly-built one
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

if ($generatedPw) {
    Write-Host "`nNOTE: SQL admin password generated & stored in Key Vault. Retrieve with:"
    Write-Host "  (Get-AzKeyVaultSecret -VaultName $KeyVaultName -Name SqlAdminPassword -AsPlainText)"
}
if ($ImportSettingsFile -or $ImportSecretsFile) {
    @"

NOTE: captured config was imported. If your app loads Key Vault via the
      configuration provider (as taskboard does for 'Jwt--Key'), make sure the
      app points at THIS vault ($KeyVaultName) — update any captured setting
      that holds the old vault URI (e.g. KeyVault__Uri / VaultUri).
"@ | Write-Host
}
Write-Host "`nTear down:  Remove-AzResourceGroup -Name $ResourceGroup -Force -AsJob"
