using System.ComponentModel;
using System.IO;
using System.Windows;

namespace Launcher.Shim;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            using var process = LauncherShimRunner.Start(AppContext.BaseDirectory, args);
            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException
                or InvalidOperationException or Win32Exception)
        {
            MessageBox.Show(
                $"Project Launcher could not start its bootstrapper. Sync the complete Launcher folder and try again. {exception.Message}",
                "Project Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return 1;
        }
    }
}
