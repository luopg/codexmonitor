using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace CodexMonitor;

internal static class Program
{
    private static Mutex? instanceMutex;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--test-sound"))
        {
            CompletionSound.PlaySync();
            return 0;
        }
        if (args.Length == 2 && args[0] == "--diagnostic-snapshot")
        {
            using (CodexLogMonitor codexLogMonitor = new CodexLogMonitor())
            {
                File.WriteAllText(args[1], JsonSerializer.Serialize(codexLogMonitor.ReadSnapshot(), new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
                return 0;
            }
        }
        if (args.Length == 2 && args[0] == "--diagnostic-balance")
        {
            using (UsageBalanceService usageBalanceService = new UsageBalanceService())
            {
                BalanceSnapshot balanceSnapshot = usageBalanceService.ReadAsync().GetAwaiter().GetResult();
                File.WriteAllText(args[1], JsonSerializer.Serialize(balanceSnapshot, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
                return 0;
            }
        }
        instanceMutex = new Mutex(initiallyOwned: true, "Local\\CodexMonitor.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            if (!args.Contains("--background"))
            {
                MessageBox.Show("Codex Monitor 已在系统托盘中运行。", "Codex Monitor", MessageBoxButtons.OK, MessageBoxIcon.Asterisk);
            }
            return 0;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MonitorApplicationContext(!args.Contains("--background")));
        instanceMutex!.ReleaseMutex();
        return 0;
    }
}
