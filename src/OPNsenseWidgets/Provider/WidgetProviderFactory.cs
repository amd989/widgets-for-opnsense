using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace OPNsenseWidgets.Provider;

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid("D40B2E99-B044-4758-9AB5-212C01B368EE")]
public sealed class WidgetProviderFactory : IClassFactory
{
    private readonly OPNsenseWidgetProvider _provider;

    public WidgetProviderFactory(OPNsenseWidgetProvider provider)
    {
        _provider = provider;
    }

    public int CreateInstance(
        [MarshalAs(UnmanagedType.Interface)] object? pUnkOuter,
        ref Guid riid,
        out IntPtr ppvObject)
    {
        OPNsenseWidgetProvider.Log($"CreateInstance called riid={riid}");
        ppvObject = IntPtr.Zero;

        try
        {
            if (pUnkOuter is not null)
            {
                OPNsenseWidgetProvider.Log("CreateInstance: CLASS_E_NOAGGREGATION");
                return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
            }

            ppvObject = MarshalInspectable<OPNsenseWidgetProvider>.FromManaged(_provider);
            OPNsenseWidgetProvider.Log($"CreateInstance: succeeded ppvObject=0x{ppvObject:X}");
            return 0; // S_OK
        }
        catch (Exception ex)
        {
            OPNsenseWidgetProvider.Log($"CreateInstance EXCEPTION: {ex}");
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
