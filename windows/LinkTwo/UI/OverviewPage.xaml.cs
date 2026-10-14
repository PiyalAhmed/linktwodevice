using System.Windows;
using System.Windows.Media;

namespace LinkTwo.UI;

public partial class OverviewPage
{
    public OverviewPage() => InitializeComponent();

    private static AppController C => AppController.Shared;
    private AppModel Model => (AppModel)DataContext;

    private void Pause_Click(object sender, RoutedEventArgs e) => C.SetPaused(!Model.Paused);
    private void Reconnect_Click(object sender, RoutedEventArgs e) => C.Reconnect();
    private void NotificationSettings_Click(object sender, RoutedEventArgs e) => C.OpenNotificationSettings();
    private void ChooseFiles_Click(object sender, RoutedEventArgs e) => C.ChooseFiles();
    private void SendClipboard_Click(object sender, RoutedEventArgs e) => C.SendClipboard();
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => C.OpenSaveFolder();
    private void ChooseFolder_Click(object sender, RoutedEventArgs e) => C.ChooseSaveFolder();
    private void ResetFolder_Click(object sender, RoutedEventArgs e) => C.ResetSaveFolder();
    private void ClipboardSync_Click(object sender, RoutedEventArgs e) => C.SetClipboardSync(!Model.ClipboardSync);
    private void StartWithWindows_Click(object sender, RoutedEventArgs e) => C.SetStartWithWindows(!Model.StartWithWindows);
    private void Pair_Click(object sender, RoutedEventArgs e) => C.ShowPairing();
    private void ResetPairing_Click(object sender, RoutedEventArgs e) => C.ResetPairing();
    private void Logs_Click(object sender, RoutedEventArgs e) => C.OpenLogs();

    // MARK: Drop zone

    private void Drop_DragEnter(object sender, DragEventArgs e)
    {
        var ok = Model.CanSend && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (!ok) return;
        DropBorder.Stroke = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.DodgerBlue;
        DropText.Text = "Release to send";
    }

    private void Drop_DragLeave(object sender, DragEventArgs e) => ResetDropZone();

    private void Drop_Drop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        if (Model.CanSend && e.Data.GetData(DataFormats.FileDrop) is string[] files) C.SendFiles(files);
    }

    private void ResetDropZone()
    {
        DropBorder.Stroke = TryFindResource("ControlStrokeColorSecondaryBrush") as Brush ?? Brushes.Gray;
        DropText.Text = "Drop files here";
    }
}
