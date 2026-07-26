param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $projectRoot 'LoyalWingman.csproj') -c $Configuration
dotnet run --project (Join-Path $projectRoot 'tests\LoyalWingman.LogicTests\LoyalWingman.LogicTests.csproj') -c $Configuration
$files = Get-ChildItem (Join-Path $projectRoot "bin\$Configuration\netstandard2.1") -File
if ($files.Name | Where-Object { $_ -ne 'LoyalWingman.dll' -and $_ -ne 'LoyalWingman.pdb' -and $_ -ne 'LoyalWingman.deps.json' }) { throw 'Unexpected dependency DLL in build output.' }
Write-Host 'Build output contains only plugin artifacts.'
