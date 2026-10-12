# Project Launcher

Project-specific user and deployment documentation lives in [README.md](README.md). Keep TeaBox settings behavior and storage details accurate there.

## TeaBox settings

- Settings are scoped to the selected project. The dialog is in `src/Launcher.App/wwwroot/index.html`; its behavior is in `teabox-settings.js`; request handling is in `LauncherMessageRouter.cs`; persistence is in `TeaBoxProjectSettingsStore.cs`.
- Keep the native stores synchronized: the current user's `%APPDATA%/Ubisoft/TeaBox/Branches.xml` and the selected project's `.teabox/DeployConfig/TeaBoxProperties.xml`, located relative to that project's configured TeaBox executable. Do not move these settings into launcher JSON or a shared launcher-wide path.
- Field conflicts require an explicit choice. Passwords must remain protected for the current Windows user and must never be logged or exposed as plain text.

## Verification

- Full suite: `dotnet test Launcher.sln`
- TeaBox UI tests: `node --test tests/Launcher.App.Tests/Web/launcher-bridge.test.cjs tests/Launcher.App.Tests/Web/teabox-settings.test.cjs`
- TeaBox store/router tests: `dotnet test tests/Launcher.App.Tests/Launcher.App.Tests.csproj --filter "FullyQualifiedName~TeaBoxProjectSettingsStoreTests|FullyQualifiedName~WindowsDpapiPasswordProtectorTests|FullyQualifiedName~LauncherMessageRouterTests"`
