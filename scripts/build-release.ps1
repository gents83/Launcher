[CmdletBinding()]
param(
    [string]$Version,

    [string]$OutputDirectory,

    [string]$DeploymentId = 'project-launcher',

    [string]$ConfigIndex = 'projects.json',

    [string]$Destination,

    [ValidateSet('NAS', 'Artifactory')]
    [string]$Provider,

    [ValidateSet('NAS', 'FileShare', 'Artifactory')]
    [string]$UpdateProvider,

    [string]$UpdateFeedBase,

    [string]$UpdateChannel,

    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $sourceRoot 'scripts\ReleaseDefaults.ps1')
Assert-LauncherReleaseWindowsHost

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Specify -Version <major.minor.patch> to build a release.'
}

$outputRoot = $null
$workRoot = $null
$releaseCliProject = Join-Path $sourceRoot 'src\Launcher.Release\Launcher.Release.csproj'
$publishVersionPattern = '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?:(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
$versionMatch = [regex]::Match($Version, $publishVersionPattern)
if (-not $versionMatch.Success) {
    throw "Version '$Version' is not a valid SemVer value."
}
$assemblyVersion = '{0}.{1}.{2}.0' -f $versionMatch.Groups['major'].Value, $versionMatch.Groups['minor'].Value, $versionMatch.Groups['patch'].Value

$hasExplicitProvider = -not [string]::IsNullOrWhiteSpace($Provider)
$hasExplicitDestination = -not [string]::IsNullOrWhiteSpace($Destination)
if (-not $Publish.IsPresent -and $hasExplicitProvider -ne $hasExplicitDestination) {
    throw 'Specify both -Provider and -Destination to publish, or use -Publish with a configured target.'
}
$publishRequested = $Publish.IsPresent -or ($hasExplicitProvider -and $hasExplicitDestination)
$releaseDefaults = Read-LauncherReleaseDefaults -Path (Join-Path $sourceRoot 'scripts\release.defaults.json')
$releaseOverrides = [pscustomobject]@{
    UpdateProvider = $UpdateProvider
    UpdateFeedBase = $UpdateFeedBase
    UpdateChannel = $UpdateChannel
    Provider = $Provider
    Destination = $Destination
}
$resolvedReleaseDefaults = Resolve-LauncherReleaseDefaults `
    -Defaults $releaseDefaults `
    -Overrides $releaseOverrides `
    -PublishRequested $publishRequested
$defaultUpdateSource = $resolvedReleaseDefaults.UpdateSource
if ($null -eq $defaultUpdateSource) {
    throw 'An update source is required. Configure UpdateSource in scripts/release.defaults.json or pass -UpdateProvider, -UpdateFeedBase, and -UpdateChannel.'
}
if ($publishRequested) {
    $Provider = $resolvedReleaseDefaults.PublishTarget.Provider
    if ($Provider -eq 'FileShare') {
        $Provider = 'NAS'
    }
    $Destination = $resolvedReleaseDefaults.PublishTarget.Destination
}

$outputRoot = Resolve-LauncherReleaseOutputDirectory `
    -SourceRoot $sourceRoot `
    -Version $Version `
    -OutputDirectory $OutputDirectory

if (($Destination -and -not $Provider) -or ($Provider -and -not $Destination)) {
    throw 'Specify both -Destination and -Provider to publish, or neither to build locally.'
}

if (Test-Path -LiteralPath $outputRoot -PathType Container) {
    if (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1) {
        throw "Output directory must be empty: $outputRoot"
    }
}
else {
    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
}

