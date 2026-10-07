$ErrorActionPreference = 'Stop'
$corePath = Join-Path $PSScriptRoot '..\launcher.core.ps1'
if (Test-Path -LiteralPath $corePath) {
    . $corePath
}

foreach ($functionName in @('Read-LauncherConfig', 'Get-PlaylistCatalog', 'Resolve-PlaylistRequest', 'Get-TeaBoxArguments')) {
    if (-not (Get-Command $functionName -ErrorAction SilentlyContinue)) {
        throw "Missing core function: $functionName"
    }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Name)
    if ($Expected -ne $Actual) {
        throw "$Name expected '$Expected' but got '$Actual'."
    }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$ExpectedText, [string]$Name)
    $caught = $null
    try { & $Action } catch { $caught = $_ }
    if (-not $caught) { throw "$Name did not throw." }
    if ($ExpectedText -and $caught.Exception.Message -notlike "*$ExpectedText*") {
        throw "$Name threw an unexpected error: $($caught.Exception.Message)"
    }
}

function Get-HttpStatus {
    param([Parameter(Mandatory = $true)][string]$Uri)
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $Uri | Out-Null
        return 200
    }
    catch {
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        throw
    }
}

function Get-HttpImage {
    param([Parameter(Mandatory = $true)][string]$Uri)
    $response = [Net.HttpWebRequest]::Create($Uri).GetResponse()
    $stream = $response.GetResponseStream()
    $buffer = [IO.MemoryStream]::new()
    try {
        $stream.CopyTo($buffer)
        [pscustomobject]@{
            ContentType = $response.ContentType
            Bytes = $buffer.ToArray()
        }
    }
    finally {
        $buffer.Dispose()
        $stream.Dispose()
        $response.Dispose()
    }
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ("Teabox Launcher Tests " + [guid]::NewGuid().ToString('N'))
$configDirectory = Join-Path $fixtureRoot 'config'
$hurricaneRoot = Join-Path $fixtureRoot 'Hurricane Project'
$otherRoot = Join-Path $fixtureRoot 'Other Project'
$hurricaneTools = Join-Path $hurricaneRoot 'Tools'
$hurricanePlaylists = Join-Path $hurricaneRoot 'Playlists'
$otherPlaylists = Join-Path $otherRoot 'Playlists'

try {
    New-Item -ItemType Directory -Path $configDirectory, $hurricaneTools, $hurricanePlaylists, $otherPlaylists -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $hurricaneTools 'TeaBox.com') -Value 'fixture executable' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $hurricaneTools 'Viewer.exe') -Value 'fixture executable' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $hurricanePlaylists '1 - Get and Launch Editor.playlist') -Value '<TeaBox />' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $hurricanePlaylists '0 - Setup.playlist') -Value '<TeaBox />' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $otherPlaylists 'Build.playlist') -Value '<TeaBox />' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $hurricanePlaylists '2 - No Image.playlist') -Value '<TeaBox />' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $hurricanePlaylists '3 - BMP Icon.playlist') -Value '<TeaBox />' -Encoding UTF8

    $snow = [char]0x96EA
    $kanji = [char]0x6F22
    $unicodeDisplayName = "Project $snow"
    $unicodeRoot = Join-Path $fixtureRoot $unicodeDisplayName
    $unicodeTools = Join-Path $unicodeRoot 'Tools'
    $unicodePlaylists = Join-Path $unicodeRoot 'Playlists'
    New-Item -ItemType Directory -Path $unicodeTools, $unicodePlaylists -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $unicodeTools 'TeaBox.com') -Value 'fixture executable' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $unicodePlaylists "Playlist $kanji.playlist") -Value '<TeaBox />' -Encoding UTF8
    $unicodeProjectConfigPath = Join-Path $configDirectory 'unicode-project.json'
    $unicodeMasterConfigPath = Join-Path $configDirectory 'unicode-master.json'
    $unicodeRelativeRoot = "../$unicodeDisplayName"
    $unicodeProjectDocument = @{
        Name = $unicodeDisplayName
        Root = $unicodeRelativeRoot
        TeaBox = @{
            Executable = "$unicodeRelativeRoot/Tools/TeaBox.com"
            PlaylistDirectory = "$unicodeRelativeRoot/Playlists"
            PlaylistPrefix = 'Shared- '
        }
    }
    [IO.File]::WriteAllText($unicodeProjectConfigPath, ($unicodeProjectDocument | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $unicodeMasterDocument = @{ DefaultProject = 'unicode'; Projects = @(@{ Id = 'unicode'; Config = 'unicode-project.json' }) }
    [IO.File]::WriteAllText($unicodeMasterConfigPath, ($unicodeMasterDocument | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $unicodeConfig = Read-LauncherConfig -ConfigPath $unicodeMasterConfigPath
    Assert-Equal $unicodeDisplayName $unicodeConfig.Projects[0].Name 'UTF-8 project name'
    Assert-Equal 1 @(Get-PlaylistCatalog -Project $unicodeConfig.Projects[0]).Count 'UTF-8 project playlist discovery'

    $portableProjectDirectory = Join-Path $configDirectory 'projects'
    $portableRoot = Join-Path $fixtureRoot 'Portable Project'
    $portableTools = Join-Path $portableRoot 'Tools'
    $portablePlaylists = Join-Path $portableRoot 'Playlists'
    New-Item -ItemType Directory -Path $portableProjectDirectory, $portableTools, $portablePlaylists -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $portableTools 'TeaBox.com') -Value 'fixture executable' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $portableTools 'Viewer.exe') -Value 'fixture executable' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $portablePlaylists 'Build.playlist') -Value '<TeaBox />' -Encoding UTF8

    $portableProjectConfigPath = Join-Path $portableProjectDirectory 'portable.json'
    $portableProjectConfig = @{
        Name = 'Portable'
        Root = '../../Portable Project'
        Buttons = @(
            @{
                Id = 'viewer'
                Label = 'Open Viewer'
                Executable = '{ProjectRoot}/Tools/Viewer.exe'
                Arguments = @('--data', '{ProjectRoot}\Content', '{ProjectConfigDirectory}')
                WorkingDirectory = '{ProjectRoot}'
                Category = 'Tools'
            }
            @{
                Id = 'launcher'
                Label = 'Open Launcher'
                Executable = '{ProjectRoot}/Tools/Viewer.exe'
                Category = '  '
            }
        )
        TeaBox = @{
            Executable = '../../Portable Project/Tools/TeaBox.com'
            PlaylistDirectory = '../../Portable Project/Playlists'
            PlaylistPrefix = 'Shared- '
        }
    }
    [IO.File]::WriteAllText($portableProjectConfigPath, ($portableProjectConfig | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

    $standaloneProjectConfigPath = Join-Path $portableProjectDirectory 'standalone.json'
    $standaloneProjectConfig = @{
        Name = 'Standalone'
        Root = '../../Portable Project'
        Buttons = @(@{
            Id = 'viewer'
            Label = 'Open Viewer'
            Executable = '{ProjectRoot}/Tools/Viewer.exe'
            Arguments = @()
        })
    }
    [IO.File]::WriteAllText($standaloneProjectConfigPath, ($standaloneProjectConfig | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

    $portableMasterPath = Join-Path $configDirectory 'portable-projects.json'
    $portableMaster = @{
        DefaultProject = 'portable'
        Theme = 'light'
        Projects = @(
            @{ Id = 'portable'; Config = 'projects/portable.json' }
            @{ Id = 'standalone'; Config = 'projects/standalone.json' }
            @{ Id = 'unavailable'; Config = 'projects/missing.json' }
        )
    }
    [IO.File]::WriteAllText($portableMasterPath, ($portableMaster | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

    $portableConfig = Read-LauncherConfig -ConfigPath $portableMasterPath
    Assert-Equal 'light' $portableConfig.Theme 'Master theme preserved'
    $portableProject = $portableConfig.Projects | Where-Object Id -eq 'portable'
    Assert-Equal 'Portable' $portableProject.Name 'Per-project name'
    Assert-Equal ([IO.Path]::GetFullPath($portableRoot)) $portableProject.Root 'Project-config-relative root'
    $portableActions = @(Get-ActionCatalog -Project $portableProject)
    Assert-Equal 3 $portableActions.Count 'Mixed application and TeaBox action count'
    Assert-Equal 'Application' $portableActions[0].Type 'Configured action type'
    Assert-Equal 'button:viewer' $portableActions[0].Id 'Configured action id'
    Assert-Equal 'Tools' $portableActions[0].Category 'Configured action category'
    Assert-Equal 'Applications' $portableActions[1].Category 'Blank action category fallback'
    Assert-Equal 'TeaBoxPlaylist' $portableActions[2].Type 'Discovered playlist action type'
    Assert-Equal 'playlist:Build' $portableActions[2].Id 'Discovered playlist action id'
    $resolvedButton = Resolve-ActionRequest -Projects $portableConfig.Projects -ProjectId 'portable' -ActionId 'button:viewer'
    $launchSpec = Resolve-ButtonLaunchSpec -Project $resolvedButton.Project -Button $resolvedButton.Button
    Assert-Equal 'Application' $resolvedButton.Type 'Resolved application action type'
    Assert-Equal ([IO.Path]::GetFullPath((Join-Path $portableTools 'Viewer.exe'))) $launchSpec.Executable 'Expanded executable path'
    Assert-Equal '--data' $launchSpec.Arguments[0] 'First application argument'
    Assert-Equal ([IO.Path]::GetFullPath((Join-Path $portableRoot 'Content'))) $launchSpec.Arguments[1] 'Expanded project-root argument'
    Assert-Equal ([IO.Path]::GetFullPath($portableProjectDirectory)) $launchSpec.Arguments[2] 'Expanded project-config argument'
    Assert-Equal ([IO.Path]::GetFullPath($portableRoot)) $launchSpec.WorkingDirectory 'Expanded working directory'
    Assert-Equal 'simple "has space" "embedded\"quote" "C:\path with space\\"' (Join-WindowsCommandLineArguments -Arguments @('simple', 'has space', 'embedded"quote', 'C:\path with space\')) 'Windows argument escaping'
    $standalone = $portableConfig.Projects | Where-Object Id -eq 'standalone'
    $standaloneActions = @(Get-ActionCatalog -Project $standalone)
    Assert-Equal 1 $standaloneActions.Count 'No-TeaBox project action count'
    Assert-Equal 'Applications' $standaloneActions[0].Category 'Missing action category fallback'
    $unavailable = $portableConfig.Projects | Where-Object Id -eq 'unavailable'
    if (-not $unavailable.Error) { throw 'Missing child config was not reported on its project.' }
    Assert-Throws { Resolve-ActionRequest -Projects $portableConfig.Projects -ProjectId 'portable' -ActionId 'button:missing' } 'Unknown action' 'Unknown action rejection'

    $configPath = Join-Path $configDirectory 'projects.json'
    $projectConfigDirectory = Join-Path $configDirectory 'main-projects'
    New-Item -ItemType Directory -Path $projectConfigDirectory -Force | Out-Null
    $hurricaneProjectConfigPath = Join-Path $projectConfigDirectory 'hurricane.json'
    $otherProjectConfigPath = Join-Path $projectConfigDirectory 'other.json'
    $hurricaneProjectConfig = @{
        Name = 'Hurricane'
        Root = '../../Hurricane Project'
        Buttons = @(@{
            Id = 'open-viewer'
            Label = 'Open Viewer'
            Executable = '../../Hurricane Project/Tools/Viewer.exe'
            Arguments = @('--safe')
            WorkingDirectory = '{ProjectRoot}'
            Icon = '../../artwork/setup.png'
            Category = 'Development'
        })
        TeaBox = @{
            Executable = '../../Hurricane Project/Tools/TeaBox.com'
            PlaylistDirectory = '../../Hurricane Project/Playlists'
            PlaylistPrefix = 'Shared- '
        }
    }
    $otherProjectConfig = @{
        Name = 'Other'
        Root = '../../Other Project'
        Buttons = @()
        TeaBox = @{
            Executable = '../../Other Project/Tools/TeaBox.com'
            PlaylistDirectory = '../../Other Project/Playlists'
            PlaylistPrefix = 'Shared- '
        }
    }
    $fixtureConfig = @{
        DefaultProject = 'hurricane'
        Theme = 'light'
        Projects = @(
            @{ Id = 'hurricane'; Config = 'main-projects/hurricane.json' }
            @{ Id = 'other'; Config = 'main-projects/other.json' }
        )
    }
    $artworkDirectory = Join-Path $fixtureRoot 'artwork'
    New-Item -ItemType Directory -Path $artworkDirectory -Force | Out-Null
    $backgroundBytes = [byte[]]@(82, 73, 70, 70, 12, 0, 0, 0, 87, 69, 66, 80)
    $playlistImageBytes = [byte[]]@(137, 80, 78, 71, 13, 10, 26, 10)
    $bmpBytes = [byte[]]@(
        66, 77, 58, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0,
        40, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 24, 0,
        0, 0, 0, 0, 4, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 255, 0
    )
    $backgroundPath = Join-Path $artworkDirectory 'background.webp'
    $setupImagePath = Join-Path $artworkDirectory 'setup.png'
    $bmpImagePath = Join-Path $artworkDirectory 'bmp-icon.bmp'
    [IO.File]::WriteAllBytes($backgroundPath, $backgroundBytes)
    [IO.File]::WriteAllBytes($setupImagePath, $playlistImageBytes)
    [IO.File]::WriteAllBytes($bmpImagePath, $bmpBytes)
    [IO.File]::WriteAllText((Join-Path $artworkDirectory 'unsupported.svg'), '<svg></svg>')
    $hurricaneProjectConfig.BackgroundImage = '../../artwork/background.webp'
    $hurricaneProjectConfig.PlaylistImages = @{
        '0 - Setup' = '../../artwork/setup.png'
        '1 - Get and Launch Editor' = '../../artwork/unsupported.svg'
        '2 - No Image' = '../../artwork/missing.jpg'
        '3 - BMP Icon' = '../../artwork/bmp-icon.bmp'
        'Missing playlist' = '../../artwork/missing.jpg'
    }
    [IO.File]::WriteAllText($hurricaneProjectConfigPath, ($hurricaneProjectConfig | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($otherProjectConfigPath, ($otherProjectConfig | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($configPath, ($fixtureConfig | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $themePath = Join-Path $configDirectory 'theme.json'
    $themeConfig = @{
        dark = @{ ink = '#010203'; 'xbox-green' = '#123456' }
        light = @{ ink = '#f1f2f3'; 'xbox-green' = '#654321' }
    }
    [IO.File]::WriteAllText($themePath, ($themeConfig | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

    $config = Read-LauncherConfig -ConfigPath $configPath
    Assert-Equal 'hurricane' $config.DefaultProject 'Default project'
    Assert-Equal 'light' $config.Theme 'Configured theme'
    $invalidThemePath = Join-Path $configDirectory 'invalid-theme.json'
    $invalidThemeConfig = @{ DefaultProject = 'hurricane'; Theme = 'sepia'; Projects = $fixtureConfig.Projects }
    $invalidThemeConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $invalidThemePath -Encoding UTF8
    Assert-Throws { Read-LauncherConfig -ConfigPath $invalidThemePath } "Theme must be 'dark' or 'light'." 'Invalid theme rejection'

    $localConfigPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\projects.json'))
    $localConfig = Read-LauncherConfig -ConfigPath $localConfigPath
    Assert-Equal 'hurricane' $localConfig.DefaultProject 'Installed default project'
    Assert-Equal 'dark' $localConfig.Theme 'Default theme for existing config'
    $localHurricane = $localConfig.Projects | Where-Object Id -eq 'hurricane'
    $localPlaylistFileCount = @(Get-ChildItem -LiteralPath $localHurricane.TeaBox.PlaylistDirectory -Filter '*.playlist' -File).Count
    Assert-Equal $localPlaylistFileCount @(Get-PlaylistCatalog -Project $localHurricane).Count 'Installed Hurricane playlist count'
    $hurricane = $config.Projects | Where-Object Id -eq 'hurricane'
    Assert-Equal ([IO.Path]::GetFullPath($hurricaneRoot)) $hurricane.Root 'Resolved project root'
    Assert-Equal ([IO.Path]::GetFullPath((Join-Path $hurricaneTools 'TeaBox.com'))) $hurricane.TeaBox.Executable 'Resolved TeaBox path'

    $catalog = @(Get-PlaylistCatalog -Project $hurricane)
    Assert-Equal 4 $catalog.Count 'Hurricane playlist count'
    Assert-Equal '0 - Setup' $catalog[0].Id 'Sorted playlist id'
    Assert-Equal 'Shared- 0 - Setup' $catalog[0].TeaBoxValue 'TeaBox playlist value'

    $resolved = Resolve-PlaylistRequest -Projects $config.Projects -ProjectId 'hurricane' -PlaylistId '1 - Get and Launch Editor'
    Assert-Equal 'Hurricane' $resolved.Project.Name 'Resolved project'
    Assert-Equal 'Shared- 1 - Get and Launch Editor' $resolved.Playlist.TeaBoxValue 'Resolved playlist'

    $arguments = Get-TeaBoxArguments -Project $hurricane -Playlist $catalog[0]
    $expectedArguments = '-Root="{0}" RunPlaylist -playlist="Shared- 0 - Setup"' -f ([IO.Path]::GetFullPath($hurricaneRoot))
    Assert-Equal $expectedArguments $arguments 'Quoted TeaBox arguments'

    $trailingRootProject = [pscustomobject]@{ Root = $hurricane.Root + '\'; TeaBox = $hurricane.TeaBox }
    $trailingRootArguments = Get-TeaBoxArguments -Project $trailingRootProject -Playlist $catalog[0]
    $expectedTrailingRoot = '"' + $hurricane.Root + '\\' + '"'
    $expectedTrailingArguments = '-Root={0} RunPlaylist -playlist="Shared- 0 - Setup"' -f $expectedTrailingRoot
    Assert-Equal $expectedTrailingArguments $trailingRootArguments 'Trailing root separator quoting'

    Assert-Throws { Resolve-PlaylistRequest -Projects $config.Projects -ProjectId 'missing' -PlaylistId '0 - Setup' } 'Unknown project' 'Unknown project rejection'
    Assert-Throws { Resolve-PlaylistRequest -Projects $config.Projects -ProjectId 'hurricane' -PlaylistId 'missing.playlist' } 'Unknown playlist' 'Unknown playlist rejection'

    $badConfigPath = Join-Path $configDirectory 'duplicate-projects.json'
    $duplicateConfig = @{ DefaultProject = 'hurricane'; Projects = @($fixtureConfig.Projects[0], $fixtureConfig.Projects[0]) }
    $duplicateConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $badConfigPath -Encoding UTF8
    Assert-Throws { Read-LauncherConfig -ConfigPath $badConfigPath } 'Duplicate project id' 'Duplicate id rejection'

    $missingProject = $config.Projects | Where-Object Id -eq 'other'
    Assert-Throws { Get-PlaylistCatalog -Project $missingProject } 'TeaBox executable not found' 'Missing executable rejection'

    $serverPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\launcher.ps1'))
    if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf)) {
        throw "Missing launcher HTTP host: $serverPath"
    }

    $portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $portProbe.Start(); $testPort = $portProbe.LocalEndpoint.Port }
    finally { $portProbe.Stop() }
    $serverStdout = Join-Path $fixtureRoot 'server.stdout.log'
    $serverStderr = Join-Path $fixtureRoot 'server.stderr.log'
    $serverArguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -ConfigPath "{1}" -Port {2} -TestMode -NoOpenBrowser' -f $serverPath, $configPath, $testPort
    $serverProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList $serverArguments -RedirectStandardOutput $serverStdout -RedirectStandardError $serverStderr -PassThru -WindowStyle Hidden
    try {
        $deadline = (Get-Date).AddSeconds(15)
        $serverOutput = ''
        $uriPattern = 'Listening at (http://127\.0\.0\.1:\d+/)'
        $uriMatch = [regex]::Match($serverOutput, $uriPattern)
        while ((Get-Date) -lt $deadline -and -not $uriMatch.Success) {
            Start-Sleep -Milliseconds 150
            if (Test-Path -LiteralPath $serverStdout) {
                $serverOutput = Get-Content -LiteralPath $serverStdout -Raw
                if ($null -eq $serverOutput) { $serverOutput = '' }
            }
            if ($serverProcess.HasExited) { throw "Launcher exited early: $(Get-Content -LiteralPath $serverStderr -Raw)" }
            $uriMatch = [regex]::Match($serverOutput, $uriPattern)
        }
        if (-not $uriMatch.Success) {
            throw "Launcher did not report its URL. Output: $serverOutput"
        }

        $baseUri = $uriMatch.Groups[1].Value
        Assert-Equal "http://127.0.0.1:$testPort/" $baseUri 'Requested host port'
        $origin = $baseUri.TrimEnd('/')
        $page = Invoke-WebRequest -UseBasicParsing -Uri $baseUri
        if (-not $page.Content.Contains('id="theme-toggle"') -or -not $page.Content.Contains("localStorage.getItem('launcher-theme')") -or -not $page.Content.Contains('Max-Age=31536000')) {
            throw 'Launcher page did not include the theme toggle and persistent saved preference support.'
        }
        Assert-Equal 'DENY' $page.Headers['X-Frame-Options'] 'Framing protection header'
        $head = Invoke-WebRequest -Method Head -UseBasicParsing -Uri $baseUri
        Assert-Equal 200 $head.StatusCode 'HEAD root status'
        if ([int64]$head.Headers['Content-Length'] -le 0) { throw 'HEAD root response did not describe the page length.' }
        $tokenMatch = [regex]::Match($page.Content, 'window\.launcherToken = "(?<token>[a-f0-9-]+)"')
        if (-not $tokenMatch.Success) {
            throw 'Served page did not include its session token.'
        }
        $token = $tokenMatch.Groups['token'].Value

        $apiCatalog = Invoke-RestMethod -Uri ($baseUri + 'api/catalog')
        Assert-Equal 'hurricane' $apiCatalog.DefaultProject 'API default project'
        Assert-Equal 'light' $apiCatalog.Theme 'API theme selection'
        Assert-Equal '#010203' $apiCatalog.ThemeColors.dark.ink 'API dark theme color'
        Assert-Equal '#f1f2f3' $apiCatalog.ThemeColors.light.ink 'API light theme color'
        Assert-Equal '#123456' $apiCatalog.ThemeColors.dark.'xbox-green' 'API dark accent color'
        Assert-Equal 5 @($apiCatalog.Projects[0].Actions).Count 'API Hurricane action count'

        [IO.File]::WriteAllText($themePath, '{"dark":', [Text.UTF8Encoding]::new($false))
        $invalidThemeCatalog = Invoke-RestMethod -Uri ($baseUri + 'api/catalog')
        Assert-Equal $null $invalidThemeCatalog.ThemeColors 'Malformed theme config fallback'
        [IO.File]::WriteAllText($themePath, '{"dark":{"INK":"#fff"},"light":{"ink":"#fff"}}', [Text.UTF8Encoding]::new($false))
        $unknownThemeCatalog = Invoke-RestMethod -Uri ($baseUri + 'api/catalog')
        Assert-Equal $null $unknownThemeCatalog.ThemeColors 'Unknown theme token fallback'
        [IO.File]::Delete($themePath)
        $missingThemeCatalog = Invoke-RestMethod -Uri ($baseUri + 'api/catalog')
        Assert-Equal $null $missingThemeCatalog.ThemeColors 'Missing theme config fallback'
        if ($apiCatalog.Projects[1].Error -notlike 'TeaBox executable not found:*') { throw "Per-project configuration error was unclear: $($apiCatalog.Projects[1].Error)" }

        $artworkProject = $apiCatalog.Projects | Where-Object Id -eq 'hurricane'
        $backgroundUrl = [string]$artworkProject.BackgroundImageUrl
        Assert-Equal '/api/artwork?projectId=hurricane&kind=background' $backgroundUrl 'Catalog background URL'
        $setupArtwork = $artworkProject.Actions | Where-Object Id -eq 'playlist:0 - Setup'
        Assert-Equal '/api/artwork?projectId=hurricane&kind=playlist&playlistId=0%20-%20Setup' $setupArtwork.ArtworkUrl 'Catalog playlist artwork URL'
        $bmpArtwork = $artworkProject.Actions | Where-Object Id -eq 'playlist:3 - BMP Icon'
        Assert-Equal '/api/artwork?projectId=hurricane&kind=playlist&playlistId=3%20-%20BMP%20Icon' $bmpArtwork.ArtworkUrl 'Catalog BMP artwork URL'
        $applicationAction = $artworkProject.Actions | Where-Object Id -eq 'button:open-viewer'
        Assert-Equal 'Application' $applicationAction.Type 'Catalog application action type'
        Assert-Equal 'Development' $applicationAction.Category 'Catalog application category'
        Assert-Equal '/api/artwork?projectId=hurricane&kind=button&buttonId=open-viewer' $applicationAction.ArtworkUrl 'Catalog application artwork URL'
        $unsupportedArtwork = $artworkProject.Actions | Where-Object Id -eq 'playlist:1 - Get and Launch Editor'
        Assert-Equal $null $unsupportedArtwork.ArtworkUrl 'Unsupported artwork fallback'
        $missingArtwork = $artworkProject.Actions | Where-Object Id -eq 'playlist:2 - No Image'
        Assert-Equal $null $missingArtwork.ArtworkUrl 'Missing artwork fallback'
        $apiCatalogJson = $apiCatalog | ConvertTo-Json -Depth 8 -Compress
        if ($apiCatalogJson.Contains($backgroundPath) -or $apiCatalogJson.Contains($setupImagePath) -or $apiCatalogJson.Contains($hurricaneRoot)) {
            throw 'Catalog exposed a configured filesystem path.'
        }

        $backgroundResponse = Get-HttpImage -Uri ($origin + $backgroundUrl)
        Assert-Equal 'image/webp' ([string]$backgroundResponse.ContentType).Split(';')[0] 'Background image content type'
        Assert-Equal ([Convert]::ToBase64String($backgroundBytes)) ([Convert]::ToBase64String($backgroundResponse.Bytes)) 'Background image bytes'
        $playlistImageResponse = Get-HttpImage -Uri ($origin + $setupArtwork.ArtworkUrl)
        Assert-Equal 'image/png' ([string]$playlistImageResponse.ContentType).Split(';')[0] 'Playlist image content type'
        Assert-Equal ([Convert]::ToBase64String($playlistImageBytes)) ([Convert]::ToBase64String($playlistImageResponse.Bytes)) 'Playlist image bytes'
        $bmpImageResponse = Get-HttpImage -Uri ($origin + $bmpArtwork.ArtworkUrl)
        Assert-Equal 'image/bmp' ([string]$bmpImageResponse.ContentType).Split(';')[0] 'BMP image content type'
        Assert-Equal ([Convert]::ToBase64String($bmpBytes)) ([Convert]::ToBase64String($bmpImageResponse.Bytes)) 'BMP image bytes'
        $applicationImageResponse = Get-HttpImage -Uri ($origin + $applicationAction.ArtworkUrl)
        Assert-Equal 'image/png' ([string]$applicationImageResponse.ContentType).Split(';')[0] 'Application image content type'
        Assert-Equal ([Convert]::ToBase64String($playlistImageBytes)) ([Convert]::ToBase64String($applicationImageResponse.Bytes)) 'Application image bytes'
        Assert-Equal 404 (Get-HttpStatus -Uri ($origin + '/api/artwork?projectId=unknown&kind=background')) 'Unknown artwork project status'
        Assert-Equal 404 (Get-HttpStatus -Uri ($origin + '/api/artwork?projectId=hurricane&kind=playlist&playlistId=not-configured')) 'Unknown artwork playlist status'
        Assert-Equal 404 (Get-HttpStatus -Uri ($origin + '/api/artwork?projectId=hurricane&kind=button&buttonId=not-configured')) 'Unknown artwork button status'
        Assert-Equal 404 (Get-HttpStatus -Uri ($origin + '/api/artwork?path=%2FWindows%2Fwin.ini')) 'Raw artwork path rejection status'
        Assert-Equal 404 (Get-HttpStatus -Uri ($origin + '/api/artwork?projectId=hurricane&kind=playlist&playlistId=1%20-%20Get%20and%20Launch%20Editor')) 'Unsupported artwork request status'
        Assert-Equal 404 (Get-HttpStatus -Uri ($origin + '/api/artwork?projectId=hurricane&kind=playlist&playlistId=2%20-%20No%20Image')) 'Missing artwork request status'

        $remoteBackgroundUrl = 'https://images.example.test/background.png?token=background%2Fsignature'
        $remotePlaylistUrl = 'http://images.example.test/setup.png?size=128&theme=dark'
        $remoteIconUrl = 'https://images.example.test/viewer.webp?version=2'
        $remoteProjectConfig = $hurricaneProjectConfig.Clone()
        $remoteProjectConfig.BackgroundImage = $remoteBackgroundUrl
        $remoteButton = $hurricaneProjectConfig.Buttons[0].Clone()
        $remoteButton.Icon = $remoteIconUrl
        $remoteProjectConfig.Buttons = @($remoteButton)
        $remoteProjectConfig.PlaylistImages = @{ '0 - Setup' = $remotePlaylistUrl }
        [IO.File]::WriteAllText($hurricaneProjectConfigPath, ($remoteProjectConfig | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

        $remoteConfig = Read-LauncherConfig -ConfigPath $configPath
        $remoteProject = $remoteConfig.Projects | Where-Object Id -eq 'hurricane'
        Assert-Equal $null $remoteProject.Error 'Remote artwork project remains valid'
        Assert-Equal $remoteBackgroundUrl $remoteProject.BackgroundImage 'HTTPS background URL preservation'
        Assert-Equal $remotePlaylistUrl (@($remoteProject.PlaylistImages | Where-Object PlaylistId -eq '0 - Setup')[0].Path) 'HTTP playlist URL preservation'
        Assert-Equal $remoteIconUrl $remoteProject.Buttons[0].Icon 'HTTPS application icon URL preservation'
        Assert-Equal 5 @(Get-ActionCatalog -Project $remoteProject).Count 'Remote artwork preserves project actions'

        $remoteCatalog = Invoke-RestMethod -Uri ($baseUri + 'api/catalog')
        $remoteApiProject = $remoteCatalog.Projects | Where-Object Id -eq 'hurricane'
        Assert-Equal $remoteBackgroundUrl $remoteApiProject.BackgroundImageUrl 'Catalog remote background URL'
        $remotePlaylistAction = $remoteApiProject.Actions | Where-Object Id -eq 'playlist:0 - Setup'
        Assert-Equal $remotePlaylistUrl $remotePlaylistAction.ArtworkUrl 'Catalog remote playlist URL'
        $remoteApplicationAction = $remoteApiProject.Actions | Where-Object Id -eq 'button:open-viewer'
        Assert-Equal $remoteIconUrl $remoteApplicationAction.ArtworkUrl 'Catalog remote application icon URL'
        Assert-Equal 404 (Get-HttpStatus -Uri ($origin + '/api/artwork?projectId=hurricane&kind=background')) 'Remote background is not proxied'
        Assert-Equal 404 (Get-HttpStatus -Uri ($origin + '/api/artwork?projectId=hurricane&kind=playlist&playlistId=0%20-%20Setup')) 'Remote playlist image is not proxied'
        Assert-Equal 404 (Get-HttpStatus -Uri ($origin + '/api/artwork?projectId=hurricane&kind=button&buttonId=open-viewer')) 'Remote application icon is not proxied'

        $unsupportedProjectConfig = $hurricaneProjectConfig.Clone()
        $unsupportedProjectConfig.BackgroundImage = 'file:///C:/Windows/win.ini'
        $unsupportedButton = $hurricaneProjectConfig.Buttons[0].Clone()
        $unsupportedButton.Icon = 'data:image/png;base64,AA=='
        $unsupportedProjectConfig.Buttons = @($unsupportedButton)
        $unsupportedProjectConfig.PlaylistImages = @{ '0 - Setup' = 'ftp://images.example.test/setup.png' }
        [IO.File]::WriteAllText($hurricaneProjectConfigPath, ($unsupportedProjectConfig | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        $unsupportedCatalog = Invoke-RestMethod -Uri ($baseUri + 'api/catalog')
        $unsupportedApiProject = $unsupportedCatalog.Projects | Where-Object Id -eq 'hurricane'
        Assert-Equal $null $unsupportedApiProject.Error 'Unsupported artwork schemes do not invalidate the project'
        Assert-Equal $null $unsupportedApiProject.BackgroundImageUrl 'Unsupported background scheme fallback'
        Assert-Equal $null (@($unsupportedApiProject.Actions | Where-Object Id -eq 'playlist:0 - Setup')[0].ArtworkUrl) 'Unsupported playlist scheme fallback'
        Assert-Equal $null (@($unsupportedApiProject.Actions | Where-Object Id -eq 'button:open-viewer')[0].ArtworkUrl) 'Unsupported icon scheme fallback'
        Assert-Equal 5 @($unsupportedApiProject.Actions).Count 'Unsupported artwork schemes preserve actions'

        $requestHeaders = @{ Origin = $origin; 'X-Launcher-Token' = $token }
        $launchBody = @{ ProjectId = 'hurricane'; ActionId = 'playlist:0 - Setup' } | ConvertTo-Json
        $launchResult = Invoke-RestMethod -Method Post -Uri ($baseUri + 'api/launch') -Headers $requestHeaders -ContentType 'application/json' -Body $launchBody
        Assert-Equal $true $launchResult.TestMode 'Test-mode dispatch marker'
        Assert-Equal $expectedArguments $launchResult.Arguments 'Test-mode dispatch arguments'

        $applicationBody = @{ ProjectId = 'hurricane'; ActionId = 'button:open-viewer' } | ConvertTo-Json
        $applicationResult = Invoke-RestMethod -Method Post -Uri ($baseUri + 'api/launch') -Headers $requestHeaders -ContentType 'application/json' -Body $applicationBody
        Assert-Equal $true $applicationResult.TestMode 'Application test-mode marker'
        Assert-Equal 'Application' $applicationResult.Type 'Application test-mode action type'
        Assert-Equal ([IO.Path]::GetFullPath((Join-Path $hurricaneTools 'Viewer.exe'))) $applicationResult.Executable 'Application test-mode executable'
        Assert-Equal '--safe' $applicationResult.Arguments[0] 'Application test-mode argument'
        Assert-Equal ([IO.Path]::GetFullPath($hurricaneRoot)) $applicationResult.WorkingDirectory 'Application test-mode working directory'

        $localhostOrigin = "http://localhost:$testPort"
        $localhostHeaders = @{ Origin = $localhostOrigin; 'X-Launcher-Token' = $token }
        $localhostResult = Invoke-RestMethod -Method Post -Uri ($baseUri + 'api/launch') -Headers $localhostHeaders -ContentType 'application/json' -Body $launchBody
        Assert-Equal $true $localhostResult.TestMode 'Localhost origin dispatch marker'

        $badTokenStatus = $null
        try {
            Invoke-RestMethod -Method Post -Uri ($baseUri + 'api/launch') -Headers @{ Origin = $origin } -ContentType 'application/json' -Body $launchBody | Out-Null
        }
        catch { if ($_.Exception.Response) { $badTokenStatus = [int]$_.Exception.Response.StatusCode } }
        Assert-Equal 403 $badTokenStatus 'Missing token rejection status'

        $wrongOriginStatus = $null
        try {
            Invoke-RestMethod -Method Post -Uri ($baseUri + 'api/launch') -Headers @{ Origin = 'http://example.invalid'; 'X-Launcher-Token' = $token } -ContentType 'application/json' -Body $launchBody | Out-Null
        }
        catch { if ($_.Exception.Response) { $wrongOriginStatus = [int]$_.Exception.Response.StatusCode } }
        Assert-Equal 403 $wrongOriginStatus 'Wrong origin rejection status'

        $unknownActionStatus = $null
        $unknownBody = @{ ProjectId = 'hurricane'; ActionId = 'playlist:not-configured' } | ConvertTo-Json
        try {
            Invoke-RestMethod -Method Post -Uri ($baseUri + 'api/launch') -Headers $requestHeaders -ContentType 'application/json' -Body $unknownBody | Out-Null
        }
        catch { if ($_.Exception.Response) { $unknownActionStatus = [int]$_.Exception.Response.StatusCode } }
        Assert-Equal 400 $unknownActionStatus 'Unknown action rejection status'
    }
    finally {
        if ($serverProcess -and -not $serverProcess.HasExited) {
            Stop-Process -Id $serverProcess.Id -Force
        }
    }

    $successFake = Join-Path $fixtureRoot 'fake-success.cmd'
    $failureFake = Join-Path $fixtureRoot 'fake-failure.cmd'
    $successFakeLines = @('@echo off', 'start "" /b /d "%TEMP%" cmd.exe /c "ping -n 6 127.0.0.1 >nul"', 'exit /b 0', '')
    [IO.File]::WriteAllText($successFake, [string]::Join("`r`n", $successFakeLines), [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText($failureFake, "@echo off`r`nexit /b 7`r`n", [Text.Encoding]::ASCII)
    $normalProjectsDirectory = Join-Path $configDirectory 'normal-projects'
    New-Item -ItemType Directory -Path $normalProjectsDirectory -Force | Out-Null
    $normalConfigPath = Join-Path $configDirectory 'normal-projects.json'
    $normalConfig = @{
        DefaultProject = 'success'
        Projects = @(
            @{ Id = 'success'; Config = 'normal-projects/success.json' }
            @{ Id = 'failure'; Config = 'normal-projects/failure.json' }
            @{ Id = 'application'; Config = 'normal-projects/application.json' }
        )
    }
    $normalTeaBoxCommon = @{
        Root = '../../Hurricane Project'
        PlaylistDirectory = '../../Hurricane Project/Playlists'
        PlaylistPrefix = 'Shared- '
    }
    $successProject = @{
        Name = 'Success fixture'
        Root = $normalTeaBoxCommon.Root
        TeaBox = @{
            Executable = '../../fake-success.cmd'
            PlaylistDirectory = $normalTeaBoxCommon.PlaylistDirectory
            PlaylistPrefix = $normalTeaBoxCommon.PlaylistPrefix
        }
    }
    $failureProject = @{
        Name = 'Failure fixture'
        Root = $normalTeaBoxCommon.Root
        TeaBox = @{
            Executable = '../../fake-failure.cmd'
            PlaylistDirectory = $normalTeaBoxCommon.PlaylistDirectory
            PlaylistPrefix = $normalTeaBoxCommon.PlaylistPrefix
        }
    }
    $appProbePath = Join-Path $fixtureRoot 'fake-app probe.ps1'
    $appOutputPath = Join-Path $fixtureRoot 'fake-app output.txt'
    $appProbeLines = @('param([string]$OutputPath)', '[IO.File]::WriteAllText($OutputPath, ''started'')', 'Start-Sleep -Seconds 3', '')
    [IO.File]::WriteAllText($appProbePath, [string]::Join("`r`n", $appProbeLines), [Text.UTF8Encoding]::new($false))
    $applicationProject = @{
        Name = 'Application fixture'
        Root = $normalTeaBoxCommon.Root
        Buttons = @(@{
            Id = 'probe'
            Label = 'Start harmless probe'
            Executable = Join-Path $PSHOME 'powershell.exe'
            Arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $appProbePath, '-OutputPath', $appOutputPath)
            WorkingDirectory = $fixtureRoot
        })
    }
    [IO.File]::WriteAllText((Join-Path $normalProjectsDirectory 'success.json'), ($successProject | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $normalProjectsDirectory 'failure.json'), ($failureProject | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $normalProjectsDirectory 'application.json'), ($applicationProject | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($normalConfigPath, ($normalConfig | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

    $normalPortProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $normalPortProbe.Start(); $normalPort = $normalPortProbe.LocalEndpoint.Port }
    finally { $normalPortProbe.Stop() }
    $normalStdout = Join-Path $fixtureRoot 'normal-server.stdout.log'
    $normalStderr = Join-Path $fixtureRoot 'normal-server.stderr.log'
    $normalArguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -ConfigPath "{1}" -Port {2} -NoOpenBrowser' -f $serverPath, $normalConfigPath, $normalPort
    $normalServer = Start-Process -FilePath 'powershell.exe' -ArgumentList $normalArguments -RedirectStandardOutput $normalStdout -RedirectStandardError $normalStderr -PassThru -WindowStyle Hidden
    try {
        $normalDeadline = (Get-Date).AddSeconds(15)
        $normalOutput = ''
        $normalUriMatch = [regex]::Match($normalOutput, $uriPattern)
        while ((Get-Date) -lt $normalDeadline -and -not $normalUriMatch.Success) {
            Start-Sleep -Milliseconds 150
            if (Test-Path -LiteralPath $normalStdout) {
                $normalOutput = Get-Content -LiteralPath $normalStdout -Raw
                if ($null -eq $normalOutput) { $normalOutput = '' }
            }
            if ($normalServer.HasExited) { throw "Normal launcher exited early: $(Get-Content -LiteralPath $normalStderr -Raw)" }
            $normalUriMatch = [regex]::Match($normalOutput, $uriPattern)
        }
        if (-not $normalUriMatch.Success) { throw "Normal launcher did not report its URL. Output: $normalOutput" }

        $normalBaseUri = $normalUriMatch.Groups[1].Value
        $normalOrigin = $normalBaseUri.TrimEnd('/')
        $normalPage = Invoke-WebRequest -UseBasicParsing -Uri $normalBaseUri
        $normalTokenMatch = [regex]::Match($normalPage.Content, 'window\.launcherToken = "(?<token>[a-f0-9-]+)"')
        if (-not $normalTokenMatch.Success) { throw 'Normal launcher page did not include its session token.' }
        $normalHeaders = @{ Origin = $normalOrigin; 'X-Launcher-Token' = $normalTokenMatch.Groups['token'].Value }
        $successBody = @{ ProjectId = 'success'; ActionId = 'playlist:0 - Setup' } | ConvertTo-Json
        $successTimer = [Diagnostics.Stopwatch]::StartNew()
        $successResult = Invoke-RestMethod -Method Post -Uri ($normalBaseUri + 'api/launch') -Headers $normalHeaders -ContentType 'application/json' -Body $successBody
        $successTimer.Stop()
        if ($successTimer.Elapsed.TotalSeconds -ge 3) { throw "Launcher waited for the child process ($([math]::Round($successTimer.Elapsed.TotalSeconds, 1))s)." }
        Assert-Equal $false $successResult.TestMode 'Normal success mode marker'
        Assert-Equal $true $successResult.Success 'Zero exit code success result'
        Assert-Equal 0 $successResult.ExitCode 'Zero exit code result'

        $failureBody = @{ ProjectId = 'failure'; ActionId = 'playlist:0 - Setup' } | ConvertTo-Json
        $failureResult = Invoke-RestMethod -Method Post -Uri ($normalBaseUri + 'api/launch') -Headers $normalHeaders -ContentType 'application/json' -Body $failureBody
        Assert-Equal $false $failureResult.Success 'Nonzero exit code failure result'
        Assert-Equal 7 $failureResult.ExitCode 'Nonzero exit code value'
        if ($failureResult.Message -notlike '*exited with code 7*') { throw "Failure result message was unclear: $($failureResult.Message)" }

        $applicationBody = @{ ProjectId = 'application'; ActionId = 'button:probe' } | ConvertTo-Json
        $applicationTimer = [Diagnostics.Stopwatch]::StartNew()
        $applicationResult = Invoke-RestMethod -Method Post -Uri ($normalBaseUri + 'api/launch') -Headers $normalHeaders -ContentType 'application/json' -Body $applicationBody
        $applicationTimer.Stop()
        if ($applicationTimer.Elapsed.TotalSeconds -ge 2) { throw "Application launch waited for its process ($([math]::Round($applicationTimer.Elapsed.TotalSeconds, 1))s)." }
        Assert-Equal $false $applicationResult.TestMode 'Normal application mode marker'
        Assert-Equal $true $applicationResult.Success 'Application start success result'
        if (-not $applicationResult.ProcessId) { throw 'Application launch did not return its process ID.' }
        Wait-Process -Id $applicationResult.ProcessId -Timeout 8 -ErrorAction Stop
        Assert-Equal 'started' (Get-Content -LiteralPath $appOutputPath -Raw) 'Harmless application process ran'
    }
    finally {
        foreach ($launchResult in @($successResult, $failureResult, $applicationResult)) {
            if ($launchResult -and $launchResult.ProcessId) {
                try { Wait-Process -Id $launchResult.ProcessId -Timeout 5 -ErrorAction Stop } catch { }
            }
        }
        if ($normalServer -and -not $normalServer.HasExited) {
            Stop-Process -Id $normalServer.Id -Force
        }
    }

    Write-Output 'All launcher tests passed.'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
