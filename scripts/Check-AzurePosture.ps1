<#
.SYNOPSIS
    Assert the data-tier security posture of the live environment (PowerShell twin of
    check-azure-posture.sh).

.DESCRIPTION
    Every check corresponds to a finding from the 2026-07-28 DevSecOps review (M2/M3 and issues
    found while remediating them). Run after any infrastructure change, and periodically — these
    are settings a well-meaning "just get it working" change can silently undo, and none of them
    are visible from the application code.

    Read-only. Exits 1 if any check fails.

.PREREQUISITES
    az login   (an account with reader access to the resource group)

.EXAMPLE
    ./Check-AzurePosture.ps1

.EXAMPLE
    ./Check-AzurePosture.ps1 -ResourceGroup rg-taskboard -SqlServer taskboard-05-sql -WebApp taskboard-06-api
#>
[CmdletBinding()]
param(
    [string] $ResourceGroup = 'rg-taskboard',
    [string] $SqlServer     = 'taskboard-05-sql',
    [string] $WebApp        = 'taskboard-06-api'
)

$ErrorActionPreference = 'Stop'

$script:pass = 0
$script:fail = 0
function Ok($m)   { Write-Host "  [PASS] $m" -ForegroundColor Green; $script:pass++ }
function Bad($m)  { Write-Host "  [FAIL] $m" -ForegroundColor Red;   $script:fail++ }
function Note($m) { Write-Host "         $m" -ForegroundColor DarkGray }

if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw 'Azure CLI (az) not found.' }
$null = az account show 2>$null
if ($LASTEXITCODE -ne 0) { throw "Not logged in. Run 'az login'." }

Write-Host ""
Write-Host "Azure security posture - $ResourceGroup / $SqlServer / $WebApp"
Write-Host '------------------------------------------------------------'

# ---- M3: the SQL firewall must not be open to all of Azure -------------------
$rules = az sql server firewall-rule list -g $ResourceGroup --server $SqlServer -o json | ConvertFrom-Json

$allAzure = $rules | Where-Object { $_.startIpAddress -eq '0.0.0.0' -and $_.endIpAddress -eq '0.0.0.0' }
if ($allAzure) {
    Bad 'M3: a rule allows ALL Azure services (0.0.0.0). Any Azure tenant can reach this server.'
    Note "offending rule(s): $($allAzure.name -join ', ')"
} else {
    Ok 'M3: no all-of-Azure (0.0.0.0) firewall rule'
}

$allNet = $rules | Where-Object { $_.startIpAddress -eq '0.0.0.0' -and $_.endIpAddress -eq '255.255.255.255' }
if ($allNet) { Bad 'M3: a rule allows the entire internet' } else { Ok 'M3: no allow-the-internet firewall rule' }

$outbound = (az webapp show -n $WebApp -g $ResourceGroup --query possibleOutboundIpAddresses -o tsv) -split ','
$missing  = @($outbound | Where-Object { $ip = $_.Trim(); -not ($rules | Where-Object { $_.startIpAddress -eq $ip }) })
if ($missing.Count -eq 0) {
    Ok 'App Service: every possible outbound IP is allow-listed'
} else {
    Bad "App Service: $($missing.Count) possible outbound IP(s) NOT allow-listed - the app will fail intermittently"
}

# ---- M2: no SQL password authentication -------------------------------------
$adOnly = az sql server ad-only-auth get -g $ResourceGroup -n $SqlServer --query azureAdOnlyAuthentication -o tsv
if ($adOnly -eq 'true') {
    Ok 'M2: Entra-only authentication is enforced (SQL password logins disabled)'
} else {
    Bad 'M2: SQL password authentication is ENABLED - the server-admin password is a live credential'
    Note "fix: az sql server ad-only-auth enable -g $ResourceGroup -n $SqlServer"
}

$admin = az sql server ad-admin list -g $ResourceGroup --server $SqlServer --query '[0].login' -o tsv
if ($admin) { Ok "M2: an Entra administrator is configured ($admin)" }
else        { Bad 'M2: no Entra administrator - with Entra-only auth on, nobody can administer this server' }

# ---- M2: the app's connection string must carry no password ------------------
$cs = az webapp config appsettings list -n $WebApp -g $ResourceGroup `
        --query "[?name=='ConnectionStrings__DefaultConnection'].value" -o tsv
if (-not $cs) {
    Bad 'could not read the connection string app setting'
} else {
    if ($cs -match '(^|;)\s*(password|pwd)\s*=') {
        Bad 'M2: the connection string contains an embedded password'
        Note "fix: use 'Authentication=Active Directory Default' with the App Service managed identity"
    } else { Ok 'M2: connection string carries no password' }

    if ($cs -match 'Authentication=Active Directory') { Ok 'M2: connection string uses Entra (managed identity) authentication' }
    else { Bad 'M2: connection string does not use Entra authentication' }

    if ($cs -match 'Encrypt=True') { Ok 'connection string enforces TLS (Encrypt=True)' }
    else { Bad 'connection string does not set Encrypt=True' }
}

# ---- Transport ---------------------------------------------------------------
$httpsOnly = az webapp show -n $WebApp -g $ResourceGroup --query httpsOnly -o tsv
if ($httpsOnly -eq 'true') { Ok 'App Service enforces HTTPS only' } else { Bad 'App Service httpsOnly is not enabled' }

$minTls = az sql server show -g $ResourceGroup -n $SqlServer --query minimalTlsVersion -o tsv
if ($minTls -eq '1.2') { Ok 'SQL minimum TLS is 1.2' } else { Bad "SQL minimum TLS is '$minTls' (want 1.2)" }

# ---- H1: the demo account must be a deliberate setting -----------------------
$seed = az webapp config appsettings list -n $WebApp -g $ResourceGroup --query "[?name=='Seed__DemoUser'].value" -o tsv
if ($seed -eq 'true') {
    $seedPw = az webapp config appsettings list -n $WebApp -g $ResourceGroup --query "[?name=='Seed__Password'].value" -o tsv
    if ($seedPw) { Ok 'H1: demo seeding is ON and its password is explicitly configured (deliberate public demo)' }
    else         { Bad 'H1: demo seeding is ON but no password is configured - the account will be unusable' }
} else {
    Ok 'H1: demo seeding is off'
}

Write-Host '------------------------------------------------------------'
Write-Host "  $script:pass passed, $script:fail failed"
Write-Host ''
if ($script:fail -gt 0) { exit 1 }
