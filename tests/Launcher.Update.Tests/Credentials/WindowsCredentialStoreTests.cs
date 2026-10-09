using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Launcher.Update.Credentials;

namespace Launcher.Update.Tests.Credentials;

public sealed class WindowsCredentialStoreTests
{
    [Fact]
    public void Read_returns_the_secret_and_frees_the_native_credential_buffer()
    {
        using var api = new FakeWindowsCredentialApi();
        api.SetReadCredential("launcher/artifactory/packages.example", "temporary-test-token");
        var store = new WindowsCredentialStore(api);

        var secret = store.Read("launcher/artifactory/packages.example");

        Assert.Equal("temporary-test-token", secret);
        Assert.Equal("launcher/artifactory/packages.example", api.ReadTargetName);
        Assert.Equal(1, api.FreeCount);
    }

    [Fact]
    public void Read_returns_no_credential_when_credential_manager_access_fails()
    {
        using var api = new FakeWindowsCredentialApi { ReadSucceeds = false };
        var store = new WindowsCredentialStore(api);

        var secret = store.Read("launcher/artifactory/packages.example");

        Assert.Null(secret);
        Assert.Equal(0, api.FreeCount);
    }

    [Fact]
    public void Read_returns_no_credential_when_the_native_blob_is_invalid_and_still_frees_it()
    {
        using var api = new FakeWindowsCredentialApi();
        api.SetInvalidReadCredential("launcher/artifactory/packages.example");
        var store = new WindowsCredentialStore(api);

        var secret = store.Read("launcher/artifactory/packages.example");

        Assert.Null(secret);
        Assert.Equal(1, api.FreeCount);
    }

    [Fact]
    public void Write_sends_the_target_and_secret_to_the_injected_native_api()
    {
        using var api = new FakeWindowsCredentialApi();
        var store = new WindowsCredentialStore(api);

        store.Write("launcher/artifactory/packages.example", "temporary-test-token");

        Assert.Equal("launcher/artifactory/packages.example", api.WrittenTargetName);
        Assert.Equal("temporary-test-token", api.WrittenSecret);
        Assert.Equal(1u, api.WrittenType);
        Assert.Equal(2u, api.WrittenPersistence);
    }

    [Fact]
    public void Write_reports_native_api_failure_without_returning_a_credential()
    {
        using var api = new FakeWindowsCredentialApi { WriteSucceeds = false };
        var store = new WindowsCredentialStore(api);

        Assert.Throws<Win32Exception>(() => store.Write(
            "launcher/artifactory/packages.example",
            "temporary-test-token"));
    }

    [Fact]
    public void Read_returns_no_credential_when_the_native_api_throws()
    {
        using var api = new FakeWindowsCredentialApi { ThrowOnRead = true };
        var store = new WindowsCredentialStore(api);

        var secret = store.Read("launcher/artifactory/packages.example");

        Assert.Null(secret);
    }

    private sealed class FakeWindowsCredentialApi : IWindowsCredentialApi, IDisposable
    {
        private IntPtr _readCredentialPointer;
        private IntPtr _readCredentialBlob;

        public bool ReadSucceeds { get; set; } = true;

        public bool WriteSucceeds { get; set; } = true;

        public bool ThrowOnRead { get; set; }

        public int FreeCount { get; private set; }

        public string? ReadTargetName { get; private set; }

        public string? WrittenTargetName { get; private set; }

        public string? WrittenSecret { get; private set; }

        public uint WrittenType { get; private set; }

        public uint WrittenPersistence { get; private set; }

        public bool CredRead(string targetName, out IntPtr credentialPointer)
        {
            ReadTargetName = targetName;
            credentialPointer = _readCredentialPointer;
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("Synthetic credential API failure.");
            }

            return ReadSucceeds;
        }

        public bool CredWrite(ref WindowsCredentialRecord credential)
        {
            WrittenTargetName = credential.TargetName;
            WrittenType = credential.Type;
            WrittenPersistence = credential.Persist;
            var secretBytes = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
            WrittenSecret = Encoding.Unicode.GetString(secretBytes);
            return WriteSucceeds;
        }

        public void CredFree(IntPtr credentialPointer)
        {
            FreeCount++;
            if (credentialPointer == IntPtr.Zero)
            {
                return;
            }

            var credential = Marshal.PtrToStructure<WindowsCredentialRecord>(credentialPointer);
            if (credential.CredentialBlob != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(credential.CredentialBlob);
            }

            Marshal.DestroyStructure<WindowsCredentialRecord>(credentialPointer);
            Marshal.FreeHGlobal(credentialPointer);
            _readCredentialPointer = IntPtr.Zero;
            _readCredentialBlob = IntPtr.Zero;
        }

        public void SetReadCredential(string targetName, string secret)
        {
            var secretBytes = Encoding.Unicode.GetBytes(secret);
            _readCredentialBlob = Marshal.AllocHGlobal(secretBytes.Length);
            Marshal.Copy(secretBytes, 0, _readCredentialBlob, secretBytes.Length);
            SetReadRecord(new WindowsCredentialRecord
            {
                Type = 1,
                TargetName = targetName,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = _readCredentialBlob,
                Persist = 2,
                UserName = "Project Launcher"
            });
        }

        public void SetInvalidReadCredential(string targetName)
        {
            SetReadRecord(new WindowsCredentialRecord
            {
                Type = 1,
                TargetName = targetName,
                CredentialBlobSize = uint.MaxValue,
                CredentialBlob = IntPtr.Zero,
                Persist = 2
            });
        }

        private void SetReadRecord(WindowsCredentialRecord credential)
        {
            _readCredentialPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WindowsCredentialRecord>());
            Marshal.StructureToPtr(credential, _readCredentialPointer, fDeleteOld: false);
        }

        public void Dispose()
        {
            if (_readCredentialPointer != IntPtr.Zero)
            {
                CredFree(_readCredentialPointer);
            }
        }
    }
}
