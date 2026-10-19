using System.IO;
using System.Xml;
using System.Xml.Linq;
using Launcher.Core.Configuration;

namespace Launcher.App.Settings;

public sealed class TeaBoxProjectSettingsStore : ITeaBoxProjectSettingsStore
{
    private static readonly object mySettingsLock = new();

    private readonly string myRoamingAppDataRoot;
    private readonly ITeaBoxPasswordProtector myPasswordProtector;

    public TeaBoxProjectSettingsStore(
        string roamingAppDataRoot,
        ITeaBoxPasswordProtector passwordProtector)
    {
        if (string.IsNullOrWhiteSpace(roamingAppDataRoot))
            throw new ArgumentException("The roaming application data root is required.", nameof(roamingAppDataRoot));

        myRoamingAppDataRoot = ResolveFullPath(roamingAppDataRoot, "The roaming application data path is invalid.");
        myPasswordProtector = passwordProtector ?? throw new ArgumentNullException(nameof(passwordProtector));
    }

    public TeaBoxProjectSettingsSnapshot Load(ProjectConfig project)
    {
        lock (mySettingsLock)
            return LoadWithoutLock(project);
    }

    public TeaBoxPerforceCredentials LoadPerforceCredentials(ProjectConfig project)
    {
        lock (mySettingsLock)
        {
            ArgumentNullException.ThrowIfNull(project);
            if (!string.IsNullOrWhiteSpace(project.Error))
                throw new InvalidDataException("The selected project configuration is invalid.");

            var projectRoot = ResolveProjectRoot(project.Root);
            var projectName = ResolveProjectName(projectRoot);
            var teaBoxExecutableDirectory = ResolveTeaBoxExecutableDirectory(project.TeaBox);
            var branchesPath = Path.Combine(myRoamingAppDataRoot, "Ubisoft", "TeaBox", "Branches.xml");
            var projectPropertiesPath = Path.Combine(
                teaBoxExecutableDirectory, ".teabox", "DeployConfig", "TeaBoxProperties.xml");

            var branchesDocument = LoadDocumentIfPresent(branchesPath, "branch settings");
            var propertiesDocument = LoadDocumentIfPresent(projectPropertiesPath, "project options");
            var branch = FindMatchingBranch(branchesDocument, projectName);
            var connection = ParseConnection(branch?.SccConnection);

            var server = RequirePerforceConnectionValue(MergeValues(
                connection?.Server,
                ReadPropertyValue(propertiesDocument, "P4Options", "Port")));
            var userName = RequirePerforceConnectionValue(MergeValues(
                connection?.UserName,
                ReadPropertyValue(propertiesDocument, "P4Options", "UserName")));
            var workspace = RequirePerforceConnectionValue(MergeValues(
                connection?.Workspace,
                ReadPropertyValue(propertiesDocument, "P4Options", "Client")));

            var branchPassword = branch?.Password;
            var projectPassword = ReadPropertyValue(propertiesDocument, "P4Options", "Password");
            var hasBranchPassword = !string.IsNullOrWhiteSpace(branchPassword);
            var hasProjectPassword = !string.IsNullOrWhiteSpace(projectPassword);
            if (!hasBranchPassword && !hasProjectPassword)
                throw new InvalidDataException("A saved TeaBox Perforce password is unavailable.");

            var unprotectedBranchPassword = hasBranchPassword
                ? UnprotectSavedPerforcePassword(branchPassword!)
                : null;
            var unprotectedProjectPassword = hasProjectPassword
                ? UnprotectSavedPerforcePassword(projectPassword!)
                : null;
            if (hasBranchPassword && hasProjectPassword && !string.Equals(
                unprotectedBranchPassword,
                unprotectedProjectPassword,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException("The saved TeaBox Perforce passwords conflict.");
            }

            return new TeaBoxPerforceCredentials(
                server,
                userName,
                workspace,
                unprotectedBranchPassword ?? unprotectedProjectPassword!);
        }
    }

    private TeaBoxProjectSettingsSnapshot LoadWithoutLock(ProjectConfig project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!string.IsNullOrWhiteSpace(project.Error))
            throw new InvalidDataException("The selected project configuration is invalid.");

        var projectRoot = ResolveProjectRoot(project.Root);
        var projectName = ResolveProjectName(projectRoot);
        var teaBoxExecutableDirectory = ResolveTeaBoxExecutableDirectory(project.TeaBox);
        var branchesPath = Path.Combine(myRoamingAppDataRoot, "Ubisoft", "TeaBox", "Branches.xml");
        var projectPropertiesPath = Path.Combine(
            teaBoxExecutableDirectory, ".teabox", "DeployConfig", "TeaBoxProperties.xml");

        var branchesDocument = LoadDocumentIfPresent(branchesPath, "branch settings");
        var propertiesDocument = LoadDocumentIfPresent(projectPropertiesPath, "project options");
        var branch = FindMatchingBranch(branchesDocument, projectName);
        var connection = ParseConnection(branch?.SccConnection);

        var projectServer = ReadPropertyValue(propertiesDocument, "P4Options", "Port");
        var projectUserName = ReadPropertyValue(propertiesDocument, "P4Options", "UserName");
        var projectWorkspace = ReadPropertyValue(propertiesDocument, "P4Options", "Client");
        var projectWorkspaceRoot = ReadPropertyValue(propertiesDocument, "TechnicalOptions", "PerforceRoot");
        var projectFolderName = ReadPropertyValue(propertiesDocument, "UnrealPluginOptions", "ProjectFolderName");
        var engineFolder = ReadPropertyValue(propertiesDocument, "UnrealPluginOptions", "EngineFolder");
        var engineFolderForRole = NormalizeEngineFolder(engineFolder);

        var branchPassword = branch?.Password;
        var projectPassword = ReadPropertyValue(propertiesDocument, "P4Options", "Password");
        return new TeaBoxProjectSettingsSnapshot(
            projectName,
            MergeValues(connection?.Server, projectServer),
            MergeValues(connection?.UserName, projectUserName),
            MergeValues(connection?.Workspace, projectWorkspace),
            MergeWorkspaceRoots(branch?.Folder, projectWorkspaceRoot),
            NormalizeValue(projectFolderName) ?? projectRoot,
            engineFolder ?? string.Empty,
            GetEngineFolderRole(engineFolderForRole),
            GetPasswordState(branchPassword, projectPassword));
    }

