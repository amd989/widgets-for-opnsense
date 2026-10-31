using System.Net;
using OPNsenseWidgets.Api;
using OPNsenseWidgets.Configuration;
using OPNsenseWidgets.Hosting;
using OPNsenseWidgets.Linux;
using OPNsenseWidgets.Linux.Platform;
using OPNsenseWidgets.Refresh;
using OPNsenseWidgets.Widgets;
using OPNsenseWidgets.Widgets.DhcpLeases;
using OPNsenseWidgets.Widgets.Firewall;
using OPNsenseWidgets.Widgets.Traffic;

// opnsense-widgetsd: serves the widget pages on 127.0.0.1 and feeds them over WebSockets.
//   GET /widget?def=<DefinitionId>&id=<instance>   redirects to the page for the widget's current view
//   GET /<Name>/Web/<file>, /Shared/Web/<file>     page files (same paths as on Windows)
//   WS  /ws?def=<DefinitionId>&id=<instance>&path=<page path>
var port = int.TryParse(Environment.GetEnvironmentVariable("OPNSENSE_WIDGETS_PORT"), out var envPort) ? envPort : 47820;
string[] allowedHosts = [$"127.0.0.1:{port}", $"localhost:{port}"];

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.IncludeScopes = false;
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var services = builder.Services;
services.AddSingleton<ISettingsManager, SettingsManager>();
services.AddSingleton<ICredentialStore, SecretServiceCredentialStore>();
services.AddSingleton<IPlatformPaths, LinuxPaths>();
services.AddSingleton<IOPNsenseApiClientFactory, OPNsenseApiClientFactory>();
services.AddSingleton<FirstSeenStore>();
services.AddSingleton<IWidgetDefinition, DhcpLeasesWidget>();
services.AddSingleton<IWidgetDefinition, TrafficWidget>();
services.AddSingleton<IWidgetDefinition, FirewallWidget>();
services.AddSingleton<WidgetController>();
services.AddSingleton<PageHost>();
services.AddSingleton<RefreshService>();

var app = builder.Build();
var pages = app.Services.GetRequiredService<PageHost>(); // attaches itself as the controller's host

// Only answer requests addressed to this port on loopback. Blocks DNS rebinding (a website whose
// name resolves to 127.0.0.1) and other sites opening the socket.
app.Use(async (context, next) =>
{
    var host = context.Request.Host.Value ?? "";
    if (!allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    var origin = context.Request.Headers.Origin.ToString();
    if (origin.Length > 0 && !allowedHosts.Any(h => string.Equals(origin, "http://" + h, StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    await next();
});

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapGet("/health", () => "ok");

app.MapGet("/widget", (HttpContext context, string? def, string? id) =>
{
    if (string.IsNullOrEmpty(def) || string.IsNullOrEmpty(id))
        return Results.BadRequest("def and id are required");

    var info = pages.GetWidget(id, def);
    var path = info is null ? null : pages.CurrentPath(info);
    if (path is null)
        return Results.NotFound($"Unknown widget {def}");

    return Results.Redirect("/" + path + context.Request.QueryString);
});

app.Map("/ws", async (HttpContext context, string? def, string? id, string? path) =>
{
    if (!context.WebSockets.IsWebSocketRequest || string.IsNullOrEmpty(def) || string.IsNullOrEmpty(id))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var info = pages.GetWidget(id, def);
    if (info is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await pages.RunAsync(socket, info, path ?? "", context.RequestAborted);
});

app.MapGet("/{**path}", (string? path) =>
{
    if (path is null || !WebAssets.TryGet(path, out var content, out var contentType))
        return Results.NotFound();
    return Results.Bytes(content, contentType);
});

var refresh = app.Services.GetRequiredService<RefreshService>();
app.Lifetime.ApplicationStarted.Register(refresh.Start);
app.Lifetime.ApplicationStopping.Register(refresh.Stop);

app.Run();
