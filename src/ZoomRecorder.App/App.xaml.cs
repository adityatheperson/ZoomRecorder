using Microsoft.UI.Xaml;
using ZoomRecorder.App.Composition;
using ZoomRecorder.App.Data;

namespace ZoomRecorder.App;

public partial class App : Application
{
    private Window? _window;
    private AppServices? services;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            services = await AppServices.CreateAsync(LibraryPaths.CreateDefault(), CancellationToken.None);
            _window = new MainWindow(services);
            _window.Closed += async (_, _) =>
            {
                if (services is not null)
                {
                    await services.DisposeAsync();
                    services = null;
                }
            };
            _window.Activate();
        }
        catch (Exception exception)
        {
            string? diagnosticPath = null;
            try
            {
                diagnosticPath = StartupFailureDiagnostic.Write(
                    exception,
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ZoomRecorder",
                        "Logs"));
            }
            catch
            {
                // The original startup failure remains the user-visible result.
            }

            _window = new Window
            {
                Content = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    Text = diagnosticPath is null
                    ? $"Zoom Recorder could not start: {exception.Message}"
                    : $"Zoom Recorder could not start: {exception.Message}\n\nDiagnostic: {diagnosticPath}",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(32)
                }
            };
            _window.Activate();
        }
    }
}
