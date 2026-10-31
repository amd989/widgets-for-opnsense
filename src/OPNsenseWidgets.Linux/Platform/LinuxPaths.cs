using OPNsenseWidgets.Hosting;

namespace OPNsenseWidgets.Linux.Platform;

public sealed class LinuxPaths : IPlatformPaths
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>XDG_DOWNLOAD_DIR from ~/.config/user-dirs.dirs (localized names), else ~/Downloads.</summary>
    public string DownloadsFolder
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(configHome))
                configHome = Path.Combine(Home, ".config");

            var userDirs = Path.Combine(configHome, "user-dirs.dirs");
            try
            {
                if (File.Exists(userDirs))
                {
                    foreach (var line in File.ReadLines(userDirs))
                    {
                        if (!line.StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal))
                            continue;
                        var value = line["XDG_DOWNLOAD_DIR=".Length..].Trim().Trim('"');
                        value = value.Replace("$HOME", Home);
                        if (!string.IsNullOrEmpty(value))
                            return value;
                    }
                }
            }
            catch (IOException)
            {
                // Fall back to the default below.
            }

            return Path.Combine(Home, "Downloads");
        }
    }
}
