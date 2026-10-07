[CmdletBinding()]
param(
    [string]$ConfigPath,
    [ValidateRange(0, 65535)][int]$Port = 0,
    [switch]$TestMode,
    [switch]$NoOpenBrowser
)

$ErrorActionPreference = 'Stop'
$launcherRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($launcherRoot)) {
    $launcherRoot = $ScriptRoot
}
if ([string]::IsNullOrWhiteSpace($launcherRoot)) {
    throw 'Could not determine the launcher folder.'
}
$corePath = Join-Path $launcherRoot 'launcher.core.ps1'
. $corePath

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $launcherRoot 'projects.json'
}
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
$indexPath = Join-Path $launcherRoot 'index.html'
$sessionToken = [guid]::NewGuid().ToString('N')

function Get-AvailablePort {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $probe.Start()
        return $probe.LocalEndpoint.Port
    }
    finally {
        $probe.Stop()
    }
}

function Write-JsonResponse {
    param($Context, [int]$StatusCode, $Value)

    $json = ConvertTo-Json -InputObject $Value -Depth 8 -Compress
    Write-TextResponse -Context $Context -StatusCode $StatusCode -ContentType 'application/json; charset=utf-8' -Text $json
}

function Write-TextResponse {
    param($Context, [int]$StatusCode, [string]$ContentType, [string]$Text)

    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    $response = $Context.Response
    try {
        $response.StatusCode = $StatusCode
        $response.ContentType = $ContentType
        $response.ContentLength64 = $bytes.Length
        $response.Headers['Cache-Control'] = 'no-store'
        $response.Headers['X-Content-Type-Options'] = 'nosniff'
        $response.Headers['Referrer-Policy'] = 'no-referrer'
        $response.Headers['X-Frame-Options'] = 'DENY'
        if ($Context.Request.HttpMethod -ne 'HEAD' -and $bytes.Length -gt 0) {
            $response.OutputStream.Write($bytes, 0, $bytes.Length)
        }
    }
    finally {
        $response.Close()
    }
}

function Get-ArtworkUrl {
    param([string]$ProjectId, [string]$Kind, [string]$PlaylistId, [string]$ButtonId)

    $url = '/api/artwork?projectId={0}&kind={1}' -f [Uri]::EscapeDataString($ProjectId), [Uri]::EscapeDataString($Kind)
    if ($Kind -eq 'playlist') {
        $url += '&playlistId={0}' -f [Uri]::EscapeDataString($PlaylistId)
    }
    elseif ($Kind -eq 'button') {
        $url += '&buttonId={0}' -f [Uri]::EscapeDataString($ButtonId)
    }
    $url
}

function Get-ArtworkContentType {
    param([Parameter(Mandatory = $true)][string]$Path)

    switch ([IO.Path]::GetExtension($Path).ToLowerInvariant()) {
        '.png' { 'image/png'; break }
        '.jpg' { 'image/jpeg'; break }
        '.jpeg' { 'image/jpeg'; break }
        '.webp' { 'image/webp'; break }
        '.bmp' { 'image/bmp'; break }
        default { throw 'Unsupported artwork image type.' }
    }
}

function Write-BinaryResponse {
    param($Context, [int]$StatusCode, [string]$ContentType, [byte[]]$Bytes)

    $response = $Context.Response
    try {
        $response.StatusCode = $StatusCode
        $response.ContentType = $ContentType
        $response.ContentLength64 = $Bytes.Length
        $response.Headers['Cache-Control'] = 'no-store'
        $response.Headers['X-Content-Type-Options'] = 'nosniff'
        $response.Headers['Referrer-Policy'] = 'no-referrer'
        $response.Headers['X-Frame-Options'] = 'DENY'
        if ($Context.Request.HttpMethod -ne 'HEAD' -and $Bytes.Length -gt 0) {
            $response.OutputStream.Write($Bytes, 0, $Bytes.Length)
        }
    }
    finally {
        $response.Close()
    }
}

