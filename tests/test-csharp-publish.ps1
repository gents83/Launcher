[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$ExpectedAssemblyVersion
)

$ErrorActionPreference = 'Stop'
$validatorPath = Join-Path $PSScriptRoot '..\scripts\PackageValidation.ps1'
. $validatorPath
$outputRoot = [IO.Path]::GetFullPath($OutputPath)
Assert-LauncherPackageContents -OutputPath $outputRoot
if (-not [string]::IsNullOrWhiteSpace($ExpectedAssemblyVersion)) {
    $launcherAssemblyPath = Join-Path $outputRoot 'Launcher.App.dll'
    $actualAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($launcherAssemblyPath).Version.ToString()
    if ($actualAssemblyVersion -ne $ExpectedAssemblyVersion) {
        throw "Published app assembly version mismatch: expected '$ExpectedAssemblyVersion', got '$actualAssemblyVersion'."
    }
}
$launcherPath = Join-Path $outputRoot 'Launcher.App.exe'

$fixtureDirectory = Join-Path $env:TEMP ("ProjectLauncher-TestConfig-" + [guid]::NewGuid().ToString('N'))
$testProcess = $null
try {
    $null = New-Item -ItemType Directory -Path $fixtureDirectory -Force
    $projectIndexPath = Join-Path $fixtureDirectory 'projects.json'
    $projectConfigPath = Join-Path $fixtureDirectory 'fixture.json'
    $encoding = [Text.UTF8Encoding]::new($false)
    $index = @{
        DefaultProject = 'fixture'
        Projects = @(@{ Id = 'fixture'; Config = 'fixture.json' })
    }
    $project = @{
        Name = 'Package Smoke Fixture'
        Root = $fixtureDirectory
        Buttons = @()
    }
    [IO.File]::WriteAllText($projectIndexPath, ($index | ConvertTo-Json -Depth 8), $encoding)
    [IO.File]::WriteAllText($projectConfigPath, ($project | ConvertTo-Json -Depth 8), $encoding)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $launcherPath
    $startInfo.WorkingDirectory = $fixtureDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $false
    $argumentListProperty = $startInfo.GetType().GetProperty('ArgumentList')
    if ($null -ne $argumentListProperty) {
        $argumentList = $argumentListProperty.GetValue($startInfo)
        [void]$argumentList.Add('--test-mode')
        [void]$argumentList.Add('--config-index')
        [void]$argumentList.Add($projectIndexPath)
    }
    else {
        # Windows PowerShell 5.1 uses .NET Framework, which has no ArgumentList property.
        $startInfo.Arguments = '--test-mode --config-index "{0}"' -f $projectIndexPath
    }

    $testProcess = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $testProcess) {
        throw 'Could not start the published app in test mode.'
    }

    $windowDeadline = (Get-Date).AddSeconds(60)
    $windowInitialized = $false
    while ((Get-Date) -lt $windowDeadline) {
        $testProcess.Refresh()
        if ($testProcess.HasExited) {
            throw "Published app exited before its window initialized (exit code $($testProcess.ExitCode))."
        }
        if ($testProcess.MainWindowHandle -ne [IntPtr]::Zero -and
            $testProcess.MainWindowTitle -eq 'Project Launcher') {
            $windowInitialized = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $windowInitialized) {
        throw 'Published app did not initialize the Project Launcher window within 60 seconds.'
    }

    Start-Sleep -Seconds 3
    $testProcess.Refresh()
    if ($testProcess.HasExited -or $testProcess.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'Published app did not remain alive after its window initialized.'
    }
    Write-Output 'Published app package and TestMode UI smoke test passed.'
}
finally {
    if ($null -ne $testProcess) {
        $testProcess.Refresh()
        if (-not $testProcess.HasExited) {
            [void]$testProcess.CloseMainWindow()
            if (-not $testProcess.WaitForExit(10000)) {
                $testProcess.Kill()
                [void]$testProcess.WaitForExit(10000)
            }
        }
        $testProcess.Dispose()
    }
    if (Test-Path -LiteralPath $fixtureDirectory) {
        Remove-Item -LiteralPath $fixtureDirectory -Recurse -Force
    }
}
