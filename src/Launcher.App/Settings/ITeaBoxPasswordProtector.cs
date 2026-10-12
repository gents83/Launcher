namespace Launcher.App.Settings;

public interface ITeaBoxPasswordProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}
