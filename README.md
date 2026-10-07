# Project Launcher

A local HTML launcher for project-specific application buttons and optional TeaBox playlists. Each project has its own JSON config. Choose a project to see its configured actions, then click a button to start it.

## Start and stop

Double-click `start-launcher.cmd`. It opens the launcher in your default browser and keeps a PowerShell console open for the local server. Press **Ctrl+C** in that console to stop the server. Keep the console open while using the page. The helper picks an available port by default; pass `-Port 61667` to bind a specific loopback port.

Do not open `index.html` directly. The page needs the local helper to read project configuration and start configured programs. The helper listens on `127.0.0.1` only and uses no external packages.

To preview the UI without starting any configured program or playlist, use test mode:

```bat
start-launcher.cmd -TestMode
```

Clicking an action in test mode reports what would be started and shows **TEST MODE**. In normal mode, custom application buttons report success as soon as their process starts. TeaBox playlist buttons wait for `TeaBox.com` to exit and report its exit code. Exit code 0 produces a green **SUCCESS** dialog; a nonzero exit code or launch error produces a red **FAILURE** dialog. **Close** dismisses the dialog without closing the launcher or stopping its local server.

## Build a portable output folder

Run `build.bat` from the project folder or double-click it. On first use, the build helper installs the PS2EXE module for the current user from PowerShell Gallery; this is a build-time dependency only.

The build creates `output\Launcher.exe` and stages the files it needs alongside it: `launcher.ps1`, `launcher.core.ps1`, `index.html`, `projects.json`, the project config files referenced by the index, `start-launcher.cmd` as a script-based fallback, and this README. The `output` folder preserves the config directory layout. Keep the folder together when moving or distributing the launcher; the EXE is not a single-file package.

Double-click `output\Launcher.exe` to start the app. Keep its console open while using the browser page and press **Ctrl+C** there to stop the local server. The output bundle does not include configured Unreal, TeaBox, or project files; those remain external and must be available at the paths in the project JSON files. Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\test-build.ps1` to build and smoke-test the packaged EXE in test mode.

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

Token names correspond to the CSS variables in `index.html` without the leading `--`. Supported names are `ink`, `muted`, `canvas`, `panel`, `tile`, `rule`, `xbox-green`, `xbox-glow`, `error`, `success`, `page-background`, `backdrop-scrim`, `brand-ink`, `badge-background`, `workspace-background`, `workspace-shadow`, `field-background`, `tile-background`, `tile-shadow`, `tile-hover-background`, `tile-hover-border`, `tile-hover-shadow`, `art-border`, `index-border`, `index-background`, `empty-background`, `empty-border`, `dialog-background`, `dialog-shadow`, `dialog-scrim`, `close-background`, and `footer-ink`. Each palette must include at least one known token. If `theme.json` is missing, malformed, or contains an unknown token, the launcher keeps its built-in palettes. Reload the page after editing the file.

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
