# Perforce Launcher distribution and unsigned updates

The launcher is a per-user app whose supported entry point is `LAUNCHER.exe` in the deployment's synchronized Perforce `Launcher` folder. The bootstrapper reads the adjacent `launcher.json`, checks the configured update feed, then launches the newest healthy app version from the per-user cache. It never modifies the Perforce folder or runs `p4 sync`.

**Trust boundary:** Schema-v2 SHA-256 checks package integrity against `latest.json`; it does not authenticate the publisher. The trust model relies on HTTPS transport for Artifactory, Windows identity authentication for NAS, feed write ACLs, and Perforce access control.

## What goes where

### Perforce: deployment runtime and project configuration

A release build produces these two separate payloads:

```text
output/release-<version>/
  perforce-launcher/              # submit this complete payload to the Perforce Launcher folder
    LAUNCHER.exe
    launcher.json
    deps/
      Launcher.Bootstrapper.exe
      ...bootstrapper runtime files...
    seed-feed/
      stable/
        latest.json
        packages/
          Launcher-<version>-win-x64.zip
    projects.json                 # master config index
    projects/
      <referenced-project>.json  # only JSON files referenced by the index
    theme.json                   # optional; beside the configured index
  update-bundle/                  # copy separately to NAS or upload to Artifactory
    stable/
      latest.json
      packages/
        Launcher-<version>-win-x64.zip
```

The `perforce-launcher/` payload is self-contained for launcher and project/theme JSON configuration. It does not contain project executables, artwork, workspace data, or other external assets referenced by the JSON; ensure those are available at the configured paths. The app ZIP and `update-bundle/` remain configuration-free.

The generated `launcher.json` is a non-secret deployment descriptor. It identifies the deployment, locates the master project index relative to the `Launcher` folder, and selects the update source:

```json
{
  "DeploymentId": "hurricane",
  "ConfigIndex": "projects.json",
  "UpdateSource": {
    "Provider": "FileShare",
    "FeedBase": "\\\\fileserver\\launcher-feed",
    "Channel": "stable"
  }
}
```

Use the same stable `DeploymentId` for releases of the same deployment. Use a different ID for separately configured deployments so their per-user caches and single-instance locks are isolated. `ConfigIndex` resolves from the `Launcher` folder; child project paths and optional `theme.json` follow the existing `projects.json` rules.

Perforce owns `projects.json`, child project JSON, `theme.json`, and configured data after deployment. Users receive config changes by syncing through their normal Perforce workflow. The app reads the currently synced files at startup, and **Refresh** reloads them in a running dashboard. App updates contain binaries only and never replace project/theme configuration.

#### Fresh Perforce deployment

Copy and submit the complete generated `perforce-launcher/` folder into the deployment's Perforce `Launcher` folder using the normal reviewed Perforce workflow, including the entire `deps/` directory. Separately make sure all project executables, artwork, workspace data, and other assets referenced by the included JSON are available at their configured paths; they are not included in the release bundle. Copy or publish `update-bundle/` separately to the NAS feed root or Artifactory repository.

#### Updating an existing deployment

Compare the generated `perforce-launcher/` folder with the current deployment and review every generated descriptor/config diff before replacing files. Preserve locally customized `launcher.json`—especially `DeploymentId`, `ConfigIndex`, and `UpdateSource`—along with `projects.json`, child project JSON, and `theme.json` unless intentionally replacing them. Replacing the descriptor can change deployment cache identity, the selected config index, or the update feed. Submit approved descriptor, config, and runtime changes through the normal Perforce review and submit process, and let users sync them normally. App updates remain automatic through the unsigned feed; they do not replace Perforce JSON or require users to install each app version manually.

### NAS or Artifactory: unsigned app-update feed

The generated `update-bundle` layout is:

```text
update-bundle/
  stable/
    latest.json
    packages/
      Launcher-<version>-win-x64.zip
```

The `perforce-launcher/seed-feed` uses the same `stable/latest.json` and `stable/packages/Launcher-<version>-win-x64.zip` layout. A release bundle contains both `perforce-launcher/` and `update-bundle/`. Neither feed contains a `.sig` file or a `.test-key-only` marker.

Each schema-v2 `latest.json` has these fields:

