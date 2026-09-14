[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$OutputDirectory = "artifacts\release-smoke",
    [switch]$SkipConsumer
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
$packageDirectory = Join-Path $output "package"
$consumerDirectory = Join-Path $output "consumer"

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Path $packageDirectory | Out-Null

Push-Location $repo
try {
    Invoke-Dotnet @("restore", "GnsNet.sln")
    Invoke-Dotnet @("pack", "src/GnsNet/GnsNet.csproj", "--configuration", $Configuration, "--no-restore", "-p:GnsNetBackend=Posix64", "--output", $packageDirectory)
    $package = Get-ChildItem -LiteralPath $packageDirectory -Filter "GnsNet.*.nupkg" | Where-Object { $_.Name -notlike "*.symbols.nupkg" } | Select-Object -First 1
    if ($null -eq $package) { throw "The release package was not produced." }
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        foreach ($entryName in @("README.md", "lib/net9.0/GnsNet.dll")) {
            if ($null -eq $archive.GetEntry($entryName)) { throw "Package $($package.Name) is missing $entryName." }
        }
    } finally { $archive.Dispose() }
    if (-not $SkipConsumer) {
        Invoke-Dotnet @("new", "classlib", "--framework", "net9.0", "--output", $consumerDirectory, "--no-restore")
        $consumerProject = Join-Path $consumerDirectory "ReleaseSmokeConsumer.csproj"
        $nugetConfig = Join-Path $consumerDirectory "NuGet.config"
        $version = $package.BaseName.Substring(7)
        $projectXml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net9.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><PackageReference Include="GnsNet" Version="$version" /></ItemGroup>
</Project>
"@
        Set-Content -LiteralPath $consumerProject -Value $projectXml -Encoding utf8
        Set-Content -LiteralPath (Join-Path $consumerDirectory "Smoke.cs") -Value "using GnsNet;`npublic sealed class Smoke { public ProtocolVersion Version => new(1, 0, 1); }`n" -Encoding utf8
        $nugetXml = @"
<configuration>
  <packageSources>
    <clear />
    <add key="local-release" value="$packageDirectory" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@
        Set-Content -LiteralPath $nugetConfig -Value $nugetXml -Encoding utf8
        Invoke-Dotnet @("restore", $consumerProject, "--configfile", $nugetConfig)
        Invoke-Dotnet @("build", $consumerProject, "--configuration", $Configuration, "--no-restore")
    }
} finally { Pop-Location }

Write-Host "Release smoke passed: $($package.Name)"
