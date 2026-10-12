using Launcher.App.Settings;

namespace Launcher.App.Tests.Settings;

public sealed class WindowsDpapiPasswordProtectorTests
{
    [Fact]
    public void Protect_returns_base64_data_that_round_trips_a_synthetic_password()
    {
        const string password = "synthetic-dpapi-password";
        var protector = new WindowsDpapiPasswordProtector();

        var protectedValue = protector.Protect(password);

        Assert.NotEqual(password, protectedValue);
        Assert.Equal(protectedValue, Convert.ToBase64String(Convert.FromBase64String(protectedValue)));
        Assert.DoesNotContain(password, protectedValue, StringComparison.Ordinal);
        Assert.Equal(password, protector.Unprotect(protectedValue));
    }

    [Fact]
    public void Unprotect_does_not_echo_invalid_password_data_in_an_exception()
    {
        const string syntheticSecret = "synthetic-invalid-protected-secret";
        var protector = new WindowsDpapiPasswordProtector();

        var exception = Assert.ThrowsAny<Exception>(() => protector.Unprotect(syntheticSecret));

        Assert.DoesNotContain(syntheticSecret, exception.Message, StringComparison.Ordinal);
    }

}
