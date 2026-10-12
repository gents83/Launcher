using System.Text.Json;
using System.Xml.Linq;
using Launcher.App.Settings;
using Launcher.Core.Configuration;

namespace Launcher.App.Tests.Settings;

public sealed class TeaBoxProjectSettingsStoreTests
{
    [Fact]
    public void Load_derives_project_name_and_reads_matching_branch_and_project_settings()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("DifferentProject", @"D:\other", "elsewhere:1666,other,other_ws");
        fixture.WriteBranch("Game", @"D:\workspace\game", "p4.example:1666,alice,game_ws");
        fixture.WriteProjectOptions(
            ("P4Options.Port", "p4.example:1666"),
            ("P4Options.UserName", "alice"),
            ("P4Options.Client", "game_ws"),
            ("TechnicalOptions.PerforceRoot", @"D:\workspace\game"),
            ("UnrealPluginOptions.ProjectName", "Game"),
            ("UnrealPluginOptions.ProjectFolderName", "Game"),
            ("UnrealPluginOptions.EngineFolder", @"src\Engine"));

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal("Game", snapshot.ProjectName);
        Assert.Equal("p4.example:1666", snapshot.PerforceServer.Value);
        Assert.Equal("alice", snapshot.PerforceUserName.Value);
        Assert.Equal("game_ws", snapshot.PerforceWorkspace.Value);
        Assert.Equal(@"D:\workspace\game", snapshot.WorkspaceRoot.Value);
        Assert.Null(snapshot.PerforceServer.BranchValue);
        Assert.Null(snapshot.PerforceServer.ProjectOptionsValue);
        Assert.Equal("Game", snapshot.ProjectFolderName);
        Assert.Equal(@"src\Engine", snapshot.EngineFolder);
        Assert.Equal(TeaBoxEngineFolderRole.Programmer, snapshot.EngineFolderRole);
    }

    [Fact]
    public void Load_reads_properties_from_canonical_fully_qualified_option_groups()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", sccConnection: "p4.branch:1666,branch_user,branch_ws");
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ProjectPropertiesPath)!);
        File.WriteAllText(fixture.ProjectPropertiesPath, """
            <TeaBoxProperties>
              <TeaBox.Unreal.Configurations.P4Options>
                <Property name="Port" value="p4.example:1666" />
                <Property name="UserName" value="alice" />
                <Property name="Client" value="game_ws" />
              </TeaBox.Unreal.Configurations.P4Options>
              <TeaBox.Unreal.Configurations.TechnicalOptions>
                <Property name="PerforceRoot" value="D:\canonical-root" />
              </TeaBox.Unreal.Configurations.TechnicalOptions>
              <TeaBox.Unreal.Configurations.UnrealPluginOptions>
                <Property name="ProjectFolderName" value="CanonicalGame" />
                <Property name="EngineFolder" value="Engine" />
              </TeaBox.Unreal.Configurations.UnrealPluginOptions>
            </TeaBoxProperties>
            """);

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal("p4.example:1666", snapshot.PerforceServer.ProjectOptionsValue);
        Assert.Equal("alice", snapshot.PerforceUserName.ProjectOptionsValue);
        Assert.Equal("game_ws", snapshot.PerforceWorkspace.ProjectOptionsValue);
        Assert.Equal(@"D:\canonical-root", snapshot.WorkspaceRoot.Value);
        Assert.Equal("CanonicalGame", snapshot.ProjectFolderName);
        Assert.Equal("Engine", snapshot.EngineFolder);
        Assert.Equal(TeaBoxEngineFolderRole.NonProgrammer, snapshot.EngineFolderRole);
    }

    [Fact]
    public void Load_uses_direct_teabox_wrapper_when_an_unrelated_nested_name_collides()
    {
        using var fixture = new TeaBoxSettingsFixture();
        WriteWrappedProjectOptionsWithNestedTeaBoxCollision(
            fixture.ProjectPropertiesPath,
            directServer: "p4.direct:1666",
            nestedServer: "p4.nested-decoy:1666");

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal("p4.direct:1666", snapshot.PerforceServer.Value);
    }

    [Fact]
    public void Save_updates_only_direct_teabox_wrapper_when_an_unrelated_nested_name_collides()
    {
        using var fixture = new TeaBoxSettingsFixture();
        WriteWrappedProjectOptionsWithNestedTeaBoxCollision(
            fixture.ProjectPropertiesPath,
            directServer: "p4.old-direct:1666",
            nestedServer: "p4.nested-decoy:1666");
        var before = XDocument.Load(fixture.ProjectPropertiesPath, LoadOptions.PreserveWhitespace);
        var unrelatedWrapperBefore = new XElement(GetNestedUnrelatedTeaBoxWrapper(before));
        var request = CreateSaveRequest(fixture, server: "p4.new-direct:1666");

        fixture.Store.Save(fixture.Project, request);

        var after = XDocument.Load(fixture.ProjectPropertiesPath, LoadOptions.PreserveWhitespace);
        Assert.Equal("p4.new-direct:1666", GetWrappedPropertyValue(after, "P4Options", "Port"));
        var unrelatedWrapperAfter = GetNestedUnrelatedTeaBoxWrapper(after);
        Assert.True(XNode.DeepEquals(unrelatedWrapperBefore, unrelatedWrapperAfter));
        Assert.DoesNotContain(after.Descendants("Property"), property =>
            (string?)property.Attribute("name") == "P4Options.Port");
    }

    [Fact]
    public void Load_rejects_duplicate_direct_teabox_wrappers_with_a_safe_error()
    {
        using var fixture = new TeaBoxSettingsFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ProjectPropertiesPath)!);
        File.WriteAllText(fixture.ProjectPropertiesPath, """
            <TeaBox>
              <Properties>
                <Property name="TeaBox">
                  <Property name="TeaBox.Unreal.Configurations.P4Options">
                    <Property name="Port" type="String" value="p4.first:1666" />
                  </Property>
                </Property>
                <Property name="TeaBox">
                  <Property name="TeaBox.Unreal.Configurations.P4Options">
                    <Property name="Port" type="String" value="p4.second:1666" />
                  </Property>
                </Property>
              </Properties>
            </TeaBox>
            """);

        var exception = Assert.Throws<InvalidDataException>(() => fixture.Store.Load(fixture.Project));

        Assert.Contains("duplicate root wrappers", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("p4.first:1666", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("p4.second:1666", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_reads_project_options_from_qualified_property_wrappers()
    {
        using var fixture = new TeaBoxSettingsFixture();
        WriteWrappedProjectOptions(fixture.ProjectPropertiesPath, """
            <Property name="TeaBox.Unreal.Configurations.P4Options">
              <Property name="Port" type="String" value="p4.wrapped:1666" />
              <Property name="UserName" type="String" value="wrapped-user" />
              <Property name="Client" type="String" value="wrapped-workspace" />
            </Property>
            <Property name="TeaBox.Unreal.Configurations.TechnicalOptions">
              <Property name="PerforceRoot" type="String" value="D:\wrapped-root" />
            </Property>
            <Property name="TeaBox.Unreal.Configurations.UnrealPluginOptions">
              <Property name="ProjectFolderName" type="String" value="WrappedGame" />
              <Property name="EngineFolder" type="String" value="wrapped\Engine" />
            </Property>
            """);

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal("p4.wrapped:1666", snapshot.PerforceServer.Value);
        Assert.Equal("wrapped-user", snapshot.PerforceUserName.Value);
        Assert.Equal("wrapped-workspace", snapshot.PerforceWorkspace.Value);
        Assert.Equal(@"D:\wrapped-root", snapshot.WorkspaceRoot.Value);
        Assert.Equal("WrappedGame", snapshot.ProjectFolderName);
        Assert.Equal(@"wrapped\Engine", snapshot.EngineFolder);
    }

    [Fact]
    public void Load_reports_perforce_source_values_that_differ_by_whitespace_as_a_conflict()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", sccConnection: "p4.example:1666 ,synthetic-user,synthetic-workspace");
        fixture.WriteProjectOptions(
            ("P4Options.Port", "p4.example:1666"),
            ("P4Options.UserName", "synthetic-user"),
            ("P4Options.Client", "synthetic-workspace"));

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal(
            new TeaBoxFieldValue(null, "p4.example:1666 ", "p4.example:1666"),
            snapshot.PerforceServer);
    }

    [Fact]
    public void Load_ignores_a_whitespace_only_perforce_source_without_trimming_the_usable_value()
    {
        using var fixture = new TeaBoxSettingsFixture();
        const string server = "p4.example:1666 ";
        fixture.WriteBranch("Game", sccConnection: $"{server},synthetic-user,synthetic-workspace");
        fixture.WriteProjectOptions(
            ("P4Options.Port", "   "),
            ("P4Options.UserName", "synthetic-user"),
            ("P4Options.Client", "synthetic-workspace"));

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal(new TeaBoxFieldValue(server, null, null), snapshot.PerforceServer);
    }

    [Fact]
    public void Load_preserves_an_options_only_workspace_root_exactly()
    {
        using var fixture = new TeaBoxSettingsFixture();
        const string workspaceRoot = " D:\\p4hur ";
        fixture.WriteBranch("Game");
        WriteWorkspaceRoot(fixture, workspaceRoot);

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal(new TeaBoxFieldValue(workspaceRoot, null, null), snapshot.WorkspaceRoot);
    }

    [Fact]
    public void Load_still_merges_equal_perforce_values_and_equivalent_workspace_roots()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch(
            "Game",
            folder: @"D:\p4hur\",
            sccConnection: "p4.example:1666,synthetic-user,synthetic-workspace");
        WriteWrappedProjectOptions(fixture.ProjectPropertiesPath, """
            <Property name="TeaBox.Unreal.Configurations.P4Options">
              <Property name="Port" type="String" value="p4.example:1666" />
              <Property name="UserName" type="String" value="synthetic-user" />
              <Property name="Client" type="String" value="synthetic-workspace" />
            </Property>
            <Property name="TeaBox.Unreal.Configurations.TechnicalOptions">
              <Property name="PerforceRoot" type="String" value="D:\p4hur" />
            </Property>
            """);

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal(new TeaBoxFieldValue("p4.example:1666", null, null), snapshot.PerforceServer);
        Assert.Equal(new TeaBoxFieldValue("synthetic-user", null, null), snapshot.PerforceUserName);
        Assert.Equal(new TeaBoxFieldValue("synthetic-workspace", null, null), snapshot.PerforceWorkspace);
        Assert.Equal(new TeaBoxFieldValue(@"D:\p4hur\", null, null), snapshot.WorkspaceRoot);
    }

    [Theory]
    [InlineData(@"D:\p4hur\", @"D:\p4hur")]
    [InlineData("d:/P4HUR/", @"D:\p4hur")]
    [InlineData(@"D:\", "d:/")]
    [InlineData(@"\\server\share\", "//SERVER/share")]
    public void Load_treats_equivalent_fully_qualified_windows_roots_as_one_value(
        string branchRoot,
        string projectOptionsRoot)
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", folder: branchRoot);
        WriteWorkspaceRoot(fixture, projectOptionsRoot);
        var branchesBefore = File.ReadAllBytes(fixture.BranchesPath);
        var optionsBefore = File.ReadAllBytes(fixture.ProjectPropertiesPath);

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal(new TeaBoxFieldValue(branchRoot, null, null), snapshot.WorkspaceRoot);
        Assert.Equal(branchesBefore, File.ReadAllBytes(fixture.BranchesPath));
        Assert.Equal(optionsBefore, File.ReadAllBytes(fixture.ProjectPropertiesPath));
    }

    [Fact]
    public void Load_keeps_genuinely_different_workspace_roots_as_raw_conflict_sources()
    {
        using var fixture = new TeaBoxSettingsFixture();
        const string branchRoot = @"D:\p4hur\";
        const string projectOptionsRoot = @"D:\other-workspace";
        fixture.WriteBranch("Game", folder: branchRoot);
        WriteWorkspaceRoot(fixture, projectOptionsRoot);

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal(new TeaBoxFieldValue(null, branchRoot, projectOptionsRoot), snapshot.WorkspaceRoot);
    }

    [Theory]
    [InlineData(@"D:\p4hur\", "relative\\p4hur")]
    [InlineData("relative/folder", @"relative\folder")]
    [InlineData(@"D:\p4hur|", @"D:\p4hur|")]
    public void Load_treats_unverifiable_workspace_roots_as_conflicts(
        string branchRoot,
        string projectOptionsRoot)
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", folder: branchRoot);
        WriteWorkspaceRoot(fixture, projectOptionsRoot);

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal(new TeaBoxFieldValue(null, branchRoot, projectOptionsRoot), snapshot.WorkspaceRoot);
    }

    [Fact]
    public void Load_does_not_trim_leading_whitespace_before_qualifying_workspace_roots()
    {
        using var fixture = new TeaBoxSettingsFixture();
        const string branchRoot = @" D:\p4hur";
        const string projectOptionsRoot = @"D:\p4hur";
        fixture.WriteBranch("Game", folder: branchRoot);
        WriteWorkspaceRoot(fixture, projectOptionsRoot);

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal(new TeaBoxFieldValue(null, branchRoot, projectOptionsRoot), snapshot.WorkspaceRoot);
    }

    [Theory]
    [InlineData(@"\\\share\folder")]
    [InlineData(@"\\server\\folder")]
    [InlineData(@"\\invalid|server\share")]
    [InlineData(@"\\server\invalid|share\folder")]
    public void Load_keeps_invalid_unc_server_and_share_components_as_conflicts(string workspaceRoot)
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", folder: workspaceRoot);
        WriteWorkspaceRoot(fixture, workspaceRoot);

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal(new TeaBoxFieldValue(null, workspaceRoot, workspaceRoot), snapshot.WorkspaceRoot);
    }

    [Fact]
    public void Load_uses_the_only_present_value_without_reporting_a_conflict()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", folder: @"D:\branch-root");
        fixture.WriteProjectOptions(
            ("P4Options.Port", "p4.options:1666"),
            ("P4Options.UserName", "options_user"),
            ("P4Options.Client", "options_ws"));

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal(new TeaBoxFieldValue("p4.options:1666", null, null), snapshot.PerforceServer);
        Assert.Equal(new TeaBoxFieldValue("options_user", null, null), snapshot.PerforceUserName);
        Assert.Equal(new TeaBoxFieldValue("options_ws", null, null), snapshot.PerforceWorkspace);
        Assert.Equal(new TeaBoxFieldValue(@"D:\branch-root", null, null), snapshot.WorkspaceRoot);
    }

    [Fact]
    public void Load_reports_mismatched_duplicate_settings_without_choosing_a_value()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", @"D:\branch-root", "p4.branch:1666,branch_user,branch_ws");
        fixture.WriteProjectOptions(
            ("P4Options.Port", "p4.options:1666"),
            ("P4Options.UserName", "options_user"),
            ("P4Options.Client", "options_ws"),
            ("TechnicalOptions.PerforceRoot", @"D:\options-root"));

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal(new TeaBoxFieldValue(null, "p4.branch:1666", "p4.options:1666"), snapshot.PerforceServer);
        Assert.Equal(new TeaBoxFieldValue(null, "branch_user", "options_user"), snapshot.PerforceUserName);
        Assert.Equal(new TeaBoxFieldValue(null, "branch_ws", "options_ws"), snapshot.PerforceWorkspace);
        Assert.Equal(new TeaBoxFieldValue(null, @"D:\branch-root", @"D:\options-root"), snapshot.WorkspaceRoot);
    }

    [Fact]
    public void Load_preserves_exact_engine_folder_text_while_detecting_programmer_role()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteProjectOptions(("UnrealPluginOptions.EngineFolder", "src/Engine/"));

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal("src/Engine/", snapshot.EngineFolder);
        Assert.Equal(TeaBoxEngineFolderRole.Programmer, snapshot.EngineFolderRole);
    }

    [Theory]
    [InlineData(@"src\Engine", TeaBoxEngineFolderRole.Programmer)]
    [InlineData("Engine", TeaBoxEngineFolderRole.NonProgrammer)]
    [InlineData(@"D:\custom-engine", TeaBoxEngineFolderRole.Custom)]
    [InlineData("", TeaBoxEngineFolderRole.Unselected)]
    public void Load_maps_engine_folder_to_role_without_rewriting_the_path(
        string engineFolder,
        TeaBoxEngineFolderRole expectedRole)
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteProjectOptions(("UnrealPluginOptions.EngineFolder", engineFolder));

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal(engineFolder, snapshot.EngineFolder);
        Assert.Equal(expectedRole, snapshot.EngineFolderRole);
    }

    [Fact]
    public void Load_defaults_missing_values_without_creating_native_files_or_choosing_a_role()
    {
        using var fixture = new TeaBoxSettingsFixture();

        var snapshot = fixture.Store.Load(fixture.Project);

        Assert.Equal("Game", snapshot.ProjectName);
        Assert.Equal(fixture.Project.Root, snapshot.ProjectFolderName);
        Assert.Equal(string.Empty, snapshot.EngineFolder);
        Assert.Equal(TeaBoxEngineFolderRole.Unselected, snapshot.EngineFolderRole);
        Assert.Equal(new TeaBoxFieldValue(null, null, null), snapshot.PerforceServer);
        Assert.Equal(new TeaBoxFieldValue(null, null, null), snapshot.PerforceUserName);
        Assert.Equal(new TeaBoxFieldValue(null, null, null), snapshot.PerforceWorkspace);
        Assert.Equal(new TeaBoxFieldValue(null, null, null), snapshot.WorkspaceRoot);
        Assert.Equal(TeaBoxPasswordState.Missing, snapshot.PasswordState);
        Assert.False(File.Exists(fixture.BranchesPath));
        Assert.False(File.Exists(fixture.ProjectPropertiesPath));
    }

    [Fact]
    public void Load_reports_saved_password_without_returning_protected_or_plaintext_secret()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", password: fixture.PasswordProtector.Protect("synthetic-password"));
        fixture.WriteProjectOptions(("P4Options.Password", fixture.PasswordProtector.Protect("synthetic-password")));

        var snapshot = fixture.Store.Load(fixture.Project);
        var serializedSnapshot = JsonSerializer.Serialize(snapshot);

        Assert.Equal(TeaBoxPasswordState.Saved, snapshot.PasswordState);
        Assert.DoesNotContain("synthetic-password", serializedSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-protected:", serializedSnapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_reports_conflicting_saved_passwords_without_returning_either_secret()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", password: fixture.PasswordProtector.Protect("synthetic-branch-password"));
        fixture.WriteProjectOptions(("P4Options.Password", fixture.PasswordProtector.Protect("synthetic-options-password")));

        var snapshot = fixture.Store.Load(fixture.Project);
        var serializedSnapshot = JsonSerializer.Serialize(snapshot);

        Assert.Equal(TeaBoxPasswordState.Conflict, snapshot.PasswordState);
        Assert.DoesNotContain("synthetic-branch-password", serializedSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-options-password", serializedSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-protected:", serializedSnapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_reports_unreadable_password_without_exposing_protector_error_or_secret()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", password: "fixture-invalid-protected-value");

        var snapshot = fixture.Store.Load(fixture.Project);
        var serializedSnapshot = JsonSerializer.Serialize(snapshot);

        Assert.Equal(TeaBoxPasswordState.Unreadable, snapshot.PasswordState);
        Assert.DoesNotContain("fixture-invalid-protected-value", serializedSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-password", serializedSnapshot, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(",user,workspace")]
    [InlineData("server,,workspace")]
    [InlineData("server,user,")]
    [InlineData("server, ,workspace")]
    [InlineData("server,user,  ")]
    public void Load_rejects_scc_connections_with_empty_components(string sccConnection)
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", sccConnection: sccConnection);

        Assert.Throws<InvalidDataException>(() => fixture.Store.Load(fixture.Project));
    }

    [Fact]
    public void Load_does_not_treat_an_unreadable_xml_path_as_an_absent_file()
    {
        using var fixture = new TeaBoxSettingsFixture();
        Directory.CreateDirectory(fixture.BranchesPath);

        var exception = Assert.Throws<InvalidDataException>(() => fixture.Store.Load(fixture.Project));

        Assert.Contains("branch settings XML", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_rejects_malformed_scc_connection_without_echoing_its_contents()
    {
        using var fixture = new TeaBoxSettingsFixture();
        const string malformedConnection = "synthetic-malformed-connection-with-secret-like-text";
        fixture.WriteBranch("Game", sccConnection: malformedConnection);

        var exception = Assert.Throws<InvalidDataException>(() => fixture.Store.Load(fixture.Project));

        Assert.DoesNotContain(malformedConnection, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_rejects_duplicate_matching_branch_names_instead_of_selecting_one()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", @"D:\first", "one:1666,user,workspace");
        fixture.WriteBranch("Game", @"D:\second", "two:1666,user,workspace");

        var exception = Assert.Throws<InvalidDataException>(() => fixture.Store.Load(fixture.Project));

        Assert.Contains("multiple", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_rejects_missing_or_multiple_top_level_project_files()
    {
        using var fixture = new TeaBoxSettingsFixture();
        File.Delete(fixture.ProjectFilePath);

        Assert.Throws<InvalidDataException>(() => fixture.Store.Load(fixture.Project));

        File.WriteAllText(fixture.ProjectFilePath, "{}");
        File.WriteAllText(Path.Combine(fixture.Project.Root!, "Second.uproject"), "{}");
        Assert.Throws<InvalidDataException>(() => fixture.Store.Load(fixture.Project));
    }

    [Fact]
    public void Load_rejects_missing_project_root_or_teabox_executable()
    {
        using var fixture = new TeaBoxSettingsFixture();

        var missingRootProject = new ProjectConfig
        {
            Id = fixture.Project.Id,
            Name = fixture.Project.Name,
            TeaBox = fixture.Project.TeaBox
        };
        Assert.Throws<InvalidDataException>(() => fixture.Store.Load(missingRootProject));

        var missingTeaBoxProject = new ProjectConfig
        {
            Id = fixture.Project.Id,
            Name = fixture.Project.Name,
            Root = fixture.Project.Root
        };
        Assert.Throws<InvalidDataException>(() => fixture.Store.Load(missingTeaBoxProject));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Save_rejects_blank_engine_folder_without_changing_either_store(string? engineFolder)
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", folder: "synthetic-existing-root", sccConnection: "p4.synthetic:1666,synthetic-user,synthetic-workspace");
        fixture.WriteProjectOptions(
            ("P4Options.Port", "p4.synthetic:1666"),
            ("P4Options.UserName", "synthetic-user"),
            ("P4Options.Client", "synthetic-workspace"),
            ("UnrealPluginOptions.EngineFolder", "ExistingEngine"));
        var branchesBefore = File.ReadAllBytes(fixture.BranchesPath);
        var optionsBefore = File.ReadAllBytes(fixture.ProjectPropertiesPath);

        Assert.Throws<InvalidDataException>(() => fixture.Store.Save(
            fixture.Project,
            CreateSaveRequest(fixture, engineFolder: engineFolder!)));

        Assert.Equal(branchesBefore, File.ReadAllBytes(fixture.BranchesPath));
        Assert.Equal(optionsBefore, File.ReadAllBytes(fixture.ProjectPropertiesPath));
    }

    [Fact]
    public void Save_updates_existing_qualified_property_wrappers_without_adding_flattened_properties()
    {
        using var fixture = new TeaBoxSettingsFixture();
        WriteWrappedProjectOptions(fixture.ProjectPropertiesPath, """
            <Property name="TeaBox.Unreal.Configurations.P4Options">
              <Property name="Port" type="String" value="old:1666" />
              <Property name="UserName" type="String" value="old-user" />
              <Property name="Client" type="String" value="old-workspace" />
              <Property name="Password" type="String" value="" />
            </Property>
            <Property name="TeaBox.Unreal.Configurations.TechnicalOptions">
              <Property name="PerforceRoot" type="String" value="old-root" />
            </Property>
            <Property name="TeaBox.Unreal.Configurations.UnrealPluginOptions">
              <Property name="ProjectName" type="String" value="OldGame" />
              <Property name="ProjectFolderName" type="String" value="OldFolder" />
              <Property name="EngineFolder" type="String" value="OldEngine" />
            </Property>
            <!-- synthetic wrapped comment -->
            <Property name="TeaBox.Unreal.Configurations.UnknownOptions">
              <Property name="KeepMe" type="String" value="synthetic-unknown-value" />
            </Property>
            """);
        var request = CreateSaveRequest(
            fixture,
            server: "p4.wrapped:1666",
            userName: "wrapped-user",
            workspace: "wrapped-workspace",
            workspaceRoot: @"D:\wrapped-root",
            projectFolderName: "WrappedGameFolder",
            engineFolder: @"wrapped\Engine");

        fixture.Store.Save(fixture.Project, request);

        var options = XDocument.Load(fixture.ProjectPropertiesPath, LoadOptions.PreserveWhitespace);
        Assert.Equal("TeaBox", options.Root!.Name.LocalName);
        Assert.Equal("p4.wrapped:1666", GetWrappedPropertyValue(options, "P4Options", "Port"));
        Assert.Equal("wrapped-user", GetWrappedPropertyValue(options, "P4Options", "UserName"));
        Assert.Equal("wrapped-workspace", GetWrappedPropertyValue(options, "P4Options", "Client"));
        Assert.Equal(request.WorkspaceRoot, GetWrappedPropertyValue(options, "TechnicalOptions", "PerforceRoot"));
        Assert.Equal("Game", GetWrappedPropertyValue(options, "UnrealPluginOptions", "ProjectName"));
        Assert.Equal("WrappedGameFolder", GetWrappedPropertyValue(options, "UnrealPluginOptions", "ProjectFolderName"));
        Assert.Equal(@"wrapped\Engine", GetWrappedPropertyValue(options, "UnrealPluginOptions", "EngineFolder"));
        Assert.Equal("synthetic-unknown-value", GetWrappedPropertyValue(options, "UnknownOptions", "KeepMe"));
        Assert.Contains(options.DescendantNodes().OfType<XComment>(), comment => comment.Value == " synthetic wrapped comment ");
        Assert.DoesNotContain(options.Descendants("Property"), property => new[]
        {
            "P4Options.Port", "P4Options.UserName", "P4Options.Client", "TechnicalOptions.PerforceRoot",
            "UnrealPluginOptions.ProjectName", "UnrealPluginOptions.ProjectFolderName", "UnrealPluginOptions.EngineFolder"
        }.Contains((string?)property.Attribute("name")));
    }

    [Fact]
    public void Save_creates_and_round_trips_a_new_qualified_property_wrapper_document()
    {
        using var fixture = new TeaBoxSettingsFixture();
        var request = CreateSaveRequest(
            fixture,
            server: "p4.first-run:1666",
            userName: "first-run-user",
            workspace: "first-run-workspace",
            workspaceRoot: @"E:\first-run-root",
            projectFolderName: "FirstRunFolder",
            engineFolder: @"src\Engine",
            passwordResolution: TeaBoxPasswordResolution.Replace,
            newPassword: "synthetic-first-run-password");

        fixture.Store.Save(fixture.Project, request);

        var options = XDocument.Load(fixture.ProjectPropertiesPath);
        Assert.Equal("TeaBox", options.Root!.Name.LocalName);
        Assert.Equal("p4.first-run:1666", GetWrappedPropertyValue(options, "P4Options", "Port"));
        Assert.Equal("first-run-user", GetWrappedPropertyValue(options, "P4Options", "UserName"));
        Assert.Equal("first-run-workspace", GetWrappedPropertyValue(options, "P4Options", "Client"));
        Assert.Equal(request.WorkspaceRoot, GetWrappedPropertyValue(options, "TechnicalOptions", "PerforceRoot"));
        Assert.Equal("Game", GetWrappedPropertyValue(options, "UnrealPluginOptions", "ProjectName"));
        Assert.Equal("FirstRunFolder", GetWrappedPropertyValue(options, "UnrealPluginOptions", "ProjectFolderName"));
        Assert.Equal(request.EngineFolder, GetWrappedPropertyValue(options, "UnrealPluginOptions", "EngineFolder"));
        Assert.Equal(
            fixture.PasswordProtector.Protect("synthetic-first-run-password"),
            GetWrappedPropertyValue(options, "P4Options", "Password"));

        var propertiesRoot = Assert.Single(options.Root.Elements("Properties"));
        var teaBoxWrapper = Assert.Single(propertiesRoot.Elements("Property"));
        Assert.Equal("TeaBox", (string?)teaBoxWrapper.Attribute("name"));
        foreach (var group in teaBoxWrapper.Elements("Property"))
        {
            foreach (var property in group.Elements("Property"))
                Assert.Equal("String", (string?)property.Attribute("type"));
        }

        var snapshot = fixture.Store.Load(fixture.Project);
        Assert.Equal("p4.first-run:1666", snapshot.PerforceServer.Value);
        Assert.Equal("first-run-user", snapshot.PerforceUserName.Value);
        Assert.Equal("first-run-workspace", snapshot.PerforceWorkspace.Value);
        Assert.Equal(request.WorkspaceRoot, snapshot.WorkspaceRoot.Value);
        Assert.Equal("FirstRunFolder", snapshot.ProjectFolderName);
        Assert.Equal(@"src\Engine", snapshot.EngineFolder);
        Assert.Equal(TeaBoxPasswordState.Saved, snapshot.PasswordState);
    }

    [Fact]
    public void Save_creates_both_missing_stores_with_synchronized_teabox_settings()
    {
        using var fixture = new TeaBoxSettingsFixture();
        const string password = "synthetic-first-run-password";
        var request = CreateSaveRequest(
            fixture,
            workspaceRoot: @"D:\synthetic\workspace",
            projectFolderName: "GameContent",
            engineFolder: @"D:\synthetic\custom-engine",
            passwordResolution: TeaBoxPasswordResolution.Replace,
            newPassword: password);

        var snapshot = Save(fixture.Store, fixture.Project, request);

        var branches = XDocument.Load(fixture.BranchesPath);
        Assert.Equal("TeaBox", branches.Root!.Name.LocalName);
        var branchOptions = Assert.Single(branches.Root.Elements("BranchOptions"));
        Assert.Equal("False", (string?)branchOptions.Attribute("IsTeaBoxInstancePerBranch"));
        var branch = Assert.Single(branchOptions.Elements("Branch"));
        Assert.Equal("Game", (string?)branch.Attribute("DisplayName"));
        Assert.Equal("G", (string?)branch.Attribute("Initial"));
        Assert.Equal("128, 128, 128", (string?)branch.Attribute("DisplayColor"));
        Assert.Equal(request.WorkspaceRoot, (string?)branch.Attribute("Folder"));
        Assert.Equal("p4.synthetic:1666,synthetic-user,synthetic-workspace", (string?)branch.Attribute("SccConnection"));

        var options = XDocument.Load(fixture.ProjectPropertiesPath);
        Assert.Equal("TeaBox", options.Root!.Name.LocalName);
        var expectedQualifiedGroups = new[]
        {
            "TeaBox.Unreal.Configurations.P4Options",
            "TeaBox.Unreal.Configurations.TechnicalOptions",
            "TeaBox.Unreal.Configurations.UnrealPluginOptions"
        };
        var writtenQualifiedGroups = options.Descendants("Property")
            .Select(property => (string?)property.Attribute("name"))
            .Where(name => name?.StartsWith("TeaBox.Unreal.Configurations.", StringComparison.Ordinal) == true)
            .OrderBy(name => name)
            .ToArray();
        Assert.Equal(expectedQualifiedGroups.OrderBy(name => name), writtenQualifiedGroups);
        Assert.Equal("p4.synthetic:1666", GetWrappedPropertyValue(options, "P4Options", "Port"));
        Assert.Equal("synthetic-user", GetWrappedPropertyValue(options, "P4Options", "UserName"));
        Assert.Equal("synthetic-workspace", GetWrappedPropertyValue(options, "P4Options", "Client"));
        Assert.Equal(request.WorkspaceRoot, GetWrappedPropertyValue(options, "TechnicalOptions", "PerforceRoot"));
        Assert.Equal("Game", GetWrappedPropertyValue(options, "UnrealPluginOptions", "ProjectName"));
        Assert.Equal("GameContent", GetWrappedPropertyValue(options, "UnrealPluginOptions", "ProjectFolderName"));
        Assert.Equal(request.EngineFolder, GetWrappedPropertyValue(options, "UnrealPluginOptions", "EngineFolder"));
        Assert.Equal(fixture.PasswordProtector.Protect(password), (string?)branch.Attribute("Password"));
        Assert.Equal(fixture.PasswordProtector.Protect(password), GetWrappedPropertyValue(options, "P4Options", "Password"));
        Assert.Equal(TeaBoxPasswordState.Saved, snapshot.PasswordState);
    }

    [Fact]
    public void Save_creates_a_missing_branch_store_and_updates_existing_project_options()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteProjectOptions(("UnknownOptions.PreserveMe", "synthetic-unknown-value"));
        var request = CreateSaveRequest(fixture);

        Save(fixture.Store, fixture.Project, request);

        var branches = XDocument.Load(fixture.BranchesPath);
        Assert.Equal("Game", (string?)Assert.Single(branches.Descendants("Branch")).Attribute("DisplayName"));
        var options = XDocument.Load(fixture.ProjectPropertiesPath);
        Assert.Equal("synthetic-unknown-value", GetFlatPropertyValue(options, "UnknownOptions.PreserveMe"));
        Assert.Equal("p4.synthetic:1666", GetFlatPropertyValue(options, "P4Options.Port"));
    }

    [Fact]
    public void Save_requires_field_conflict_resolutions_before_writing_either_store()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", @"D:\branch-root", "branch-server:1666,branch-user,branch-workspace");
        fixture.WriteProjectOptions(
            ("P4Options.Port", "options-server:1666"),
            ("P4Options.UserName", "options-user"),
            ("P4Options.Client", "options-workspace"),
            ("TechnicalOptions.PerforceRoot", @"D:\options-root"));
        var branchesBefore = File.ReadAllBytes(fixture.BranchesPath);
        var optionsBefore = File.ReadAllBytes(fixture.ProjectPropertiesPath);

        Assert.Throws<InvalidDataException>(() => Save(
            fixture.Store,
            fixture.Project,
            CreateSaveRequest(fixture)));

        Assert.Equal(branchesBefore, File.ReadAllBytes(fixture.BranchesPath));
        Assert.Equal(optionsBefore, File.ReadAllBytes(fixture.ProjectPropertiesPath));
    }

    [Fact]
    public void Save_uses_explicit_conflict_sources_and_synchronizes_custom_values_and_paths()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", @"D:\branch-root", "branch-server:1666,branch-user,branch-workspace");
        fixture.WriteProjectOptions(
            ("P4Options.Port", "options-server:1666"),
            ("P4Options.UserName", "options-user"),
            ("P4Options.Client", "options-workspace"),
            ("TechnicalOptions.PerforceRoot", @"D:\options-root"));
        var request = CreateSaveRequest(
            fixture,
            server: "branch-server:1666",
            userName: "options-user",
            workspace: "custom-workspace",
            workspaceRoot: @"E:\synthetic\custom-root",
            projectFolderName: "GameContentOverride",
            engineFolder: @"F:\synthetic\engine",
            resolutions:
            [
                new TeaBoxFieldResolution("PerforceServer", TeaBoxFieldSource.Branch),
                new TeaBoxFieldResolution("PerforceUserName", TeaBoxFieldSource.ProjectOptions),
                new TeaBoxFieldResolution("PerforceWorkspace", TeaBoxFieldSource.Custom),
                new TeaBoxFieldResolution("WorkspaceRoot", TeaBoxFieldSource.Custom)
            ]);

        Save(fixture.Store, fixture.Project, request);

        var branch = Assert.Single(XDocument.Load(fixture.BranchesPath).Descendants("Branch"));
        Assert.Equal(request.WorkspaceRoot, (string?)branch.Attribute("Folder"));
        Assert.Equal("branch-server:1666,options-user,custom-workspace", (string?)branch.Attribute("SccConnection"));
        var options = XDocument.Load(fixture.ProjectPropertiesPath);
        Assert.Equal("branch-server:1666", GetFlatPropertyValue(options, "P4Options.Port"));
        Assert.Equal("options-user", GetFlatPropertyValue(options, "P4Options.UserName"));
        Assert.Equal("custom-workspace", GetFlatPropertyValue(options, "P4Options.Client"));
        Assert.Equal(request.WorkspaceRoot, GetFlatPropertyValue(options, "TechnicalOptions.PerforceRoot"));
        Assert.Equal("Game", GetFlatPropertyValue(options, "UnrealPluginOptions.ProjectName"));
        Assert.Equal("GameContentOverride", GetFlatPropertyValue(options, "UnrealPluginOptions.ProjectFolderName"));
        Assert.Equal(@"F:\synthetic\engine", GetFlatPropertyValue(options, "UnrealPluginOptions.EngineFolder"));
    }

    [Fact]
    public void Save_rejects_a_field_resolution_that_does_not_match_its_selected_source()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", sccConnection: "branch-server:1666,branch-user,branch-workspace");
        fixture.WriteProjectOptions(("P4Options.Port", "options-server:1666"));
        var branchesBefore = File.ReadAllBytes(fixture.BranchesPath);
        var optionsBefore = File.ReadAllBytes(fixture.ProjectPropertiesPath);
        var request = CreateSaveRequest(
            fixture,
            server: "options-server:1666",
            resolutions: [new TeaBoxFieldResolution("PerforceServer", TeaBoxFieldSource.Branch)]);

        Assert.Throws<InvalidDataException>(() => Save(fixture.Store, fixture.Project, request));

        Assert.Equal(branchesBefore, File.ReadAllBytes(fixture.BranchesPath));
        Assert.Equal(optionsBefore, File.ReadAllBytes(fixture.ProjectPropertiesPath));
    }

    [Fact]
    public void Save_uses_project_options_password_when_branch_password_is_blank()
    {
        using var fixture = new TeaBoxSettingsFixture();
        const string password = "synthetic-project-options-password";
        var protectedPassword = fixture.PasswordProtector.Protect(password);
        fixture.WriteBranch("Game", password: string.Empty);
        fixture.WriteProjectOptions(("P4Options.Password", protectedPassword));

        var snapshot = Save(
            fixture.Store,
            fixture.Project,
            CreateSaveRequest(fixture));

        var branch = Assert.Single(XDocument.Load(fixture.BranchesPath).Descendants("Branch"));
        var options = XDocument.Load(fixture.ProjectPropertiesPath);
        Assert.Equal(protectedPassword, (string?)branch.Attribute("Password"));
        Assert.Equal(protectedPassword, GetFlatPropertyValue(options, "P4Options.Password"));
        Assert.Equal(TeaBoxPasswordState.Saved, snapshot.PasswordState);
    }

    [Fact]
    public void Save_reprotects_equal_stored_passwords_and_writes_the_same_value_to_both_stores()
    {
        using var fixture = new TeaBoxSettingsFixture();
        const string password = "synthetic-equal-password";
        fixture.WriteBranch("Game", password: fixture.PasswordProtector.Protect(password));
        fixture.WriteProjectOptions(("P4Options.Password", fixture.PasswordProtector.Protect(password)));

        var snapshot = Save(
            fixture.Store,
            fixture.Project,
            CreateSaveRequest(fixture));

        var expectedProtectedValue = fixture.PasswordProtector.Protect(password);
        var branch = Assert.Single(XDocument.Load(fixture.BranchesPath).Descendants("Branch"));
        var options = XDocument.Load(fixture.ProjectPropertiesPath);
        Assert.Equal(expectedProtectedValue, (string?)branch.Attribute("Password"));
        Assert.Equal(expectedProtectedValue, GetFlatPropertyValue(options, "P4Options.Password"));
        Assert.Equal(TeaBoxPasswordState.Saved, snapshot.PasswordState);
        var serializedSnapshot = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(password, serializedSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain(expectedProtectedValue, serializedSnapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_requires_an_explicit_password_resolution_for_different_stored_passwords()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", password: fixture.PasswordProtector.Protect("synthetic-branch-password"));
        fixture.WriteProjectOptions(("P4Options.Password", fixture.PasswordProtector.Protect("synthetic-options-password")));
        var branchesBefore = File.ReadAllBytes(fixture.BranchesPath);
        var optionsBefore = File.ReadAllBytes(fixture.ProjectPropertiesPath);

        Assert.Throws<InvalidDataException>(() => Save(
            fixture.Store,
            fixture.Project,
            CreateSaveRequest(fixture)));

        Assert.Equal(branchesBefore, File.ReadAllBytes(fixture.BranchesPath));
        Assert.Equal(optionsBefore, File.ReadAllBytes(fixture.ProjectPropertiesPath));
    }

    [Theory]
    [InlineData(TeaBoxPasswordResolution.UseBranch, "synthetic-branch-password")]
    [InlineData(TeaBoxPasswordResolution.UseProjectOptions, "synthetic-options-password")]
    [InlineData(TeaBoxPasswordResolution.Replace, "synthetic-replacement-password")]
    public void Save_resolves_different_passwords_without_returning_the_selected_secret(
        TeaBoxPasswordResolution resolution,
        string selectedPassword)
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", password: fixture.PasswordProtector.Protect("synthetic-branch-password"));
        fixture.WriteProjectOptions(("P4Options.Password", fixture.PasswordProtector.Protect("synthetic-options-password")));

        var snapshot = Save(
            fixture.Store,
            fixture.Project,
            CreateSaveRequest(
                fixture,
                passwordResolution: resolution,
                newPassword: resolution == TeaBoxPasswordResolution.Replace ? selectedPassword : null));

        var expectedProtectedValue = fixture.PasswordProtector.Protect(selectedPassword);
        var branch = Assert.Single(XDocument.Load(fixture.BranchesPath).Descendants("Branch"));
        var options = XDocument.Load(fixture.ProjectPropertiesPath);
        Assert.Equal(expectedProtectedValue, (string?)branch.Attribute("Password"));
        Assert.Equal(expectedProtectedValue, GetFlatPropertyValue(options, "P4Options.Password"));
        Assert.Equal(TeaBoxPasswordState.Saved, snapshot.PasswordState);
        var serializedSnapshot = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain(selectedPassword, serializedSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain(expectedProtectedValue, serializedSnapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_preserves_unrelated_branches_comments_and_unknown_option_content()
    {
        using var fixture = new TeaBoxSettingsFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.BranchesPath)!);
        var preservedProtectedPassword = fixture.PasswordProtector.Protect("synthetic-preserved-password");
        File.WriteAllText(fixture.BranchesPath, $"""
            <TeaBox><!--synthetic branch root comment-->
              <BranchOptions IsTeaBoxInstancePerBranch="False">
                <Branch DisplayName="Game" Folder="old-root" SccConnection="old:1666,user,workspace" Password="{preservedProtectedPassword}" Initial="G" DisplayColor="1, 2, 3" P4Path="preserve-p4-path">
                  <!--synthetic branch comment--><UnknownBranchNode marker="preserve-branch-node" />
                </Branch>
                <Branch DisplayName="OtherProject" Folder="other-root" SccConnection="other:1666,user,workspace" />
              </BranchOptions>
              <UnknownRootNode marker="preserve-root-node" />
            </TeaBox>
            """);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ProjectPropertiesPath)!);
        File.WriteAllText(fixture.ProjectPropertiesPath, """
            <TeaBoxProperties><!--synthetic options root comment-->
              <Properties>
                <Property name="P4Options.Port" value="old:1666" />
                <Property name="UnknownOptions.PreserveMe" value="preserve-option-value" />
                <Property name="TechnicalOptions.UnknownProperty" value="preserve-technical-value" />
              </Properties>
              <UnknownOptionsGroup marker="preserve-options-node" />
            </TeaBoxProperties>
            """);

        Save(fixture.Store, fixture.Project, CreateSaveRequest(fixture));

        var branches = XDocument.Load(fixture.BranchesPath, LoadOptions.PreserveWhitespace);
        var gameBranch = branches.Descendants("Branch").Single(branch => (string?)branch.Attribute("DisplayName") == "Game");
        Assert.Equal("preserve-p4-path", (string?)gameBranch.Attribute("P4Path"));
        Assert.Equal("preserve-branch-node", (string?)gameBranch.Element("UnknownBranchNode")?.Attribute("marker"));
        Assert.Contains(branches.Descendants("Branch"), branch => (string?)branch.Attribute("DisplayName") == "OtherProject");
        Assert.Equal("preserve-root-node", (string?)branches.Root!.Element("UnknownRootNode")?.Attribute("marker"));
        Assert.Contains(branches.DescendantNodes().OfType<XComment>(), comment => comment.Value == "synthetic branch root comment");
        Assert.Contains(branches.DescendantNodes().OfType<XComment>(), comment => comment.Value == "synthetic branch comment");

        var options = XDocument.Load(fixture.ProjectPropertiesPath, LoadOptions.PreserveWhitespace);
        Assert.Equal("preserve-option-value", GetFlatPropertyValue(options, "UnknownOptions.PreserveMe"));
        Assert.Equal("preserve-technical-value", GetFlatPropertyValue(options, "TechnicalOptions.UnknownProperty"));
        Assert.Equal("preserve-options-node", (string?)options.Root!.Element("UnknownOptionsGroup")?.Attribute("marker"));
        Assert.Contains(options.DescendantNodes().OfType<XComment>(), comment => comment.Value == "synthetic options root comment");
    }

    [Fact]
    public void Save_restores_the_first_native_file_when_the_second_replacement_fails()
    {
        using var fixture = new TeaBoxSettingsFixture();
        fixture.WriteBranch("Game", folder: @"D:\original-root", sccConnection: "old:1666,user,workspace");
        var branchesBefore = File.ReadAllBytes(fixture.BranchesPath);
        var protector = new FixturePasswordProtector
        {
            OnProtect = () => Directory.CreateDirectory(fixture.ProjectPropertiesPath)
        };
        var store = fixture.CreateStore(protector);
        var request = CreateSaveRequest(
            fixture,
            passwordResolution: TeaBoxPasswordResolution.Replace,
            newPassword: "synthetic-rollback-password");

        var exception = Assert.Throws<InvalidOperationException>(() => Save(store, fixture.Project, request));

        Assert.Equal(branchesBefore, File.ReadAllBytes(fixture.BranchesPath));
        Assert.True(Directory.Exists(fixture.ProjectPropertiesPath));
        Assert.DoesNotContain("synthetic-rollback-password", exception.Message, StringComparison.Ordinal);
    }

    private static TeaBoxProjectSettingsSnapshot Save(
        TeaBoxProjectSettingsStore store,
        ProjectConfig project,
        TeaBoxProjectSettingsSaveRequest request) => store.Save(project, request);

    private static TeaBoxProjectSettingsSaveRequest CreateSaveRequest(
        TeaBoxSettingsFixture fixture,
        string server = "p4.synthetic:1666",
        string userName = "synthetic-user",
        string workspace = "synthetic-workspace",
        string workspaceRoot = @"D:\synthetic\workspace",
        string projectFolderName = "SyntheticGameFolder",
        string engineFolder = @"src\Engine",
        TeaBoxFieldResolution[]? resolutions = null,
        TeaBoxPasswordResolution passwordResolution = TeaBoxPasswordResolution.KeepCurrent,
        string? newPassword = null) => new(
            server,
            userName,
            workspace,
            workspaceRoot,
            projectFolderName,
            engineFolder,
            resolutions ?? [],
            passwordResolution,
            newPassword);

    private static void WriteWorkspaceRoot(TeaBoxSettingsFixture fixture, string workspaceRoot) =>
        WriteWrappedProjectOptions(fixture.ProjectPropertiesPath, $"""
            <Property name="TeaBox.Unreal.Configurations.TechnicalOptions">
              <Property name="PerforceRoot" type="String" value="{workspaceRoot}" />
            </Property>
            """);

    private static string? GetFlatPropertyValue(XDocument document, string name)
    {
        var property = Assert.Single(
            document.Descendants("Property"),
            element => (string?)element.Attribute("name") == name);
        return (string?)property.Attribute("value");
    }

    private static string? GetWrappedPropertyValue(XDocument document, string groupName, string propertyName)
    {
        var propertiesContainer = Assert.Single(document.Root!.Elements("Properties"));
        var teaBoxWrapper = Assert.Single(
            propertiesContainer.Elements("Property"),
            element => (string?)element.Attribute("name") == "TeaBox");
        var group = Assert.Single(
            teaBoxWrapper.Elements("Property"),
            element => (string?)element.Attribute("name") == $"TeaBox.Unreal.Configurations.{groupName}");
        var property = Assert.Single(
            group.Elements("Property"),
            element => (string?)element.Attribute("name") == propertyName);
        return (string?)property.Attribute("value");
    }

    private static XElement GetNestedUnrelatedTeaBoxWrapper(XDocument document)
    {
        var propertiesContainer = Assert.Single(document.Root!.Elements("Properties"));
        var teaBoxWrapper = Assert.Single(
            propertiesContainer.Elements("Property"),
            element => (string?)element.Attribute("name") == "TeaBox");
        var unrelatedGroup = Assert.Single(
            teaBoxWrapper.Elements("Property"),
            element => (string?)element.Attribute("name") == "UnrelatedPluginOptions");
        return Assert.Single(
            unrelatedGroup.Descendants("Property"),
            element => (string?)element.Attribute("name") == "TeaBox");
    }

    private static void WriteWrappedProjectOptionsWithNestedTeaBoxCollision(
        string path,
        string directServer,
        string nestedServer) => WriteWrappedProjectOptions(path, $"""
            <Property name="TeaBox.Unreal.Configurations.P4Options">
              <Property name="Port" type="String" value="{directServer}" />
              <Property name="UserName" type="String" value="synthetic-direct-user" />
              <Property name="Client" type="String" value="synthetic-direct-workspace" />
            </Property>
            <Property name="TeaBox.Unreal.Configurations.TechnicalOptions">
              <Property name="PerforceRoot" type="String" value="D:\synthetic-direct-root" />
            </Property>
            <Property name="TeaBox.Unreal.Configurations.UnrealPluginOptions">
              <Property name="ProjectFolderName" type="String" value="DirectFolder" />
              <Property name="EngineFolder" type="String" value="Engine" />
            </Property>
            <Property name="UnrelatedPluginOptions">
              <Property name="TeaBox">
                <Property name="TeaBox.Unreal.Configurations.P4Options">
                  <Property name="Port" type="String" value="{nestedServer}" />
                </Property>
              </Property>
            </Property>
            """);

    private static void WriteWrappedProjectOptions(string path, string wrappedProperties)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var document = string.Concat(
            "<TeaBox>\n",
            "  <Properties>\n",
            "    <Property name=\"TeaBox\">\n",
            wrappedProperties,
            "\n    </Property>\n",
            "  </Properties>\n",
            "</TeaBox>");
        File.WriteAllText(path, document);
    }

    private sealed class TeaBoxSettingsFixture : IDisposable
    {
        private readonly string myRoot = Path.Combine(
            Path.GetTempPath(), "Launcher.App.Tests", "TeaBoxSettings", Guid.NewGuid().ToString("N"));
        private readonly string myTeaBoxDirectory;

        public TeaBoxSettingsFixture()
        {
            var projectRoot = Path.Combine(myRoot, "Workspace", "Game");
            myTeaBoxDirectory = Path.Combine(myRoot, "Tools", "TeaBox");
            var appDataRoot = Path.Combine(myRoot, "Roaming");
            RoamingAppDataRoot = appDataRoot;
            Directory.CreateDirectory(projectRoot);
            Directory.CreateDirectory(myTeaBoxDirectory);
            ProjectFilePath = Path.Combine(projectRoot, "Game.uproject");
            File.WriteAllText(ProjectFilePath, "{}");
            ExecutablePath = Path.Combine(myTeaBoxDirectory, "TeaBox.exe");
            File.WriteAllBytes(ExecutablePath, Array.Empty<byte>());
            BranchesPath = Path.Combine(appDataRoot, "Ubisoft", "TeaBox", "Branches.xml");
            ProjectPropertiesPath = Path.Combine(
                myTeaBoxDirectory, ".teabox", "DeployConfig", "TeaBoxProperties.xml");
            Project = new ProjectConfig
            {
                Id = "game",
                Name = "Game",
                ConfigPath = Path.Combine(myRoot, "game.json"),
                ConfigDirectory = myRoot,
                Root = projectRoot,
                TeaBox = new TeaBoxConfig
                {
                    Executable = ExecutablePath,
                    PlaylistDirectory = Path.Combine(myTeaBoxDirectory, "Playlists"),
                    PlaylistPrefix = string.Empty
                }
            };
            PasswordProtector = new FixturePasswordProtector();
            Store = new TeaBoxProjectSettingsStore(appDataRoot, PasswordProtector);
        }

        public ProjectConfig Project { get; }
        public TeaBoxProjectSettingsStore Store { get; }
        public FixturePasswordProtector PasswordProtector { get; }
        public string RoamingAppDataRoot { get; }

        public TeaBoxProjectSettingsStore CreateStore(ITeaBoxPasswordProtector passwordProtector) =>
            new(RoamingAppDataRoot, passwordProtector);
        public string BranchesPath { get; }
        public string ProjectPropertiesPath { get; }
        public string ProjectFilePath { get; }
        public string ExecutablePath { get; }

        public void WriteBranch(
            string displayName,
            string? folder = null,
            string? sccConnection = null,
            string? password = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BranchesPath)!);
            var branch = new System.Xml.Linq.XElement("Branch",
                new System.Xml.Linq.XAttribute("DisplayName", displayName));
            AddAttribute(branch, "Folder", folder);
            AddAttribute(branch, "SccConnection", sccConnection);
            AddAttribute(branch, "Password", password);
            System.Xml.Linq.XDocument document;
            if (File.Exists(BranchesPath))
            {
                document = System.Xml.Linq.XDocument.Load(BranchesPath);
                document.Root!.Element("BranchOptions")!.Add(branch);
            }
            else
            {
                document = new System.Xml.Linq.XDocument(
                    new System.Xml.Linq.XElement("TeaBox",
                        new System.Xml.Linq.XElement("BranchOptions",
                            new System.Xml.Linq.XAttribute("IsTeaBoxInstancePerBranch", "False"),
                            branch)));
            }

            document.Save(BranchesPath);
        }

        public void WriteProjectOptions(params (string Name, string? Value)[] properties)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectPropertiesPath)!);
            var propertyElements = properties.Select(property =>
            {
                var element = new System.Xml.Linq.XElement("Property",
                    new System.Xml.Linq.XAttribute("name", property.Name));
                AddAttribute(element, "value", property.Value);
                return element;
            });
            var document = new System.Xml.Linq.XDocument(
                new System.Xml.Linq.XElement("TeaBoxProperties",
                    new System.Xml.Linq.XElement("Properties", propertyElements)));
            document.Save(ProjectPropertiesPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(myRoot))
                Directory.Delete(myRoot, recursive: true);
        }

        private static void AddAttribute(System.Xml.Linq.XElement element, string name, string? value)
        {
            if (value is not null)
                element.Add(new System.Xml.Linq.XAttribute(name, value));
        }
    }

    private sealed class FixturePasswordProtector : ITeaBoxPasswordProtector
    {
        private const string ProtectedPrefix = "fixture-protected:";

        public Action? OnProtect { get; init; }

        public string Protect(string plaintext)
        {
            OnProtect?.Invoke();
            return ProtectedPrefix + plaintext;
        }

        public string Unprotect(string protectedValue)
        {
            if (!protectedValue.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
                throw new InvalidDataException("Synthetic fixture password cannot be unprotected.");

            return protectedValue[ProtectedPrefix.Length..];
        }
    }
}
