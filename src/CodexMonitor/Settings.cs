using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CodexMonitor;

internal static class Settings
{
    private static readonly string SettingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexMonitor");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.ini");

    public static bool SoundEnabled
    {
        get
        {
            return Read("sound", "true") != "false";
        }
        set
        {
            Write("sound", value ? "true" : "false");
        }
    }

    public static bool AlwaysOnTop
    {
        get
        {
            return Read("always_on_top", "false") == "true";
        }
        set
        {
            Write("always_on_top", value ? "true" : "false");
        }
    }

    public static bool CompactMode
    {
        get
        {
            return Read("compact_mode", "false") == "true";
        }
        set
        {
            Write("compact_mode", value ? "true" : "false");
        }
    }

    public static bool ReminderMode
    {
        get
        {
            return Read("reminder_mode", "false") == "true";
        }
        set
        {
            Write("reminder_mode", value ? "true" : "false");
        }
    }

    public static int ReminderTaskThreshold
    {
        get
        {
            if (!int.TryParse(Read("reminder_task_threshold", "5"), out var result))
            {
                return 5;
            }
            return Math.Clamp(result, 0, 99);
        }
        set
        {
            Write("reminder_task_threshold", Math.Clamp(value, 0, 99).ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string Read(string key, string fallback)
    {
        string key2 = key;
        try
        {
            string? obj = (File.Exists(SettingsPath) ? File.ReadAllLines(SettingsPath).FirstOrDefault((string x) => x.StartsWith(key2 + "=", StringComparison.OrdinalIgnoreCase)) : null);
            return ((obj != null) ? obj!.Split('=', 2)[1] : null) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void Write(string key, string value)
    {
        string key2 = key;
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            List<string> list = (File.Exists(SettingsPath) ? (from x in File.ReadAllLines(SettingsPath)
                                                              where !x.StartsWith(key2 + "=", StringComparison.OrdinalIgnoreCase)
                                                              select x).ToList() : new List<string>());
            list.Add(key2 + "=" + value);
            File.WriteAllLines(SettingsPath, list);
        }
        catch
        {
        }
    }
}
