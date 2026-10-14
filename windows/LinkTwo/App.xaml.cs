using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using H.NotifyIcon;
using LinkTwo.Core;
using LinkTwo.UI;
using Wpf.Ui.Appearance;

namespace LinkTwo;

public partial class App
{
    private static Mutex? _single;
    private TaskbarIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One LinkTwo per user session.
        _single = new Mutex(true, @"Local\LinkTwo.SingleInstance", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("unhandled UI exception", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("unhandled exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("unobserved task exception", ex.Exception);
            ex.SetObserved();
        };

        base.OnStartup(e);
        Log.Info($"LinkTwo {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion}");

        try
        {
            ApplicationThemeManager.ApplySystemTheme();
            Toasts.Init();
            var controller = new AppController();
            controller.Start();
            CreateTray(controller);
        }
        catch (Exception ex)
        {
            Log.Error("startup failed", ex);
            MessageBox.Show($"LinkTwo couldn't start:\n\n{ex.Message}\n\nDetails are in {AppPaths.Logs}.", "LinkTwo",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void CreateTray(AppController controller)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Open LinkTwo", () => controller.ShowDashboard()));
        menu.Items.Add(Item("Pause / resume syncing", () => controller.SetPaused(!controller.Model.Paused)));
        menu.Items.Add(Item("Reconnect", controller.Reconnect));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit LinkTwo", controller.Quit));

        _tray = new TaskbarIcon
        {
            ToolTipText = "LinkTwo",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/LinkTwo.ico")),
            ContextMenu = menu,
        };
        _tray.TrayLeftMouseUp += (_, _) => controller.ToggleFlyout();
        _tray.ForceCreate(enablesEfficiencyMode: false);

        controller.Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AppModel.StatusText))
                _tray.ToolTipText = $"LinkTwo — {controller.Model.PhoneTitle}: {controller.Model.StatusText}";
        };
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
