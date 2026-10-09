using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Launcher.Bootstrapper;

public enum HealthStatus
{
    Healthy,
    ProcessStartFailed,
    ProcessExited,
    TimedOut,
    InvalidAcknowledgement
}

public sealed record HealthResult(HealthStatus Status, string Version, string? Message = null)
{
    public bool IsHealthy => Status == HealthStatus.Healthy;
}

/// <summary>
/// Accepts one same-user health message for a single candidate app launch.
/// </summary>
public sealed class HealthPipeServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;

    public HealthPipeServer(string pipeName)
    {
        if (!IsValidPipeName(pipeName))
        {
            throw new ArgumentException("The health pipe name is invalid.", nameof(pipeName));
        }

        PipeName = pipeName;
        _pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 128,
            outBufferSize: 0);
    }

    public string PipeName { get; }

    public static string CreatePipeName() => $"launcher-health-{Guid.NewGuid():N}";

    public static string CreateNonce() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public async Task<HealthStatus> WaitForAcknowledgementAsync(
        string expectedNonce,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!IsValidNonce(expectedNonce))
        {
            throw new ArgumentException("The health nonce must be 64 hexadecimal characters.", nameof(expectedNonce));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The health timeout must be positive.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await _pipe.WaitForConnectionAsync(timeoutSource.Token).ConfigureAwait(false);
            var message = new byte[65];
            var messageLength = 0;
            while (messageLength < message.Length)
            {
                var bytesRead = await _pipe.ReadAsync(
                    message.AsMemory(messageLength),
                    timeoutSource.Token).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                messageLength += bytesRead;
                var newlineIndex = Array.IndexOf(message, (byte)'\n', 0, messageLength);
                if (newlineIndex >= 0)
                {
                    if (newlineIndex != 64)
                    {
                        return HealthStatus.InvalidAcknowledgement;
                    }

                    var expectedBytes = Encoding.ASCII.GetBytes(expectedNonce);
                    return CryptographicOperations.FixedTimeEquals(expectedBytes, message.AsSpan(0, 64))
                        ? HealthStatus.Healthy
                        : HealthStatus.InvalidAcknowledgement;
                }
            }

            return HealthStatus.InvalidAcknowledgement;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthStatus.TimedOut;
        }
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();

    private static bool IsValidPipeName(string? pipeName) =>
        !string.IsNullOrWhiteSpace(pipeName)
        && pipeName.Length <= 128
        && pipeName.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsValidNonce(string? nonce) =>
        nonce is { Length: 64 }
        && nonce.All(Uri.IsHexDigit);
}
