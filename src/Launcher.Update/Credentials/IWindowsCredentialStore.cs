namespace Launcher.Update.Credentials;

/// <summary>
/// Reads and writes secrets through the current user's Windows Credential Manager.
/// </summary>
public interface IWindowsCredentialStore
{
    /// <summary>
    /// Reads a secret by target name, returning null when it is unavailable.
    /// </summary>
    string? Read(string targetName);

    /// <summary>
    /// Stores a secret under the specified target name.
    /// </summary>
    void Write(string targetName, string secret);
}
