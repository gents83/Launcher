$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $sourceRoot 'scripts\ReleaseDefaults.ps1')

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -cne $Actual) {
        throw "$Message Expected '$Expected'; got '$Actual'."
    }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Message)
    try {
        & $Action
    }
    catch {
        return
    }

    throw $Message
}

Assert-LauncherReleaseWindowsHost -PlatformID ([System.PlatformID]::Win32NT)
$unixHostRejected = $false
try {
    Assert-LauncherReleaseWindowsHost -PlatformID ([System.PlatformID]::Unix)
}
catch {
    $unixHostRejected = $_.Exception.Message -match 'Release bundle generation requires Windows'
}
if (-not $unixHostRejected) {
    throw 'Release bundle generation must reject non-Windows hosts with a clear error.'
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('LauncherReleaseDefaults-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
try {
    $missingPath = Join-Path $testRoot 'missing.json'
    $emptyDefaults = Read-LauncherReleaseDefaults -Path $missingPath
    Assert-Equal $null $emptyDefaults.UpdateSource 'Missing defaults should not select an update source.'
    Assert-Equal $null $emptyDefaults.Publish 'Missing defaults should not configure publishing.'

    $examplePath = Join-Path $sourceRoot 'scripts\release.defaults.example.json'
    $exampleDocument = [IO.File]::ReadAllText($examplePath) | ConvertFrom-Json -ErrorAction Stop
    $examplePropertyNames = @($exampleDocument.PSObject.Properties.Name | Sort-Object)
    if (($examplePropertyNames.Count -ne 2) -or (($examplePropertyNames -join ',') -cne 'Publish,UpdateSource')) {
        throw 'The example release defaults must expose only UpdateSource and Publish.'
    }
    $exampleDefaults = Read-LauncherReleaseDefaults -Path $examplePath
    Assert-Equal $null $exampleDefaults.UpdateSource 'An empty example feed base should mean no update-source default.'
    Assert-Equal $null $exampleDefaults.Publish 'An empty example destination should mean publishing is disabled.'

    $signingDefaultsPath = Join-Path $testRoot 'obsolete-signing-defaults.json'
    [IO.File]::WriteAllText($signingDefaultsPath, '{"UpdateSource":{"Provider":"FileShare","FeedBase":"","Channel":"stable"},"Publish":{"Provider":"FileShare","Destination":""},"Signing":{}}')
    Assert-Throws { Read-LauncherReleaseDefaults -Path $signingDefaultsPath } 'An obsolete Signing block should be rejected.'

    $defaultsPath = Join-Path $testRoot 'release.defaults.json'
    [IO.File]::WriteAllText($defaultsPath, @'
{
  "UpdateSource": {
    "Provider": "FileShare",
    "FeedBase": "\\\\fileserver\\launcher-feed",
    "Channel": "stable"
  },
  "Publish": {
    "Provider": "NAS",
    "Destination": "\\\\fileserver\\launcher-feed"
  }
}
'@)
    $defaults = Read-LauncherReleaseDefaults -Path $defaultsPath
    Assert-Equal 'FileShare' $defaults.UpdateSource.Provider 'The source provider should be read.'
    Assert-Equal '\\fileserver\launcher-feed' $defaults.UpdateSource.FeedBase 'The NAS root should be read literally.'
    Assert-Equal 'stable' $defaults.UpdateSource.Channel 'The channel should be read.'
    Assert-Equal 'NAS' $defaults.Publish.Provider 'The publish provider should be read.'

    $overrides = [pscustomobject]@{
        UpdateProvider = 'Artifactory'
        UpdateFeedBase = 'https://packages.example/artifactory/launcher'
        UpdateChannel = 'beta'
        Provider = 'Artifactory'
        Destination = 'https://packages.example/artifactory/launcher'
    }
    $resolved = Resolve-LauncherReleaseDefaults -Defaults $defaults -Overrides $overrides -PublishRequested $false
    Assert-Equal 'Artifactory' $resolved.UpdateSource.Provider 'Explicit source provider should override defaults.'
    Assert-Equal 'https://packages.example/artifactory/launcher' $resolved.UpdateSource.FeedBase 'Explicit source base should override defaults.'
    Assert-Equal 'beta' $resolved.UpdateSource.Channel 'Explicit channel should override defaults.'
    Assert-Equal $false $resolved.PublishRequested 'A configured or overridden destination must not publish by itself.'
    Assert-Equal $null $resolved.PublishTarget 'An unrequested publish must not expose an active target.'

    $publishFromDefaults = Resolve-LauncherReleaseDefaults -Defaults $defaults -Overrides ([pscustomobject]@{}) -PublishRequested $true
    Assert-Equal $true $publishFromDefaults.PublishRequested 'An explicit publish request should be retained.'
    Assert-Equal 'NAS' $publishFromDefaults.PublishTarget.Provider 'An explicit publish may use the configured provider.'
    Assert-Equal '\\fileserver\launcher-feed' $publishFromDefaults.PublishTarget.Destination 'An explicit publish may use the configured destination.'

    $partialSourcePath = Join-Path $testRoot 'partial-source.json'
    [IO.File]::WriteAllText($partialSourcePath, '{"UpdateSource":{"Provider":"FileShare","FeedBase":"\\\\fileserver\\feed","Channel":""}}')
    Assert-Throws { Read-LauncherReleaseDefaults -Path $partialSourcePath } 'A source with a base but no channel should be rejected.'

    $partialPublishPath = Join-Path $testRoot 'partial-publish.json'
    [IO.File]::WriteAllText($partialPublishPath, '{"Publish":{"Provider":"","Destination":"\\\\fileserver\\launcher-feed"}}')
    Assert-Throws { Read-LauncherReleaseDefaults -Path $partialPublishPath } 'A destination with no publisher should be rejected.'

    $invalidProviderPath = Join-Path $testRoot 'invalid-provider.json'
    [IO.File]::WriteAllText($invalidProviderPath, '{"UpdateSource":{"Provider":"Unknown","FeedBase":"\\\\fileserver\\feed","Channel":"stable"}}')
    Assert-Throws { Read-LauncherReleaseDefaults -Path $invalidProviderPath } 'An unsupported source provider should be rejected.'

    $invalidJsonPath = Join-Path $testRoot 'invalid-json.json'
    [IO.File]::WriteAllText($invalidJsonPath, '{ not valid json')
    Assert-Throws { Read-LauncherReleaseDefaults -Path $invalidJsonPath } 'Malformed JSON should be rejected.'

    $versionFilePath = Join-Path $sourceRoot 'version.txt'
    if (-not [IO.File]::Exists($versionFilePath)) {
        throw 'The tracked version.txt file is missing.'
    }
    $versionFromFile = [IO.File]::ReadAllText($versionFilePath).Trim()
    if ($versionFromFile -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
        throw "version.txt must contain one SemVer version; got '$versionFromFile'."
    }

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $missingVersionOutput = & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $sourceRoot 'scripts\build-release.ps1') 2>&1
        $missingVersionExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if (($missingVersionExitCode -eq 0) -or (($missingVersionOutput -join [Environment]::NewLine) -notmatch 'Specify -Version')) {
        throw 'Direct PowerShell release invocation without -Version should fail with an actionable error.'
    }

    $retiredTestKeyOutputDirectory = Join-Path $testRoot 'retired-test-key-output'
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $retiredTestKeyOutput = & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $sourceRoot 'scripts\build-release.ps1') -Version '1.2.3' -UpdateProvider 'FileShare' -UpdateFeedBase (Join-Path $testRoot 'retired-test-key-feed') -UpdateChannel 'stable' -OutputDirectory $retiredTestKeyOutputDirectory -UseTestKey 2>&1
        $retiredTestKeyExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if (($retiredTestKeyExitCode -eq 0) -or
        (($retiredTestKeyOutput -join [Environment]::NewLine) -notmatch 'UseTestKey') -or
        (Test-Path -LiteralPath $retiredTestKeyOutputDirectory)) {
        throw 'The retired -UseTestKey option must fail before creating output.'
    }

    $expectedDefaultOutput = [IO.Path]::GetFullPath((Join-Path $sourceRoot 'output\release-1.2.3'))
    $actualDefaultOutput = Resolve-LauncherReleaseOutputDirectory -SourceRoot $sourceRoot -Version '1.2.3' -OutputDirectory ''
    Assert-Equal $expectedDefaultOutput $actualDefaultOutput 'The release output should default beneath output by version.'
    $customOutput = Join-Path $testRoot 'custom-output'
    Assert-Equal ([IO.Path]::GetFullPath($customOutput)) (Resolve-LauncherReleaseOutputDirectory -SourceRoot $sourceRoot -Version '1.2.3' -OutputDirectory $customOutput) 'An explicit output path should take precedence.'

    $occupiedOutput = Join-Path $testRoot 'occupied-output'
    [IO.Directory]::CreateDirectory($occupiedOutput) | Out-Null
    $markerPath = Join-Path $occupiedOutput 'keep.txt'
    [IO.File]::WriteAllText($markerPath, 'keep')
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $releaseBuildOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $sourceRoot 'scripts\build-release.ps1') -Version '1.2.3' -UpdateProvider 'FileShare' -UpdateFeedBase (Join-Path $testRoot 'feed') -UpdateChannel 'stable' -OutputDirectory $occupiedOutput 2>&1
        $releaseBuildExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if (($releaseBuildExitCode -eq 0) -or
        (($releaseBuildOutput -join [Environment]::NewLine) -notmatch 'Output directory must be empty') -or
        [IO.File]::ReadAllText($markerPath) -cne 'keep') {
        throw 'The release build must reject a non-empty output directory before other work and preserve its contents.'
    }

    $releaseBatchPath = Join-Path $sourceRoot 'build-release.bat'
    if (-not [IO.File]::Exists($releaseBatchPath)) {
        throw 'The release batch wrapper is missing.'
    }
    $batchWorkspace = Join-Path $testRoot 'batch-workspace'
    $batchScriptsDirectory = Join-Path $batchWorkspace 'scripts'
    [IO.Directory]::CreateDirectory($batchScriptsDirectory) | Out-Null
    Copy-Item -LiteralPath $releaseBatchPath -Destination (Join-Path $batchWorkspace 'build-release.bat')
    [IO.File]::WriteAllText((Join-Path $batchWorkspace 'version.txt'), '9.8.7')
    $batchStubPath = Join-Path $batchScriptsDirectory 'build-release.ps1'
    [IO.File]::WriteAllText($batchStubPath, @'
[CmdletBinding()]
param(
    [string]$Version,
    [string]$UpdateProvider,
    [string]$UpdateFeedBase,
    [string]$UpdateChannel,
    [string]$OutputDirectory
)
$workspace = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath (Join-Path $workspace 'output')) {
    throw 'Output directory was not cleared before the release script started.'
}
$arguments = @($Version, $UpdateProvider, $UpdateFeedBase, $UpdateChannel, $OutputDirectory) -join '|'
[IO.File]::WriteAllText((Join-Path $workspace 'arguments.txt'), $arguments)
'@)
    $batchTestPath = Join-Path $batchWorkspace 'build-release.bat'
    $batchOutputDirectory = Join-Path $batchWorkspace 'output'
    $batchCustomOutputDirectory = Join-Path $testRoot 'batch-custom-output'
    $batchFeedDirectory = Join-Path $testRoot 'batch-feed'
    $batchArgumentsPath = Join-Path $batchWorkspace 'arguments.txt'
    [IO.Directory]::CreateDirectory((Join-Path $batchOutputDirectory 'old-release')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $batchOutputDirectory 'old-release\keep.txt'), 'old release')
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $batchVersionOutput = & $batchTestPath -UpdateProvider 'FileShare' -UpdateFeedBase $batchFeedDirectory -UpdateChannel 'stable' -OutputDirectory $batchCustomOutputDirectory 2>&1
        $batchVersionExitCode = $LASTEXITCODE
        if ($batchVersionExitCode -ne 0) {
            throw "The isolated release batch failed: $($batchVersionOutput -join ' ')"
        }
        $batchVersionArguments = [IO.File]::ReadAllText($batchArgumentsPath)
        [IO.Directory]::CreateDirectory((Join-Path $batchOutputDirectory 'old-release')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $batchOutputDirectory 'old-release\keep.txt'), 'old release')
        $batchVersionOverrideOutput = & $batchTestPath -Version '2.3.4' -UpdateProvider 'FileShare' -UpdateFeedBase $batchFeedDirectory -UpdateChannel 'stable' -OutputDirectory $batchCustomOutputDirectory 2>&1
        $batchVersionOverrideExitCode = $LASTEXITCODE
        $batchVersionOverrideArguments = [IO.File]::ReadAllText($batchArgumentsPath)
        [IO.Directory]::CreateDirectory((Join-Path $batchOutputDirectory 'old-release')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $batchOutputDirectory 'old-release\keep.txt'), 'old release')
        $null = & $batchTestPath -UnknownReleaseOption 2>&1
        $releaseBatchExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if (($batchVersionExitCode -ne 0) -or
        (Test-Path -LiteralPath $batchOutputDirectory) -or
        ($batchVersionArguments -cne "9.8.7|FileShare|$batchFeedDirectory|stable|$batchCustomOutputDirectory")) {
        throw 'The batch wrapper must clear the complete output tree before invoking the release script and preserve version/argument handling.'
    }
    if (($batchVersionOverrideExitCode -ne 0) -or
        (Test-Path -LiteralPath $batchOutputDirectory) -or
        ($batchVersionOverrideArguments -cne "2.3.4|FileShare|$batchFeedDirectory|stable|$batchCustomOutputDirectory")) {
        throw 'An explicit -Version argument must override version.txt without being duplicated, after clearing output.'
    }
    if (($releaseBatchExitCode -eq 0) -or (Test-Path -LiteralPath $batchOutputDirectory)) {
        throw 'The release batch wrapper must propagate a PowerShell parameter error after clearing output.'
    }

    Write-Host 'Release defaults tests passed.'
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