Push-Location $sourceRoot
try {
    Write-Host 'Running solution tests...'
    & dotnet test Launcher.sln
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test Launcher.sln failed with exit code $LASTEXITCODE."
    }

    Write-Host 'Running package validation tests...'
    & (Join-Path $sourceRoot 'tests\test-package-validation.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw "Package validation tests failed with exit code $LASTEXITCODE."
    }

    $buildToken = [guid]::NewGuid().ToString('N')
    $workRoot = Join-Path $outputRoot ('.launcher-release-staging-' + $buildToken)
    $appPublishDirectory = Join-Path $workRoot 'app'
    $bootstrapperPublishDirectory = Join-Path $workRoot 'bootstrapper'
    $shimPublishDirectory = Join-Path $workRoot 'shim'
    $feedDirectory = Join-Path $workRoot 'feed'
    $perforceLauncherDirectory = Join-Path $workRoot 'perforce-launcher'
    [IO.Directory]::CreateDirectory($workRoot) | Out-Null

    $publishCommon = @(
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        "-p:Version=$Version",
        "-p:AssemblyVersion=$assemblyVersion",
        "-p:FileVersion=$assemblyVersion",
        "-p:InformationalVersion=$Version",
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )

    Write-Host "Publishing Launcher.App $Version..."
    & dotnet publish (Join-Path $sourceRoot 'src\Launcher.App\Launcher.App.csproj') @publishCommon -o $appPublishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Launcher.App publish failed with exit code $LASTEXITCODE."
    }

    Write-Host 'Validating the app publish directory...'
    . (Join-Path $sourceRoot 'scripts\PackageValidation.ps1')
    Assert-LauncherPackageContents -OutputPath $appPublishDirectory

    Write-Host 'Publishing the stable bootstrapper...'
    & dotnet publish (Join-Path $sourceRoot 'src\Launcher.Bootstrapper\Launcher.Bootstrapper.csproj') @publishCommon -o $bootstrapperPublishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Launcher.Bootstrapper publish failed with exit code $LASTEXITCODE."
    }

    Write-Host 'Publishing the single-file launcher shim...'
    & dotnet publish (Join-Path $sourceRoot 'src\Launcher.Shim\Launcher.Shim.csproj') @publishCommon -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $shimPublishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Launcher shim publish failed with exit code $LASTEXITCODE."
    }

    Write-Host 'Smoke-testing the published app in non-executing test mode...'
    & (Join-Path $sourceRoot 'tests\test-csharp-publish.ps1') -OutputPath $appPublishDirectory -ExpectedAssemblyVersion $assemblyVersion
    if ($LASTEXITCODE -ne 0) {
        throw "The published app smoke test failed with exit code $LASTEXITCODE."
    }

    $packageArguments = @(
        'run', '--project', $releaseCliProject, '--configuration', 'Release', '--no-launch-profile', '--',
        'package',
        '--app-publish-directory', $appPublishDirectory,
        '--version', $Version,
        '--output-directory', $feedDirectory
    )

    Write-Host 'Creating the unsigned stable app feed...'
    & dotnet @packageArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Release package creation failed with exit code $LASTEXITCODE."
    }

    Write-Host 'Creating the Perforce launcher folder...'
    $bundleArguments = @(
        'run', '--project', $releaseCliProject, '--configuration', 'Release', '--no-launch-profile', '--',
        'perforce-bundle',
        '--shim-publish-directory', $shimPublishDirectory,
        '--bootstrapper-publish-directory', $bootstrapperPublishDirectory,
        '--feed-directory', $feedDirectory,
        '--config-source-directory', $sourceRoot,
        '--output-directory', $perforceLauncherDirectory,
        '--deployment-id', $DeploymentId,
        '--config-index', $ConfigIndex,
        '--default-update-provider', $defaultUpdateSource.Provider,
        '--default-update-feed-base', $defaultUpdateSource.FeedBase,
        '--default-update-channel', $defaultUpdateSource.Channel
    )
    & dotnet @bundleArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Perforce launcher bundle creation failed with exit code $LASTEXITCODE."
    }

    Write-Host 'Smoke-testing the Perforce launcher shim and bundle layout...'
    & (Join-Path $sourceRoot 'tests\test-perforce-launcher-publish.ps1') `
        -ShimPublishDirectory $shimPublishDirectory `
        -PerforceLauncherDirectory $perforceLauncherDirectory `
        -FixtureDirectory (Join-Path $sourceRoot 'tests\Launcher.ProcessFixture\bin\Debug\net10.0')
    if ($LASTEXITCODE -ne 0) {
        throw "The Perforce launcher publish smoke test failed with exit code $LASTEXITCODE."
    }

    $publishedUpdateBundleDirectory = Join-Path $outputRoot 'update-bundle'
    $publishedPerforceLauncherDirectory = Join-Path $outputRoot 'perforce-launcher'
    [IO.Directory]::Move($feedDirectory, $publishedUpdateBundleDirectory)
    [IO.Directory]::Move($perforceLauncherDirectory, $publishedPerforceLauncherDirectory)

    if ($Destination) {
        Write-Host "Publishing the app update feed to $Provider..."
        $publishArguments = @(
            'run', '--project', $releaseCliProject, '--configuration', 'Release', '--no-launch-profile', '--',
            'publish',
            '--feed-directory', $publishedUpdateBundleDirectory,
            '--provider', $Provider,
            '--destination', $Destination
        )
        & dotnet @publishArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Feed publishing failed with exit code $LASTEXITCODE."
        }
    }

    Write-Host "Perforce launcher folder: $publishedPerforceLauncherDirectory"
    Write-Host "Update bundle: $publishedUpdateBundleDirectory"
}
finally {
    if ($workRoot -and (Test-Path -LiteralPath $workRoot -PathType Container)) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
    Pop-Location
}
