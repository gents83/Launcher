# Project Launcher

A .NET 10 WPF desktop launcher with an embedded WebView2 dashboard for project-specific application buttons and optional TeaBox playlists. Project configuration stays external in the Perforce-synced config tree; the published app package does not contain project or theme JSON files.

## Requirements and entry point

The app runs on Windows x64 and requires the Microsoft Edge WebView2 Evergreen Runtime. Building from source requires the .NET 10 SDK. Published app versions are self-contained for `win-x64`; WebView2 remains a machine prerequisite.

The supported entry point is **`LAUNCHER.exe`** in the deployment's synchronized Perforce `Launcher` folder. The bootstrapper reads the adjacent `launcher.json`, checks the configured unsigned NAS/Artifactory feed, and starts the newest healthy app version from `%LOCALAPPDATA%\ProjectLauncher\deployments\<DeploymentId>`. That per-user cache is used for safe staging and rollback; the Perforce folder is not modified. Do not run a versioned `Launcher.App.exe` directly, because that bypasses update checks.

After deployment, the Perforce `Launcher` folder contains the complete `perforce-launcher/` payload: `LAUNCHER.exe`, its bootstrapper runtime files under `deps/`, `launcher.json`, the unsigned schema-v2 `seed-feed`, the master `projects.json`, only its referenced child project JSON files, and optional `theme.json` beside the index. The generated bundle does not include project executables, artwork, workspace data, or other assets referenced by those JSON files; ensure they are available at their configured paths. `launcher.json` points to the master index using a relative path. The app reads the Perforce-synced files at startup; after syncing config changes, start the launcher again or use **Refresh** to reload them. The launcher never runs `p4 sync`.

NAS/Artifactory updates contain app binaries only. Project/theme JSON remains Perforce-owned and is never replaced by an app update. NAS access uses the user's Windows identity. For Artifactory, the feed uses HTTPS and the user's read token must already be in Windows Credential Manager; never put tokens in Perforce. If the update feed is offline, the unsigned Perforce seed or last healthy cached version is used.

## Test mode

To inspect the published UI without starting configured applications or playlists, pass a temporary fixture index:

```bat
output\app\Launcher.App.exe --test-mode --config-index "C:\path\to\temporary\projects.json"
```

Test mode displays **TEST MODE** results for clicked actions and never starts them. Use a temporary fixture config for smoke checks, not a real project index.

## Build and package

Run `dotnet test Launcher.sln` to execute the .NET tests.

### App-only developer build

`build.bat` is a thin wrapper around `build.ps1`. It publishes only `Launcher.App` to `output\app`; it does not create an update feed.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Version 1.0.1
```

`-Version` defaults to `1.0.0`. A stable `major.minor.patch` version maps to `major.minor.patch.0` (for example, `1.0.1` becomes `1.0.1.0`). The package is self-contained for `win-x64`, excludes project configuration and Perforce files, and requires WebView2 Evergreen Runtime. Check it with `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\test-csharp-publish.ps1 -OutputPath .\output\app`.

### Perforce launcher and update-feed bundles

Release bundle generation must run on Windows for WPF `win-x64` publishing and handle-bound configuration validation.

Set one `major.minor.patch` value in tracked [`version.txt`](version.txt) (currently `1.0.0`), bump it for the next release, then run `build-release.bat`. One invocation creates a folder to merge into the Perforce Launcher folder and an unsigned schema-v2 app-update feed. `-Version` may be passed to override the file for a one-off build. Unless `-OutputDirectory` is supplied, output goes to `output\release-<version>`:

```text
output/release-1.2.3/
  perforce-launcher/  # submit this complete payload into the Perforce Launcher folder
    LAUNCHER.exe
    launcher.json
    deps/
      Launcher.Bootstrapper.exe
      ...bootstrapper runtime files...
    seed-feed/stable/latest.json
    seed-feed/stable/packages/Launcher-1.2.3-win-x64.zip
    projects.json     # master config index
    projects/
      <referenced-project>.json
    theme.json        # optional; beside projects.json
  update-bundle/      # copy separately to NAS or upload to Artifactory
    stable/latest.json
    stable/packages/Launcher-1.2.3-win-x64.zip