    public TeaBoxProjectSettingsSnapshot Save(
        ProjectConfig project,
        TeaBoxProjectSettingsSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequiredSaveValue(request.EngineFolder, "EngineFolder");

        lock (mySettingsLock)
        {
            var snapshot = LoadWithoutLock(project);
            var teaBoxExecutableDirectory = ResolveTeaBoxExecutableDirectory(project.TeaBox);
            var branchesPath = Path.Combine(myRoamingAppDataRoot, "Ubisoft", "TeaBox", "Branches.xml");
            var projectPropertiesPath = Path.Combine(
                teaBoxExecutableDirectory, ".teabox", "DeployConfig", "TeaBoxProperties.xml");

            var branchesDocument = LoadDocumentIfPresent(branchesPath, "branch settings");
            var propertiesDocument = LoadDocumentIfPresent(projectPropertiesPath, "project options");
            var branch = FindMatchingBranchElement(branchesDocument, snapshot.ProjectName);
            var branchPassword = GetAttributeValue(branch, "Password");
            var projectPassword = ReadPropertyValue(propertiesDocument, "P4Options", "Password");

            var values = ResolveFieldValues(snapshot, request);
            var protectedPassword = ResolvePassword(
                snapshot.PasswordState,
                branchPassword,
                projectPassword,
                request);

            branchesDocument ??= new XDocument(new XElement("TeaBox"));
            propertiesDocument ??= CreateProjectPropertiesDocument();
            var branchOptions = GetOrCreateBranchOptions(branchesDocument);
            var isNewBranch = branch is null;
            branch ??= new XElement(branchOptions.Name.Namespace + "Branch");
            if (isNewBranch)
            {
                SetAttributeValueIgnoreCase(branch, "Initial", snapshot.ProjectName[..1].ToUpperInvariant());
                SetAttributeValueIgnoreCase(branch, "DisplayColor", "128, 128, 128");
                branchOptions.Add(branch);
            }

            SetAttributeValueIgnoreCase(branch, "Folder", request.WorkspaceRoot);
            SetAttributeValueIgnoreCase(branch, "DisplayName", snapshot.ProjectName);
            SetAttributeValueIgnoreCase(
                branch,
                "SccConnection",
                string.Join(',', values.PerforceServer, values.PerforceUserName, values.PerforceWorkspace));
            SetAttributeValueIgnoreCase(branch, "Password", protectedPassword);

            SetProjectOptionValue(propertiesDocument, "P4Options.Port", values.PerforceServer);
            SetProjectOptionValue(propertiesDocument, "P4Options.UserName", values.PerforceUserName);
            SetProjectOptionValue(propertiesDocument, "P4Options.Client", values.PerforceWorkspace);
            SetProjectOptionValue(propertiesDocument, "P4Options.Password", protectedPassword);
            SetProjectOptionValue(propertiesDocument, "TechnicalOptions.PerforceRoot", request.WorkspaceRoot);
            SetProjectOptionValue(propertiesDocument, "UnrealPluginOptions.ProjectName", snapshot.ProjectName);
            SetProjectOptionValue(
                propertiesDocument,
                "UnrealPluginOptions.ProjectFolderName",
                request.ProjectFolderName);
            SetProjectOptionValue(
                propertiesDocument,
                "UnrealPluginOptions.EngineFolder",
                request.EngineFolder);

            WriteSettingsPair(
                branchesPath,
                SerializeDocument(branchesDocument),
                "branch settings",
                projectPropertiesPath,
                SerializeDocument(propertiesDocument),
                "project options");

            return LoadWithoutLock(project);
        }
    }