| Field | Meaning |
| --- | --- |
| `SchemaVersion` | Integer `2`. |
| `Channel` | Feed channel, `stable`. |
| `Version` | Release version. |
| `RuntimeIdentifier` | `win-x64`. |
| `PackagePath` | Path relative to the channel directory, e.g. `packages/Launcher-1.2.3-win-x64.zip`. |
| `PackageBytes` | Package size in bytes. |
| `PackageSha256` | 64-character hexadecimal SHA-256 of the package. |

The package is a self-contained `win-x64` app payload. It excludes project/theme JSON, Perforce metadata, and credentials. The launcher requires the Microsoft Edge WebView2 Evergreen Runtime; if WebView2 is missing, it reports the prerequisite and stops.

Copy the *contents* of `update-bundle` to the NAS feed root, or upload them to the Artifactory generic repository, preserving the `stable` directory. `UpdateFeedBase` is the repository/share root before the channel directory. For NAS, it is an absolute local path or UNC share root; access uses the user's Windows identity. For Artifactory, use an HTTPS generic-repository base URI without `/stable`, embedded credentials, query, or fragment. Users need read permission and an approved read token already provisioned in Windows Credential Manager. Release maintainers need write/deploy permission. Never place tokens in `launcher.json`, release defaults, or Perforce.

## Runtime, offline fallback, and rollback

The bootstrapper in the synchronized Perforce `Launcher` folder is the supported entry point. Do not start a versioned `Launcher.App.exe` directly because doing so bypasses the update check. The bootstrapper reads the adjacent `launcher.json`, uses its configured `projects.json` path, and checks the configured feed before launching the dashboard.

If the remote feed is unavailable, the last healthy cached app is used. On a first launch with no healthy cached app, the unsigned Perforce seed feed is used. Updates stage under `%LOCALAPPDATA%\ProjectLauncher\deployments\<DeploymentId>`; a candidate must pass manifest, package, and archive checks and acknowledge health before it becomes active. A failed or unhealthy candidate is rejected, and the last healthy app remains available for rollback. The cache retains the active and previous healthy app versions.

## Build and publish a release

Release bundle generation must run on Windows for WPF `win-x64` publishing and handle-bound configuration validation.

Run from the repository root. Put one `major.minor.patch` version in tracked `version.txt`; an explicit `-Version` overrides it for a one-off build. `build-release.bat` creates both bundles in `output\release-<version>` by default. A valid update source is required to generate `launcher.json`.

For release defaults, copy `scripts/release.defaults.example.json` to the Git-ignored `scripts/release.defaults.json`. The only supported top-level sections are `UpdateSource` and `Publish`; configure the source required by the deployment and an optional publish target. A configured publish target does not enable publishing. The defaults must not contain secrets.

`-DeploymentId` defaults to `project-launcher`; use a distinct stable ID for each separately configured deployment. `-ConfigIndex` defaults to `projects.json` and is only needed when the master index uses another relative path. `-UpdateProvider`, `-UpdateFeedBase`, and `-UpdateChannel` can override the source for a one-off release.

Publishing is opt-in. Add `-Publish` to publish `update-bundle` to the configured NAS/Artifactory target, or provide both `-Provider` and `-Destination` to override it. Publication writes the versioned package first and `stable/latest.json` last; a rejected package upload stops before the manifest is sent. The NAS FileShare publisher rejects an existing package path. Artifactory package immutability is enforced by repository permissions, not by the publisher (see below). Publishing never submits the generated `perforce-launcher` directory to Perforce; merge those runtime files through the normal reviewed Perforce workflow, keeping the deployment's project configuration and data intact.

### Artifactory package immutability

The Artifactory publisher uploads to a generic repository with HTTP PUT. The [Deploy Artifact API](https://docs.jfrog.com/artifactory/reference/deployArtifact) documents PUT and checksum headers but no create-only precondition, so the client cannot independently guarantee that a versioned package is not replaced. Before enabling production publishing, configure and verify repository permissions as follows:

- On `stable/packages/**`, grant release maintainers `Deploy/Cache` so they can upload packages, but withhold `Delete/Overwrite`. Ensure no broader or overlapping permission target grants those maintainers `Delete/Overwrite` on package paths.
- Give mutable `stable/latest.json` its own narrowly scoped permission target that allows overwrite, so releases can advance the manifest without granting package replacement rights.

See the official [JFrog permission documentation](https://docs.jfrog.com/administration/docs/permissions) for permission targets and the distinction between `Deploy/Cache` and `Delete/Overwrite`. This repository policy is a production deployment prerequisite; this guide does not claim that any live Artifactory repository is configured.
