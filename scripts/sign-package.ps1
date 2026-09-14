[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$CapabilityOutput = ""
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $PackagePath)) { throw "Package not found: $PackagePath" }
$dotnet = Get-Command dotnet -ErrorAction Stop
$signHelp = (& $dotnet.Source nuget sign --help 2>&1 | Out-String)
$supported = $signHelp -match "certificate-path"
$configured = -not [string]::IsNullOrWhiteSpace($CertificatePath)
$result = [ordered]@{ supported = $supported; configured = $configured; signed = $false; package = (Resolve-Path -LiteralPath $PackagePath).Path }
if ($configured) {
    if (-not $supported) { throw "This dotnet SDK does not support NuGet package signing." }
    if (-not (Test-Path -LiteralPath $CertificatePath)) { throw "Certificate not found: $CertificatePath" }
    $arguments = @("nuget", "sign", $PackagePath, "--certificate-path", $CertificatePath, "--overwrite")
    if ($CertificatePassword) { $arguments += @("--certificate-password", $CertificatePassword) }
    & $dotnet.Source @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet nuget sign failed with exit code $LASTEXITCODE." }
    $result.signed = $true
}
$json = $result | ConvertTo-Json -Depth 4
if ($CapabilityOutput) { Set-Content -LiteralPath $CapabilityOutput -Value $json -Encoding utf8 }
Write-Output $json
if (-not $configured) { Write-Warning "Signing capability detected; no certificate supplied, so the package was not modified." }
