function Resolve-ConfigRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$BaseDirectory
    )

    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }
    [IO.Path]::GetFullPath((Join-Path $BaseDirectory $Path))
}

function Resolve-ConfigArtworkReference {
    param(
        [Parameter(Mandatory = $true)][string]$Reference,
        [Parameter(Mandatory = $true)][string]$BaseDirectory
    )

    if ([IO.Path]::IsPathRooted($Reference)) {
        return Resolve-ConfigRelativePath -Path $Reference -BaseDirectory $BaseDirectory
    }

    if ($Reference -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') {
        $uri = $null
        if ([Uri]::TryCreate($Reference, [UriKind]::Absolute, [ref]$uri) -and $uri.Scheme -in @('http', 'https') -and -not [string]::IsNullOrWhiteSpace($uri.Host)) {
            return $Reference
        }
        return $null
    }

    Resolve-ConfigRelativePath -Path $Reference -BaseDirectory $BaseDirectory
}

function Read-LauncherConfig {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ConfigPath)

    $fullConfigPath = [IO.Path]::GetFullPath($ConfigPath)
    if (-not (Test-Path -LiteralPath $fullConfigPath -PathType Leaf)) {
        throw "Launcher config not found: $fullConfigPath"
    }

    try {
        $document = Get-Content -LiteralPath $fullConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        throw "Invalid launcher config: $($_.Exception.Message)"
    }

    if ([string]::IsNullOrWhiteSpace([string]$document.DefaultProject) -or -not $document.PSObject.Properties['Projects'] -or @($document.Projects).Count -eq 0) {
        throw 'Launcher config requires DefaultProject and a nonempty Projects array.'
    }
    if ($document.Projects -isnot [array]) {
        throw 'Launcher config Projects must be an array.'
    }

    $theme = 'dark'
    $themeProperty = $document.PSObject.Properties['Theme']
    if ($themeProperty -and $null -ne $themeProperty.Value) {
        if ($themeProperty.Value -isnot [string]) {
            throw "Theme must be 'dark' or 'light'."
        }
        if (-not [string]::IsNullOrWhiteSpace($themeProperty.Value)) {
            $theme = $themeProperty.Value.ToLowerInvariant()
            if ($theme -notin @('dark', 'light')) {
                throw "Theme must be 'dark' or 'light'."
            }
        }
    }

    $masterDirectory = Split-Path -Parent $fullConfigPath
    $seenIds = @{}
    $projects = @(
        foreach ($entry in @($document.Projects)) {
            if ($entry -isnot [System.Management.Automation.PSCustomObject]) {
                throw 'Each project index entry must be an object.'
            }
            foreach ($field in @('Id', 'Config')) {
                $property = $entry.PSObject.Properties[$field]
                if (-not $property -or $property.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($property.Value)) {
                    throw "Project index entry is missing required field '$field'."
                }
            }

            $id = [string]$entry.Id
            if ($seenIds.ContainsKey($id)) {
                throw "Duplicate project id: $id"
            }
            $seenIds[$id] = $true

            $projectConfigPath = Resolve-ConfigRelativePath -Path ([string]$entry.Config) -BaseDirectory $masterDirectory
            $projectConfigDirectory = Split-Path -Parent $projectConfigPath
            $name = $id
            $root = $null
            $backgroundImage = $null
            $playlistImages = @()
            $buttons = @()
            $teaBox = $null
            $projectError = $null

            try {
                if (-not (Test-Path -LiteralPath $projectConfigPath -PathType Leaf)) {
                    throw "Project config not found: $projectConfigPath"
                }
                try {
                    $projectDocument = Get-Content -LiteralPath $projectConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
                }
                catch {
                    throw "Invalid project config: $($_.Exception.Message)"
                }

                foreach ($field in @('Name', 'Root')) {
                    $property = $projectDocument.PSObject.Properties[$field]
                    if (-not $property -or $property.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($property.Value)) {
                        throw "Project '$id' config is missing required field '$field'."
                    }
                }
                $name = [string]$projectDocument.Name
                $root = Resolve-ConfigRelativePath -Path ([string]$projectDocument.Root) -BaseDirectory $projectConfigDirectory

                $backgroundProperty = $projectDocument.PSObject.Properties['BackgroundImage']
                if ($backgroundProperty -and $null -ne $backgroundProperty.Value) {
                    if ($backgroundProperty.Value -isnot [string]) {
                        throw "Project '$id' has an invalid BackgroundImage; expected a path or HTTP(S) URL string."
                    }
                    if (-not [string]::IsNullOrWhiteSpace($backgroundProperty.Value)) {
                        $backgroundImage = Resolve-ConfigArtworkReference -Reference $backgroundProperty.Value -BaseDirectory $projectConfigDirectory
                    }
                }

                $playlistImagesProperty = $projectDocument.PSObject.Properties['PlaylistImages']
                if ($playlistImagesProperty -and $null -ne $playlistImagesProperty.Value) {
                    if ($playlistImagesProperty.Value -isnot [System.Management.Automation.PSCustomObject]) {
                        throw "Project '$id' has invalid PlaylistImages; expected an object mapping playlist names to image paths or HTTP(S) URLs."
                    }
                    $playlistImages = @(
                        foreach ($imageProperty in $playlistImagesProperty.Value.PSObject.Properties) {
                            if ($imageProperty.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($imageProperty.Value)) {
                                throw "Project '$id' has an invalid image reference for playlist '$($imageProperty.Name)'."
                            }
                            [pscustomobject]@{
                                PlaylistId = [string]$imageProperty.Name
                                Path = Resolve-ConfigArtworkReference -Reference $imageProperty.Value -BaseDirectory $projectConfigDirectory
                            }
                        }
                    )
                }

                $seenButtonIds = @{}
                $buttonsProperty = $projectDocument.PSObject.Properties['Buttons']
                if ($buttonsProperty -and $null -ne $buttonsProperty.Value) {
                    if ($buttonsProperty.Value -isnot [array]) {
                        throw "Project '$id' Buttons must be an array."
                    }
                    $buttons = @(
                        foreach ($buttonEntry in $buttonsProperty.Value) {
                            if ($buttonEntry -isnot [System.Management.Automation.PSCustomObject]) {
                                throw "Project '$id' contains a button that is not an object."
                            }
                            foreach ($field in @('Id', 'Label', 'Executable')) {
                                $property = $buttonEntry.PSObject.Properties[$field]
                                if (-not $property -or $property.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($property.Value)) {
                                    throw "Project '$id' button is missing required field '$field'."
                                }
                            }
                            $buttonId = [string]$buttonEntry.Id
                            if ($seenButtonIds.ContainsKey($buttonId)) {
                                throw "Project '$id' has duplicate button id '$buttonId'."
                            }
                            $seenButtonIds[$buttonId] = $true

                            $arguments = @()
                            $argumentsProperty = $buttonEntry.PSObject.Properties['Arguments']
                            if ($argumentsProperty) {
                                if ($argumentsProperty.Value -isnot [array]) {
                                    throw "Project '$id' button '$buttonId' Arguments must be an array of strings."
                                }
                                foreach ($argument in $argumentsProperty.Value) {
                                    if ($argument -isnot [string]) {
                                        throw "Project '$id' button '$buttonId' Arguments must contain only strings."
                                    }
                                    $arguments += $argument
                                }
                            }

                            $workingDirectory = $null
                            $workingDirectoryProperty = $buttonEntry.PSObject.Properties['WorkingDirectory']
                            if ($workingDirectoryProperty -and $null -ne $workingDirectoryProperty.Value) {
                                if ($workingDirectoryProperty.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($workingDirectoryProperty.Value)) {
                                    throw "Project '$id' button '$buttonId' has an invalid WorkingDirectory."
                                }
                                $workingDirectory = [string]$workingDirectoryProperty.Value
                            }

                            $icon = $null
                            $iconProperty = $buttonEntry.PSObject.Properties['Icon']
                            if ($iconProperty -and $null -ne $iconProperty.Value) {
                                if ($iconProperty.Value -isnot [string]) {
                                    throw "Project '$id' button '$buttonId' Icon must be a path or HTTP(S) URL string."
                                }
                                if (-not [string]::IsNullOrWhiteSpace($iconProperty.Value)) {
                                    $icon = Resolve-ConfigArtworkReference -Reference $iconProperty.Value -BaseDirectory $projectConfigDirectory
                                }
                            }

                            $category = 'Applications'
                            $categoryProperty = $buttonEntry.PSObject.Properties['Category']
                            if ($categoryProperty -and $null -ne $categoryProperty.Value) {
                                if ($categoryProperty.Value -isnot [string]) {
                                    throw "Project '$id' button '$buttonId' Category must be a string."
                                }
                                if (-not [string]::IsNullOrWhiteSpace($categoryProperty.Value)) {
                                    $category = $categoryProperty.Value.Trim()
                                }
                            }

                            [pscustomobject]@{
                                Id = $buttonId
                                Label = [string]$buttonEntry.Label
                                Executable = [string]$buttonEntry.Executable
                                Arguments = $arguments
                                WorkingDirectory = $workingDirectory
                                Icon = $icon
                                Category = $category
                            }
                        }
                    )
                }

                $teaBoxProperty = $projectDocument.PSObject.Properties['TeaBox']
                if ($teaBoxProperty -and $null -ne $teaBoxProperty.Value) {
                    $teaBoxDocument = $teaBoxProperty.Value
                    if ($teaBoxDocument -isnot [System.Management.Automation.PSCustomObject]) {
                        throw "Project '$id' TeaBox must be an object."
                    }
                    foreach ($field in @('Executable', 'PlaylistDirectory', 'PlaylistPrefix')) {
                        $property = $teaBoxDocument.PSObject.Properties[$field]
                        if (-not $property -or $property.Value -isnot [string] -or ($field -ne 'PlaylistPrefix' -and [string]::IsNullOrWhiteSpace($property.Value))) {
                            throw "Project '$id' TeaBox config is missing required field '$field'."
                        }
                    }
                    $prefix = [string]$teaBoxDocument.PlaylistPrefix
                    if ($prefix.Contains('"') -or $prefix.Contains("`r") -or $prefix.Contains("`n")) {
                        throw "Project '$id' has an invalid PlaylistPrefix."
                    }
                    $teaBox = [pscustomobject]@{
                        Executable = Resolve-ConfigRelativePath -Path $teaBoxDocument.Executable -BaseDirectory $projectConfigDirectory
                        PlaylistDirectory = Resolve-ConfigRelativePath -Path $teaBoxDocument.PlaylistDirectory -BaseDirectory $projectConfigDirectory
                        PlaylistPrefix = $prefix
                    }
                }
            }
            catch {
                $projectError = $_.Exception.Message
                $name = $id
                $root = $null
                $backgroundImage = $null
                $playlistImages = @()
                $buttons = @()
                $teaBox = $null
            }

            [pscustomobject]@{
                Id = $id
                Name = $name
                ConfigPath = $projectConfigPath
                ConfigDirectory = $projectConfigDirectory
                Root = $root
                BackgroundImage = $backgroundImage
                PlaylistImages = $playlistImages
                Buttons = $buttons
                TeaBox = $teaBox
                Error = $projectError
            }
        }
    )

    $default = $projects | Where-Object { $_.Id -ieq [string]$document.DefaultProject } | Select-Object -First 1
    if (-not $default) {
        throw "Default project '$($document.DefaultProject)' is not configured."
    }

    [pscustomobject]@{
        DefaultProject = $default.Id
        Theme = $theme
        Projects = $projects
    }
}