    private static ResolvedSettingsFields ResolveFieldValues(
        TeaBoxProjectSettingsSnapshot snapshot,
        TeaBoxProjectSettingsSaveRequest request)
    {
        var fields = new Dictionary<string, TeaBoxFieldValue>(StringComparer.Ordinal)
        {
            ["PerforceServer"] = snapshot.PerforceServer,
            ["PerforceUserName"] = snapshot.PerforceUserName,
            ["PerforceWorkspace"] = snapshot.PerforceWorkspace,
            ["WorkspaceRoot"] = snapshot.WorkspaceRoot
        };
        var conflicts = fields
            .Where(pair => IsConflict(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var resolutions = new Dictionary<string, TeaBoxFieldSource>(StringComparer.Ordinal);
        if (request.Resolutions is null)
            throw new InvalidDataException("TeaBox settings conflict resolutions are invalid.");

        foreach (var resolution in request.Resolutions)
        {
            if (resolution is null || string.IsNullOrWhiteSpace(resolution.Field) ||
                !conflicts.ContainsKey(resolution.Field) || !Enum.IsDefined(resolution.Source) ||
                !resolutions.TryAdd(resolution.Field, resolution.Source))
            {
                throw new InvalidDataException("TeaBox settings conflict resolutions are invalid.");
            }
        }

        foreach (var field in conflicts.Keys)
        {
            if (!resolutions.ContainsKey(field))
                throw new InvalidDataException("Resolve every conflicting TeaBox setting before saving.");
        }

        var server = ResolveField("PerforceServer", request.PerforceServer, conflicts, resolutions);
        var userName = ResolveField("PerforceUserName", request.PerforceUserName, conflicts, resolutions);
        var workspace = ResolveField("PerforceWorkspace", request.PerforceWorkspace, conflicts, resolutions);
        var workspaceRoot = ResolveField("WorkspaceRoot", request.WorkspaceRoot, conflicts, resolutions);

        ValidateRequiredSaveValue(server, "PerforceServer", containsComma: true);
        ValidateRequiredSaveValue(userName, "PerforceUserName", containsComma: true);
        ValidateRequiredSaveValue(workspace, "PerforceWorkspace", containsComma: true);
        ValidateRequiredSaveValue(workspaceRoot, "WorkspaceRoot");
        ValidateRequiredSaveValue(request.ProjectFolderName, "ProjectFolderName");

        return new ResolvedSettingsFields(server, userName, workspace, workspaceRoot);
    }

    private static string ResolveField(
        string fieldName,
        string requestValue,
        IReadOnlyDictionary<string, TeaBoxFieldValue> conflicts,
        IReadOnlyDictionary<string, TeaBoxFieldSource> resolutions)
    {
        if (!conflicts.TryGetValue(fieldName, out var conflict))
            return requestValue;

        var selectedSource = resolutions[fieldName];
        var selectedValue = selectedSource switch
        {
            TeaBoxFieldSource.Branch => conflict.BranchValue,
            TeaBoxFieldSource.ProjectOptions => conflict.ProjectOptionsValue,
            TeaBoxFieldSource.Custom => requestValue,
            _ => null
        };
        if (selectedValue is null || !string.Equals(requestValue, selectedValue, StringComparison.Ordinal))
            throw new InvalidDataException("The selected TeaBox setting does not match its declared source.");

        return requestValue;
    }

    private static bool IsConflict(TeaBoxFieldValue value) =>
        value.BranchValue is not null && value.ProjectOptionsValue is not null &&
        !string.Equals(value.BranchValue, value.ProjectOptionsValue, StringComparison.Ordinal);

    private static void ValidateRequiredSaveValue(
        string? value,
        string fieldName,
        bool containsComma = false)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            (containsComma && value.Contains(',')))
        {
            throw new InvalidDataException($"The TeaBox {fieldName} setting is invalid.");
        }
    }

    private string? ResolvePassword(
        TeaBoxPasswordState passwordState,
        string? branchPassword,
        string? projectPassword,
        TeaBoxProjectSettingsSaveRequest request)
    {
        if (request.PasswordResolution != TeaBoxPasswordResolution.Replace && request.NewPassword is not null)
            throw new InvalidDataException("A new TeaBox password requires the Replace resolution.");

        return request.PasswordResolution switch
        {
            TeaBoxPasswordResolution.KeepCurrent when passwordState == TeaBoxPasswordState.Missing => null,
            TeaBoxPasswordResolution.KeepCurrent when passwordState == TeaBoxPasswordState.Saved =>
                ProtectPassword(UnprotectPassword(
                    !string.IsNullOrWhiteSpace(branchPassword) ? branchPassword : projectPassword)),
            TeaBoxPasswordResolution.KeepCurrent =>
                throw new InvalidDataException("Choose a readable TeaBox password source or replace the password."),
            TeaBoxPasswordResolution.UseBranch =>
                ProtectPassword(UnprotectPassword(RequireProtectedPassword(branchPassword))),
            TeaBoxPasswordResolution.UseProjectOptions =>
                ProtectPassword(UnprotectPassword(RequireProtectedPassword(projectPassword))),
            TeaBoxPasswordResolution.Replace when !string.IsNullOrWhiteSpace(request.NewPassword) =>
                ProtectPassword(request.NewPassword),
            TeaBoxPasswordResolution.Replace =>
                throw new InvalidDataException("A replacement TeaBox password is required."),
            _ => throw new InvalidDataException("The TeaBox password resolution is invalid.")
        };
    }

    private static string RequireProtectedPassword(string? protectedPassword) =>
        string.IsNullOrWhiteSpace(protectedPassword)
            ? throw new InvalidDataException("The selected TeaBox password source is unavailable.")
            : protectedPassword;

    private string UnprotectPassword(string? protectedPassword)
    {
        if (string.IsNullOrWhiteSpace(protectedPassword))
            throw new InvalidDataException("The current TeaBox password is unavailable.");

        try
        {
            return myPasswordProtector.Unprotect(protectedPassword);
        }
        catch (Exception)
        {
            throw new InvalidDataException("The selected TeaBox password could not be read.");
        }
    }

