namespace WebAirIG.Common.Video;

/// <summary>Finds ffmpeg.exe: explicit path, WEBAIRIG_FFMPEG env var, ./tools/ffmpeg/bin next to the app or repo, then PATH.</summary>
public static class FfmpegLocator
{
    public static string? Find(string? configuredPath, params string[] extraSearchRoots)
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath)) return Path.GetFullPath(configuredPath);
            var inDir = Path.Combine(configuredPath, exe);
            if (File.Exists(inDir)) return Path.GetFullPath(inDir);
        }

        var env = System.Environment.GetEnvironmentVariable("WEBAIRIG_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        foreach (var root in extraSearchRoots.Append(AppContext.BaseDirectory))
        {
            var dir = root;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                var candidate = Path.Combine(dir, "tools", "ffmpeg", "bin", exe);
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
        }

        // winget installs (Gyan.FFmpeg / BtbN.FFmpeg): a Links shim folder plus the extracted package folders.
        // Checked explicitly because already-running processes do not see PATH changes made by the installer.
        if (OperatingSystem.IsWindows())
        {
            var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            var link = Path.Combine(local, "Microsoft", "WinGet", "Links", exe);
            if (File.Exists(link)) return link;
            var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                foreach (var pkg in Directory.EnumerateDirectories(packages, "*FFmpeg*"))
                {
                    var found = Directory.EnumerateFiles(pkg, exe, SearchOption.AllDirectories).FirstOrDefault();
                    if (found is not null) return found;
                }
            }
        }

        var pathVar = System.Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(p.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