function Get-PlaylistCatalog {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Project)

    if ($Project.Error) { throw $Project.Error }
    if (-not $Project.TeaBox) { return @() }
    if (-not (Test-Path -LiteralPath $Project.Root -PathType Container)) {
        throw "Project root not found: $($Project.Root)"
    }
    if (-not (Test-Path -LiteralPath $Project.TeaBox.Executable -PathType Leaf)) {
        throw "TeaBox executable not found: $($Project.TeaBox.Executable)"
    }
    if (-not (Test-Path -LiteralPath $Project.TeaBox.PlaylistDirectory -PathType Container)) {
        throw "Playlist directory not found: $($Project.TeaBox.PlaylistDirectory)"
    }

    @(Get-ChildItem -LiteralPath $Project.TeaBox.PlaylistDirectory -Filter '*.playlist' -File |
        Sort-Object -Property Name |
        ForEach-Object {
            [pscustomobject]@{
                Id = $_.BaseName
                Name = $_.BaseName
                Path = $_.FullName
                TeaBoxValue = "$($Project.TeaBox.PlaylistPrefix)$($_.BaseName)"
            }
        })
}

function Get-ActionCatalog {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Project)

    if ($Project.Error) { throw $Project.Error }

    $actions = @(
        foreach ($button in @($Project.Buttons)) {
            [pscustomobject]@{
                Id = "button:$($button.Id)"
                Name = $button.Label
                Type = 'Application'
                Category = $button.Category
                Button = $button
                PlaylistId = $null
                Playlist = $null
            }
        }
    )

    if ($Project.TeaBox) {
        $actions += @(Get-PlaylistCatalog -Project $Project | ForEach-Object {
            [pscustomobject]@{
                Id = "playlist:$($_.Id)"
                Name = $_.Name
                Type = 'TeaBoxPlaylist'
                Button = $null
                PlaylistId = $_.Id
                Playlist = $_
            }
        })
    }

    $actions
}