function Get-ThemeColors {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }

    $allowedTokens = @(
        'ink', 'muted', 'canvas', 'panel', 'tile', 'rule', 'xbox-green', 'xbox-glow', 'error', 'success',
        'page-background', 'backdrop-scrim', 'brand-ink', 'badge-background', 'workspace-background', 'workspace-shadow',
        'field-background', 'tile-background', 'tile-shadow', 'tile-hover-background', 'tile-hover-border', 'tile-hover-shadow',
        'art-border', 'index-border', 'index-background', 'empty-background', 'empty-border', 'dialog-background',
        'dialog-shadow', 'dialog-scrim', 'close-background', 'footer-ink'
    )

    try {
        $document = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($document -isnot [System.Management.Automation.PSCustomObject]) { throw 'Theme config must be an object.' }

        $themes = [ordered]@{}
        foreach ($themeName in @('dark', 'light')) {
            $themeProperty = $document.PSObject.Properties[$themeName]
            if (-not $themeProperty -or $themeProperty.Value -isnot [System.Management.Automation.PSCustomObject]) {
                throw "Theme config requires a '$themeName' object."
            }

            $palette = [ordered]@{}
            foreach ($property in $themeProperty.Value.PSObject.Properties) {
                if ($allowedTokens -cnotcontains $property.Name) { throw "Unknown theme token: $($property.Name)" }
                if ($property.Value -isnot [string] -or [string]::IsNullOrWhiteSpace($property.Value)) {
                    throw "Theme token '$($property.Name)' must be a nonempty string."
                }
                $palette[$property.Name] = [string]$property.Value
            }
            if ($palette.Count -eq 0) { throw "Theme '$themeName' must define at least one token." }
            $themes[$themeName] = $palette
        }
        foreach ($property in $document.PSObject.Properties) {
            if ($property.Name -notin @('dark', 'light')) { throw "Unknown theme group: $($property.Name)" }
        }
        return $themes
    }
    catch {
        return $null
    }
}

function Get-ApiCatalog {
    $config = Read-LauncherConfig -ConfigPath $ConfigPath
    $themePath = Join-Path (Split-Path -Parent $ConfigPath) 'theme.json'
    $themeColors = Get-ThemeColors -Path $themePath
    $projects = @(
        foreach ($project in $config.Projects) {
            $actions = @()
            $projectError = $project.Error
            if (-not $projectError) {
                try {
                    $actions = @(Get-ActionCatalog -Project $project | ForEach-Object {
                        $imageUrl = $null
                        if ($_.Type -eq 'TeaBoxPlaylist') {
                            $artworkSource = Get-ConfiguredArtworkSource -Project $project -Kind 'playlist' -PlaylistId $_.PlaylistId
                            if ($artworkSource) {
                                if ($artworkSource.Type -eq 'Remote') {
                                    $imageUrl = $artworkSource.Value
                                }
                                else {
                                    $imageUrl = Get-ArtworkUrl -ProjectId $project.Id -Kind 'playlist' -PlaylistId $_.PlaylistId
                                }
                            }
                        }
                        elseif ($_.Type -eq 'Application') {
                            $artworkSource = Get-ConfiguredArtworkSource -Project $project -Kind 'button' -ButtonId $_.Button.Id
                            if ($artworkSource) {
                                if ($artworkSource.Type -eq 'Remote') {
                                    $imageUrl = $artworkSource.Value
                                }
                                else {
                                    $imageUrl = Get-ArtworkUrl -ProjectId $project.Id -Kind 'button' -ButtonId $_.Button.Id
                                }
                            }
                        }
                        [pscustomobject]@{
                            Id = $_.Id
                            Name = $_.Name
                            Type = $_.Type
                            Category = $_.Category
                            ArtworkUrl = $imageUrl
                        }
                    })
                }
                catch {
                    $projectError = $_.Exception.Message
                }
            }

            $backgroundUrl = $null
            if (-not $project.Error) {
                $backgroundSource = Get-ConfiguredArtworkSource -Project $project -Kind 'background'
                if ($backgroundSource) {
                    if ($backgroundSource.Type -eq 'Remote') {
                        $backgroundUrl = $backgroundSource.Value
                    }
                    else {
                        $backgroundUrl = Get-ArtworkUrl -ProjectId $project.Id -Kind 'background'
                    }
                }
            }
            [pscustomobject]@{
                Id = $project.Id
                Name = $project.Name
                Actions = $actions
                Error = $projectError
                BackgroundImageUrl = $backgroundUrl
            }
        }
    )

    [pscustomobject]@{
        DefaultProject = $config.DefaultProject
        Theme = $config.Theme
        ThemeColors = $themeColors
        Projects = $projects
    }
}

