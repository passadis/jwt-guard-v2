<#
.SYNOPSIS
Prints a menu of paste-able JWTs for the JWT Sentinel token playground —
each one exercises a different gateway validation check.

.EXAMPLE
./get-sample-tokens.ps1 -TenantId <tid> -ApiClientId <api-guid> `
  -DaemonClientId <daemon-guid> -DaemonSecret <secret>
#>
param(
  [Parameter(Mandatory)] [string] $TenantId,
  [Parameter(Mandatory)] [string] $ApiClientId,
  [string] $DaemonClientId,
  [string] $DaemonSecret
)

$ErrorActionPreference = "Stop"

Write-Warning "This utility prints short-lived bearer tokens to the current console. Disable transcript capture, do not redirect output, and close or clear the console after use."

function Show([string] $Title, [string] $Expected, [string] $Token) {
  Write-Host "`n=== $Title" -ForegroundColor Cyan
  Write-Host "    gateway verdict: $Expected" -ForegroundColor Yellow
  if ($Token) { Write-Host $Token } else { Write-Host "(unavailable)" -ForegroundColor DarkGray }
}

# 1. Your own user token for the gate (needs the Azure CLI pre-authorization).
$user = az account get-access-token --scope "api://$ApiClientId/.default" --query accessToken -o tsv 2>$null
Show "Your user token (correct audience, YOUR oid)" "Allow -> 200, header carries your identity" $user

# 2. Your ARM token — genuine Entra token, wrong audience.
$arm = az account get-access-token --query accessToken -o tsv 2>$null
Show "Your ARM token (aud=management.azure.com)" "Deny -> 401, audience mismatch" $arm

# 3. Daemon tokens (if secret provided): correct + Graph audience.
if ($DaemonClientId -and $DaemonSecret) {
  function Get-Tok($Scope) {
    $b = @{ client_id = $DaemonClientId; client_secret = $DaemonSecret; grant_type = 'client_credentials'; scope = $Scope }
    (Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" -Body $b).access_token
  }
  Show "Daemon token (correct audience, app identity)" "Allow -> 200, header carries daemon oid" (Get-Tok "api://$ApiClientId/.default")
  Show "Daemon token for Microsoft Graph" "Deny -> 401, audience mismatch" (Get-Tok "https://graph.microsoft.com/.default")
}

# 4. A non-Entra JWT (classic jwt.io HS256 sample) — wrong issuer, everything wrong.
$jwtio = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c"
Show "jwt.io sample (HS256, not Entra)" "Deny -> 401, no Entra issuer/tenant/audience at all" $jwtio

Write-Host "`nPaste any of these into the UI's 'Decode & judge a pasted token' box," -ForegroundColor Green
Write-Host "or into the chat: 'what would the gate do with this token: <paste>'" -ForegroundColor Green
