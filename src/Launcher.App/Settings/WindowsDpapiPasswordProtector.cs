using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Launcher.App.Settings;

public sealed class WindowsDpapiPasswordProtector : ITeaBoxPasswordProtector
{
    private static readonly UTF8Encoding myStrictUtf8 = new(false, true);

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                plaintextBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(protectedValue);
        }
        catch (FormatException)
        {
            throw new InvalidDataException("The protected TeaBox password data is invalid.");
        }

        byte[] plaintextBytes;
        try
        {
            plaintextBytes = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            throw new InvalidDataException("The protected TeaBox password could not be unprotected.");
        }

        try
        {
            return myStrictUtf8.GetString(plaintextBytes);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("The protected TeaBox password data is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }
}
