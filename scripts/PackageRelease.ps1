[CmdletBinding()]
param(
    [string]$Version = '0.0.1'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'LoyalWingman.csproj'
$testProject = Join-Path $projectRoot 'tests\LoyalWingman.LogicTests\LoyalWingman.LogicTests.csproj'
$pluginSource = Join-Path $projectRoot 'src\Plugin.cs'
$buildDirectory = Join-Path $projectRoot 'bin\Release\netstandard2.1'
$sourceDll = Join-Path $buildDirectory 'LoyalWingman.dll'
$stagingDirectory = Join-Path $projectRoot (Join-Path 'dist' "LoyalWingman-$Version")
$releaseDll = Join-Path $stagingDirectory 'LoyalWingman.dll'
$checksumFile = Join-Path $stagingDirectory 'LoyalWingman.dll.sha256'

if ($Version -notmatch '^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' is not a parseable release version."
}

$versionMatch = [regex]::Match(
    [System.IO.File]::ReadAllText($pluginSource),
    'public\s+const\s+string\s+Guid\s*=\s*"[^"]+",\s*Name\s*=\s*"[^"]+",\s*Version\s*=\s*"(?<version>[^"]+)"')
if (-not $versionMatch.Success -or $versionMatch.Groups['version'].Value -ne $Version) {
    throw "Plugin.cs version must match requested version '$Version'."
}

& dotnet run --project $testProject -c Release
if ($LASTEXITCODE -ne 0) { throw 'Focused logic tests failed.' }

& dotnet build $projectFile -c Release '-p:Deploy=false' '-p:DebugType=None' '-p:DebugSymbols=false'
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

if (-not (Test-Path -LiteralPath $sourceDll -PathType Leaf)) {
    throw "Expected plugin DLL was not built: $sourceDll"
}

$unexpectedDlls = @(Get-ChildItem -LiteralPath $buildDirectory -Filter '*.dll' -File |
    Where-Object { $_.Name -ne 'LoyalWingman.dll' })
if ($unexpectedDlls.Count -ne 0) {
    throw "Unexpected dependency DLLs in build output: $($unexpectedDlls.Name -join ', ')"
}

$dllText = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($sourceDll))
$privacyMarkers = @($projectRoot, [Environment]::GetFolderPath('UserProfile'), [Environment]::UserName) |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
foreach ($marker in $privacyMarkers) {
    if ($dllText.IndexOf($marker, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Built DLL contains local path or user marker: $marker"
    }
}

if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
Copy-Item -LiteralPath $sourceDll -Destination $releaseDll -Force

$hash = (Get-FileHash -LiteralPath $releaseDll -Algorithm SHA256).Hash
[System.IO.File]::WriteAllText($checksumFile, "$hash  LoyalWingman.dll$([Environment]::NewLine)")

Write-Host "Release DLL: $releaseDll"
Write-Host "SHA-256: $hash"
Write-Host "Checksum: $checksumFile"
