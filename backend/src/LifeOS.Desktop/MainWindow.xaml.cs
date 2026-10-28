using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace LifeOS.Desktop;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource shutdown = new();
    private DesktopBackendHost? host;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            FitToWorkArea();
            StartupMessage.Text = "Loading your private workspace.";
            host = await DesktopBackendHost.StartAsync(shutdown.Token);
            await LifeOsView.EnsureCoreWebView2Async();
            var frontend = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            LifeOsView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "lifeos.local", frontend, CoreWebView2HostResourceAccessKind.DenyCors);
            LifeOsView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            LifeOsView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            LifeOsView.Source = new Uri("https://lifeos.local/index.html");
            LifeOsView.Visibility = Visibility.Visible;
            StartupPanel.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { Close(); }
        catch (Exception exception)
        {
            StartupTitle.Text = "LifeOS could not start";
            StartupMessage.Text = $"{exception.Message}\n\nClose this window and try again.";
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (host is null || !Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || source.Host != "lifeos.local") return;
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            id = root.GetProperty("id").GetString() ?? throw new InvalidDataException("A bridge request ID is required.");
            var operation = root.GetProperty("operation").GetString() ?? "unknown";
            var token = root.TryGetProperty("token", out var tokenValue) ? tokenValue.GetString() : null;
            var entityId = root.TryGetProperty("entityId", out var idValue) && idValue.TryGetInt32(out var parsedId) ? parsedId : (int?)null;
            var data = root.TryGetProperty("data", out var dataValue) ? dataValue.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
            var result = await host.Backend.ExecuteAsync(new LifeOS.Api.Services.DesktopCommand(operation, data, token, entityId), shutdown.Token);
            var responseBytes = result.Value is null ? [] : JsonSerializer.SerializeToUtf8Bytes(result.Value);
            Reply(id, new { id, status = result.Status, headers = new Dictionary<string, string> { ["content-type"] = "application/json" }, body = Convert.ToBase64String(responseBytes) });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (id is not null)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { message = exception.Message });
                Reply(id, new { id, status = 500, headers = new Dictionary<string, string> { ["content-type"] = "application/json" }, body = Convert.ToBase64String(bytes) });
            }
        }
    }

    private void Reply(string id, object payload) => LifeOsView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));

    private void FitToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        WindowState = WindowState.Normal;
        Left = workArea.Left; Top = workArea.Top; Width = workArea.Width; Height = workArea.Height;
    }

    private void TitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    protected override async void OnClosed(EventArgs e)
    {
        shutdown.Cancel();
        if (host is not null) await host.DisposeAsync();
        shutdown.Dispose();
        base.OnClosed(e);
    }
}
