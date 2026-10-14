using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace LinkTwo.UI;

/// <summary>This session's activity, with Notifications / Activities / All tabs.</summary>
public partial class ActivityPage
{
    private ICollectionView? _view;
    private ActivityFilter _filter = ActivityFilter.Notifications;

    public ActivityPage()
    {
        InitializeComponent();
        TabNotifications.IsChecked = true;
    }

    public void Bind(AppModel model)
    {
        _view = new CollectionViewSource { Source = model.Activity }.View;
        _view.Filter = o => o is ActivityItem a && Matches(a);
        List.ItemsSource = _view;
        model.Activity.CollectionChanged += (_, _) => UpdateEmpty();
        UpdateEmpty();
    }

    private bool Matches(ActivityItem a) => _filter switch
    {
        ActivityFilter.Notifications => a.Kind == ActivityKind.Notification,
        ActivityFilter.Activities => a.Kind != ActivityKind.Notification,
        _ => true,
    };

    public void SetFilter(ActivityFilter f)
    {
        switch (f)
        {
            case ActivityFilter.Notifications: TabNotifications.IsChecked = true; break;
            case ActivityFilter.Activities: TabActivities.IsChecked = true; break;
            default: TabAll.IsChecked = true; break;
        }
    }

    /// <summary>Scroll to and select the row for a clicked toast.</summary>
    public void Highlight(string key)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var item = (_view?.Cast<object>() ?? Enumerable.Empty<object>()).OfType<ActivityItem>().FirstOrDefault(a => a.Key == key);
            if (item == null) return;
            List.SelectedItem = item;
            List.ScrollIntoView(item);
            List.Focus();
        });
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        _filter = sender == TabActivities ? ActivityFilter.Activities : sender == TabAll ? ActivityFilter.All : ActivityFilter.Notifications;
        _view?.Refresh();
        UpdateEmpty();
    }

    private void UpdateEmpty()
    {
        if (_view == null) return;
        var empty = _view.IsEmpty;
        Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = _filter switch
        {
            ActivityFilter.Notifications => "No notifications",
            ActivityFilter.Activities => "No activity",
            _ => "Nothing yet",
        };
        EmptyIcon.Symbol = _filter == ActivityFilter.Notifications ? SymbolRegular.Alert24 : SymbolRegular.History24;
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => AppController.Shared.Model.Activity.Clear();
}
