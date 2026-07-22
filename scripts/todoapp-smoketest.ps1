<#
  TaskBoard API smoke test — exercises the api backend end to end against a locally
  running instance. Covers EVERY Swagger endpoint, including Google sign-in via the
  Development-only fake Google validator (no real Google token required).

  1. Start the API with the fake Google validator enabled (Development only):
        $env:ASPNETCORE_ENVIRONMENT   = "Development"
        $env:Authentication__Google__UseFake = "true"
        dotnet run --project src\TodoApp.WebApi
     Without Authentication:Google:UseFake=true the Google tests will fail (the real
     validator either rejects the fake token or errors when no client id is configured).
  2. Then run this script (PowerShell 7 recommended, but Windows PowerShell 5.1 works too):
        pwsh .\todoapp-smoketest.ps1
     or:
        powershell -ExecutionPolicy Bypass -File .\todoapp-smoketest.ps1

  Override the base URL if needed:  .\todoapp-smoketest.ps1 -BaseUrl http://localhost:5080

  The fake validator accepts a token of the form  fake:{email}  as a verified Google
  identity and rejects anything else (401) — mirroring the real validator's behavior.
#>

param(
    [string]$BaseUrl = "http://localhost:5080"
)

$script:pass = 0
$script:fail = 0

function Invoke-Api {
    param([string]$Method, [string]$Path, $Body, [string]$Token)
    $headers = @{}
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }
    $params = @{ Method = $Method; Uri = "$BaseUrl$Path"; Headers = $headers; UseBasicParsing = $true }
    if ($null -ne $Body) {
        $params['Body'] = ($Body | ConvertTo-Json -Depth 6)
        $params['ContentType'] = 'application/json'
    }
    try {
        $resp = Invoke-WebRequest @params
        $data = $null
        if ($resp.Content) { try { $data = $resp.Content | ConvertFrom-Json } catch { $data = $resp.Content } }
        return [pscustomobject]@{ Status = [int]$resp.StatusCode; Data = $data }
    }
    catch {
        $status = -1
        if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
        $data = $null
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) {
            try { $data = $_.ErrorDetails.Message | ConvertFrom-Json } catch { $data = $_.ErrorDetails.Message }
        }
        return [pscustomobject]@{ Status = $status; Data = $data }
    }
}

function Check {
    param([string]$Name, [int]$Expected, $Result)
    if ($Result.Status -eq $Expected) {
        Write-Host ("  [PASS] {0} -> {1}" -f $Name, $Result.Status) -ForegroundColor Green
        $script:pass++
    }
    else {
        Write-Host ("  [FAIL] {0} -> got {1}, expected {2}" -f $Name, $Result.Status, $Expected) -ForegroundColor Red
        if ($Result.Data) { Write-Host ("         body: " + ($Result.Data | ConvertTo-Json -Depth 6 -Compress)) -ForegroundColor DarkGray }
        $script:fail++
    }
}

function Assert {
    param([string]$Name, [bool]$Condition)
    if ($Condition) { Write-Host "  [PASS] $Name" -ForegroundColor Green; $script:pass++ }
    else { Write-Host "  [FAIL] $Name" -ForegroundColor Red; $script:fail++ }
}

# Registers a throwaway user and returns the parsed AuthResponse (accessToken, refreshToken, user).
function New-User {
    $email = "smoke-$([guid]::NewGuid().ToString('N').Substring(0,8))@example.com"
    $r = Invoke-Api -Method POST -Path "/api/auth/register" -Body @{ email = $email; password = "Password1" }
    if (($r.Status -ne 200) -and ($r.Status -ne 201)) { throw "register failed with $($r.Status)" }
    $r.Data | Add-Member -NotePropertyName email -NotePropertyValue $email -Force -PassThru
}

Write-Host "TaskBoard API smoke test against $BaseUrl" -ForegroundColor Cyan
Write-Host ""