function Resolve-ActionRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object[]]$Projects,
        [Parameter(Mandatory = $true)][string]$ProjectId,
        [Parameter(Mandatory = $true)][string]$ActionId
    )

    $project = $Projects | Where-Object { $_.Id -ceq $ProjectId } | Select-Object -First 1
    if (-not $project) { throw "Unknown project id: $ProjectId" }
    if ($project.Error) { throw "Project '$ProjectId' is invalid: $($project.Error)" }

    $action = Get-ActionCatalog -Project $project | Where-Object { $_.Id -ceq $ActionId } | Select-Object -First 1
    if (-not $action) { throw "Unknown action id: $ActionId" }

    [pscustomobject]@{
        Project = $project
        Type = $action.Type
        Button = $action.Button
        Playlist = $action.Playlist
    }
}

function Expand-ProjectConfigTokens {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)]$Project
    )

    $Value.Replace('{ProjectRoot}', [string]$Project.Root).Replace('{ProjectConfigDirectory}', [string]$Project.ConfigDirectory)
}

function Resolve-ButtonLaunchSpec {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Project,
        [Parameter(Mandatory = $true)]$Button
    )

    $executableValue = Expand-ProjectConfigTokens -Value $Button.Executable -Project $Project
    $executable = Resolve-ConfigRelativePath -Path $executableValue -BaseDirectory $Project.ConfigDirectory
    $workingDirectoryValue = $Project.Root
    if (-not [string]::IsNullOrWhiteSpace([string]$Button.WorkingDirectory)) {
        $workingDirectoryValue = Expand-ProjectConfigTokens -Value $Button.WorkingDirectory -Project $Project
    }
    $workingDirectory = Resolve-ConfigRelativePath -Path $workingDirectoryValue -BaseDirectory $Project.ConfigDirectory
    $arguments = @(
        foreach ($argument in @($Button.Arguments)) {
            Expand-ProjectConfigTokens -Value ([string]$argument) -Project $Project
        }
    )

    [pscustomobject]@{
        Executable = $executable
        Arguments = $arguments
        WorkingDirectory = $workingDirectory
    }
}

