using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OPNsenseWidgets.Api;
using OPNsenseWidgets.Configuration;
using OPNsenseWidgets.Hosting;
using OPNsenseWidgets.Logging;
using OPNsenseWidgets.Platform;
using OPNsenseWidgets.Provider;
using OPNsenseWidgets.Refresh;
using OPNsenseWidgets.Widgets;
using OPNsenseWidgets.Widgets.DhcpLeases;
using OPNsenseWidgets.Widgets.Firewall;
using OPNsenseWidgets.Widgets.Traffic;

namespace OPNsenseWidgets;

internal class Program
{
    private static readonly Guid ProviderClsid = Guid.Parse("D40B2E99-B044-4758-9AB5-212C01B368EE");

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "opnsense-widget-debug.log");

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
        var services = new ServiceCollection();

        // Logging
        services.AddLogging(builder =>
        {
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss.fff ";
            });
            builder.AddProvider(new FileLoggerProvider(LogPath));
        });

        // Configuration
        services.AddSingleton<ISettingsManager, SettingsManager>();
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<IPlatformPaths, WindowsPaths>();

        // API
        services.AddSingleton<IOPNsenseApiClientFactory, OPNsenseApiClientFactory>();

        // Widget definitions
        services.AddSingleton<FirstSeenStore>();
        services.AddSingleton<IWidgetDefinition, DhcpLeasesWidget>();
        services.AddSingleton<IWidgetDefinition, TrafficWidget>();
        services.AddSingleton<IWidgetDefinition, FirewallWidget>();

        // Shared widget logic, with the Widget Board provider as its host
        services.AddSingleton<WidgetController>();
        services.AddSingleton<OPNsenseWidgetProvider>();
        services.AddSingleton<WidgetProviderFactory>();
        services.AddSingleton<RefreshService>();

        using var serviceProvider = services.BuildServiceProvider();

        var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

        logger.LogInformation("Main started, args=[{Args}]", string.Join(", ", args));

        WinRT.ComWrappersSupport.InitializeComWrappers();
        logger.LogInformation("InitializeComWrappers done");

        var provider = serviceProvider.GetRequiredService<OPNsenseWidgetProvider>();
        var factory = serviceProvider.GetRequiredService<WidgetProviderFactory>();

        var hr = CoRegisterClassObject(
            ProviderClsid,
            factory,
            CLSCTX_LOCAL_SERVER,
            REGCLS_MULTIPLEUSE,
            out var cookie);

        logger.LogInformation("CoRegisterClassObject hr=0x{Hr:X8} cookie={Cookie}", hr, cookie);

        if (hr != 0)
        {
            logger.LogError("Failed to register COM class object: 0x{Hr:X8}", hr);
            return;
        }

        var refreshService = serviceProvider.GetRequiredService<RefreshService>();
        refreshService.Start();

        // GetWidgetInfos can hang while the board is calling into a freshly started provider,
        // so never block startup on it. Widgets the board talks to are adopted on first contact anyway.
        _ = Task.Run(() =>
        {
            try
            {
                provider.RecoverRunningWidgets();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "RecoverRunningWidgets failed");
            }
        });

        logger.LogInformation("Waiting for EmptyEvent...");
        serviceProvider.GetRequiredService<WidgetController>().EmptyEvent.Wait();

        logger.LogInformation("EmptyEvent fired, shutting down");
        refreshService.Stop();
        CoRevokeClassObject(cookie);
    }
}