# ---- Auth ----
Write-Host "Auth" -ForegroundColor Yellow
$a = New-User
Assert "POST /auth/register ($($a.email)) returns a token" ([bool]$a.accessToken)
$token = $a.accessToken

$unauth = Invoke-Api -Method GET -Path "/api/todos"
Check "GET /todos without token is 401" 401 $unauth

$me = Invoke-Api -Method GET -Path "/api/auth/me" -Token $token
Check "GET /auth/me" 200 $me
Assert "GET /auth/me returns the current user's email" ($me.Data.email -eq $a.email)

$login = Invoke-Api -Method POST -Path "/api/auth/login" -Body @{ email = $a.email; password = "Password1" }
Check "POST /auth/login" 200 $login
Assert "POST /auth/login returns a token" ([bool]$login.Data.accessToken)

# ---- Categories ----
Write-Host ""
Write-Host "Categories" -ForegroundColor Yellow
$cats = Invoke-Api -Method GET -Path "/api/categories" -Token $token
Check "GET /categories" 200 $cats
Write-Host ("         seeded: " + (($cats.Data | ForEach-Object { $_.name }) -join ", ")) -ForegroundColor DarkGray
Assert "5 default categories seeded" ($cats.Data.Count -eq 5)

$newCat = Invoke-Api -Method POST -Path "/api/categories" -Token $token -Body @{ name = "Fitness"; color = "#22c55e" }
Check "POST /categories (Fitness)" 201 $newCat
$catId = $newCat.Data.id

$dupe = Invoke-Api -Method POST -Path "/api/categories" -Token $token -Body @{ name = "Fitness"; color = "#3b82f6" }
Check "POST /categories duplicate name is 409" 409 $dupe

$badColor = Invoke-Api -Method POST -Path "/api/categories" -Token $token -Body @{ name = "Bad"; color = "not-a-color" }
Check "POST /categories invalid color is 400" 400 $badColor

$updCat = Invoke-Api -Method PUT -Path "/api/categories/$catId" -Token $token -Body @{ name = "Fitness and Health"; color = "#16a34a" }
Check "PUT /categories/$catId" 200 $updCat

# ---- Todos ----
Write-Host ""
Write-Host "Todos" -ForegroundColor Yellow
$newTodo = Invoke-Api -Method POST -Path "/api/todos" -Token $token -Body @{ title = "Buy milk"; priority = 2; categoryId = $catId }
Check "POST /todos" 201 $newTodo
$todoId = $newTodo.Data.id
$tok1 = $newTodo.Data.concurrencyToken

$todos = Invoke-Api -Method GET -Path "/api/todos" -Token $token
Check "GET /todos" 200 $todos
Assert "created todo is in the list" (@($todos.Data | Where-Object { $_.id -eq $todoId }).Count -eq 1)

$byId = Invoke-Api -Method GET -Path "/api/todos/$todoId" -Token $token
Check "GET /todos/$todoId" 200 $byId

$updTodo = Invoke-Api -Method PUT -Path "/api/todos/$todoId" -Token $token -Body @{ title = "Buy oat milk"; priority = 1; concurrencyToken = $tok1 }
Check "PUT /todos/$todoId (current token)" 200 $updTodo
$tok2 = $updTodo.Data.concurrencyToken
Assert "concurrency token rotated on update" ($tok2 -and ($tok2 -ne $tok1))

$stale = Invoke-Api -Method PUT -Path "/api/todos/$todoId" -Token $token -Body @{ title = "Hijack"; priority = 0; concurrencyToken = $tok1 }
Check "PUT /todos/$todoId (stale token) is 409" 409 $stale

$patch = Invoke-Api -Method PATCH -Path "/api/todos/$todoId/status" -Token $token -Body @{ status = 2 }
Check "PATCH /todos/$todoId/status -> Done" 200 $patch
Assert "todo isCompleted=true after moving to Done" ($patch.Data.isCompleted -eq $true)

