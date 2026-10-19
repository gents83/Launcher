using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using Launcher.App.Perforce;
using Launcher.App.Settings;
using Launcher.App.Web;
using Launcher.Core.Configuration;
using Launcher.Core.Processes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Launcher.App;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions WebMessageJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _virtualHostName;
    private readonly string _artworkHostName;
    private readonly string _entryPoint;
    private readonly LauncherMessageRouter _messageRouter;
    private readonly ArtworkRequestHandler _artworkRequestHandler;
    private readonly Func<Task>? _acknowledgeHealth;
    private readonly CancellationTokenSource _windowLifetime = new();
    private CoreWebView2? _webViewCore;
    private bool _hasAcknowledgedHealth;

    public MainWindow(
        LauncherConfiguration configuration,
        LauncherSettings settings,
        bool testMode,
        Func<Task>? acknowledgeHealth = null)
    {
        InitializeComponent();
        _acknowledgeHealth = acknowledgeHealth;
        var assembly = typeof(MainWindow).Assembly;
        var assemblyVersion = assembly.GetName().Version
            ?? throw new InvalidOperationException("The launcher assembly has no version.");
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assemblyVersion.ToString(3);
        var launcherVersion = FormatLauncherVersion(informationalVersion);
        _virtualHostName = LauncherHostOrigin.Create(assemblyVersion);
        _artworkHostName = LauncherHostOrigin.CreateArtworkHost(assemblyVersion);
        _entryPoint = CreateEntryPoint(_virtualHostName, launcherVersion);
        ConfigIndexPath = settings.ConfigIndexPath;
        WebView2UserDataDirectory = settings.WebView2UserDataDirectory;
        TestMode = testMode;
        var teaBoxSettingsStore = new TeaBoxProjectSettingsStore(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            new WindowsDpapiPasswordProtector());
        _messageRouter = new LauncherMessageRouter(
            configuration,
            new ProcessLauncher(),
            testMode,
            cancellationToken => LauncherConfigLoader.LoadAsync(ConfigIndexPath, cancellationToken),
            teaBoxSettingsStore,
            new PerforceLoginService(),
            _artworkHostName);
        _artworkRequestHandler = new ArtworkRequestHandler(
            _artworkHostName,
            () => _messageRouter.Configuration);
    }

    public LauncherConfiguration Configuration => _messageRouter.Configuration;

    public string ConfigIndexPath { get; }

    public string WebView2UserDataDirectory { get; }

    public bool TestMode { get; }

    internal static string FormatLauncherVersion(string informationalVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationalVersion);
        var buildMetadataStart = informationalVersion.IndexOf('+');
        return buildMetadataStart < 0
            ? informationalVersion
            : informationalVersion[..buildMetadataStart];
    }

    internal static string CreateEntryPoint(string virtualHostName, string launcherVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualHostName);
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherVersion);
        return $"https://{virtualHostName}/index.html?launcherVersion={Uri.EscapeDataString(launcherVersion)}";
    }

    internal static bool IsTrustedWebMessageSource(string? source, string expectedHost)
    {
        if (string.IsNullOrWhiteSpace(expectedHost) || !Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase) &&
               uri.Port == 443 &&
               string.IsNullOrEmpty(uri.UserInfo);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var webRootDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
            var entryFile = Path.Combine(webRootDirectory, "index.html");
            if (!File.Exists(entryFile))
            {
                throw new FileNotFoundException("The launcher dashboard was not found in the app's wwwroot directory.", entryFile);
            }

            Directory.CreateDirectory(WebView2UserDataDirectory);
            LauncherWebView.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = WebView2UserDataDirectory
            };

            await LauncherWebView.EnsureCoreWebView2Async();
            _webViewCore = LauncherWebView.CoreWebView2;
            _webViewCore.SetVirtualHostNameToFolderMapping(
                _virtualHostName,
                webRootDirectory,
                CoreWebView2HostResourceAccessKind.DenyCors);
            _webViewCore.WebMessageReceived += WebView_WebMessageReceived;
            _webViewCore.NavigationCompleted += WebView_NavigationCompleted;
            _webViewCore.AddWebResourceRequestedFilter(
                $"https://{_artworkHostName}/artwork*",
                CoreWebView2WebResourceContext.Image);
            _webViewCore.WebResourceRequested += WebView_WebResourceRequested;
            _webViewCore.Navigate(_entryPoint);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"The launcher dashboard could not be initialized. {exception.Message}",
                "Project Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private async void WebView_NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || _hasAcknowledgedHealth || _acknowledgeHealth is null)
        {
            return;
        }

        _hasAcknowledgedHealth = true;
        await _acknowledgeHealth();
    }

    private async void WebView_WebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        var webViewCore = _webViewCore;
        if (webViewCore is null || !IsTrustedWebMessageSource(e.Source, _virtualHostName))
        {
            return;
        }

        var message = e.WebMessageAsJson;
        LauncherResponse response;
        if (Encoding.UTF8.GetByteCount(message) > LauncherMessageRouter.MaximumMessageSizeBytes)
        {
            response = new LauncherResponse(
                string.Empty,
                false,
                null,
                $"The message exceeds the {LauncherMessageRouter.MaximumMessageSizeBytes}-byte limit.");
        }
        else
        {
            try
            {
                response = await _messageRouter.HandleMessageAsync(message, _windowLifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!_windowLifetime.IsCancellationRequested)
        {
            webViewCore.PostWebMessageAsJson(JsonSerializer.Serialize(response, WebMessageJsonOptions));
        }
    }

    private void WebView_WebResourceRequested(
        object? sender,
        CoreWebView2WebResourceRequestedEventArgs e)
    {
        var webViewCore = _webViewCore;
        if (webViewCore is null || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var requestUri))
        {
            return;
        }

        var resource = _artworkRequestHandler.Handle(requestUri);
        var reasonPhrase = resource.StatusCode == 200 ? "OK" : "Not Found";
        var headers = $"Content-Type: {resource.ContentType}\r\n" +
                      "Cache-Control: no-store\r\n" +
                      "X-Content-Type-Options: nosniff\r\n";
        e.Response = webViewCore.Environment.CreateWebResourceResponse(
            new MemoryStream(resource.Content, writable: false),
            resource.StatusCode,
            reasonPhrase,
            headers);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _windowLifetime.Cancel();
        if (_webViewCore is not null)
        {
            _webViewCore.WebMessageReceived -= WebView_WebMessageReceived;
            _webViewCore.NavigationCompleted -= WebView_NavigationCompleted;
            _webViewCore.WebResourceRequested -= WebView_WebResourceRequested;
            _webViewCore = null;
        }
    }
}
