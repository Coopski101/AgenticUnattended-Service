using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AgenticUnattended.Tray;

public static class AutoStartManager
{
    private const string AppName = "AgenticUnattendedService";

    public static bool IsEnabled()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return IsEnabledWindows();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return IsEnabledMacOS();
        return false;
    }

    public static void SetEnabled(bool enabled)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            SetEnabledWindows(enabled);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            SetEnabledMacOS(enabled);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsEnabledWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
        return key?.GetValue(AppName) is not null;
    }

    [SupportedOSPlatform("windows")]
    private static void SetEnabledWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key is null) return;

        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path");
            key.SetValue(AppName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }

    private static bool IsEnabledMacOS()
    {
        return File.Exists(GetMacPlistPath());
    }

    private static void SetEnabledMacOS(bool enabled)
    {
        var path = GetMacPlistPath();
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path");
            var plist = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>com.agentic-unattended</string>
                    <key>ProgramArguments</key>
                    <array>
                        <string>{exe}</string>
                    </array>
                    <key>RunAtLoad</key>
                    <true/>
                    <key>KeepAlive</key>
                    <false/>
                </dict>
                </plist>
                """;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, plist);
        }
        else
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static string GetMacPlistPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "LaunchAgents", "com.agentic-unattended.plist");
    }
}
