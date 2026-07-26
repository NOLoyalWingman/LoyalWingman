param(
    [string]$GameDir,
    [string]$IlspyCmdPath
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    $GameDir = $env:NUCLEAR_OPTION_DIR
}
if ([string]::IsNullOrWhiteSpace($GameDir)) {
    throw 'GameDir is required. Supply -GameDir for a Nuclear Option installation or set NUCLEAR_OPTION_DIR.'
}

$assemblyPath = Join-Path $GameDir 'NuclearOption_Data\Managed\Assembly-CSharp.dll'
if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
    throw "Assembly-CSharp.dll was not found at '$assemblyPath'. Supply -GameDir for a Nuclear Option installation."
}
$assemblyPath = (Resolve-Path -LiteralPath $assemblyPath).Path

if ($IlspyCmdPath) {
    if (-not (Test-Path -LiteralPath $IlspyCmdPath -PathType Leaf)) {
        throw "ilspycmd was not found at '$IlspyCmdPath'. Supply a valid -IlspyCmdPath or install ilspycmd on PATH."
    }
    $ilspycmd = (Resolve-Path -LiteralPath $IlspyCmdPath).Path
} else {
    $command = Get-Command ilspycmd -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw 'Prerequisite missing: ilspycmd is required. Install it separately or supply -IlspyCmdPath; this script does not download tools.'
    }
    $ilspycmd = $command.Source
}

$temporaryText = Join-Path ([System.IO.Path]::GetTempPath()) ("LoyalWingman-Aircraft-" + [guid]::NewGuid().ToString('N') + '.cs')
try {
    & $ilspycmd --type Aircraft $assemblyPath | Out-File -LiteralPath $temporaryText -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        throw "ilspycmd failed while decompiling '$assemblyPath' (exit code $LASTEXITCODE)."
    }
    $source = [System.IO.File]::ReadAllText($temporaryText)

    function Get-MethodBody([string]$prefix) {
        $match = [regex]::Match($source, "(?m)^\s*(?:public|private|protected|internal)\s+(?:static\s+)?[\w<>\[\]?,.]+\s+($prefix(?:_[A-Za-z0-9-]+)?)\s*\([^)]*\)\s*\{")
        if (-not $match.Success) {
            throw "Missing generated method with stable prefix '$prefix'."
        }
        $openBrace = $match.Index + $match.Length - 1
        $depth = 0
        for ($index = $openBrace; $index -lt $source.Length; $index++) {
            if ($source[$index] -eq '{') { $depth++ }
            elseif ($source[$index] -eq '}') {
                $depth--
                if ($depth -eq 0) {
                    return $source.Substring($openBrace + 1, $index - $openBrace - 1)
                }
            }
        }
        throw "Could not read body for generated method '$($match.Groups[1].Value)'."
    }

    $cmdBody = Get-MethodBody 'UserCode_CmdLaunchMissile'
    $rpcBody = Get-MethodBody 'UserCode_RpcLaunchMissile'
    $cmdLaunchMount = [regex]::Match($cmdBody, '\bweaponStations\s*\[[^\]]+\]\s*\.\s*LaunchMount\s*\(')
    $cmdRpc = [regex]::Match($cmdBody, '\bRpcLaunchMissile\s*\(')
    if (-not $cmdLaunchMount.Success) { throw 'Contract failed: UserCode_CmdLaunchMissile* does not call WeaponStation.LaunchMount.' }
    if (-not $cmdRpc.Success -or $cmdRpc.Index -le $cmdLaunchMount.Index) { throw 'Contract failed: UserCode_CmdLaunchMissile* does not call RpcLaunchMissile after LaunchMount.' }

    if (-not [regex]::IsMatch($source, '(?s)\[ClientRpc\s*\(\s*excludeOwner\s*=\s*true\s*\)\s*\]\s*public\s+void\s+RpcLaunchMissile\s*\(')) {
        throw 'Contract failed: RpcLaunchMissile is not a ClientRpc with excludeOwner = true.'
    }
    if (-not [regex]::IsMatch($rpcBody, '\bLaunchMount\s*\(')) { throw 'Contract failed: UserCode_RpcLaunchMissile* does not call LaunchMount.' }
    if (-not [regex]::IsMatch($rpcBody, '!\s*(?:(?:base\s*\.\s*)?IsServer|NetworkManagerNuclearOption\s*\.\s*i\s*\.\s*Server\s*\.\s*Active)') -or
        -not [regex]::IsMatch($rpcBody, '!\s*CheckIfLocalSim\s*\(')) {
        throw 'Contract failed: UserCode_RpcLaunchMissile* does not retain non-server/non-local-sim replica gating.'
    }

    Write-Host "PASS: $assemblyPath"
    Write-Host '  UserCode_CmdLaunchMissile*: LaunchMount -> RpcLaunchMissile'
    Write-Host '  RpcLaunchMissile: ClientRpc excludeOwner=true'
    Write-Host '  UserCode_RpcLaunchMissile*: non-server/non-local-sim LaunchMount replica gate'
} finally {
    if (Test-Path -LiteralPath $temporaryText) {
        Remove-Item -LiteralPath $temporaryText -Force
    }
}
