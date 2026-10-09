using System.Runtime.InteropServices;

namespace Launcher.Update.Credentials;

/// <summary>
/// Isolates the native Windows Credential Manager API for deterministic tests.
/// </summary>
public interface IWindowsCredentialApi
{
    bool CredRead(string targetName, out IntPtr credentialPointer);

    bool CredWrite(ref WindowsCredentialRecord credential);

    void CredFree(IntPtr credentialPointer);
}

/// <summary>
/// The native CREDENTIALW structure used by Windows Credential Manager.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct WindowsCredentialRecord
{
    public uint Flags;

    public uint Type;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string? TargetName;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string? Comment;

    public long LastWritten;

    public uint CredentialBlobSize;

    public IntPtr CredentialBlob;

    public uint Persist;

    public uint AttributeCount;

    public IntPtr Attributes;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string? TargetAlias;

    [MarshalAs(UnmanagedType.LPWStr)]
    public string? UserName;
}
