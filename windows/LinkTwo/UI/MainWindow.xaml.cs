using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Appearance;

namespace LinkTwo.UI;

public enum DashboardPage { Overview, Activity }

/// <summary>The dashboard: Overview (status, sending, settings) and Activity (this session's live history).</summary>
public partial class MainWindow
{
    public MainWindow(AppModel model)
    {
        InitializeComponent();
        DataContext = model;
        Overview.DataContext = model;
        Activity.DataContext = model;
        Activity.Bind(model);
        Nav.SelectedIndex = 0;
        TitleBar.Icon = new Wpf.Ui.Controls.ImageIcon
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/LinkTwo.png")),
        };
        SystemThemeWatcher.Watch(this);
    }

    public void Navigate(DashboardPage page, ActivityFilter? filter, string? highlightKey)
    {
        Nav.SelectedIndex = page == DashboardPage.Overview ? 0 : 1;
        if (filter is { } f) Activity.SetFilter(f);
        if (highlightKey != null) Activity.Highlight(highlightKey);
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Overview == null || Activity == null) return;
        var activity = Nav.SelectedIndex == 1;
        Overview.Visibility = activity ? Visibility.Collapsed : Visibility.Visible;
        Activity.Visibility = activity ? Visibility.Visible : Visibility.Collapsed;
    }
}