function ConvertTo-WindowsProcessArgument {
    param([AllowEmptyString()][string]$Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }

    $builder = [Text.StringBuilder]::new()
    $null = $builder.Append('"')
    $backslashCount = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq [char]92) {
            $backslashCount++
        }
        elseif ($character -eq '"') {
            if ($backslashCount -gt 0) {
                $null = $builder.Append([string]::new([char]92, ($backslashCount * 2) + 1))
            }
            else {
                $null = $builder.Append([char]92)
            }
            $null = $builder.Append('"')
            $backslashCount = 0
        }
        else {
            if ($backslashCount -gt 0) {
                $null = $builder.Append([string]::new([char]92, $backslashCount))
                $backslashCount = 0
            }
            $null = $builder.Append($character)
        }
    }
    if ($backslashCount -gt 0) {
        $null = $builder.Append([string]::new([char]92, $backslashCount * 2))
    }
    $null = $builder.Append('"')
    $builder.ToString()
}

function Join-WindowsCommandLineArguments {
    [CmdletBinding()]
    param([AllowEmptyCollection()][string[]]$Arguments = @())

    $quotedArguments = @(
        foreach ($argument in $Arguments) {
            ConvertTo-WindowsProcessArgument -Value $argument
        }
    )
    [string]::Join(' ', $quotedArguments)
}

