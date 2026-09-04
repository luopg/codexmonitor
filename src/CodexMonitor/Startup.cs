using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexMonitor;

internal static class Startup
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

    private const string Name = "CodexMonitor";

    private static string ExpectedCommand => $"\"{Application.ExecutablePath}\" --background";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using RegistryKey? registryKey = Registry.CurrentUser.OpenSubKey(RunKey);
                return registryKey?.GetValue(Name) is string command
                    && string.Equals(command, ExpectedCommand, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey? registryKey = Registry.CurrentUser.CreateSubKey(RunKey);
            if (registryKey is null)
            {
                return IsEnabled;
            }
            if (enabled)
            {
                registryKey.SetValue(Name, ExpectedCommand);
            }
            else
            {
                registryKey.DeleteValue(Name, throwOnMissingValue: false);
            }
            return enabled;
        }
        catch
        {
            return IsEnabled;
        }
    }
}