# ---- Cascade + cleanup ----
Write-Host ""
Write-Host "Cascade + cleanup" -ForegroundColor Yellow
$delCat = Invoke-Api -Method DELETE -Path "/api/categories/$catId" -Token $token
Check "DELETE /categories/$catId" 204 $delCat

$afterDel = Invoke-Api -Method GET -Path "/api/todos/$todoId" -Token $token
Check "GET /todos/$todoId after category delete" 200 $afterDel
Assert "todo left uncategorized (FK ON DELETE SET NULL)" ($null -eq $afterDel.Data.categoryId)

$delTodo = Invoke-Api -Method DELETE -Path "/api/todos/$todoId" -Token $token
Check "DELETE /todos/$todoId" 204 $delTodo

# ---- Session lifecycle (fresh throwaway users, since these revoke sessions) ----
Write-Host ""
Write-Host "Session lifecycle" -ForegroundColor Yellow

# Refresh rotation + reuse detection
$b = New-User
$refresh = Invoke-Api -Method POST -Path "/api/auth/refresh" -Body @{ refreshToken = $b.refreshToken }
Check "POST /auth/refresh (rotate)" 200 $refresh
Assert "refresh returns a new refresh token" ($refresh.Data.refreshToken -and ($refresh.Data.refreshToken -ne $b.refreshToken))
$reuse = Invoke-Api -Method POST -Path "/api/auth/refresh" -Body @{ refreshToken = $b.refreshToken }
Check "POST /auth/refresh replaying the old token is 401 (reuse detected)" 401 $reuse

# Logout revokes a single refresh token
$c = New-User
$logout = Invoke-Api -Method POST -Path "/api/auth/logout" -Token $c.accessToken -Body @{ refreshToken = $c.refreshToken }
Check "POST /auth/logout" 204 $logout
$afterLogout = Invoke-Api -Method POST -Path "/api/auth/refresh" -Body @{ refreshToken = $c.refreshToken }
Check "refresh with a logged-out token is 401" 401 $afterLogout

# Revoke-all rotates the security stamp -> the existing access token is rejected immediately
$d = New-User
Check "GET /todos works before revoke-all" 200 (Invoke-Api -Method GET -Path "/api/todos" -Token $d.accessToken)
$revokeAll = Invoke-Api -Method POST -Path "/api/auth/revoke-all" -Token $d.accessToken -Body @{}
Check "POST /auth/revoke-all" 204 $revokeAll
Check "the same access token is rejected after revoke-all" 401 (Invoke-Api -Method GET -Path "/api/todos" -Token $d.accessToken)

# ---- Google sign-in (via the Development-only fake validator) ----
Write-Host ""
Write-Host "Google sign-in (fake identity)" -ForegroundColor Yellow
$gEmail = "google-$([guid]::NewGuid().ToString('N').Substring(0,8))@example.com"
$gGood = Invoke-Api -Method POST -Path "/api/auth/google" -Body @{ idToken = "fake:$gEmail" }
Check "POST /auth/google with a fake VALID identity -> 200" 200 $gGood
Assert "Google sign-in issues a token for the new user" ([bool]$gGood.Data.accessToken)
Assert "Google sign-in creates the expected account" ($gGood.Data.user.email -eq $gEmail)
if ($gGood.Status -ne 200) {
    Write-Host "         (is the API running with Authentication:Google:UseFake=true in Development?)" -ForegroundColor DarkGray
}
$gBad = Invoke-Api -Method POST -Path "/api/auth/google" -Body @{ idToken = "not-a-real-token" }
Check "POST /auth/google with an INVALID token -> 401" 401 $gBad

# ---- Summary ----
Write-Host ""
$color = if ($script:fail -eq 0) { 'Green' } else { 'Red' }
Write-Host ("Result: {0} passed, {1} failed" -f $script:pass, $script:fail) -ForegroundColor $color
