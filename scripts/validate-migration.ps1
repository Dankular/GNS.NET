[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CurrentManifest,
    [Parameter(Mandatory = $true)][string]$CandidateManifest,
    [switch]$AllowBreaking
)

$ErrorActionPreference = "Stop"
function Read-Manifest([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Migration manifest not found: $Path" }
    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    foreach ($property in @("protocolMajor", "protocolMinor", "schema", "replaySchema")) {
        $value = $manifest.$property
        if ($null -eq $value -or "$value" -notmatch '^-?\d+$') { throw "$Path must define integer '$property'." }
        if ([long]$value -lt 0) { throw "$Path has a negative '$property'." }
    }
    return $manifest
}
$current = Read-Manifest $CurrentManifest
$candidate = Read-Manifest $CandidateManifest
$violations = [Collections.Generic.List[string]]::new()
if ($current.protocolMajor -ne $candidate.protocolMajor) { $violations.Add("protocolMajor changes from $($current.protocolMajor) to $($candidate.protocolMajor)") }
if ($candidate.protocolMinor -lt $current.protocolMinor) { $violations.Add("protocolMinor decreases from $($current.protocolMinor) to $($candidate.protocolMinor)") }
if ($current.schema -ne $candidate.schema) { $violations.Add("schema changes from $($current.schema) to $($candidate.schema)") }
if ($candidate.replaySchema -lt $current.replaySchema) { $violations.Add("replaySchema decreases from $($current.replaySchema) to $($candidate.replaySchema)") }
$result = [ordered]@{ compatible = $violations.Count -eq 0 -or $AllowBreaking; breakingOverride = [bool]$AllowBreaking; violations = @($violations); current = $current; candidate = $candidate }
$result | ConvertTo-Json -Depth 5
if ($violations.Count -gt 0 -and -not $AllowBreaking) { exit 2 }
exit 0