    private string UnprotectSavedPerforcePassword(string protectedPassword)
    {
        string password;
        try
        {
            password = myPasswordProtector.Unprotect(protectedPassword);
        }
        catch (Exception)
        {
            throw new InvalidDataException("The saved TeaBox Perforce password could not be read.");
        }

        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidDataException("A saved TeaBox Perforce password is unavailable.");

        return password;
    }

    private string ProtectPassword(string plaintext)
    {
        try
        {
            return myPasswordProtector.Protect(plaintext);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The TeaBox password could not be protected.");
        }
    }

    private static XElement? FindMatchingBranchElement(XDocument? document, string projectName)
    {
        if (document?.Root is null)
            return null;

        var matchingBranches = document.Root
            .DescendantsAndSelf()
            .Where(element => IsNamed(element, "Branch"))
            .Where(element => string.Equals(
                GetAttributeValue(element, "DisplayName"),
                projectName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matchingBranches.Length > 1)
            throw new InvalidDataException("Multiple TeaBox branches match this project.");

        return matchingBranches.SingleOrDefault();
    }

    private static XElement GetOrCreateBranchOptions(XDocument document)
    {
        var root = EnsureRoot(document, "TeaBox");
        var branchOptions = root
            .DescendantsAndSelf()
            .Where(element => IsNamed(element, "BranchOptions"))
            .ToArray();
        if (branchOptions.Length > 1)
            throw new InvalidDataException("TeaBox branch settings contain duplicate branch options.");
        if (branchOptions.Length == 1)
            return branchOptions[0];

        var createdBranchOptions = new XElement(root.Name.Namespace + "BranchOptions",
            new XAttribute("IsTeaBoxInstancePerBranch", "False"));
        root.Add(createdBranchOptions);
        return createdBranchOptions;
    }

    private static XElement EnsureRoot(XDocument document, string rootName)
    {
        if (document.Root is not null)
            return document.Root;

        var root = new XElement(rootName);
        document.Add(root);
        return root;
    }

    private static XDocument CreateProjectPropertiesDocument() =>
        new XDocument(
            new XElement("TeaBox",
                new XElement("Properties",
                    new XElement("Property", new XAttribute("name", "TeaBox")))));

    private static XElement? FindTeaBoxRootWrapper(XElement root)
    {
        if (!IsNamed(root, "TeaBox"))
            return null;

        var propertiesContainers = root.Elements()
            .Where(element => IsNamed(element, "Properties"))
            .ToArray();
        if (propertiesContainers.Length > 1)
            throw new InvalidDataException("The TeaBox project options contain duplicate Properties containers.");
        if (propertiesContainers.Length == 0)
            return null;

        var wrappers = propertiesContainers[0]
            .Elements()
            .Where(element => IsNamed(element, "Property"))
            .Where(element => string.Equals(
                GetAttributeValue(element, "name"),
                "TeaBox",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (wrappers.Length > 1)
            throw new InvalidDataException("The TeaBox project options contain duplicate root wrappers.");

        return wrappers.SingleOrDefault();
    }

    private static void SetWrappedProjectOptionValue(
        XElement teaBoxWrapper,
        string qualifiedGroupName,
        string propertyName,
        string? value)
    {
        var groups = teaBoxWrapper
            .Elements()
            .Where(element => IsNamed(element, "Property"))
            .Where(element => string.Equals(
                GetAttributeValue(element, "name"),
                qualifiedGroupName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (groups.Length > 1)
            throw new InvalidDataException("The TeaBox project options contain a duplicate option group.");

        var group = groups.SingleOrDefault();
        if (group is null)
        {
            group = new XElement(teaBoxWrapper.Name.Namespace + "Property",
                new XAttribute("name", qualifiedGroupName));
            teaBoxWrapper.Add(group);
        }

        var properties = group
            .Elements()
            .Where(element => IsNamed(element, "Property"))
            .Where(element => string.Equals(
                GetAttributeValue(element, "name"),
                propertyName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (properties.Length > 1)
            throw new InvalidDataException("The TeaBox project options contain a duplicate property.");
        if (properties.Length == 1)
        {
            SetPropertyElementValue(properties[0], value);
            return;
        }

        group.Add(new XElement(group.Name.Namespace + "Property",
            new XAttribute("name", propertyName),
            new XAttribute("type", "String"),
            new XAttribute("value", value ?? string.Empty)));
    }

    private static void SetProjectOptionValue(XDocument document, string fullPropertyName, string? value)
    {
        var root = EnsureRoot(document, "TeaBox");
        var separator = fullPropertyName.IndexOf('.');
        if (separator <= 0 || separator == fullPropertyName.Length - 1)
            throw new InvalidDataException("A TeaBox project option name is invalid.");

        var groupName = fullPropertyName[..separator];
        var propertyName = fullPropertyName[(separator + 1)..];
        var qualifiedGroupName = $"TeaBox.Unreal.Configurations.{groupName}";
        var flatNames = new[] { fullPropertyName, $"TeaBox.Unreal.Configurations.{fullPropertyName}" };
        var teaBoxWrapper = FindTeaBoxRootWrapper(root);
        if (teaBoxWrapper is not null)
        {
            SetWrappedProjectOptionValue(teaBoxWrapper, qualifiedGroupName, propertyName, value);
            return;
        }

        var flatProperties = root
            .DescendantsAndSelf()
            .Where(element => IsNamed(element, "Property"))
            .Where(element => flatNames.Any(name => string.Equals(
                GetAttributeValue(element, "name"),
                name,
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (flatProperties.Length > 1)
            throw new InvalidDataException("The TeaBox project options contain a duplicate property.");
        if (flatProperties.Length == 1)
        {
            SetPropertyElementValue(flatProperties[0], value);
            return;
        }

        var directProperties = root
            .DescendantsAndSelf()
            .Where(element => flatNames.Any(name => IsNamed(element, name)))
            .ToArray();
        if (directProperties.Length > 1)
            throw new InvalidDataException("The TeaBox project options contain a duplicate property.");
        if (directProperties.Length == 1)
        {
            SetPropertyElementValue(directProperties[0], value);
            return;
        }

        var groups = root
            .DescendantsAndSelf()
            .Where(element => IsNamed(element, groupName) || IsNamed(element, qualifiedGroupName))
            .ToArray();
        if (groups.Length > 1)
            throw new InvalidDataException("The TeaBox project options contain a duplicate option group.");
        if (groups.Length == 1)
        {
            var group = groups[0];
            var propertyElements = group
                .DescendantsAndSelf()
                .Where(element => IsNamed(element, propertyName))
                .ToArray();
            var namedProperties = group
                .DescendantsAndSelf()
                .Where(element => IsNamed(element, "Property"))
                .Where(element => string.Equals(
                    GetAttributeValue(element, "name"),
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (propertyElements.Length + namedProperties.Length > 1)
                throw new InvalidDataException("The TeaBox project options contain a duplicate property.");
            if (propertyElements.Length == 1)
            {
                SetPropertyElementValue(propertyElements[0], value);
                return;
            }
            if (namedProperties.Length == 1)
            {
                SetPropertyElementValue(namedProperties[0], value);
                return;
            }
            if (GetAttributeValue(group, propertyName) is not null)
            {
                SetAttributeValueIgnoreCase(group, propertyName, value ?? string.Empty);
                return;
            }

            group.Add(new XElement(group.Name.Namespace + "Property",
                new XAttribute("name", propertyName),
                new XAttribute("value", value ?? string.Empty)));
            return;
        }

        var propertiesContainers = root
            .DescendantsAndSelf()
            .Where(element => IsNamed(element, "Properties"))
            .ToArray();
        var propertiesContainer = propertiesContainers.FirstOrDefault();
        if (propertiesContainer is null)
        {
            propertiesContainer = new XElement(root.Name.Namespace + "Properties");
            root.Add(propertiesContainer);
        }

        propertiesContainer.Add(new XElement(propertiesContainer.Name.Namespace + "Property",
            new XAttribute("name", fullPropertyName),
            new XAttribute("value", value ?? string.Empty)));
    }

    private static void SetPropertyElementValue(XElement element, string? value)
    {
        var valueAttribute = element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals("value", StringComparison.OrdinalIgnoreCase));
        if (valueAttribute is not null)
        {
            valueAttribute.Value = value ?? string.Empty;
            return;
        }

        var textNodes = element.Nodes().OfType<XText>().ToArray();
        if (textNodes.Length != 0 || !element.Nodes().Any())
        {
            foreach (var textNode in textNodes)
                textNode.Remove();
            if (!string.IsNullOrEmpty(value))
                element.AddFirst(new XText(value));
            return;
        }

        element.SetAttributeValue("value", value ?? string.Empty);
    }

    private static byte[] SerializeDocument(XDocument document)
    {
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            NewLineHandling = NewLineHandling.None,
            CloseOutput = false
        };
        using (var writer = XmlWriter.Create(stream, settings))
            document.Save(writer);
        return stream.ToArray();
    }

    private static void WriteSettingsPair(
        string firstPath,
        byte[] firstContent,
        string firstDescription,
        string secondPath,
        byte[] secondContent,
        string secondDescription)
    {
        var targets = new[]
        {
            new SettingsWriteTarget(firstPath, firstContent, firstDescription),
            new SettingsWriteTarget(secondPath, secondContent, secondDescription)
        };
        var originals = new byte[]?[targets.Length];
        var temporaryPaths = new string?[targets.Length];
        var replacedTargets = new bool[targets.Length];

        try
        {
            for (var index = 0; index < targets.Length; index++)
            {
                var target = targets[index];
                var directory = Path.GetDirectoryName(target.Path);
                if (string.IsNullOrWhiteSpace(directory))
                    throw new IOException();

                Directory.CreateDirectory(directory);
                if (File.Exists(target.Path))
                    originals[index] = File.ReadAllBytes(target.Path);

                temporaryPaths[index] = Path.Combine(
                    directory,
                    $".{Path.GetFileName(target.Path)}.{Guid.NewGuid():N}.tmp");
                File.WriteAllBytes(temporaryPaths[index]!, target.Content);
            }

            for (var index = 0; index < targets.Length; index++)
            {
                File.Move(temporaryPaths[index]!, targets[index].Path, overwrite: true);
                temporaryPaths[index] = null;
                replacedTargets[index] = true;
            }
        }
        catch (Exception)
        {
            var rollbackFailures = new List<string>();
            for (var index = targets.Length - 1; index >= 0; index--)
            {
                if (!replacedTargets[index])
                    continue;

                try
                {
                    RestoreFile(targets[index].Path, originals[index]);
                }
                catch (Exception)
                {
                    rollbackFailures.Add(targets[index].Description);
                }
            }

            if (rollbackFailures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"TeaBox settings could not be saved, and rollback failed for {string.Join(" and ", rollbackFailures)}.");
            }

            throw new InvalidOperationException("TeaBox settings could not be saved; previous files were restored.");
        }
        finally
        {
            foreach (var temporaryPath in temporaryPaths)
                TryDeleteFile(temporaryPath);
        }
    }

    private static void RestoreFile(string path, byte[]? originalContent)
    {
        if (originalContent is null)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new IOException();
        var restorePath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.rollback");
        try
        {
            File.WriteAllBytes(restorePath, originalContent);
            File.Move(restorePath, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(restorePath);
            File.WriteAllBytes(path, originalContent);
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (path is null)
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Temporary files are best-effort cleanup after the original error.
        }
    }

    private static string ResolveProjectRoot(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new InvalidDataException("The selected project has no configured root.");

        var fullPath = ResolveFullPath(projectRoot, "The selected project root is invalid.");
        if (!Directory.Exists(fullPath))
            throw new InvalidDataException("The selected project root does not exist.");

        return fullPath;
    }

    private static string ResolveProjectName(string projectRoot)
    {
        string[] projectFiles;
        try
        {
            projectFiles = Directory.GetFiles(projectRoot)
                .Where(filePath => Path.GetExtension(filePath).Equals(".uproject", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("The selected project root could not be read.");
        }

        if (projectFiles.Length != 1)
            throw new InvalidDataException("The selected project root must contain exactly one top-level .uproject file.");

        var projectName = Path.GetFileNameWithoutExtension(projectFiles[0]);
        if (string.IsNullOrWhiteSpace(projectName))
            throw new InvalidDataException("The selected project has an invalid .uproject file name.");

        return projectName;
    }

    private static string ResolveTeaBoxExecutableDirectory(TeaBoxConfig? teaBox)
    {
        if (teaBox is null || string.IsNullOrWhiteSpace(teaBox.Executable))
            throw new InvalidDataException("The selected project has no configured TeaBox executable.");

        var executablePath = ResolveFullPath(teaBox.Executable, "The configured TeaBox executable path is invalid.");
        if (!File.Exists(executablePath))
            throw new InvalidDataException("The configured TeaBox executable does not exist.");

        var executableDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(executableDirectory) || !Directory.Exists(executableDirectory))
            throw new InvalidDataException("The configured TeaBox executable path is invalid.");

        return executableDirectory;
    }

    private static XDocument? LoadDocumentIfPresent(string path, string description)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = false
            };
            using var reader = XmlReader.Create(path, settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is XmlException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidDataException($"The TeaBox {description} XML is invalid or unreadable.");
        }
    }

    private static TeaBoxBranch? FindMatchingBranch(XDocument? document, string projectName)
    {
        if (document?.Root is null)
            return null;

        var matchingBranches = document.Root
            .DescendantsAndSelf()
            .Where(element => IsNamed(element, "Branch"))
            .Where(element => string.Equals(
                GetAttributeValue(element, "DisplayName"),
                projectName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matchingBranches.Length > 1)
            throw new InvalidDataException("Multiple TeaBox branches match this project.");

        var matchingBranch = matchingBranches.SingleOrDefault();
        return matchingBranch is null
            ? null
            : new TeaBoxBranch(
                GetAttributeValue(matchingBranch, "Folder"),
                GetAttributeValue(matchingBranch, "DisplayName"),
                GetAttributeValue(matchingBranch, "SccConnection"),
                GetAttributeValue(matchingBranch, "Password"),
                GetAttributeValue(matchingBranch, "Initial"),
                GetAttributeValue(matchingBranch, "DisplayColor"));
    }

    private static P4Connection? ParseConnection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value.Split(',');
        if (parts.Length != 3)
            throw new InvalidDataException("The matching TeaBox branch has a malformed SccConnection value.");

        var server = parts[0];
        var userName = parts[1];
        var workspace = parts[2];
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(workspace))
            throw new InvalidDataException("The matching TeaBox branch has a malformed SccConnection value.");

        return new P4Connection(server, userName, workspace);
    }

    private static string? ReadWrappedProjectOptionValue(
        XElement root,
        string qualifiedGroupName,
        string propertyName)
    {
        var wrapper = FindTeaBoxRootWrapper(root);
        if (wrapper is null)
            return null;

        var groups = wrapper
            .Elements()
            .Where(element => IsNamed(element, "Property"))
            .Where(element => string.Equals(
                GetAttributeValue(element, "name"),
                qualifiedGroupName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (groups.Length > 1)
            throw new InvalidDataException("The TeaBox project options contain a duplicate option group.");
        if (groups.Length == 0)
            return null;

        var properties = groups[0]
            .Elements()
            .Where(element => IsNamed(element, "Property"))
            .Where(element => string.Equals(
                GetAttributeValue(element, "name"),
                propertyName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (properties.Length > 1)
            throw new InvalidDataException("The TeaBox project options contain a duplicate property.");

        return properties.Length == 1 ? GetPropertyElementValue(properties[0]) : null;
    }

    private static string? ReadPropertyValue(XDocument? document, string groupName, string propertyName)
    {
        if (document?.Root is null)
            return null;

        var qualifiedGroupName = $"TeaBox.Unreal.Configurations.{groupName}";
        var wrappedValue = ReadWrappedProjectOptionValue(document.Root, qualifiedGroupName, propertyName);
        if (wrappedValue is not null)
            return wrappedValue;

        var propertyNames = new[]
        {
            $"{groupName}.{propertyName}",
            $"{qualifiedGroupName}.{propertyName}"
        };
        var flatProperties = document.Root
            .DescendantsAndSelf()
            .Where(element => IsNamed(element, "Property"))
            .Where(element => propertyNames.Any(property => string.Equals(
                GetAttributeValue(element, "name"),
                property,
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (flatProperties.Length > 1)
            throw new InvalidDataException("The TeaBox project options contain a duplicate property.");
        if (flatProperties.Length == 1)
            return GetPropertyElementValue(flatProperties[0]);

        var qualifiedElements = document.Root
            .DescendantsAndSelf()
            .Where(element => propertyNames.Any(property => IsNamed(element, property)))
            .ToArray();
        if (qualifiedElements.Length > 1)
            throw new InvalidDataException("The TeaBox project options contain a duplicate property.");
        if (qualifiedElements.Length == 1)
            return GetPropertyElementValue(qualifiedElements[0]);

        var groups = document.Root
            .DescendantsAndSelf()
            .Where(element => IsNamed(element, groupName) || IsNamed(element, qualifiedGroupName))
            .ToArray();
        if (groups.Length > 1)
            throw new InvalidDataException("The TeaBox project options contain a duplicate option group.");
        if (groups.Length == 0)
            return null;

        var group = groups[0];
        var propertyElements = group
            .DescendantsAndSelf()
            .Where(element => IsNamed(element, propertyName))
            .ToArray();
        var namedProperties = group
            .DescendantsAndSelf()
            .Where(element => IsNamed(element, "Property"))
            .Where(element => string.Equals(
                GetAttributeValue(element, "name"),
                propertyName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (propertyElements.Length + namedProperties.Length > 1)
            throw new InvalidDataException("The TeaBox project options contain a duplicate property.");
        if (propertyElements.Length == 1)
            return GetPropertyElementValue(propertyElements[0]);
        if (namedProperties.Length == 1)
            return GetPropertyElementValue(namedProperties[0]);

        return GetAttributeValue(group, propertyName);
    }

    private static string? GetPropertyElementValue(XElement element)
    {
        var valueAttribute = element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals("value", StringComparison.OrdinalIgnoreCase));
        return valueAttribute is null ? element.Value : valueAttribute.Value;
    }

    private TeaBoxPasswordState GetPasswordState(string? branchPassword, string? projectPassword)
    {
        var hasBranchPassword = !string.IsNullOrWhiteSpace(branchPassword);
        var hasProjectPassword = !string.IsNullOrWhiteSpace(projectPassword);
        if (!hasBranchPassword && !hasProjectPassword)
            return TeaBoxPasswordState.Missing;

        string? unprotectedBranchPassword = null;
        string? unprotectedProjectPassword = null;
        try
        {
            if (hasBranchPassword)
                unprotectedBranchPassword = myPasswordProtector.Unprotect(branchPassword!);
            if (hasProjectPassword)
                unprotectedProjectPassword = myPasswordProtector.Unprotect(projectPassword!);
        }
        catch (Exception)
        {
            return TeaBoxPasswordState.Unreadable;
        }

        if (hasBranchPassword && hasProjectPassword && !string.Equals(
            unprotectedBranchPassword,
            unprotectedProjectPassword,
            StringComparison.Ordinal))
        {
            return TeaBoxPasswordState.Conflict;
        }

        return TeaBoxPasswordState.Saved;
    }

    private static TeaBoxFieldValue MergeWorkspaceRoots(string? branchValue, string? projectOptionsValue)
    {
        if (string.IsNullOrWhiteSpace(branchValue))
            return new TeaBoxFieldValue(
                string.IsNullOrWhiteSpace(projectOptionsValue) ? null : projectOptionsValue,
                null,
                null);
        if (string.IsNullOrWhiteSpace(projectOptionsValue))
            return new TeaBoxFieldValue(branchValue, null, null);
        if (AreEquivalentWorkspaceRoots(branchValue, projectOptionsValue))
            return new TeaBoxFieldValue(branchValue, null, null);

        return new TeaBoxFieldValue(null, branchValue, projectOptionsValue);
    }

    private static bool AreEquivalentWorkspaceRoots(string branchValue, string projectOptionsValue)
    {
        var branchComparisonPath = branchValue;
        var projectOptionsComparisonPath = projectOptionsValue;
        if (!string.Equals(branchComparisonPath, branchComparisonPath.Trim(), StringComparison.Ordinal) ||
            !string.Equals(projectOptionsComparisonPath, projectOptionsComparisonPath.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var branchPathForQualification = branchComparisonPath.Replace('/', '\\');
            var projectOptionsPathForQualification = projectOptionsComparisonPath.Replace('/', '\\');
            var branchIsFullyQualified = Path.IsPathFullyQualified(branchPathForQualification);
            var projectOptionsIsFullyQualified = Path.IsPathFullyQualified(projectOptionsPathForQualification);
            if (branchIsFullyQualified != projectOptionsIsFullyQualified)
                return false;
            if (!branchIsFullyQualified)
            {
                return string.Equals(
                    branchComparisonPath,
                    projectOptionsComparisonPath,
                    StringComparison.Ordinal);
            }

            if (!IsValidWindowsPathForComparison(branchPathForQualification) ||
                !IsValidWindowsPathForComparison(projectOptionsPathForQualification))
            {
                return false;
            }

            return string.Equals(
                NormalizePathForComparison(branchPathForQualification),
                NormalizePathForComparison(projectOptionsPathForQualification),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsValidWindowsPathForComparison(string path)
    {
        var invalidFileNameCharacters = Path.GetInvalidFileNameChars();
        if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var uncComponents = path[2..].Split('\\');
            if (uncComponents.Length < 2 ||
                !IsValidWindowsPathComponent(uncComponents[0], invalidFileNameCharacters) ||
                !IsValidWindowsPathComponent(uncComponents[1], invalidFileNameCharacters))
            {
                return false;
            }

            for (var index = 2; index < uncComponents.Length; index++)
            {
                var component = uncComponents[index];
                if (component.Length == 0)
                {
                    if (index == uncComponents.Length - 1 && path.EndsWith('\\'))
                        continue;
                    return false;
                }
                if (!IsValidWindowsPathComponent(component, invalidFileNameCharacters))
                    return false;
            }

            return true;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return false;

        var pathComponents = path[root.Length..].Split('\\');
        return pathComponents.All(component =>
            component.Length == 0 || IsValidWindowsPathComponent(component, invalidFileNameCharacters));
    }

    private static bool IsValidWindowsPathComponent(string component, char[] invalidFileNameCharacters) =>
        component.Length > 0 &&
        component.IndexOfAny(invalidFileNameCharacters) < 0 &&
        !component.Any(char.IsControl);

    private static string NormalizePathForComparison(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
            throw new ArgumentException("A fully qualified workspace root is required.");

        var pathWithoutTrailingSeparators = Path.TrimEndingDirectorySeparator(fullPath);
        var rootWithoutTrailingSeparators = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(
            pathWithoutTrailingSeparators,
            rootWithoutTrailingSeparators,
            StringComparison.OrdinalIgnoreCase)
            ? root
            : pathWithoutTrailingSeparators;
    }

    private static TeaBoxFieldValue MergeValues(string? branchValue, string? projectOptionsValue)
    {
        var hasBranchValue = !string.IsNullOrWhiteSpace(branchValue);
        var hasProjectOptionsValue = !string.IsNullOrWhiteSpace(projectOptionsValue);
        if (!hasBranchValue)
            return new TeaBoxFieldValue(hasProjectOptionsValue ? projectOptionsValue : null, null, null);
        if (!hasProjectOptionsValue)
            return new TeaBoxFieldValue(branchValue, null, null);
        if (string.Equals(branchValue, projectOptionsValue, StringComparison.Ordinal))
            return new TeaBoxFieldValue(branchValue, null, null);

        return new TeaBoxFieldValue(null, branchValue, projectOptionsValue);
    }

    private static string RequirePerforceConnectionValue(TeaBoxFieldValue value)
    {
        if (value.Value is null || value.BranchValue is not null || value.ProjectOptionsValue is not null)
            throw new InvalidDataException("The TeaBox Perforce connection settings are incomplete or conflicting.");

        return value.Value;
    }

    private static string NormalizeEngineFolder(string? engineFolder)
    {
        var normalizedPath = NormalizeValue(engineFolder);
        if (normalizedPath is null)
            return string.Empty;

        return Path.TrimEndingDirectorySeparator(normalizedPath.Replace('/', '\\'));
    }

    private static TeaBoxEngineFolderRole GetEngineFolderRole(string engineFolder)
    {
        if (string.IsNullOrEmpty(engineFolder))
            return TeaBoxEngineFolderRole.Unselected;
        if (engineFolder.Equals(@"src\Engine", StringComparison.OrdinalIgnoreCase))
            return TeaBoxEngineFolderRole.Programmer;
        if (engineFolder.Equals("Engine", StringComparison.OrdinalIgnoreCase))
            return TeaBoxEngineFolderRole.NonProgrammer;

        return TeaBoxEngineFolderRole.Custom;
    }

    private static string? GetAttributeValue(XElement? element, string name)
    {
        if (element is null)
            return null;

        return element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?
            .Value;
    }

    private static void SetAttributeValueIgnoreCase(XElement element, string name, string? value)
    {
        var attribute = element.Attributes()
            .FirstOrDefault(candidate => candidate.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (attribute is null)
            element.SetAttributeValue(name, value);
        else if (value is null)
            attribute.Remove();
        else
            attribute.Value = value;
    }

    private static bool IsNamed(XElement element, string name) =>
        element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ResolveFullPath(string path, string errorMessage)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(errorMessage);
        }
    }

    private sealed record ResolvedSettingsFields(
        string PerforceServer,
        string PerforceUserName,
        string PerforceWorkspace,
        string WorkspaceRoot);

    private sealed record SettingsWriteTarget(string Path, byte[] Content, string Description);

    private sealed record TeaBoxBranch(
        string? Folder,
        string? DisplayName,
        string? SccConnection,
        string? Password,
        string? Initial,
        string? DisplayColor);

    private sealed record P4Connection(string? Server, string? UserName, string? Workspace);
}
