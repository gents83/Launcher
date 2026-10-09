[CmdletBinding()]
param(
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$semanticVersionPattern = '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?:(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
$versionMatch = [regex]::Match($Version, $semanticVersionPattern)
if (-not $versionMatch.Success) {
    throw "Version '$Version' is not a valid SemVer value."
}
$assemblyVersion = '{0}.{1}.{2}.0' -f $versionMatch.Groups['major'].Value, $versionMatch.Groups['minor'].Value, $versionMatch.Groups['patch'].Value
$sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$outputRoot = Join-Path $sourceRoot 'output'
$targetRoot = Join-Path $outputRoot 'app'
$stagingRoot = $null
$backupRoot = $null
$previousPackageMoved = $false
Push-Location $sourceRoot
try {
    Write-Host 'Running .NET test suite...'
    & dotnet test Launcher.sln
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test Launcher.sln failed with exit code $LASTEXITCODE."
    }

    Write-Host 'Running package validation tests...'
    & (Join-Path $sourceRoot 'tests\test-package-validation.ps1')

    $null = New-Item -ItemType Directory -Path $outputRoot -Force
    $buildToken = [guid]::NewGuid().ToString('N')
    $stagingRoot = Join-Path $outputRoot ('.launcher-app-staging-' + $buildToken)
    $backupRoot = Join-Path $outputRoot ('.launcher-app-backup-' + $buildToken)
    [IO.Directory]::CreateDirectory($stagingRoot) | Out-Null

    Write-Host "Publishing self-contained win-x64 app version $Version to staging: $stagingRoot"
    $publishArguments = @(
        'src/Launcher.App/Launcher.App.csproj'
        '-c'
        'Release'
        '-r'
        'win-x64'
        '--self-contained'
        'true'
        '-o'
        $stagingRoot
        "-p:Version=$Version"
        "-p:AssemblyVersion=$assemblyVersion"
        "-p:FileVersion=$assemblyVersion"
        "-p:InformationalVersion=$Version"
    )
    & dotnet publish @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $appAssemblyPath = Join-Path $stagingRoot 'Launcher.App.dll'
    if (-not (Test-Path -LiteralPath $appAssemblyPath -PathType Leaf)) {
        throw "Published app managed assembly is missing: $appAssemblyPath"
    }
    $publishedAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($appAssemblyPath).Version.ToString()
    if ($publishedAssemblyVersion -ne $assemblyVersion) {
        throw "Published app assembly version mismatch: expected '$assemblyVersion', got '$publishedAssemblyVersion'."
    }
    Write-Host "Published app assembly version validated: $publishedAssemblyVersion"

    . (Join-Path $sourceRoot 'scripts\PackageValidation.ps1')
    Assert-LauncherPackageContents -OutputPath $stagingRoot
    Write-Host 'Staged package contents validated.'

    if (Test-Path -LiteralPath $targetRoot -PathType Container) {
        [IO.Directory]::Move($targetRoot, $backupRoot)
        $previousPackageMoved = $true
    }

    try {
        [IO.Directory]::Move($stagingRoot, $targetRoot)
    }
    catch {
        $promotionError = $_.Exception.Message
        if ($previousPackageMoved) {
            try {
                if (Test-Path -LiteralPath $targetRoot) {
                    throw "Cannot restore the previous app package because '$targetRoot' already exists."
                }
                [IO.Directory]::Move($backupRoot, $targetRoot)
                $previousPackageMoved = $false
            }
            catch {
                throw "App promotion failed ($promotionError), and restoring the previous package failed ($($_.Exception.Message)). The previous package remains at '$backupRoot'."
            }
        }
        throw "App promotion failed: $promotionError"
    }

    if ($previousPackageMoved) {
        try {
            Remove-Item -LiteralPath $backupRoot -Recurse -Force -ErrorAction Stop
            $previousPackageMoved = $false
        }
        catch {
            Write-Warning "The new app was promoted, but the previous package backup could not be removed: $backupRoot."
        }
    }

    Write-Host "Build complete: $(Join-Path $targetRoot 'Launcher.App.exe')"
}
finally {
    if ($stagingRoot -and (Test-Path -LiteralPath $stagingRoot -PathType Container)) {
        try {
            Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction Stop
        }
        catch {
            Write-Warning "Could not remove the temporary staging directory: $stagingRoot."
        }
    }

    if ($previousPackageMoved -and
        $backupRoot -and
        (Test-Path -LiteralPath $backupRoot -PathType Container) -and
        -not (Test-Path -LiteralPath $targetRoot)) {
        [IO.Directory]::Move($backupRoot, $targetRoot)
    }

    Pop-Location
}
