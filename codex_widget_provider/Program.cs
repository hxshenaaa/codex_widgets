using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using CodexUsageWidgetProvider;
using COM;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace CodexUsageWidgetProvider;

internal static class Program
{
    private const string ProviderClsid = "d1db3a28-42d3-41d2-b968-eb646f3069d2";

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
        uint dwClsContext,
        uint flags,
        out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);

    private static async Task<int> Main(string[] args)
    {
        ProviderLog.Write("provider starting, args=" + string.Join(" ", args));
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            ProviderLog.Write("UNHANDLED exception: " + e.ExceptionObject);

        if (args.Length > 0 && args[0] == "--probe")
        {
            return await ProbeAsync();
        }

        // CLSCTX_LOCAL_SERVER = 0x4, REGCLS_SINGLEUSE = 0x1.
        int hr = CoRegisterClassObject(
            Guid.Parse(ProviderClsid),
            new WidgetProviderFactory<WidgetProvider>(),
            0x4,
            0x1,
            out uint cookie);

        ProviderLog.Write("CoRegisterClassObject hr=0x" + hr.ToString("X8"));
        if (hr != 0)
        {
            return hr;
        }

        if (GetConsoleWindow() != IntPtr.Zero)
        {
            // Debug console mode: keep the process alive until Enter.
            Console.WriteLine("Codex Usage Widget provider registered. Press ENTER to exit.");
            Console.ReadLine();
        }
        else
        {
            // Production: stay alive until the last pinned widget is removed.
            using var emptyWidgetListEvent = WidgetProvider.GetEmptyWidgetListEvent();
            emptyWidgetListEvent.WaitOne();
        }

        CoRevokeClassObject(cookie);
        return 0;
    }

    /// <summary>
    /// Self-test: query codex once and dump the widget payload without the widgets host.
    /// </summary>
    private static async Task<int> ProbeAsync()
    {
        var provider = new WidgetProvider();
        var inspectable = MarshalInspectable<object>.FromManaged(provider);
        Guid iwpGuid = typeof(IWidgetProvider).GUID;
        Guid iwrpGuid = typeof(IWidgetResourceProvider).GUID;
        int hr1 = Marshal.QueryInterface(inspectable, ref iwpGuid, out IntPtr pIwp);
        int hr2 = Marshal.QueryInterface(inspectable, ref iwrpGuid, out IntPtr pIwrp);
        Console.WriteLine($"QI IWidgetProvider ({iwpGuid}): hr=0x{hr1:X8}, ptr={pIwp}");
        Console.WriteLine($"QI IWidgetResourceProvider ({iwrpGuid}): hr=0x{hr2:X8}, ptr={pIwrp}");

        var service = new CodexUsageService();
        try
        {
            var data = await service.GetLatestAsync();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            Console.WriteLine(JsonSerializer.Serialize(data, options));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"probe failed: {ex.Message}");
            return 1;
        }
        finally
        {
            service.Shutdown();
        }
    }
}
