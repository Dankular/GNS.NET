[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$CapabilityOutput = "",
    [switch]$RequireCertificate
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $PackagePath)) { throw "Package not found: $PackagePath" }
$dotnet = Get-Command dotnet -ErrorAction Stop
$signHelp = (& $dotnet.Source nuget sign --help 2>&1 | Out-String)
$supported = $signHelp -match "certificate-path"
$configured = -not [string]::IsNullOrWhiteSpace($CertificatePath)
$package = Get-Item -LiteralPath $PackagePath -ErrorAction Stop
if ($package.Extension -ne '.nupkg' -or $package.Name -like '*.symbols.nupkg') { throw "PackagePath must identify a non-symbol .nupkg package." }
$result = [ordered]@{ supported = $supported; configured = $configured; certificateValid = $false; hasPrivateKey = $false; signed = $false; package = $package.FullName }
if (-not $configured -and $RequireCertificate) { throw "A signing certificate is required, but none was supplied." }
if ($configured) {
    if (-not $supported) { throw "This dotnet SDK does not support NuGet package signing." }
    if (-not (Test-Path -LiteralPath $CertificatePath)) { throw "Certificate not found: $CertificatePath" }
    try {
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path -LiteralPath $CertificatePath).Path, $CertificatePassword, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        if (-not $certificate.HasPrivateKey) { throw "Certificate does not contain a private key." }
        $result.certificateValid = $true; $result.hasPrivateKey = $true
    } catch { throw "Signing certificate could not be loaded: $($_.Exception.Message)" }
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
