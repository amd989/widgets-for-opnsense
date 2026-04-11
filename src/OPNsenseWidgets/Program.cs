using System.Runtime.InteropServices;
using OPNsenseWidgets.Provider;
using OPNsenseWidgets.Refresh;

namespace OPNsenseWidgets;

internal class Program
{
    private static readonly Guid ProviderClsid = Guid.Parse("D40B2E99-B044-4758-9AB5-212C01B368EE");

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
        uint dwClsContext,
        uint flags,
        out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);

    private const uint CLSCTX_LOCAL_SERVER = 0x4;
    private const uint REGCLS_MULTIPLEUSE = 0x1;

    static void Main(string[] args)
    {
        OPNsenseWidgets.Provider.OPNsenseWidgetProvider.Log($"Main started, args=[{string.Join(", ", args)}]");

        WinRT.ComWrappersSupport.InitializeComWrappers();
        OPNsenseWidgets.Provider.OPNsenseWidgetProvider.Log("InitializeComWrappers done");

        var provider = new OPNsenseWidgetProvider();
        var factory = new WidgetProviderFactory(provider);

        var hr = CoRegisterClassObject(
            ProviderClsid,
            factory,
            CLSCTX_LOCAL_SERVER,
            REGCLS_MULTIPLEUSE,
            out var cookie);

        OPNsenseWidgets.Provider.OPNsenseWidgetProvider.Log($"CoRegisterClassObject hr=0x{hr:X8} cookie={cookie}");

        if (hr != 0)
        {
            Console.Error.WriteLine($"Failed to register COM class object: 0x{hr:X8}");
            return;
        }

        provider.RecoverRunningWidgets();

        using var refreshService = new RefreshService(provider);
        refreshService.Start();

        OPNsenseWidgets.Provider.OPNsenseWidgetProvider.Log("Waiting for EmptyEvent...");
        provider.EmptyEvent.Wait();

        OPNsenseWidgets.Provider.OPNsenseWidgetProvider.Log("EmptyEvent fired, shutting down");
        refreshService.Stop();
        CoRevokeClassObject(cookie);
    }
}
