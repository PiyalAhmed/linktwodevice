using System.Security.Cryptography;
using System.Text;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace LinkTwo.Core;

/// <summary>
/// Phone notifications, calls, media and received files as native Windows toasts.
/// Live only: everything is removed again when the PC locks or the phone leaves.
/// Activation callbacks arrive on a background thread.
/// </summary>
public static class Toasts
{
    private const string PhoneGroup = "phone";
    private const string CallTag = "call";
    private const string MediaGroup = "media";

    public static event Action<string, string>? Reply;          // key, text
    public static event Action<string>? ClearOnPhone;           // key
    public static event Action<string>? Open;                   // key (toast body clicked)
    public static event Action<string>? CallAction;             // "answer" | "decline"
    public static event Action<string, string>? MediaAction;    // key, "previous" | "playpause" | "next"
    public static event Action? OpenMedia;
    public static event Action<string>? RevealFile;             // path

    public static void Init()
    {
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            try
            {
                var args = ToastArguments.Parse(e.Argument);
                args.TryGetValue("kind", out var kind);
                args.TryGetValue("action", out var action);
                args.TryGetValue("key", out var key);
                switch (kind)
                {
                    case "notif" when action == "reply":
                        if (e.UserInput.TryGetValue("reply", out var text) && text is string s && s.Length > 0) Reply?.Invoke(key!, s);
                        break;
                    case "notif" when action == "clear":
                        ClearOnPhone?.Invoke(key!);
                        break;
                    case "notif":
                        Open?.Invoke(key!);
                        break;
                    case "call" when action is "answer" or "decline":
                        CallAction?.Invoke(action);
                        break;
                    case "media" when action is "previous" or "playpause" or "next":
                        MediaAction?.Invoke(key!, action);
                        break;
                    case "media":
                        OpenMedia?.Invoke();
                        break;
                    case "file" when args.TryGetValue("path", out var path):
                        RevealFile?.Invoke(path);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error("toast activation", ex);
            }
        };
    }

    /// <summary>False when notifications for LinkTwo are turned off in Windows Settings.</summary>
    public static bool Allowed
    {
        get
        {
            try { return ToastNotificationManagerCompat.CreateToastNotifier().Setting == NotificationSetting.Enabled; }
            catch { return true; }
        }
    }

    /// <summary>Toast tags are limited to 64 chars; phone notification keys can be longer.</summary>
    private static string TagFor(string key) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    public static void ShowPhone(string key, string app, string title, string body, string? iconPath, string? imagePath, bool canReply)
    {
        try
        {
            var b = new ToastContentBuilder()
                .AddArgument("kind", "notif")
                .AddArgument("key", key)
                .AddText(string.IsNullOrEmpty(title) ? app : title)
                .AddAttributionText(app);
            if (!string.IsNullOrEmpty(body)) b.AddText(body);
            if (iconPath != null) b.AddAppLogoOverride(new Uri(iconPath));
            if (imagePath != null) b.AddInlineImage(new Uri(imagePath));
            if (canReply)
            {
                b.AddInputTextBox("reply", placeHolderContent: "Reply");
                b.AddButton(new ToastButton().SetContent("Send").AddArgument("action", "reply").SetTextBoxId("reply").SetBackgroundActivation());
            }
            b.AddButton(new ToastButton().SetContent("Clear on phone").AddArgument("action", "clear").SetBackgroundActivation());
            b.Show(t =>
            {
                t.Tag = TagFor(key);
                t.Group = PhoneGroup;
            });
        }
        catch (Exception e)
        {
            Log.Error("showing notification", e);
        }
    }

    public static void RemovePhone(string key)
    {
        try { ToastNotificationManagerCompat.History.Remove(TagFor(key), PhoneGroup); }
        catch { /* already gone */ }
    }

    /// <summary>Windows' own incoming-call toast: stays on screen with a looping ringtone until answered or dismissed.</summary>
    public static void ShowCall(string name, string number, string phone)
    {
        try
        {
            new ToastContentBuilder()
                .SetToastScenario(ToastScenario.IncomingCall)
                .AddArgument("kind", "call")
                .AddText(name)
                .AddText(string.IsNullOrEmpty(number) || number == name ? "Incoming call" : number)
                .AddAttributionText(phone)
                .AddButton(new ToastButton().SetContent("Decline").AddArgument("action", "decline").SetBackgroundActivation())
                .AddButton(new ToastButton().SetContent("Answer").AddArgument("action", "answer").SetBackgroundActivation())
                .AddAudio(new Uri("ms-winsoundevent:Notification.Looping.Call"), loop: true)
                .Show(t =>
                {
                    t.Tag = CallTag;
                    t.Group = CallTag;
                });
        }
        catch (Exception e)
        {
            Log.Error("showing call", e);
        }
    }

    public static void DismissCall()
    {
        try { ToastNotificationManagerCompat.History.Remove(CallTag, CallTag); }
        catch { /* already gone */ }
    }

    /// <summary>A new track: a quiet toast with transport buttons. Replaces the previous one from the same player.</summary>
    public static void ShowMedia(string key, string app, string title, string artist, string? artPath)
    {
        try
        {
            var b = new ToastContentBuilder()
                .AddArgument("kind", "media")
                .AddArgument("key", key)
                .AddText(title)
                .AddText(string.IsNullOrEmpty(artist) ? $"Playing on your phone" : artist)
                .AddAttributionText(app)
                .AddAudio(new Uri("ms-winsoundevent:Notification.Default"), silent: true)
                .AddButton(new ToastButton().SetContent("Previous").AddArgument("action", "previous").SetBackgroundActivation())
                .AddButton(new ToastButton().SetContent("Play / Pause").AddArgument("action", "playpause").SetBackgroundActivation())
                .AddButton(new ToastButton().SetContent("Next").AddArgument("action", "next").SetBackgroundActivation());
            if (artPath != null) b.AddAppLogoOverride(new Uri(artPath));
            b.Show(t =>
            {
                t.Tag = TagFor(key);
                t.Group = MediaGroup;
            });
        }
        catch (Exception e)
        {
            Log.Error("showing media", e);
        }
    }

    public static void RemoveMedia(string key)
    {
        try { ToastNotificationManagerCompat.History.Remove(TagFor(key), MediaGroup); }
        catch { /* already gone */ }
    }

    public static void ShowFile(string path, string detail)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument("kind", "file")
                .AddArgument("path", path)
                .AddText(Path.GetFileName(path))
                .AddText(detail)
                .AddAttributionText("From your phone")
                .Show();
        }
        catch (Exception e)
        {
            Log.Error("showing file", e);
        }
    }

    public static void ClearAll()
    {
        try { ToastNotificationManagerCompat.History.Clear(); }
        catch { /* nothing to clear */ }
    }
}
