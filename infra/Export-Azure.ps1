<#
.SYNOPSIS
    Capture the CURRENT Azure setup as it exists, into ARM template(s),
    decompiled Bicep, and an inventory (Markdown + CSV).

.DESCRIPTION
    Read-only: this script exports/reads from Azure and does not modify anything.
    Uses the Azure CLI (az) so behavior matches export-azure.sh exactly.

.PREREQUISITES
    - Azure CLI logged in:  az login
    - Subscription selected: az account set --subscription <id-or-name>
    - Bicep CLI (az installs it on demand)

.EXAMPLE
    ./Export-Azure.ps1 -ResourceGroup rg-contoso-dev

.EXAMPLE
    ./Export-Azure.ps1 -All -OutputDir ./export

.EXAMPLE
    ./Export-Azure.ps1 -ResourceGroup rg-contoso-dev -NoBicep
#>
[CmdletBinding(DefaultParameterSetName = 'Group')]
param(
    [Parameter(ParameterSetName = 'Group', Mandatory)]
    [string] $ResourceGroup,

    [Parameter(ParameterSetName = 'All', Mandatory)]
    [switch] $All,

    [string] $OutputDir = './azure-export',
    [switch] $NoBicep,
    [switch] $NoInventory,
    [switch] $NoAppConfig,        # skip capturing web-app settings / Key Vault secret names
    [switch] $IncludeSecretValues # ALSO pull Key Vault secret VALUES (sensitive!)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step($m) { Write-Host "`n==> $m" -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host "  OK  $m"   -ForegroundColor Green }
function Write-Warn2($m){ Write-Host "  !   $m"   -ForegroundColor Yellow }

# Preflight
if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw "Azure CLI (az) not found." }
try { $null = az account show 2>$null } catch {}
$acct = az account show -o json 2>$null | ConvertFrom-Json
if (-not $acct) { throw "Not logged in. Run 'az login'." }

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
Write-Step "Subscription: $($acct.name) ($($acct.id))"
Write-Step "Output directory: $OutputDir"

# Which resource groups?
if ($All) {
    $groups = az group list --query '[].name' -o tsv
    $groups = @($groups -split "`n" | Where-Object { $_ -ne '' })
    Write-Step "Exporting ALL resource groups ($($groups.Count) found)"
} else {
    $groups = @($ResourceGroup)
}

foreach ($rg in $groups) {
    Write-Step "Resource group: $rg"
    az group show --name $rg 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warn2 "Resource group '$rg' not found — skipping."; continue }

    $armFile = Join-Path $OutputDir "$rg.arm.json"
    az group export --name $rg --skip-all-params -o json 2>$null | Out-File -FilePath $armFile -Encoding utf8
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $armFile) -or (Get-Item $armFile).Length -eq 0) {
        az group export --name $rg -o json 2>$null | Out-File -FilePath $armFile -Encoding utf8
        if ($LASTEXITCODE -ne 0 -or (Get-Item $armFile).Length -eq 0) {
            Write-Warn2 "Could not export ARM for '$rg' (unsupported resource types?). Skipping."
            continue
        }
        Write-Warn2 "ARM exported with warnings -> $armFile"
    } else {
        Write-Ok "ARM template  -> $armFile"
    }

    if (-not $NoBicep) {
        $bicepFile = Join-Path $OutputDir "$rg.bicep"
        az bicep decompile --file $armFile --outfile $bicepFile 2>$null
        if ($LASTEXITCODE -eq 0) { Write-Ok "Bicep         -> $bicepFile" }
        else { Write-Warn2 "Bicep decompile failed for '$rg'. Try: az bicep install" }
    }

    # ---- App settings (env vars) + Key Vault secret names -------------------
    if (-not $NoAppConfig) {
        # Web apps (API/web): application settings + connection strings.
        $apps = az webapp list -g $rg --query '[].name' -o tsv 2>$null
        $apps = @($apps -split "`n" | Where-Object { $_ -ne '' })
        foreach ($app in $apps) {
            $settingsJson = az webapp config appsettings list -g $rg -n $app -o json 2>$null
            if ($LASTEXITCODE -ne 0 -or -not $settingsJson) {
                Write-Warn2 "Could not read app settings for '$app' (permissions?)."
                continue
            }
            $settingsJson | Out-File (Join-Path $OutputDir "$app.appsettings.json") -Encoding utf8
            $csJson = az webapp config connection-string list -g $rg -n $app -o json 2>$null
            if ($LASTEXITCODE -eq 0 -and $csJson) {
                $csJson | Out-File (Join-Path $OutputDir "$app.connectionstrings.json") -Encoding utf8
            }
            # Flatten to KEY=value. Key Vault references are kept as @kv:SecretName
            # (never resolved), so no secret values leak into this file.
            $envLines = [System.Collections.Generic.List[string]]::new()
            $envLines.Add("# App settings (env vars) exported from web app '$app' — resource group '$rg'")
            $envLines.Add("# Key Vault-backed values are shown as @kv:<SecretName> (values stay in the vault).")
            foreach ($s in ($settingsJson | ConvertFrom-Json)) {
                $val = [string]$s.value
                $m = [regex]::Match($val, 'SecretName=([^;)\s]+)')
                if ($val -match 'Microsoft\.KeyVault' -and $m.Success) {
                    $envLines.Add("$($s.name)=@kv:$($m.Groups[1].Value)")
                } else {
                    $envLines.Add("$($s.name)=$val")
                }
            }
            Set-Content -Path (Join-Path $OutputDir "$app.settings.env") -Value ($envLines -join [Environment]::NewLine) -Encoding utf8
            Write-Ok "App settings  -> $app.settings.env  (+ $app.appsettings.json)"
        }

        # Key Vaults: secret NAMES by default; VALUES only with -IncludeSecretValues.
        $vaults = az keyvault list -g $rg --query '[].name' -o tsv 2>$null
        $vaults = @($vaults -split "`n" | Where-Object { $_ -ne '' })
        foreach ($kv in $vaults) {
            $names = az keyvault secret list --vault-name $kv --query '[].name' -o tsv 2>$null
            if ($LASTEXITCODE -ne 0) {
                Write-Warn2 "No data-plane access to list secrets in '$kv' (need 'Key Vault Secrets User' or a list policy)."
                continue
            }
            $names = @($names -split "`n" | Where-Object { $_ -ne '' })
            $kvLines = [System.Collections.Generic.List[string]]::new()
            if ($IncludeSecretValues) {
                Write-Warn2 "Exporting SECRET VALUES from '$kv' to disk — handle the output file carefully."
                $kvLines.Add("# Key Vault '$kv' secrets WITH VALUES — SENSITIVE. Do not commit or share.")
                foreach ($nm in $names) {
                    $v = az keyvault secret show --vault-name $kv --name $nm --query value -o tsv 2>$null
                    $kvLines.Add("$nm=$v")
                }
                $outName = "keyvault-$kv.secrets.env"
            } else {
                $kvLines.Add("# Key Vault '$kv' — secret NAMES only (values NOT exported; use -IncludeSecretValues to include).")
                foreach ($nm in $names) { $kvLines.Add($nm) }
                $outName = "keyvault-$kv.secrets.txt"
            }
            Set-Content -Path (Join-Path $OutputDir $outName) -Value ($kvLines -join [Environment]::NewLine) -Encoding utf8
            Write-Ok "Key Vault     -> $outName  ($($names.Count) secrets$(if ($IncludeSecretValues) {' WITH VALUES'} else {''}))"
        }
    }
}

