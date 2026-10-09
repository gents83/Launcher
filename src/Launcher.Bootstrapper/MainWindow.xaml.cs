using System.Windows;

namespace Launcher.Bootstrapper;

public partial class MainWindow : Window
{
    private readonly BootstrapperViewModel _viewModel;

    public MainWindow(BootstrapperViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.StartAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"Project Launcher could not start. {exception.Message}",
                "Project Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Close();
        }
    }
}
