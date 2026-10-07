$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$outputRoot = Join-Path $sourceRoot 'output'
$launcherScript = Join-Path $sourceRoot 'launcher.ps1'
$exePath = Join-Path $outputRoot 'Launcher.exe'

$ps2exeModule = Get-Module -ListAvailable -Name ps2exe | Sort-Object Version -Descending | Select-Object -First 1
if (-not $ps2exeModule) {
    Write-Host 'Installing the PS2EXE build dependency for the current user...'
    Install-Module -Name ps2exe -Scope CurrentUser -Force -AllowClobber -Confirm:$false -ErrorAction Stop
}
Import-Module ps2exe -ErrorAction Stop
if (-not (Get-Command Invoke-PS2EXE -ErrorAction SilentlyContinue)) {
    throw 'The PS2EXE module does not provide Invoke-PS2EXE.'
}

$null = New-Item -ItemType Directory -Path $outputRoot -Force
$runtimeFiles = @(
    'launcher.ps1',
    'launcher.core.ps1',
    'index.html',
    'projects.json',
    'theme.json',
    'start-launcher.cmd',
    'README.md'
)
foreach ($relativePath in $runtimeFiles) {
    $sourcePath = Join-Path $sourceRoot $relativePath
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Required runtime file is missing: $sourcePath"
    }
    Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $outputRoot $relativePath) -Force
}

$sourceRootPrefix = $sourceRoot.TrimEnd('\') + '\'
$projectIndexPath = Join-Path $sourceRoot 'projects.json'
$projectIndex = Get-Content -LiteralPath $projectIndexPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $projectIndex.Projects -or @($projectIndex.Projects).Count -eq 0) {
    throw 'projects.json must reference at least one project config.'
}
foreach ($project in @($projectIndex.Projects)) {
    $configRelativePath = [string]$project.Config
    if ([string]::IsNullOrWhiteSpace($configRelativePath) -or [IO.Path]::IsPathRooted($configRelativePath)) {
        throw "Project '$($project.Id)' must reference a relative config path."
    }

    $configSourcePath = [IO.Path]::GetFullPath((Join-Path $sourceRoot $configRelativePath))
    if (-not $configSourcePath.StartsWith($sourceRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Project config must be inside the source folder: $configRelativePath"
    }
    if (-not (Test-Path -LiteralPath $configSourcePath -PathType Leaf)) {
        throw "Project config is missing: $configSourcePath"
    }

    $configOutputPath = Join-Path $outputRoot $configSourcePath.Substring($sourceRootPrefix.Length)
    $configOutputDirectory = Split-Path -Parent $configOutputPath
    $null = New-Item -ItemType Directory -Path $configOutputDirectory -Force
    Copy-Item -LiteralPath $configSourcePath -Destination $configOutputPath -Force
}

Invoke-PS2EXE -InputFile $launcherScript -OutputFile $exePath
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "PS2EXE did not create the launcher executable: $exePath"
}

Write-Host "Build complete: $exePath"
Write-Host "Portable runtime files staged in: $outputRoot"
