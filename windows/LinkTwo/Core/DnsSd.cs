using System.Net;
using System.Runtime.InteropServices;

namespace LinkTwo.Core;

/// <summary>
/// Announces "_linktwo._tcp" with TXT id=&lt;computerId&gt; using Windows' built-in DNS-SD (dnsapi.dll, Windows 10 1809+),
/// so the phone can find this PC even after its IP changes. Best effort: if it fails, the phone still uses the
/// addresses from the QR code / last connection.
/// </summary>
public static class DnsSd
{
    private const uint DNS_QUERY_REQUEST_VERSION1 = 1;
    private const uint DNS_REQUEST_PENDING = 9506;

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_SERVICE_REGISTER_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr pServiceInstance;
        public IntPtr pRegisterCompletionCallback;
        public IntPtr pQueryContext;
        public IntPtr hCredentials;
        public int unicastEnabled;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RegisterComplete(uint status, IntPtr queryContext, IntPtr instance);

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DnsServiceConstructInstance(
        string pServiceName, string pHostName, IntPtr pIp4, IntPtr pIp6, ushort wPort, ushort wPriority, ushort wWeight,
        uint dwPropertiesCount, string[] keys, string[] values);

    [DllImport("dnsapi.dll")]
    private static extern uint DnsServiceRegister(ref DNS_SERVICE_REGISTER_REQUEST request, IntPtr pCancel);

    [DllImport("dnsapi.dll")]
    private static extern uint DnsServiceDeRegister(ref DNS_SERVICE_REGISTER_REQUEST request, IntPtr pCancel);

    [DllImport("dnsapi.dll")]
    private static extern void DnsServiceFreeInstance(IntPtr instance);

    private static readonly RegisterComplete Callback = OnComplete; // kept alive for the native side
    private static DNS_SERVICE_REGISTER_REQUEST _request;
    private static bool _registered;

    public static void Register(string name, int port, string id)
    {
        try
        {
            var safeName = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or ' ').ToArray());
            var instance = DnsServiceConstructInstance(
                $"{safeName}._linktwo._tcp.local", $"{Dns.GetHostName()}.local",
                IntPtr.Zero, IntPtr.Zero, (ushort)port, 0, 0, 1, new[] { "id" }, new[] { id });
            if (instance == IntPtr.Zero)
            {
                Log.Error("DNS-SD: could not construct instance");
                return;
            }
            _request = new DNS_SERVICE_REGISTER_REQUEST
            {
                Version = DNS_QUERY_REQUEST_VERSION1,
                InterfaceIndex = 0,
                pServiceInstance = instance,
                pRegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(Callback),
            };
            var status = DnsServiceRegister(ref _request, IntPtr.Zero);
            _registered = status == DNS_REQUEST_PENDING;
            Log.Info($"DNS-SD register: status {status}");
        }
        catch (Exception e)
        {
            Log.Error("DNS-SD unavailable", e);
        }
    }

    public static void Unregister()
    {
        if (!_registered) return;
        try { DnsServiceDeRegister(ref _request, IntPtr.Zero); }
        catch (Exception e) { Log.Error("DNS-SD deregister", e); }
        _registered = false;
    }

    private static void OnComplete(uint status, IntPtr queryContext, IntPtr instance)
    {
        Log.Info($"DNS-SD registration completed: {status}");
        if (instance != IntPtr.Zero) DnsServiceFreeInstance(instance);
    }
}
