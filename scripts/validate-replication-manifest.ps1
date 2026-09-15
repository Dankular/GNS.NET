[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CurrentManifest,
    [Parameter(Mandatory = $true)][string]$CandidateManifest
)

$ErrorActionPreference = 'Stop'

function Read-Manifest([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Manifest not found: $Path" }
    try { $value = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -ErrorAction Stop }
    catch { throw "Invalid JSON in ${Path}: $($_.Exception.Message)" }
    if ($null -eq $value -or $value -is [array] -or $null -eq $value.components) { throw "${Path} must define a components array." }
    return $value
}

function IdSet($values) { @($values | ForEach-Object { [int]$_ }) }

$current = Read-Manifest $CurrentManifest
$candidate = Read-Manifest $CandidateManifest
$violations = [Collections.Generic.List[string]]::new()

if ([int]$candidate.replicationProtocol -lt [int]$current.replicationProtocol) { $violations.Add('replicationProtocol regressed.') }
$reservedComponents = IdSet $candidate.reservedComponentIds
$currentById = @{}
$candidateById = @{}
foreach ($component in @($current.components)) { if ($currentById.ContainsKey([int]$component.id)) { $violations.Add("Duplicate current component ID $($component.id).") } else { $currentById[[int]$component.id] = $component } }
foreach ($component in @($candidate.components)) { if ($candidateById.ContainsKey([int]$component.id)) { $violations.Add("Duplicate candidate component ID $($component.id).") } else { $candidateById[[int]$component.id] = $component } }
foreach ($id in $currentById.Keys) {
    if (-not $candidateById.ContainsKey($id)) { if ($reservedComponents -notcontains $id) { $violations.Add("Component ID $id was removed without reservation.") }; continue }
    $old = $currentById[$id]; $new = $candidateById[$id]
    if ([string]$old.name -ne [string]$new.name) { $violations.Add("Component ID $id changed name from '$($old.name)' to '$($new.name)'.") }
    if ([int]$new.schema -lt [int]$old.schema) { $violations.Add("Component ID $id schema regressed.") }
    $oldFields = @{}; $newFields = @{}
    foreach ($field in @($old.fields)) { $oldFields[[int]$field.id] = $field }
    foreach ($field in @($new.fields)) { $newFields[[int]$field.id] = $field }
    $reservedFields = IdSet $new.reservedFieldIds
    foreach ($fieldId in $oldFields.Keys) {
        if (-not $newFields.ContainsKey($fieldId)) { if ($reservedFields -notcontains $fieldId) { $violations.Add("Component ID $id field ID $fieldId was removed without reservation.") }; continue }
        $oldField = $oldFields[$fieldId]; $newField = $newFields[$fieldId]
        if ([string]$oldField.wireType -ne [string]$newField.wireType -and [int]$new.schema -le [int]$old.schema) { $violations.Add("Component ID $id field ID $fieldId changed wireType without schema advancement.") }
    }
}
foreach ($id in $candidateById.Keys) { if ($reservedComponents -contains $id) { $violations.Add("Active component ID $id is also reserved.") } }

[ordered]@{ compatible = $violations.Count -eq 0; violations = @($violations); current = $current; candidate = $candidate } | ConvertTo-Json -Depth 10
if ($violations.Count -gt 0) { exit 2 }
exit 0