```

Both feeds contain only `stable/latest.json` and the immutable `stable/packages/Launcher-<version>-win-x64.zip`; no `.sig` file or `.test-key-only` marker is generated. The schema-v2 `latest.json` contains these fields:

- `SchemaVersion`: integer `2`.
- `Channel`: feed channel, normally `stable`.
- `Version`: release version.
- `RuntimeIdentifier`: `win-x64`.
- `PackagePath`: package path relative to the channel directory, for example `packages/Launcher-1.2.3-win-x64.zip`.
- `PackageBytes`: package size in bytes.
- `PackageSha256`: hexadecimal SHA-256 of the package.

**Trust boundary:** SHA-256 verifies package integrity against the manifest; it does not authenticate the publisher. The trust model relies on HTTPS transport for Artifactory, Windows identity authentication for NAS, feed write ACLs, and Perforce access control.

#### Perforce deployment and updates

For a fresh deployment, place and submit the complete generated `perforce-launcher/` folder in the depot's `Launcher` location through the normal Perforce workflow, including the complete `deps/` directory. Separately ensure project executables, artwork, workspace data, and other assets referenced by the included JSON are available at their configured paths; those assets remain outside the release bundle. Copy the contents of `update-bundle/` separately to the NAS feed root or Artifactory repository.

For an existing deployment, compare the generated `perforce-launcher/` payload with the current depot contents and review every generated descriptor/config diff. Preserve locally customized `launcher.json`—especially `DeploymentId`, `ConfigIndex`, and `UpdateSource`—along with `projects.json`, child project JSON, and `theme.json` unless intentionally replacing them. Submit approved descriptor, config, and runtime changes through the usual Perforce review/submit workflow; users continue to receive config changes through normal Perforce sync. App updates remain automatic through the unsigned feed and do not replace Perforce configuration.

The generated `launcher.json` selects the feed and the relative path to `projects.json`. Set `-DeploymentId` to the stable ID for that deployment; it scopes that user's cache. Set `-ConfigIndex` only when the master index is not named `projects.json` at the Perforce folder root.

`UpdateFeedBase` is the **repository/share root before the channel folder**. For NAS, use a UNC root such as `\\server\share\launcher-feed`; the updater reads `\\server\share\launcher-feed\stable\latest.json`. For Artifactory, use the HTTPS generic-repository base URL without `/stable`, credentials, query, or fragment. The selected `Channel` is appended in either case. NAS access uses the user's Windows identity. Artifactory read tokens must already be in Windows Credential Manager; release maintainers need write/deploy permission.

For release configuration, copy `scripts/release.defaults.example.json` to the Git-ignored `scripts/release.defaults.json` and configure its `UpdateSource` and optional `Publish` sections. The example exposes only those two sections. `UpdateSource` is required to generate the deployment's `launcher.json`; configuring `Publish` does not publish by itself. With local defaults configured, `build-release.bat` reads `version.txt` and creates the bundles without publishing. `-DeploymentId` defaults to `project-launcher`; use a distinct, stable ID for each separately configured deployment.

Publishing is opt-in. Add `-Publish` to publish only `update-bundle` to the configured NAS/Artifactory target, or supply both `-Provider` and `-Destination` to override it. The publisher writes the versioned package first and `stable/latest.json` last; a rejected package upload stops publication before the manifest is sent. The NAS FileShare publisher rejects an existing package path. Artifactory's generic-repository publisher uses HTTP PUT and cannot independently guarantee create-only package writes ([Deploy Artifact API](https://docs.jfrog.com/artifactory/reference/deployArtifact)). Before publishing to production Artifactory, configure and verify repository permissions: grant release maintainers `Deploy/Cache` on `stable/packages/**` but withhold `Delete/Overwrite` there, and use a separate narrowly scoped permission target for `stable/latest.json` that allows overwrite. Ensure no broader target grants those maintainers overwrite access to package paths. See [JFrog permission documentation](https://docs.jfrog.com/administration/docs/permissions). This is a deployment prerequisite; it does not assert that any live repository is configured. The batch tool never submits files to Perforce; use the deployment workflow above for Perforce changes.

## 1. Fill in `projects.json`

`projects.json` is the master index. It selects the default project and points to each project's JSON file. `Config` paths are relative to the folder containing `projects.json`:

```json
{
  "DefaultProject": "hurricane",
  "Theme": "dark",
  "Projects": [
    {
      "Id": "hurricane",
      "Config": "projects/hurricane.json"
    },
    {
      "Id": "sandstorm",
      "Config": "projects/sandstorm.json"
    }
  ]
}
```

Master fields:

- `DefaultProject` (required): the `Id` of the project selected when the page opens.
- `Projects` (required, nonempty array): the projects available in the selector.
- `Projects[].Id` (required): a stable, unique identifier. Use the same value for `DefaultProject` when selecting it as the default.
- `Projects[].Config` (required): path to that project's JSON file, relative to `projects.json`.
- `Theme` (optional): initial launcher theme, either `"dark"` (default) or `"light"`. The theme button switches between modes and remembers the choice in that browser; its saved choice takes precedence over this default.

### Edit theme colors

`theme.json` sits beside `projects.json` and defines the dark and light palettes. Edit known token values to customize the page; values may be CSS colors or paint values such as gradients and shadows. Omitted tokens keep the built-in CSS value.

```json
{
  "dark": {
    "ink": "#f3f4f1",
    "canvas": "#090b10",
    "xbox-green": "#9bf00b"
  },
  "light": {
    "ink": "#1d252b",
    "canvas": "#f4f6f8",
    "xbox-green": "#107c10"
  }
}
```

Token names correspond to the CSS variables in `src/Launcher.App/wwwroot/index.html` without the leading `--`. Supported names are `ink`, `muted`, `canvas`, `panel`, `tile`, `rule`, `xbox-green`, `xbox-glow`, `error`, `success`, `page-background`, `backdrop-scrim`, `brand-ink`, `badge-background`, `workspace-background`, `workspace-shadow`, `field-background`, `tile-background`, `tile-shadow`, `tile-hover-background`, `tile-hover-border`, `tile-hover-shadow`, `art-border`, `index-border`, `index-background`, `empty-background`, `empty-border`, `dialog-background`, `dialog-shadow`, `dialog-scrim`, `close-background`, and `footer-ink`. Each palette must include at least one known token. If `theme.json` is missing, malformed, or contains an unknown token, the launcher keeps its built-in palettes. Click **Refresh** after editing the file.

To add a project, create its per-project JSON file, then add an `{ "Id", "Config" }` entry to `Projects`. Use a different ID for each project. If a project JSON is missing or invalid, the launcher shows an error for that project while leaving the other projects available.

## 2. Fill in each project JSON

The project JSON contains that project's name and root, custom application buttons, optional TeaBox playlist discovery, and artwork. Filesystem paths in this file are relative to the directory containing this project JSON; artwork fields may also use absolute HTTP(S) URLs.

Example `projects/hurricane.json`:

```json
{
  "Name": "Hurricane",
  "Root": "../../project/Hurricane",
  "BackgroundImage": "../../project/Hurricane/Data/Images/Hurricane.png",
  "Buttons": [
    {
      "Id": "open-editor",
      "Label": "Open Editor",
      "Category": "Development",
      "Executable": "{ProjectRoot}/Tools/Editor.exe",
      "Icon": "../../project/Hurricane/Data/Images/Editor.png",
      "Arguments": [
        "--project",
        "{ProjectRoot}/Hurricane.uproject"
      ],
      "WorkingDirectory": "{ProjectRoot}"
    }
  ],
  "TeaBox": {
    "Executable": "../../project/Hurricane/Tools/Teabox/TeaBox.com",
    "PlaylistDirectory": "../../project/Hurricane/Tools/Teabox/Playlists/Hurricane",
    "PlaylistPrefix": "Shared- "
  },
  "PlaylistImages": {
    "0 - Setup": "../../project/Hurricane/Data/Images/setup.png"
  }
}
```

A hosted image can be used in any artwork field, for example `"BackgroundImage": "https://example.com/background.png?width=1200"`. Query strings are preserved.

Required project fields:

- `Name` (string): project name shown in the selector.
- `Root` (path): project or workspace root. It is also the default working directory for application buttons.

Optional project fields:

- `Buttons` (array): application buttons shown in the dashboard. Omit it or use `[]` if there are no custom buttons.
- `TeaBox` (object): enables automatic discovery of TeaBox playlists. Omit it for a custom-button-only project.
- `BackgroundImage` (path or HTTP(S) URL): image used as the project's background.
- `PlaylistImages` (object): maps exact playlist basenames, without `.playlist`, to tile-image paths or HTTP(S) URLs. This is a project-level field beside `Buttons` and `TeaBox`.

### Application button fields

Each object in `Buttons` has:

- `Id` (required string): unique within that project's button list and used to identify the action.
- `Label` (required string): text shown on the button.
- `Category` (optional string): section title for the button. Buttons with the same category are grouped together; missing or blank values use `Applications`.
- `Executable` (required path): the local executable to start.
- `Arguments` (optional array of strings): command-line arguments in order. Use `[]` or omit this field for no arguments.
- `WorkingDirectory` (optional path): directory to use when starting the executable. If omitted, `Root` is used.
- `Icon` (optional path or HTTP(S) URL): image shown on that application tile instead of the `APP` fallback. Set one `Icon` value inside each `Buttons` entry that should have artwork.

`Executable` and `WorkingDirectory` may be absolute paths. Relative executable, working-directory, and local artwork paths resolve from the project JSON. Artwork URLs must be absolute HTTP(S) URLs. The launcher starts configured executables directly, without running arbitrary shell commands. Application buttons return as soon as the process starts; they do not wait for it to exit.

### Path placeholders

In an application's `Executable`, each `Arguments` entry, and `WorkingDirectory`, you may use:

- `{ProjectRoot}` — the resolved `Root` path.
- `{ProjectConfigDirectory}` — the directory containing the per-project JSON.

For example, `"{ProjectRoot}/Tools/Editor.exe"` uses the project's root even if the workspace is moved. Placeholders are replaced before the program starts. Arguments are an array so spaces and quotes can be passed as part of an individual argument.

### Optional TeaBox settings

When `TeaBox` is present, all of these fields are required:

- `Executable`: path to that project's `TeaBox.com`.
- `PlaylistDirectory`: directory containing `.playlist` files.
- `PlaylistPrefix`: text prepended to a playlist basename for TeaBox's `RunPlaylist -playlist` value. It may be an empty string.

The launcher finds `.playlist` files in that directory and creates one button per file. The filename without `.playlist` is the button label. For example, `0 - Setup.playlist` with prefix `Shared- ` runs the TeaBox playlist `Shared- 0 - Setup`. Application buttons and discovered playlist buttons can coexist in the same project. Application buttons appear in category sections in the order each category first appears in `Buttons`; TeaBox playlists appear together in a separate **TeaBox playlists** section.

`PlaylistImages` is outside the `TeaBox` object. Its keys must exactly match playlist basenames, such as `"0 - Setup"`. Each configured image replaces the playlist number on that tile; if the image is missing, the number remains.

## Artwork and path rules

Local project-file paths—including `Root`, `BackgroundImage`, `PlaylistImages` values, `TeaBox` paths, `Buttons[].Executable`, `Buttons[].WorkingDirectory`, and `Buttons[].Icon`—resolve relative to that project's JSON. The master `Config` path alone resolves relative to `projects.json`. Relative paths are recommended so the workspace can move together; absolute paths are also accepted. Image URLs must be absolute HTTP(S) URLs and are used as-is, including their query strings.

BMP, PNG, JPG/JPEG, and WebP files are supported for local backgrounds, playlist tiles, and application icons. Local images are served only from explicitly configured paths through the launcher. HTTP(S) artwork URLs load directly in the browser; the launcher does not proxy or cache them. Remote image hosts must be reachable from the browser, and inaccessible or non-image URLs use the existing background or tile fallback.

Project files are trusted local configuration. Add a button only when you intend its executable to run after a user clicks it. The browser submits only project and action IDs; it cannot supply executable paths or arguments, and the launcher never starts an action when the page loads.
