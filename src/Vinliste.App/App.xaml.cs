using System.Windows;
using System.Windows.Threading;
using Vinliste.App.Services;
using Vinliste.App.ViewModels;

namespace Vinliste.App;

public partial class App : Application
{
    private AppServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        // WPF formats bindings as en-US unless told otherwise; use the user's own number and date format.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag)));

        try
        {
            _services = new AppServices(Dispatcher);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Vinliste could not open its database:\n\n{ex.Message}", "Vinliste", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        ThemeManager.Apply(_services.Settings.Theme);
        var window = new MainWindow { DataContext = new MainViewModel(_services) };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app alive on unexpected UI errors; the user's data is in the database already.
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}", "Vinliste", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
