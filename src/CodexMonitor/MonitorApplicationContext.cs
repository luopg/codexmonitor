using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexMonitor;

internal sealed class MonitorApplicationContext : ApplicationContext
{
    private const string ReminderModeDescription = "平时隐藏监控窗口；任务完成或运行中任务数降至设定阈值时，窗口会自动弹出并置前，同时按声音设置播放提示音。";

    private readonly NotifyIcon tray;

    private readonly MonitorWindow window;

    private readonly CodexLogMonitor monitor;

    private readonly UsageBalanceService balanceService;

    private readonly object monitorGate = new();

    private readonly System.Windows.Forms.Timer pollTimer;

    private readonly ToolStripMenuItem soundItem;

    private readonly ToolStripMenuItem startupItem;

    private readonly ToolStripMenuItem topMostItem;

    private readonly ToolStripMenuItem compactItem;

    private readonly ToolStripMenuItem reminderItem;

    private readonly ToolStripMenuItem reminderThresholdItem;

    private bool closing;

    private bool balanceRefreshing;

    private bool pollInProgress;

    private DateTime nextBalanceRefresh = DateTime.MinValue;

    private int currentProjectCount;

    private int? previousActiveTaskCount;

    private string? currentBalanceText;

    public MonitorApplicationContext(bool showOnStart)
    {
        window = new MonitorWindow();
        monitor = new CodexLogMonitor();
        balanceService = new UsageBalanceService();
        soundItem = new ToolStripMenuItem("完成时播放声音")
        {
            Checked = Settings.SoundEnabled,
            CheckOnClick = true
        };
        soundItem.CheckedChanged += delegate
        {
            Settings.SoundEnabled = soundItem.Checked;
        };
        startupItem = new ToolStripMenuItem("随 Windows 启动")
        {
            Checked = Startup.IsEnabled,
            CheckOnClick = true
        };
        startupItem.CheckedChanged += delegate
        {
            startupItem.Checked = Startup.SetEnabled(startupItem.Checked);
            window.SetStartupState(startupItem.Checked);
        };
        topMostItem = new ToolStripMenuItem("窗口置顶")
        {
            Checked = Settings.AlwaysOnTop,
            CheckOnClick = true
        };
        topMostItem.CheckedChanged += delegate
        {
            window.SetAlwaysOnTop(topMostItem.Checked, persist: true);
        };
        window.AlwaysOnTopChanged += delegate (bool value)
        {
            if (topMostItem.Checked != value)
            {
                topMostItem.Checked = value;
            }
        };
        compactItem = new ToolStripMenuItem("窄窗模式")
        {
            Checked = Settings.CompactMode,
            CheckOnClick = true
        };
        compactItem.CheckedChanged += delegate
        {
            window.SetCompactMode(compactItem.Checked, persist: true);
        };
        window.CompactModeChanged += delegate (bool value)
        {
            if (compactItem.Checked != value)
            {
                compactItem.Checked = value;
            }
        };
        window.StartupChanged += delegate (bool value)
        {
            if (startupItem.Checked != value)
            {
                startupItem.Checked = value;
            }
        };
        reminderItem = new ToolStripMenuItem("提醒模式（完成时弹出）")
        {
            Checked = Settings.ReminderMode,
            CheckOnClick = true,
            ToolTipText = "平时隐藏监控窗口；任务完成或运行中任务数降至设定阈值时，窗口会自动弹出并置前，同时按声音设置播放提示音。"
        };
        reminderItem.CheckedChanged += delegate
        {
            window.SetReminderMode(reminderItem.Checked, persist: true);
        };
        DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(14, 1);
        defaultInterpolatedStringHandler.AppendLiteral("设置提醒阈值（≤ ");
        defaultInterpolatedStringHandler.AppendFormatted(Settings.ReminderTaskThreshold);
        defaultInterpolatedStringHandler.AppendLiteral(" 个任务）");
        reminderThresholdItem = new ToolStripMenuItem(defaultInterpolatedStringHandler.ToStringAndClear());
        reminderThresholdItem.Click += delegate
        {
            window.PromptReminderThreshold();
        };
        ContextMenuStrip contextMenuStrip = new ContextMenuStrip
        {
            Items =
            {
                {
                    "打开监控面板",
                    (Image?)null,
                    (EventHandler)delegate
                    {
                        ShowWindow();
                    }
                },
                (ToolStripItem)new ToolStripSeparator(),
                (ToolStripItem)soundItem,
                (ToolStripItem)startupItem,
                (ToolStripItem)topMostItem,
                (ToolStripItem)compactItem,
                (ToolStripItem)reminderItem,
                (ToolStripItem)reminderThresholdItem,
                {
                    "播放测试声音",
                    (Image?)null,
                    (EventHandler)delegate
                    {
                        CompletionSound.Play();
                    }
                },
                (ToolStripItem)new ToolStripSeparator(),
                {
                    "退出",
                    (Image?)null,
                    (EventHandler)delegate
                    {
                        ExitThread();
                    }
                }
            }
        };
        tray = new NotifyIcon
        {
            Visible = true,
            Text = "Codex Monitor",
            ContextMenuStrip = contextMenuStrip,
            Icon = TrayIcon.Create(0)
        };
        window.ReminderModeChanged += delegate (bool value)
        {
            if (reminderItem.Checked != value)
            {
                reminderItem.Checked = value;
            }
            if (value)
            {
                window.Hide();
                tray.BalloonTipTitle = "提醒模式已开启";
                tray.BalloonTipText = "平时隐藏监控窗口；任务完成或运行中任务数降至设定阈值时，窗口会自动弹出并置前，同时按声音设置播放提示音。";
                tray.ShowBalloonTip(5000);
            }
        };
        window.ReminderThresholdChanged += delegate (int value)
        {
            ToolStripMenuItem toolStripMenuItem = reminderThresholdItem;
            DefaultInterpolatedStringHandler defaultInterpolatedStringHandler2 = new DefaultInterpolatedStringHandler(14, 1);
            defaultInterpolatedStringHandler2.AppendLiteral("设置提醒阈值（≤ ");
            defaultInterpolatedStringHandler2.AppendFormatted(value);
            defaultInterpolatedStringHandler2.AppendLiteral(" 个任务）");
            toolStripMenuItem.Text = defaultInterpolatedStringHandler2.ToStringAndClear();
        };
        tray.DoubleClick += delegate
        {
            ShowWindow();
        };
        tray.MouseClick += delegate (object? _, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowWindow();
            }
        };
        pollTimer = new System.Windows.Forms.Timer
        {
            Interval = 1200
        };
        pollTimer.Tick += Poll;
        pollTimer.Start();
        Poll(null, EventArgs.Empty);
        if (showOnStart)
        {
            ShowWindow();
        }
    }

    private void ShowWindow()
    {
        if (window.WindowState == FormWindowState.Minimized)
        {
            window.WindowState = FormWindowState.Normal;
        }
        window.Show();
        window.BringToFront();
        window.Activate();
    }

    private async void Poll(object? sender, EventArgs e)
    {
        if (closing || pollInProgress)
        {
            return;
        }

        pollInProgress = true;
        try
        {
            MonitorSnapshot monitorSnapshot = await Task.Run(() =>
            {
                lock (monitorGate)
                {
                    return monitor.ReadSnapshot();
                }
            });
            if (closing)
            {
                return;
            }
            if (monitorSnapshot.Error is string snapshotError)
            {
                window.UpdateError(snapshotError);
                tray.Text = "Codex Monitor — 等待 Codex 状态";
                if (!balanceRefreshing && DateTime.Now >= nextBalanceRefresh)
                {
                    _ = RefreshBalanceAsync();
                }
                return;
            }

            int completedEvents = monitorSnapshot.CompletedEvents;
            int reminderTaskThreshold = Settings.ReminderTaskThreshold;
            bool thresholdReached = Settings.ReminderMode
                && previousActiveTaskCount is int previousCount
                && previousCount > reminderTaskThreshold
                && monitorSnapshot.ActiveTasks.Count <= reminderTaskThreshold;
            previousActiveTaskCount = monitorSnapshot.ActiveTasks.Count;
            if ((completedEvents > 0 || thresholdReached) && Settings.SoundEnabled)
            {
                CompletionSound.Play();
            }
            if (thresholdReached)
            {
                tray.BalloonTipTitle = "Codex 任务数提醒";
                NotifyIcon notifyIcon = tray;
                DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(26, 2);
                defaultInterpolatedStringHandler.AppendLiteral("当前有 ");
                defaultInterpolatedStringHandler.AppendFormatted(monitorSnapshot.ActiveTasks.Count);
                defaultInterpolatedStringHandler.AppendLiteral(" 个任务运行中，已达到 ≤ ");
                defaultInterpolatedStringHandler.AppendFormatted(reminderTaskThreshold);
                defaultInterpolatedStringHandler.AppendLiteral(" 个的提醒阈值。");
                notifyIcon.BalloonTipText = defaultInterpolatedStringHandler.ToStringAndClear();
                tray.ShowBalloonTip(5000);
                ShowWindow();
            }
            else if (completedEvents > 0)
            {
                tray.BalloonTipTitle = "Codex 已完成";
                NotifyIcon notifyIcon2 = tray;
                object balloonTipText;
                if (completedEvents != 1)
                {
                    DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(15, 1);
                    defaultInterpolatedStringHandler.AppendFormatted(completedEvents);
                    defaultInterpolatedStringHandler.AppendLiteral(" 个 Codex 任务已完成。");
                    balloonTipText = defaultInterpolatedStringHandler.ToStringAndClear();
                }
                else
                {
                    balloonTipText = "一个 Codex 任务已完成。";
                }
                notifyIcon2.BalloonTipText = (string)balloonTipText;
                tray.ShowBalloonTip(3500);
                if (Settings.ReminderMode)
                {
                    ShowWindow();
                }
            }
            window.UpdateSnapshot(monitorSnapshot, completedEvents);
            Icon? icon = tray.Icon;
            tray.Icon = TrayIcon.Create(monitorSnapshot.ProjectCount);
            icon?.Dispose();
            currentProjectCount = monitorSnapshot.ProjectCount;
            UpdateTrayText(hasError: false);
            if (!balanceRefreshing && DateTime.Now >= nextBalanceRefresh)
            {
                _ = RefreshBalanceAsync();
            }
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                window.UpdateError(ex.Message);
                tray.Text = "Codex Monitor — 状态读取失败";
            }
        }
        finally
        {
            pollInProgress = false;
        }
    }

    private async Task RefreshBalanceAsync()
    {
        balanceRefreshing = true;
        try
        {
            BalanceSnapshot balanceSnapshot = await balanceService.ReadAsync();
            if (closing)
            {
                return;
            }
            window.UpdateBalance(balanceSnapshot);
            decimal? remaining = balanceSnapshot.Remaining;
            if (remaining.HasValue)
            {
                decimal valueOrDefault = remaining.GetValueOrDefault();
                if (!(balanceSnapshot.Unit == "USD"))
                {
                    currentBalanceText = $"{valueOrDefault:N2}{balanceSnapshot.Unit}";
                }
                else
                {
                    currentBalanceText = $"${valueOrDefault:N2}";
                }
            }
            else
            {
                currentBalanceText = null;
            }
            UpdateTrayText(hasError: false);
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                window.UpdateBalance(new BalanceSnapshot("API", null, "USD", null, null, ex.Message, DateTime.Now, IsConfigured: false));
            }
        }
        finally
        {
            nextBalanceRefresh = DateTime.Now.AddSeconds(30.0);
            balanceRefreshing = false;
        }
    }

    private void UpdateTrayText(bool hasError)
    {
        if (hasError)
        {
            tray.Text = "Codex Monitor — 等待 Codex 状态";
            return;
        }
        NotifyIcon notifyIcon = tray;
        string text;
        if (currentBalanceText != null)
        {
            DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(26, 2);
            defaultInterpolatedStringHandler.AppendLiteral("Codex Monitor · ");
            defaultInterpolatedStringHandler.AppendFormatted(currentProjectCount);
            defaultInterpolatedStringHandler.AppendLiteral(" 个项目 · 余额 ");
            defaultInterpolatedStringHandler.AppendFormatted(currentBalanceText);
            text = defaultInterpolatedStringHandler.ToStringAndClear();
        }
        else
        {
            DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(23, 1);
            defaultInterpolatedStringHandler.AppendLiteral("Codex Monitor · ");
            defaultInterpolatedStringHandler.AppendFormatted(currentProjectCount);
            defaultInterpolatedStringHandler.AppendLiteral(" 个项目工作中");
            text = defaultInterpolatedStringHandler.ToStringAndClear();
        }
        notifyIcon.Text = text;
    }

    protected override void ExitThreadCore()
    {
        if (!closing)
        {
            closing = true;
            pollTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            window.Dispose();
            lock (monitorGate)
            {
                monitor.Dispose();
            }
            balanceService.Dispose();
            base.ExitThreadCore();
        }
    }
}
