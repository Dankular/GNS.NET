[CmdletBinding()]
param(
    [string]$VpsCommand = "D:\Dev Proj\vps-ctrl\vps.cmd",
    [string]$TurnImage = "coturn/coturn:4.6.3",
    [string]$PublicAddress = "2.28.23.253"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $VpsCommand -PathType Leaf)) { throw "VPS helper was not found: $VpsCommand" }

& $VpsCommand test
if ($LASTEXITCODE -ne 0) { throw "VPS connectivity check failed." }

# Keep the TURN shared secret inside the VPS shell. Only the final pass summary is emitted. Encode
# the non-secret script because the PuTTY wrapper accepts a single remote command argument.
$scriptBody = @'
set -eu
container=$(docker ps --filter "ancestor=__TURN_IMAGE__" --format '{{.Names}}' | head -n 1)
test -n "$container"
docker inspect "$container" >/dev/null
docker port "$container" | grep -E '3478/(tcp|udp)|49152|49252' >/dev/null
secret=$(docker inspect -f '{{range .Config.Cmd}}{{println .}}{{end}}' "$container" | sed -n 's/^--static-auth-secret=//p' | head -n 1)
test -n "$secret"
docker run --rm --network host __TURN_IMAGE__ turnutils_uclient -y -W "$secret" -p 3478 -n 1 __PUBLIC_ADDRESS__ >/dev/null
unset secret
printf 'Coturn VPS validation passed: container=%s address=%s image=%s\n' "$container" __PUBLIC_ADDRESS__ __TURN_IMAGE__
'@

$scriptBody = $scriptBody.Replace('__TURN_IMAGE__', $TurnImage).Replace('__PUBLIC_ADDRESS__', $PublicAddress)
$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($scriptBody))
$remote = "printf '%s' '$encoded' | base64 --decode | sh"
$remote = $remote.Replace("`r`n", "`n").TrimEnd("`r", "`n")
& $VpsCommand exec $remote
if ($LASTEXITCODE -ne 0) { throw "Coturn VPS validation failed." }
