[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ShimPublishDirectory,
    [Parameter(Mandatory)][string]$PerforceLauncherDirectory,
    [Parameter(Mandatory)][string]$FixtureDirectory
)

$ErrorActionPreference = 'Stop'

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -cne $Actual) {
        throw "$Message Expected '$Expected'; got '$Actual'."
    }
}

function ConvertTo-WindowsProcessArgument {
    param([Parameter(Mandatory)][string]$Argument)

    if (($Argument.Length -gt 0) -and ($Argument -notmatch '[\s"]')) {
        return $Argument
    }

    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashCount = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq '\') {
            $backslashCount++
            continue
        }

        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($backslashCount * 2) + 1)))
            [void]$builder.Append('"')
            $backslashCount = 0
            continue
        }

        if ($backslashCount -gt 0) {
            [void]$builder.Append(('\' * $backslashCount))
            $backslashCount = 0
        }
        [void]$builder.Append($character)
    }

    if ($backslashCount -gt 0) {
        [void]$builder.Append(('\' * ($backslashCount * 2)))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

$shimRoot = [IO.Path]::GetFullPath($ShimPublishDirectory)
$bundleRoot = [IO.Path]::GetFullPath($PerforceLauncherDirectory)
$fixtureRoot = [IO.Path]::GetFullPath($FixtureDirectory)
$shimPath = Join-Path $shimRoot 'LAUNCHER.exe'
$bootstrapperPath = Join-Path $bundleRoot 'deps\Launcher.Bootstrapper.exe'
if (-not [IO.File]::Exists($shimPath)) {
    throw "The shim publish directory does not contain LAUNCHER.exe: $shimPath"
}
if (-not [IO.File]::Exists($bootstrapperPath)) {
    throw "The Perforce bundle does not contain deps\Launcher.Bootstrapper.exe: $bootstrapperPath"
}

$shimFiles = @(Get-ChildItem -LiteralPath $shimRoot -File -Recurse)
if (($shimFiles.Count -ne 1) -or ($shimFiles[0].Name -cne 'LAUNCHER.exe')) {
    throw 'The shim publish directory must contain only the single-file LAUNCHER.exe.'
}
foreach ($requiredFile in @(
    'launcher.json',
    'projects.json',
    'seed-feed\stable\latest.json')) {
    if (-not [IO.File]::Exists((Join-Path $bundleRoot $requiredFile))) {
        throw "The Perforce bundle is missing its root file: $requiredFile"
    }
}
if (-not [IO.Directory]::Exists((Join-Path $bundleRoot 'projects'))) {
    throw 'The Perforce bundle is missing the root projects directory.'
}
$dependencyFiles = @(Get-ChildItem -LiteralPath (Join-Path $bundleRoot 'deps') -File -Recurse)
if (-not ($dependencyFiles | Where-Object { $_.Extension -ieq '.dll' })) {
    throw 'The bootstrapper dependency payload does not contain any DLL files under deps/.'
}
if (Get-ChildItem -LiteralPath $bundleRoot -File | Where-Object { $_.Extension -ieq '.dll' }) {
    throw 'Bootstrapper DLLs must not be copied beside the root LAUNCHER.exe.'
}
if (Get-ChildItem -LiteralPath $bundleRoot -File -Recurse | Where-Object { $_.Extension -ieq '.pdb' }) {
    throw 'The Perforce launcher bundle must not contain PDB files.'
}

foreach ($requiredFixtureFile in @(
    'Launcher.ProcessFixture.exe',
    'Launcher.ProcessFixture.dll',
    'Launcher.ProcessFixture.deps.json',
    'Launcher.ProcessFixture.runtimeconfig.json')) {
    if (-not [IO.File]::Exists((Join-Path $fixtureRoot $requiredFixtureFile))) {
        throw "The process fixture output is missing: $requiredFixtureFile"
    }
}

$tempParent = Join-Path ([IO.Path]::GetTempPath()) ('launcher-publish-smoke-' + [guid]::NewGuid().ToString('N'))
$deploymentRoot = Join-Path $tempParent (Split-Path -Leaf $bundleRoot)
try {
    [IO.Directory]::CreateDirectory($tempParent) | Out-Null
    Copy-Item -LiteralPath $bundleRoot -Destination $tempParent -Recurse

    $testDependencies = Join-Path $deploymentRoot 'deps'
    Remove-Item -LiteralPath $testDependencies -Recurse -Force
    [IO.Directory]::CreateDirectory($testDependencies) | Out-Null
    Copy-Item -LiteralPath (Join-Path $fixtureRoot 'Launcher.ProcessFixture.exe') `
        -Destination (Join-Path $testDependencies 'Launcher.Bootstrapper.exe') -Force
    foreach ($fixtureFile in @(
        'Launcher.ProcessFixture.dll',
        'Launcher.ProcessFixture.deps.json',
        'Launcher.ProcessFixture.runtimeconfig.json')) {
        Copy-Item -LiteralPath (Join-Path $fixtureRoot $fixtureFile) -Destination $testDependencies -Force
    }

    $capturePath = Join-Path $deploymentRoot 'shim-capture.json'
    $expectedArguments = @('capture', $capturePath, '0', 'argument with spaces', 'tail')
    $startInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $deploymentRoot 'LAUNCHER.exe'))
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = $deploymentRoot
    $startInfo.Arguments = (@($expectedArguments | ForEach-Object { ConvertTo-WindowsProcessArgument $_ }) -join ' ')

    $shimProcess = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $shimProcess) {
        throw 'Windows did not start the LAUNCHER.exe shim.'
    }
    try {
        if (-not $shimProcess.WaitForExit(10000)) {
            $shimProcess.Kill()
            throw 'The LAUNCHER.exe shim did not exit within 10 seconds.'
        }
        Assert-Equal 0 $shimProcess.ExitCode 'The LAUNCHER.exe shim should start the bootstrapper fixture.'
    }
    finally {
        $shimProcess.Dispose()
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not [IO.File]::Exists($capturePath) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if (-not [IO.File]::Exists($capturePath)) {
        throw 'The bootstrapper fixture did not record its forwarded arguments.'
    }

    $capture = [IO.File]::ReadAllText($capturePath) | ConvertFrom-Json -ErrorAction Stop
    $capturedArguments = @($capture.Arguments)
    Assert-Equal $expectedArguments.Count $capturedArguments.Count 'The shim must preserve the argument count.'
    for ($index = 0; $index -lt $expectedArguments.Count; $index++) {
        Assert-Equal $expectedArguments[$index] $capturedArguments[$index] "The shim must preserve argument $index."
    }
    Assert-Equal $deploymentRoot $capture.WorkingDirectory 'The bootstrapper must run with the deployment root as its working directory.'

    Write-Host 'Perforce launcher publish smoke test passed.'
}
finally {
    $cleanupAttempts = 0
    while ([IO.Directory]::Exists($tempParent)) {
        try {
            Remove-Item -LiteralPath $tempParent -Recurse -Force -ErrorAction Stop
        }
        catch {
            $cleanupAttempts++
            if ($cleanupAttempts -ge 10) {
                throw
            }
            Start-Sleep -Milliseconds 200
        }
    }
}
