using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OPNsenseWidgets.Configuration;

namespace OPNsenseWidgets.Linux.Platform;

/// <summary>
/// Keeps the API key and secret in the desktop keyring through the Secret Service API (KWallet on
/// KDE, GNOME Keyring on GNOME), using the <c>secret-tool</c> command from libsecret.
/// Without a keyring it falls back to ~/.opnsense-widgets/credentials.json, readable only by the user.
/// </summary>
public sealed class SecretServiceCredentialStore : ICredentialStore
{
    private static readonly string[] Attributes = ["service", "opnsense-widgets", "account", "api"];
    private const string Label = "Widgets for OPNsense API key";
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MissRetry = TimeSpan.FromSeconds(15);

    private readonly string _fallbackPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opnsense-widgets", "credentials.json");
    private readonly ILogger<SecretServiceCredentialStore> _logger;
    private readonly object _lock = new();

    // Lookups run on every widget update, so keep the last result instead of starting secret-tool each time.
    private (string ApiKey, string ApiSecret)? _cached;
    private DateTime _missAt = DateTime.MinValue;

    public SecretServiceCredentialStore(ILogger<SecretServiceCredentialStore> logger) => _logger = logger;

    public (string ApiKey, string ApiSecret)? LoadCredentials()
    {
        lock (_lock)
        {
            if (_cached is not null)
                return _cached;
            if (DateTime.Now - _missAt < MissRetry)
                return null;

            var json = RunSecretTool(["lookup", .. Attributes], stdin: null);
            if (json is null && File.Exists(_fallbackPath))
                json = File.ReadAllText(_fallbackPath);

            _cached = Parse(json);
            if (_cached is null)
                _missAt = DateTime.Now;
            return _cached;
        }
    }

    public void SaveCredentials(string apiKey, string apiSecret)
    {
        var json = JsonSerializer.Serialize(new StoredCredentials(apiKey, apiSecret));
        lock (_lock)
        {
            if (RunSecretTool(["store", "--label", Label, .. Attributes], stdin: json) is not null)
            {
                if (File.Exists(_fallbackPath))
                    File.Delete(_fallbackPath);
            }
            else
            {
                _logger.LogWarning("No Secret Service keyring available; saving credentials to {Path} (mode 600)", _fallbackPath);
                WriteFallback(json);
            }

            _cached = (apiKey, apiSecret);
        }
    }

    public void DeleteCredentials()
    {
        lock (_lock)
        {
            RunSecretTool(["clear", .. Attributes], stdin: null);
            if (File.Exists(_fallbackPath))
                File.Delete(_fallbackPath);
            _cached = null;
            _missAt = DateTime.MinValue;
        }
    }

    private void WriteFallback(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_fallbackPath)!);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(_fallbackPath, json); // local testing only
            return;
        }

        // Create with 600 before writing, so the secret is never readable by others.
        using (var stream = new FileStream(_fallbackPath, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        }))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(json);
        }
        File.SetUnixFileMode(_fallbackPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static (string, string)? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredCredentials>(json);
            return stored is { Key.Length: > 0, Secret.Length: > 0 } ? (stored.Key, stored.Secret) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Runs secret-tool. Returns its output (empty for store/clear) on success, null on any failure.</summary>
    private string? RunSecretTool(string[] args, string? stdin)
    {
        try
        {
            var psi = new ProcessStartInfo("secret-tool")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            if (stdin is not null)
                process.StandardInput.Write(stdin);
            process.StandardInput.Close();

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(ToolTimeout))
            {
                process.Kill();
                _logger.LogWarning("secret-tool {Command} timed out (keyring locked?)", args[0]);
                return null;
            }

            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd().Trim();
                if (error.Length > 0)
                    _logger.LogWarning("secret-tool {Command} failed: {Error}", args[0], error);
                return null;
            }

            return output.Result;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning("secret-tool not found; install libsecret to keep credentials in the keyring");
            return null;
        }
    }

    private sealed record StoredCredentials(string Key, string Secret);
}
