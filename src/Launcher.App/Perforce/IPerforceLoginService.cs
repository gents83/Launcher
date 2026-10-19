using Launcher.App.Settings;

namespace Launcher.App.Perforce;

public interface IPerforceLoginService
{
    Task<PerforceLoginResult> LoginAsync(
        TeaBoxPerforceCredentials credentials,
        CancellationToken cancellationToken = default);
}

public sealed record PerforceLoginResult(bool Succeeded, string StatusMessage);
