using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace COM;

static class Guids
{
    public const string IClassFactory = "00000001-0000-0000-C000-000000000046";
    public const string IUnknown = "00000000-0000-0000-C000-000000000046";
}

/// <summary>
/// IClassFactory declaration.
/// </summary>
[ComImport, ComVisible(false), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid(COM.Guids.IClassFactory)]
internal interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);
    [PreserveSig]
    int LockServer(bool fLock);
}

[ComVisible(true)]
class WidgetProviderFactory<T> : IClassFactory
    where T : IWidgetProvider, new()
{
    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;

        if (pUnkOuter != IntPtr.Zero)
        {
            Marshal.ThrowExceptionForHR(CLASS_E_NOAGGREGATION);
        }

        CodexUsageWidgetProvider.ProviderLog.Write($"CreateInstance riid={riid}");

        if (riid == typeof(IWidgetProvider).GUID ||
            riid == typeof(IWidgetResourceProvider).GUID ||
            riid == Guid.Parse(COM.Guids.IUnknown) ||
            riid == Guid.Parse("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90") ||
            riid == typeof(T).GUID)
        {
            // Create the instance of the .NET object supporting all implemented WinRT interfaces.
            ppvObject = MarshalInspectable<object>.FromManaged(new T());
            CodexUsageWidgetProvider.ProviderLog.Write($"CreateInstance success for riid={riid}");
            return 0;
        }

        CodexUsageWidgetProvider.ProviderLog.Write($"CreateInstance E_NOINTERFACE for riid={riid}");
        Marshal.ThrowExceptionForHR(E_NOINTERFACE);
        return 0;
    }

    int IClassFactory.LockServer(bool fLock)
    {
        return 0;
    }

    private const int CLASS_E_NOAGGREGATION = -2147221232;
    private const int E_NOINTERFACE = -2147467262;
}
