$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$buildBatch = Join-Path $root 'build.bat'
if (-not (Test-Path -LiteralPath $buildBatch -PathType Leaf)) {
    throw "Build entry point is missing: $buildBatch"
}

& $buildBatch
if ($LASTEXITCODE -ne 0) {
    throw "Build batch failed with exit code $LASTEXITCODE."
}

$outputRoot = Join-Path $root 'output'
$runtimeFiles = @(
    'Launcher.exe',
    'launcher.ps1',
    'launcher.core.ps1',
    'index.html',
    'projects.json',
    'theme.json',
    'projects/hurricane.json',
    'projects/sandstorm.json',
    'start-launcher.cmd',
    'README.md'
)
foreach ($relativePath in $runtimeFiles) {
    $sourcePath = Join-Path $root $relativePath
    $outputPath = Join-Path $outputRoot $relativePath
    if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
        throw "Required runtime file was not staged: $relativePath"
    }
    if ($relativePath -ne 'Launcher.exe') {
        $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
        $outputHash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash
        if ($sourceHash -ne $outputHash) {
            throw "Staged file differs from its source: $relativePath"
        }
    }
}
if (Test-Path -LiteralPath (Join-Path $outputRoot 'build.ps1')) {
    throw 'The build helper must not be included in the runtime output.'
}
if (Test-Path -LiteralPath (Join-Path $outputRoot 'tests')) {
    throw 'Test files must not be included in the runtime output.'
}

$portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
try {
    $portProbe.Start()
    $port = $portProbe.LocalEndpoint.Port
}
finally {
    $portProbe.Stop()
}
$baseUri = "http://127.0.0.1:$port/"
$exePath = Join-Path $outputRoot 'Launcher.exe'
$server = $null
$stdoutTask = $null
$stderrTask = $null
try {
    $serverArguments = '-TestMode -NoOpenBrowser -Port {0}' -f $port
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $exePath
    $startInfo.Arguments = $serverArguments
    $startInfo.WorkingDirectory = [IO.Path]::GetTempPath()
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $server = [Diagnostics.Process]::Start($startInfo)
    $server.StandardInput.Close()
    $stdoutTask = $server.StandardOutput.ReadToEndAsync()
    $stderrTask = $server.StandardError.ReadToEndAsync()

    $deadline = (Get-Date).AddSeconds(20)
    $catalog = $null
    while ((Get-Date) -lt $deadline -and $null -eq $catalog) {
        if ($server.HasExited) {
            throw "Packaged launcher exited early. stdout: $($stdoutTask.Result) stderr: $($stderrTask.Result)"
        }
        try {
            $catalog = Invoke-RestMethod -Uri ($baseUri + 'api/catalog') -TimeoutSec 2
        }
        catch {
            Start-Sleep -Milliseconds 200
        }
    }
    if ($null -eq $catalog) {
        throw 'Packaged launcher did not serve its catalog in time.'
    }
    if ($catalog.DefaultProject -ne 'hurricane') {
        throw "Packaged launcher returned unexpected default project '$($catalog.DefaultProject)'."
    }
    if ($catalog.ThemeColors.dark.ink -ne '#f3f4f1' -or $catalog.ThemeColors.light.ink -ne '#1d252b') {
        throw 'Packaged launcher did not load both configured theme palettes.'
    }

    $page = Invoke-WebRequest -UseBasicParsing -Uri $baseUri -TimeoutSec 5
    if ($page.StatusCode -ne 200 -or $page.Content -notmatch 'launcherToken') {
        throw 'Packaged launcher did not serve the staged HTML page.'
    }
    Write-Output 'Build package test passed.'
}
finally {
    if ($server) {
        if (-not $server.HasExited) {
            $server.Kill()
            $server.WaitForExit()
        }
        $server.Dispose()
    }
}