function Handle-Request {
    param($Context, [string[]]$AllowedOrigins)

    $request = $Context.Request
    $path = $request.Url.AbsolutePath.TrimEnd('/')
    if (-not $path) { $path = '/' }

    if ($request.HttpMethod -in @('GET', 'HEAD') -and $path -eq '/') {
        if (-not (Test-Path -LiteralPath $indexPath -PathType Leaf)) {
            Write-JsonResponse $Context 500 @{ Error = 'Launcher page is missing.' }
            return
        }
        $html = Get-Content -LiteralPath $indexPath -Raw -Encoding UTF8
        $html = $html.Replace('__LAUNCHER_TOKEN__', $sessionToken)
        Write-TextResponse $Context 200 'text/html; charset=utf-8' $html
        return
    }

    if ($request.HttpMethod -in @('GET', 'HEAD') -and $path -eq '/api/catalog') {
        try {
            Write-JsonResponse $Context 200 (Get-ApiCatalog)
        }
        catch {
            Write-JsonResponse $Context 500 @{ Error = $_.Exception.Message }
        }
        return
    }

    if ($request.HttpMethod -in @('GET', 'HEAD') -and $path -eq '/api/artwork') {
        $allowedQueryNames = @('projectId', 'kind', 'playlistId', 'buttonId')
        $hasUnexpectedQuery = $false
        foreach ($key in $request.QueryString.AllKeys) {
            if ($key -and $allowedQueryNames -notcontains $key) {
                $hasUnexpectedQuery = $true
                break
            }
        }
        if ($hasUnexpectedQuery) {
            Write-JsonResponse $Context 404 @{ Error = 'Artwork not found.' }
            return
        }

        $artworkPath = $null
        try {
            $config = Read-LauncherConfig -ConfigPath $ConfigPath
            $projectId = $request.QueryString['projectId']
            $kind = $request.QueryString['kind']
            $playlistId = $request.QueryString['playlistId']
            $buttonId = $request.QueryString['buttonId']
            $project = $config.Projects | Where-Object { $_.Id -ceq $projectId } | Select-Object -First 1
            if ($project -and -not $project.Error) {
                if ($kind -eq 'background' -and [string]::IsNullOrWhiteSpace($playlistId) -and [string]::IsNullOrWhiteSpace($buttonId)) {
                    $artworkPath = Get-ConfiguredArtworkPath -Project $project -Kind 'background'
                }
                elseif ($kind -eq 'playlist' -and -not [string]::IsNullOrWhiteSpace($playlistId) -and [string]::IsNullOrWhiteSpace($buttonId)) {
                    $resolved = Resolve-PlaylistRequest -Projects $config.Projects -ProjectId $projectId -PlaylistId $playlistId
                    $artworkPath = Get-ConfiguredArtworkPath -Project $resolved.Project -Kind 'playlist' -PlaylistId $resolved.Playlist.Id
                }
                elseif ($kind -eq 'button' -and -not [string]::IsNullOrWhiteSpace($buttonId) -and [string]::IsNullOrWhiteSpace($playlistId)) {
                    $button = $project.Buttons | Where-Object { $_.Id -ceq $buttonId } | Select-Object -First 1
                    if ($button) {
                        $artworkPath = Get-ConfiguredArtworkPath -Project $project -Kind 'button' -ButtonId $buttonId
                    }
                }
            }
            if ($artworkPath) {
                $contentType = Get-ArtworkContentType -Path $artworkPath
                $artworkBytes = [IO.File]::ReadAllBytes($artworkPath)
            }
        }
        catch {
            $artworkPath = $null
        }

        if (-not $artworkPath) {
            Write-JsonResponse $Context 404 @{ Error = 'Artwork not found.' }
            return
        }
        Write-BinaryResponse -Context $Context -StatusCode 200 -ContentType $contentType -Bytes $artworkBytes
        return
    }

    if ($request.HttpMethod -eq 'POST' -and $path -eq '/api/launch') {
        if ($AllowedOrigins -notcontains $request.Headers['Origin'] -or $request.Headers['X-Launcher-Token'] -ne $sessionToken) {
            Write-JsonResponse $Context 403 @{ Error = 'Request origin or launcher token is invalid.' }
            return
        }
        if ($request.ContentLength64 -gt 8192) {
            Write-JsonResponse $Context 413 @{ Error = 'Launch request is too large.' }
            return
        }
        if ($request.ContentType -notlike 'application/json*') {
            Write-JsonResponse $Context 415 @{ Error = 'Launch requests must use application/json.' }
            return
        }

        try {
            $reader = [IO.StreamReader]::new($request.InputStream, [Text.Encoding]::UTF8)
            try { $body = $reader.ReadToEnd() | ConvertFrom-Json }
            finally { $reader.Dispose() }
            if (-not ($body.ProjectId -is [string]) -or -not ($body.ActionId -is [string])) {
                throw 'Launch request requires string ProjectId and ActionId values.'
            }
            $resolved = Resolve-ActionRequest -Projects (Read-LauncherConfig -ConfigPath $ConfigPath).Projects -ProjectId $body.ProjectId -ActionId $body.ActionId
            if ($resolved.Type -eq 'Application') {
                $launchSpec = Resolve-ButtonLaunchSpec -Project $resolved.Project -Button $resolved.Button
                $commandLineArguments = Join-WindowsCommandLineArguments -Arguments $launchSpec.Arguments
            }
            else {
                $arguments = Get-TeaBoxArguments -Project $resolved.Project -Playlist $resolved.Playlist
            }
        }
        catch {
            Write-JsonResponse $Context 400 @{ Error = $_.Exception.Message }
            return
        }

        if ($TestMode) {
            if ($resolved.Type -eq 'Application') {
                Write-JsonResponse $Context 200 @{
                    TestMode = $true
                    Type = $resolved.Type
                    Project = $resolved.Project.Name
                    Action = $resolved.Button.Label
                    Executable = $launchSpec.Executable
                    Arguments = @($launchSpec.Arguments)
                    WorkingDirectory = $launchSpec.WorkingDirectory
                    Message = 'Test mode: application was not started.'
                }
            }
            else {
                Write-JsonResponse $Context 200 @{
                    TestMode = $true
                    Type = $resolved.Type
                    Project = $resolved.Project.Name
                    Playlist = $resolved.Playlist.Name
                    Executable = $resolved.Project.TeaBox.Executable
                    Arguments = $arguments
                    Message = 'Test mode: TeaBox was not launched.'
                }
            }
            return
        }

        if ($resolved.Type -eq 'Application') {
            try {
                $startInfo = [Diagnostics.ProcessStartInfo]::new()
                $startInfo.FileName = $launchSpec.Executable
                $startInfo.Arguments = $commandLineArguments
                $startInfo.WorkingDirectory = $launchSpec.WorkingDirectory
                $startInfo.UseShellExecute = $false
                $process = [Diagnostics.Process]::Start($startInfo)
                if (-not $process) { throw 'The application process did not start.' }
                $processId = $process.Id
                $process.Dispose()
                Write-JsonResponse $Context 200 @{
                    TestMode = $false
                    Success = $true
                    Type = $resolved.Type
                    ProcessId = $processId
                    Message = "$($resolved.Project.Name): $($resolved.Button.Label) started successfully."
                }
            }
            catch {
                Write-JsonResponse $Context 500 @{ Error = "Could not start application: $($_.Exception.Message)" }
            }
            return
        }

        try {
            $process = Start-Process -FilePath $resolved.Project.TeaBox.Executable -ArgumentList $arguments -WorkingDirectory $resolved.Project.Root -PassThru -ErrorAction Stop
            # Wait for TeaBox.com itself, not child editor/game processes.
            Wait-Process -InputObject $process -ErrorAction Stop
            $process.Refresh()
            $exitCode = [int]$process.ExitCode
            $success = $exitCode -eq 0
            if ($success) {
                $message = "$($resolved.Project.Name): $($resolved.Playlist.Name) completed successfully."
            }
            else {
                $message = "Teabox exited with code $exitCode while running $($resolved.Playlist.Name)."
            }
            Write-JsonResponse $Context 200 @{
                TestMode = $false
                Success = $success
                Type = $resolved.Type
                ExitCode = $exitCode
                ProcessId = $process.Id
                Message = $message
            }
        }
        catch {
            Write-JsonResponse $Context 500 @{ Error = "Could not start Teabox: $($_.Exception.Message)" }
        }
        return
    }

    Write-JsonResponse $Context 404 @{ Error = 'Route not found.' }
}

