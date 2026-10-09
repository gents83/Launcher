function Assert-LauncherReleaseWindowsHost {
    param([System.PlatformID]$PlatformID = [System.Environment]::OSVersion.Platform)

    if ($PlatformID -ne [System.PlatformID]::Win32NT) {
        throw 'Release bundle generation requires Windows (Win32NT).'
    }
}

function Get-ReleaseDefaultProperty {
    param($Value, [string]$Name)

    if ($null -eq $Value) {
        return $null
    }

    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Assert-ReleaseDefaultProperties {
    param($Value, [string[]]$Allowed, [string]$Section)

    if ($null -eq $Value -or $Value -isnot [System.Management.Automation.PSCustomObject]) {
        throw "Release defaults '$Section' must be a JSON object."
    }

    foreach ($property in $Value.PSObject.Properties) {
        if ($Allowed -notcontains $property.Name) {
            throw "Release defaults '$Section' contains unsupported property '$($property.Name)'."
        }
    }
}

function Test-ReleaseProvider {
    param([string]$Provider)

    return @('FileShare', 'NAS', 'Artifactory') -contains $Provider
}

function Test-ReleaseChannel {
    param([string]$Channel)

    if ([string]::IsNullOrWhiteSpace($Channel) -or $Channel -in @('.', '..')) {
        return $false
    }

    foreach ($character in $Channel.ToCharArray()) {
        if ([char]::IsControl($character) -or $character -in @('/', '\', ':', '?', '#', '%')) {
            return $false
        }
    }

    return $true
}

function ConvertTo-ReleaseUpdateSource {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    Assert-ReleaseDefaultProperties -Value $Value -Allowed @('Provider', 'FeedBase', 'Channel') -Section 'UpdateSource'
    $provider = [string](Get-ReleaseDefaultProperty -Value $Value -Name 'Provider')
    $feedBase = ([string](Get-ReleaseDefaultProperty -Value $Value -Name 'FeedBase')).Trim()
    $channel = ([string](Get-ReleaseDefaultProperty -Value $Value -Name 'Channel')).Trim()

    if ([string]::IsNullOrWhiteSpace($feedBase)) {
        return $null
    }

    if (-not (Test-ReleaseProvider -Provider $provider)) {
        throw "Release defaults 'UpdateSource.Provider' must be FileShare, NAS, or Artifactory."
    }

    if (-not (Test-ReleaseChannel -Channel $channel)) {
        throw "Release defaults 'UpdateSource.Channel' must be one safe path segment."
    }

    return [pscustomobject]@{
        Provider = $provider
        FeedBase = $feedBase
        Channel = $channel
    }
}

function ConvertTo-ReleasePublishTarget {
    param($Value)

    if ($null -eq $Value) {
        return $null
    }

    Assert-ReleaseDefaultProperties -Value $Value -Allowed @('Provider', 'Destination') -Section 'Publish'
    $provider = [string](Get-ReleaseDefaultProperty -Value $Value -Name 'Provider')
    $destination = ([string](Get-ReleaseDefaultProperty -Value $Value -Name 'Destination')).Trim()

    if ([string]::IsNullOrWhiteSpace($destination)) {
        return $null
    }

    if (-not (Test-ReleaseProvider -Provider $provider)) {
        throw "Release defaults 'Publish.Provider' must be FileShare, NAS, or Artifactory."
    }

    return [pscustomobject]@{
        Provider = $provider
        Destination = $destination
    }
}

function Read-LauncherReleaseDefaults {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not [IO.File]::Exists($Path)) {
        return [pscustomobject]@{ UpdateSource = $null; Publish = $null }
    }

    try {
        $document = [IO.File]::ReadAllText($Path) | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Release defaults file '$Path' is not valid JSON: $($_.Exception.Message)"
    }

    if ($null -eq $document -or $document -isnot [System.Management.Automation.PSCustomObject]) {
        throw "Release defaults file '$Path' must contain a JSON object."
    }

    Assert-ReleaseDefaultProperties -Value $document -Allowed @('UpdateSource', 'Publish') -Section 'root'
    $sourceValue = Get-ReleaseDefaultProperty -Value $document -Name 'UpdateSource'
    $publishValue = Get-ReleaseDefaultProperty -Value $document -Name 'Publish'

    return [pscustomobject]@{
        UpdateSource = ConvertTo-ReleaseUpdateSource -Value $sourceValue
        Publish = ConvertTo-ReleasePublishTarget -Value $publishValue
    }
}

function Resolve-LauncherReleaseDefaults {
    param(
        [Parameter(Mandatory = $true)]$Defaults,
        [Parameter(Mandatory = $true)]$Overrides,
        [Parameter(Mandatory = $true)][bool]$PublishRequested
    )

    $sourceDefaults = $Defaults.UpdateSource
    $provider = [string](Get-ReleaseDefaultProperty -Value $Overrides -Name 'UpdateProvider')
    $feedBase = [string](Get-ReleaseDefaultProperty -Value $Overrides -Name 'UpdateFeedBase')
    $channel = [string](Get-ReleaseDefaultProperty -Value $Overrides -Name 'UpdateChannel')

    if ([string]::IsNullOrWhiteSpace($provider)) { $provider = $sourceDefaults.Provider }
    if ([string]::IsNullOrWhiteSpace($feedBase)) { $feedBase = $sourceDefaults.FeedBase }
    if ([string]::IsNullOrWhiteSpace($channel)) { $channel = $sourceDefaults.Channel }

    $resolvedSource = $null
    if (-not [string]::IsNullOrWhiteSpace($feedBase)) {
        if (-not (Test-ReleaseProvider -Provider $provider) -or -not (Test-ReleaseChannel -Channel $channel)) {
            throw 'Specify a valid update provider, feed base, and channel together.'
        }

        $resolvedSource = [pscustomobject]@{
            Provider = $provider.Trim()
            FeedBase = $feedBase.Trim()
            Channel = $channel.Trim()
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($provider) -or -not [string]::IsNullOrWhiteSpace($channel)) {
        throw 'Update provider and channel require an update feed base.'
    }

    $publishTarget = $null
    if ($PublishRequested) {
        $publishProvider = [string](Get-ReleaseDefaultProperty -Value $Overrides -Name 'Provider')
        $destination = [string](Get-ReleaseDefaultProperty -Value $Overrides -Name 'Destination')
        if ([string]::IsNullOrWhiteSpace($publishProvider)) { $publishProvider = $Defaults.Publish.Provider }
        if ([string]::IsNullOrWhiteSpace($destination)) { $destination = $Defaults.Publish.Destination }
        if ([string]::IsNullOrWhiteSpace($destination) -or -not (Test-ReleaseProvider -Provider $publishProvider)) {
            throw 'An explicit publish request requires a valid provider and destination.'
        }

        $publishTarget = [pscustomobject]@{
            Provider = $publishProvider.Trim()
            Destination = $destination.Trim()
        }
    }

    return [pscustomobject]@{
        UpdateSource = $resolvedSource
        PublishRequested = $PublishRequested
        PublishTarget = $publishTarget
    }
}

function Resolve-LauncherReleaseOutputDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$Version,
        [string]$OutputDirectory
    )

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path $SourceRoot (Join-Path 'output' "release-$Version")
    }

    return [IO.Path]::GetFullPath($OutputDirectory)
}
