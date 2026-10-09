using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Update.Install;

/// <summary>
/// Loads and atomically replaces the per-user installed-version state file.
/// </summary>
public sealed class InstallationStateStore
{
    private readonly string _stateDirectory;
    private readonly string _stateFilePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Action<Stream, byte[]> _writeTemporaryFile;

    public InstallationStateStore(string? localAppDataRoot = null)
        : this(localAppDataRoot, deploymentId: null, WriteTemporaryFile)
    {
    }

    public InstallationStateStore(string? localAppDataRoot, string deploymentId)
        : this(localAppDataRoot, deploymentId, WriteTemporaryFile)
    {
    }

    internal InstallationStateStore(
        string? localAppDataRoot,
        Action<Stream, byte[]> writeTemporaryFile)
        : this(localAppDataRoot, deploymentId: null, writeTemporaryFile)
    {
    }

    internal InstallationStateStore(
        string? localAppDataRoot,
        string? deploymentId,
        Action<Stream, byte[]> writeTemporaryFile)
    {
        ArgumentNullException.ThrowIfNull(writeTemporaryFile);

        var appDataRoot = string.IsNullOrWhiteSpace(localAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.GetFullPath(localAppDataRoot);
        if (string.IsNullOrWhiteSpace(appDataRoot))
        {
            throw new InvalidOperationException("The local application data directory is unavailable.");
        }

        _stateDirectory = Path.Combine(appDataRoot, "ProjectLauncher");
        if (deploymentId is not null)
        {
            ValidateDeploymentId(deploymentId);
            _stateDirectory = Path.Combine(_stateDirectory, "deployments", deploymentId);
        }

        _stateFilePath = Path.Combine(_stateDirectory, "state.json");
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        _writeTemporaryFile = writeTemporaryFile;
    }

    public string StateFilePath => _stateFilePath;

    public InstallationState Load(string initialVersion)
    {
        if (!File.Exists(_stateFilePath))
        {
            var initialState = new InstallationState(initialVersion, null, []);
            Save(initialState);
            return initialState;
        }

        try
        {
            var state = JsonSerializer.Deserialize<InstallationState>(
                File.ReadAllBytes(_stateFilePath),
                _jsonOptions);
            return state ?? throw new InvalidDataException("The installation state is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The installation state file is invalid.", exception);
        }
    }

    public void Save(InstallationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(state, _jsonOptions);
        Directory.CreateDirectory(_stateDirectory);

        var temporaryPath = Path.Combine(_stateDirectory, $"state.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var temporaryFile = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                _writeTemporaryFile(temporaryFile, stateBytes);
                temporaryFile.Flush(flushToDisk: true);
            }

            if (File.Exists(_stateFilePath))
            {
                File.Replace(temporaryPath, _stateFilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, _stateFilePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ValidateDeploymentId(string deploymentId)
    {
        if (string.IsNullOrWhiteSpace(deploymentId)
            || deploymentId.Length > 64
            || !char.IsAsciiLetterOrDigit(deploymentId[0])
            || deploymentId.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("The deployment ID is not a safe directory name.", nameof(deploymentId));
        }
    }

    private static void WriteTemporaryFile(Stream stream, byte[] contents) => stream.Write(contents);
}