$listener = [Net.HttpListener]::new()
$started = $false
if ($Port -gt 0) {
    $baseUri = "http://127.0.0.1:$Port/"
    $listener.Prefixes.Add($baseUri)
    try {
        $listener.Start()
        $started = $true
    }
    catch {
        $listener.Close()
        throw "Could not start the local launcher on port ${Port}: $($_.Exception.Message)"
    }
}
else {
    for ($attempt = 0; $attempt -lt 10 -and -not $started; $attempt++) {
        $port = Get-AvailablePort
        $baseUri = "http://127.0.0.1:$port/"
        $listener.Prefixes.Clear()
        $listener.Prefixes.Add($baseUri)
        try {
            $listener.Start()
            $started = $true
        }
        catch {
            if ($attempt -eq 9) { throw "Could not start the local launcher: $($_.Exception.Message)" }
        }
    }
}

$origin = $baseUri.TrimEnd('/')
$portNumber = ([Uri]$baseUri).Port
$allowedOrigins = @($origin, "http://localhost:$portNumber")
[Console]::WriteLine("Listening at $baseUri")
[Console]::Out.Flush()
if (-not $NoOpenBrowser) {
    Start-Process -FilePath $baseUri
}

try {
    while ($listener.IsListening) {
        try {
            $context = $listener.GetContext()
            Handle-Request -Context $context -AllowedOrigins $allowedOrigins
        }
        catch [Net.HttpListenerException] {
            if ($listener.IsListening) { throw }
        }
        catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            if ($context -and -not $context.Response.OutputStream.CanWrite) { continue }
            if ($context) {
                try { Write-JsonResponse $context 500 @{ Error = 'Unexpected launcher error.' } } catch { }
            }
        }
    }
}
finally {
    if ($listener.IsListening) { $listener.Stop() }
    $listener.Close()
}
