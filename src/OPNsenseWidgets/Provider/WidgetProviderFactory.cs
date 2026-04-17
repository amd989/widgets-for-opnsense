using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace OPNsenseWidgets.Provider;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid("D40B2E99-B044-4758-9AB5-212C01B368EE")]
public sealed class WidgetProviderFactory : IClassFactory
{
    private readonly OPNsenseWidgetProvider _provider;
    private readonly ILogger<WidgetProviderFactory> _logger;

    public WidgetProviderFactory(OPNsenseWidgetProvider provider, ILogger<WidgetProviderFactory> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public int CreateInstance(
        [MarshalAs(UnmanagedType.Interface)] object? pUnkOuter,
        ref Guid riid,
        out IntPtr ppvObject)
    {
        _logger.LogInformation("CreateInstance called riid={Riid}", riid);
        ppvObject = IntPtr.Zero;

        try
        {
            if (pUnkOuter is not null)
            {
                _logger.LogWarning("CreateInstance: CLASS_E_NOAGGREGATION");
                return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
            }

            ppvObject = MarshalInspectable<OPNsenseWidgetProvider>.FromManaged(_provider);
            _logger.LogInformation("CreateInstance: succeeded ppvObject=0x{Pointer:X}", ppvObject);
            return 0; // S_OK
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateInstance failed");
            return unchecked((int)0x80004005); // E_FAIL
        }
    }

    public int LockServer(bool fLock) => 0; // S_OK
}

[ComImport]
[Guid("00000001-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(
        [MarshalAs(UnmanagedType.Interface)] object? pUnkOuter,
        ref Guid riid,
        out IntPtr ppvObject);

    [PreserveSig]
    int LockServer(bool fLock);
}
