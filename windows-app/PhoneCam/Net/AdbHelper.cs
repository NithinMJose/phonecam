using System.Diagnostics;
using System.IO;
using PhoneCam.Logging;

namespace PhoneCam.Net;

public static class AdbHelper
{
    public static async Task<(bool Ok, string Message)> TrySetupReverseAsync(int port, DebugLogger logger)
    {
        var adbPath = FindAdbPath();
        if (adbPath == null)
        {
            return (false, "adb.exe not found (USB mode needs platform-tools; WiFi still works)");
        }

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = adbPath,
                    Arguments = $"reverse tcp:{port} tcp:{port}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0)
            {
                logger.Log($"adb reverse tcp:{port} succeeded", force: true);
                return (true, $"adb reverse active (tcp:{port})");
            }

            string err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            string clean = err.Trim();
            if (clean.Contains("no devices", StringComparison.OrdinalIgnoreCase))
            {
                clean = "No USB phone detected (WiFi still works)";
            }
            logger.Log($"adb reverse skipped ({proc.ExitCode}): {clean}", force: true);
            return (false, clean);
        }
        catch (Exception ex)
        {
            logger.Log($"adb reverse error: {ex.Message}", force: true);
            return (false, $"adb error: {ex.Message}");
        }
    }

    private static string? FindAdbPath()
    {
        var bundledAdb = Path.Combine(AppContext.BaseDirectory, "adb.exe");
        if (File.Exists(bundledAdb)) return bundledAdb;

        var localSdkAdb = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Android", "Sdk", "platform-tools", "adb.exe");
        if (File.Exists(localSdkAdb)) return localSdkAdb;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "adb.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

