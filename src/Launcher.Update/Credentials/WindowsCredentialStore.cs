using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Launcher.Update.Credentials;

/// <summary>
/// Stores Artifactory tokens in the current user's Windows Credential Manager.
/// </summary>
public sealed class WindowsCredentialStore : IWindowsCredentialStore
{
    private const uint GenericCredentialType = 1;
    private const uint LocalMachinePersistence = 2;
    private const int MaximumCredentialBlobBytes = 5 * 512;

    private readonly IWindowsCredentialApi _credentialApi;

    public WindowsCredentialStore()
        : this(new WindowsCredentialApi())
    {
    }

    public WindowsCredentialStore(IWindowsCredentialApi credentialApi)
    {
        _credentialApi = credentialApi ?? throw new ArgumentNullException(nameof(credentialApi));
    }

    public string? Read(string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            return null;
        }

        var credentialPointer = IntPtr.Zero;
        try
        {
            if (!_credentialApi.CredRead(targetName, out credentialPointer)
                || credentialPointer == IntPtr.Zero)
            {
                return null;
            }

            var credential = Marshal.PtrToStructure<WindowsCredentialRecord>(credentialPointer);
            if (credential.Type != GenericCredentialType
                || credential.CredentialBlob == IntPtr.Zero
                || credential.CredentialBlobSize == 0
                || credential.CredentialBlobSize > MaximumCredentialBlobBytes
                || credential.CredentialBlobSize % sizeof(char) != 0)
            {
                return null;
            }

            var secretBytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
            var secret = Encoding.Unicode.GetString(secretBytes);
            return secret.Length == 0 ? null : secret;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (credentialPointer != IntPtr.Zero)
            {
                try
                {
                    _credentialApi.CredFree(credentialPointer);
                }
                catch (Exception)
                {
                    // Credential read failures must not expose a partially-read secret.
                }
            }
        }
    }

    public void Write(string targetName, string secret)
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            throw new ArgumentException("A credential target name is required.", nameof(targetName));
        }

        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length == 0)
        {
            throw new ArgumentException("A credential secret is required.", nameof(secret));
        }

        var secretBytes = Encoding.Unicode.GetBytes(secret);
        if (secretBytes.Length > MaximumCredentialBlobBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(secret), "The secret exceeds the Windows credential size limit.");
        }

        var secretPointer = Marshal.AllocHGlobal(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);
            var credential = new WindowsCredentialRecord
            {
                Type = GenericCredentialType,
                TargetName = targetName,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = secretPointer,
                Persist = LocalMachinePersistence
            };

            if (!_credentialApi.CredWrite(ref credential))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager could not store the credential.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(secretPointer);
        }
    }

    private sealed class WindowsCredentialApi : IWindowsCredentialApi
    {
        public bool CredRead(string targetName, out IntPtr credentialPointer)
        {
            credentialPointer = IntPtr.Zero;
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            return CredReadNative(targetName, GenericCredentialType, flags: 0, out credentialPointer);
        }

        public bool CredWrite(ref WindowsCredentialRecord credential)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            return CredWriteNative(ref credential, flags: 0);
        }

        public void CredFree(IntPtr credentialPointer)
        {
            if (OperatingSystem.IsWindows() && credentialPointer != IntPtr.Zero)
            {
                CredFreeNative(credentialPointer);
            }
        }

        [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredReadNative(
            string targetName,
            uint type,
            uint flags,
            out IntPtr credentialPointer);

        [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWriteNative(ref WindowsCredentialRecord credential, uint flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredFree")]
        private static extern void CredFreeNative(IntPtr credentialPointer);
    }
}