function Get-ConfiguredArtworkSource {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Project,
        [Parameter(Mandatory = $true)][ValidateSet('background', 'playlist', 'button')][string]$Kind,
        [string]$PlaylistId,
        [string]$ButtonId
    )

    $reference = $null
    if ($Kind -eq 'background') {
        $reference = $Project.BackgroundImage
    }
    elseif ($Kind -eq 'playlist' -and -not [string]::IsNullOrWhiteSpace($PlaylistId)) {
        $image = @($Project.PlaylistImages | Where-Object { $_.PlaylistId -ceq $PlaylistId } | Select-Object -First 1)
        if ($image.Count -gt 0) { $reference = $image[0].Path }
    }
    elseif ($Kind -eq 'button' -and -not [string]::IsNullOrWhiteSpace($ButtonId)) {
        $button = @($Project.Buttons | Where-Object { $_.Id -ceq $ButtonId } | Select-Object -First 1)
        if ($button.Count -gt 0) { $reference = $button[0].Icon }
    }

    if ([string]::IsNullOrWhiteSpace([string]$reference)) { return $null }

    if ([IO.Path]::IsPathRooted($reference)) {
        if ([IO.Path]::GetExtension($reference).ToLowerInvariant() -notin @('.png', '.jpg', '.jpeg', '.webp', '.bmp')) { return $null }
        if (Test-Path -LiteralPath $reference -PathType Leaf -ErrorAction SilentlyContinue) {
            return [pscustomobject]@{ Type = 'Local'; Value = $reference }
        }
        return $null
    }

    $uri = $null
    if ([Uri]::TryCreate($reference, [UriKind]::Absolute, [ref]$uri) -and $uri.Scheme -in @('http', 'https') -and -not [string]::IsNullOrWhiteSpace($uri.Host)) {
        return [pscustomobject]@{ Type = 'Remote'; Value = $reference }
    }
    $null
}

function Get-ConfiguredArtworkPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Project,
        [Parameter(Mandatory = $true)][ValidateSet('background', 'playlist', 'button')][string]$Kind,
        [string]$PlaylistId,
        [string]$ButtonId
    )

    $source = Get-ConfiguredArtworkSource -Project $Project -Kind $Kind -PlaylistId $PlaylistId -ButtonId $ButtonId
    if ($source -and $source.Type -eq 'Local') { return $source.Value }
    $null
}

function Resolve-PlaylistRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object[]]$Projects,
        [Parameter(Mandatory = $true)][string]$ProjectId,
        [Parameter(Mandatory = $true)][string]$PlaylistId
    )

    $project = $Projects | Where-Object { $_.Id -ceq $ProjectId } | Select-Object -First 1
    if (-not $project) {
        throw "Unknown project id: $ProjectId"
    }

    $playlist = Get-PlaylistCatalog -Project $project |
        Where-Object { $_.Id -ceq $PlaylistId } |
        Select-Object -First 1
    if (-not $playlist) {
        throw "Unknown playlist id: $PlaylistId"
    }

    [pscustomobject]@{ Project = $project; Playlist = $playlist }
}

function Quote-TeaBoxArgumentValue {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value.Contains('"')) {
        throw 'TeaBox argument values cannot contain double quotes.'
    }

    $escapedValue = $Value
    $trailingBackslashes = 0
    while ($escapedValue.EndsWith([string][char]92)) {
        $escapedValue = $escapedValue.Substring(0, $escapedValue.Length - 1)
        $trailingBackslashes++
    }
    if ($trailingBackslashes -gt 0) {
        # Windows command-line parsing needs trailing backslashes doubled before a closing quote.
        $escapedValue += [string]::new([char]92, $trailingBackslashes * 2)
    }

    '"{0}"' -f $escapedValue
}

function Get-TeaBoxArguments {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Project,
        [Parameter(Mandatory = $true)]$Playlist
    )

    '-Root={0} RunPlaylist -playlist={1}' -f (Quote-TeaBoxArgumentValue $Project.Root), (Quote-TeaBoxArgumentValue ($Project.TeaBox.PlaylistPrefix + $Playlist.Id))
}
