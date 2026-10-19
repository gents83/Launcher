namespace Launcher.App.Settings;

public sealed class TeaBoxPerforceCredentials
{
    public TeaBoxPerforceCredentials(
        string perforceServer,
        string perforceUserName,
        string perforceWorkspace,
        string password)
    {
        PerforceServer = perforceServer;
        PerforceUserName = perforceUserName;
        PerforceWorkspace = perforceWorkspace;
        Password = password;
    }

    public string PerforceServer { get; }

    public string PerforceUserName { get; }

    public string PerforceWorkspace { get; }

    public string Password { get; }
}
