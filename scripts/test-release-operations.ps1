[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
$temp = Join-Path ([IO.Path]::GetTempPath()) ("gnsnet-release-operations-" + [Guid]::NewGuid().ToString("N"))
if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $current = Join-Path $temp "current.json"; $candidate = Join-Path $temp "candidate.json"
    '{"protocolMajor":1,"protocolMinor":2,"schema":3,"replaySchema":4,"nativeBackend":"Posix64"}' | Set-Content -LiteralPath $current
    '{"protocolMajor":1,"protocolMinor":3,"schema":3,"replaySchema":4,"nativeBackend":"Win64"}' | Set-Content -LiteralPath $candidate
    & (Join-Path $PSScriptRoot "validate-migration.ps1") -CurrentManifest $current -CandidateManifest $candidate *> $null
    if ($LASTEXITCODE -ne 2) { throw "Migration validator did not reject a native backend change." }
    $package = Join-Path $temp "GnsNet.test.nupkg"
    [IO.File]::WriteAllBytes($package, [byte[]](80,75,3,4))
    $capability = Join-Path $temp "signing.json"
    & (Join-Path $PSScriptRoot "sign-package.ps1") -PackagePath $package -CapabilityOutput $capability *> $null
    if ($LASTEXITCODE -ne 0) { throw "Signing capability probe failed." }
    $report = Get-Content -LiteralPath $capability -Raw | ConvertFrom-Json
    if (-not $report.supported -or $report.configured -or $report.signed) { throw "Signing capability report is inconsistent." }
    Write-Host "Release operations tests passed."
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