# Inventory (Markdown + CSV)
if (-not $NoInventory) {
    Write-Step "Building resource inventory"
    $query = "[].{name:name, type:type, group:resourceGroup, location:location, sku:sku.name, kind:kind}"
    if ($All) {
        $json = az resource list --query $query -o json
        $scope = "subscription '$($acct.name)'"
    } else {
        $json = az resource list --resource-group $ResourceGroup --query $query -o json
        $scope = "resource group '$ResourceGroup'"
    }

    $cols = 'name','type','group','location','sku','kind'
    $csvPath = Join-Path $OutputDir 'inventory.csv'
    $mdPath  = Join-Path $OutputDir 'inventory.md'

    # Normalize into plain objects (nulls -> empty string) for stable output
    $rows = foreach ($item in @($json | ConvertFrom-Json)) {
        $o = [ordered]@{}
        foreach ($c in $cols) {
            $v = $item.PSObject.Properties[$c]
            $o[$c] = if ($v -and $null -ne $v.Value) { [string]$v.Value } else { '' }
        }
        [pscustomobject]$o
    }
    $resources = @($rows | Sort-Object group, type, name)

    $resources | Export-Csv -Path $csvPath -NoTypeInformation

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# Azure resource inventory — $scope")
    $lines.Add('')
    $lines.Add("Total resources: **$($resources.Count)**")
    $lines.Add('')
    $lines.Add('| Name | Type | Resource group | Location | SKU | Kind |')
    $lines.Add('|---|---|---|---|---|---|')
    foreach ($r in $resources) {
        $vals = foreach ($c in $cols) { $r.$c }   # already normalized to strings
        $lines.Add('| ' + ($vals -join ' | ') + ' |')
    }
    Set-Content -Path $mdPath -Value ($lines -join [Environment]::NewLine) -Encoding utf8
    Write-Ok "Inventory     -> $mdPath  (+ inventory.csv)  [$($resources.Count) resources]"
}

Write-Step "Export complete"
Write-Host "`nContents of $OutputDir:"
Get-ChildItem $OutputDir | Select-Object Name, Length | Format-Table -AutoSize

@"

Next steps:
  * Review the .bicep file(s) — the cleanest re-deployable form.
  * Re-deploy a captured group with:
      az deployment group create -g <target-rg> --template-file $OutputDir/<rg>.bicep
  * The ARM JSON is authoritative if a Bicep decompile looks off.
"@ | Write-Host
