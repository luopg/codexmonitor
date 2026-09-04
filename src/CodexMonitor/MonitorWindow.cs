using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace CodexMonitor;

internal sealed class MonitorWindow : Form
{
    private const string ReminderModeDescription = "平时隐藏监控窗口；任务完成或运行中任务数降至设定阈值时，窗口会自动弹出并置前，同时按声音设置播放提示音。你仍可随时点击托盘图标打开窗口。";

    private readonly Label projectValue = new Label();

    private readonly Label projectMeta = new Label();

    private readonly Label balanceValue = new Label();

    private readonly Label balanceMeta = new Label();

    private readonly Label statusLabel = new Label();

    private readonly DataGridView grid = new DataGridView();

    private readonly Label emptyLabel = new Label();

    private readonly Button pinButton = new Button();

    private readonly Button startupButton = new Button();

    private readonly Button compactButton = new Button();

    private readonly Button reminderButton = new Button();

    private readonly Button reminderThresholdButton = new Button();

    private readonly Button compactExpandButton = new Button();

    private readonly ToolTip helpTips = new ToolTip();

    private readonly TableLayoutPanel rootLayout;

    private readonly Control headerPanel;

    private readonly Control listHeaderPanel;

    private readonly Control gridHostPanel;

    private readonly Control footerControl;

    private Size expandedClientSize = new Size(860, 570);

    private bool compactMode;

    public event Action<bool>? AlwaysOnTopChanged;

    public event Action<bool>? CompactModeChanged;

    public event Action<bool>? StartupChanged;

    public event Action<bool>? ReminderModeChanged;

    public event Action<int>? ReminderThresholdChanged;

    public MonitorWindow()
    {
        Text = "Codex Monitor";
        base.StartPosition = FormStartPosition.CenterScreen;
        base.ClientSize = new Size(860, 570);
        MinimumSize = new Size(720, 500);
        BackColor = Color.FromArgb(246, 248, 252);
        Font = new Font("Microsoft YaHei UI", 9.5f);
        base.AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        base.Icon = TrayIcon.Create(0);
        base.TopMost = Settings.AlwaysOnTop;
        base.FormClosing += delegate (object? _, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
        rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 20, 24, 16),
            ColumnCount = 1,
            RowCount = 5,
            BackColor = BackColor
        };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 130f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        Panel header = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor
        };
        headerPanel = header;
        Label label = new Label
        {
            AutoSize = true,
            Text = "Codex 工作台",
            Font = new Font("Microsoft YaHei UI", 19f, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42),
            Location = new Point(2, 0)
        };
        statusLabel.AutoSize = true;
        statusLabel.Text = "正在读取 Codex 状态…";
        statusLabel.ForeColor = Color.FromArgb(100, 116, 139);
        statusLabel.Location = new Point(5, 40);
        ConfigureHeaderButton(startupButton, 104);
        ConfigureHeaderButton(reminderButton, 108);
        ConfigureHeaderButton(reminderThresholdButton, 82);
        ConfigureHeaderButton(compactButton, 88);
        ConfigureHeaderButton(pinButton, 88);
        compactButton.Text = "▣ 窄窗";
        startupButton.Click += delegate
        {
            bool flag = Startup.SetEnabled(!Startup.IsEnabled);
            SetStartupState(flag);
            this.StartupChanged?.Invoke(flag);
        };
        reminderButton.Click += delegate
        {
            SetReminderMode(!Settings.ReminderMode, persist: true);
        };
        helpTips.SetToolTip(reminderButton, "平时隐藏监控窗口；任务完成或运行中任务数降至设定阈值时，窗口会自动弹出并置前，同时按声音设置播放提示音。你仍可随时点击托盘图标打开窗口。");
        reminderThresholdButton.Click += delegate
        {
            PromptReminderThreshold();
        };
        helpTips.SetToolTip(reminderThresholdButton, "设置运行中任务数提醒阈值。例如设为 5：任务数从 6 降到 5 时提醒一次。启动时已经 ≤5 不会立即提醒。");
        compactButton.Click += delegate
        {
            SetCompactMode(compact: true, persist: true);
        };
        pinButton.Click += delegate
        {
            SetAlwaysOnTop(!base.TopMost, persist: true);
        };
        FlowLayoutPanel headerActions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Anchor = (AnchorStyles.Top | AnchorStyles.Right),
            BackColor = BackColor,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        headerActions.Controls.AddRange(new Control[5] { startupButton, reminderButton, reminderThresholdButton, compactButton, pinButton });
        header.Resize += delegate
        {
            headerActions.Location = new Point(Math.Max(0, header.ClientSize.Width - headerActions.Width - 2), 7);
        };
        header.Controls.AddRange(new Control[3] { label, statusLabel, headerActions });
        UpdatePinButton();
        UpdateStartupButton(Startup.IsEnabled);
        UpdateReminderButton(Settings.ReminderMode);
        TableLayoutPanel tableLayoutPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = BackColor
        };
        tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        tableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        DashboardCard control = CreateCard("工作中项目", projectValue, projectMeta, Color.FromArgb(16, 185, 129), new Padding(0, 0, 9, 0));
        DashboardCard balanceCard = CreateCard("API 余额", balanceValue, balanceMeta, Color.FromArgb(99, 102, 241), new Padding(9, 0, 0, 0));
        compactExpandButton.Text = "展开";
        compactExpandButton.Size = new Size(58, 28);
        compactExpandButton.FlatStyle = FlatStyle.Flat;
        compactExpandButton.FlatAppearance.BorderColor = Color.FromArgb(199, 210, 254);
        compactExpandButton.BackColor = Color.FromArgb(238, 242, 255);
        compactExpandButton.ForeColor = Color.FromArgb(67, 56, 202);
        compactExpandButton.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
        compactExpandButton.Cursor = Cursors.Hand;
        compactExpandButton.Visible = false;
        compactExpandButton.Click += delegate
        {
            SetCompactMode(compact: false, persist: true);
        };
        balanceCard.Resize += delegate
        {
            compactExpandButton.Location = new Point(Math.Max(100, balanceCard.ClientSize.Width - compactExpandButton.Width - 16), 12);
        };
        balanceCard.Controls.Add(compactExpandButton);
        compactExpandButton.BringToFront();
        tableLayoutPanel.Controls.Add(control, 0, 0);
        tableLayoutPanel.Controls.Add(balanceCard, 1, 0);
        projectValue.Text = "0";
        projectMeta.Text = "0 个任务正在运行";
        balanceValue.Text = "读取中…";
        balanceMeta.Text = "正在连接 CCSwitch 余额接口";
        Panel panel = (Panel)(listHeaderPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor
        });
        Label value = new Label
        {
            AutoSize = true,
            Text = "正在工作的任务",
            Font = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 41, 59),
            Location = new Point(2, 17)
        };
        panel.Controls.Add(value);
        ConfigureGrid();
        Panel gridHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(1)
        };
        gridHostPanel = gridHost;
        gridHost.Paint += delegate (object? _, PaintEventArgs e)
        {
            using Pen pen = new Pen(Color.FromArgb(226, 232, 240));
            e.Graphics.DrawRectangle(pen, 0, 0, gridHost.Width - 1, gridHost.Height - 1);
        };
        emptyLabel.Dock = DockStyle.Fill;
        emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
        emptyLabel.Text = "当前没有运行中的 Codex 任务";
        emptyLabel.ForeColor = Color.FromArgb(148, 163, 184);
        emptyLabel.BackColor = Color.White;
        gridHost.Controls.Add(grid);
        gridHost.Controls.Add(emptyLabel);
        Label obj = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };
        DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(34, 1);
        defaultInterpolatedStringHandler.AppendLiteral("提醒模式：任务完成或任务数降至 ≤");
        defaultInterpolatedStringHandler.AppendFormatted(Settings.ReminderTaskThreshold);
        defaultInterpolatedStringHandler.AppendLiteral(" 时提示 · 余额每 30 秒刷新");
        obj.Text = defaultInterpolatedStringHandler.ToStringAndClear();
        obj.ForeColor = Color.FromArgb(148, 163, 184);
        obj.Font = new Font("Microsoft YaHei UI", 8.5f);
        Label control2 = (Label)(footerControl = obj);
        UpdateReminderThresholdButton(Settings.ReminderTaskThreshold);
        rootLayout.Controls.Add(header, 0, 0);
        rootLayout.Controls.Add(tableLayoutPanel, 0, 1);
        rootLayout.Controls.Add(panel, 0, 2);
        rootLayout.Controls.Add(gridHost, 0, 3);
        rootLayout.Controls.Add(control2, 0, 4);
        base.Controls.Add(rootLayout);
        SetCompactMode(Settings.CompactMode, persist: false);
    }

    private static void ConfigureHeaderButton(Button button, int width)
    {
        button.Size = new Size(width, 36);
        button.Margin = new Padding(4, 0, 0, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.Cursor = Cursors.Hand;
        button.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Bold);
        button.BackColor = Color.White;
        button.ForeColor = Color.FromArgb(71, 85, 105);
        button.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
    }

    private DashboardCard CreateCard(string heading, Label value, Label meta, Color accent, Padding margin)
    {
        Label meta2 = meta;
        DashboardCard card = new DashboardCard
        {
            Dock = DockStyle.Fill,
            Margin = margin,
            Padding = new Padding(20, 16, 20, 12)
        };
        Panel panel = new Panel
        {
            BackColor = accent,
            Location = new Point(0, 0),
            Size = new Size(5, 130)
        };
        Label label = new Label
        {
            AutoSize = true,
            Text = heading,
            ForeColor = Color.FromArgb(100, 116, 139),
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold),
            Location = new Point(22, 16)
        };
        value.AutoSize = true;
        value.Font = new Font("Microsoft YaHei UI", 27f, FontStyle.Bold);
        value.ForeColor = Color.FromArgb(15, 23, 42);
        value.Location = new Point(20, 39);
        meta2.AutoSize = false;
        meta2.AutoEllipsis = true;
        meta2.ForeColor = Color.FromArgb(100, 116, 139);
        meta2.Location = new Point(23, 96);
        meta2.Size = new Size(340, 22);
        card.Resize += delegate
        {
            meta2.Width = Math.Max(100, card.ClientSize.Width - 42);
        };
        card.Controls.AddRange(new Control[4] { panel, label, value, meta2 });
        return card;
    }

    private void ConfigureGrid()
    {
        grid.Dock = DockStyle.Fill;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.None;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeight = 38;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(248, 250, 252),
            ForeColor = Color.FromArgb(71, 85, 105),
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold),
            Padding = new Padding(10, 0, 6, 0),
            SelectionBackColor = Color.FromArgb(248, 250, 252),
            SelectionForeColor = Color.FromArgb(71, 85, 105)
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White,
            ForeColor = Color.FromArgb(51, 65, 85),
            SelectionBackColor = Color.FromArgb(238, 242, 255),
            SelectionForeColor = Color.FromArgb(30, 41, 59),
            Font = new Font("Microsoft YaHei UI", 9f),
            Padding = new Padding(10, 0, 6, 0)
        };
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Color.FromArgb(241, 245, 249);
        grid.RowTemplate.Height = 46;
        grid.ScrollBars = ScrollBars.Vertical;
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Task",
            HeaderText = "项目 / 任务",
            FillWeight = 43f,
            MinimumWidth = 150
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Cwd",
            HeaderText = "工作目录",
            FillWeight = 42f,
            MinimumWidth = 150
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Elapsed",
            HeaderText = "已工作",
            FillWeight = 15f,
            MinimumWidth = 78
        });
    }

    public void SetAlwaysOnTop(bool enabled, bool persist)
    {
        base.TopMost = enabled;
        if (persist)
        {
            Settings.AlwaysOnTop = enabled;
        }
        UpdatePinButton();
        this.AlwaysOnTopChanged?.Invoke(enabled);
    }

    public void SetStartupState(bool enabled)
    {
        UpdateStartupButton(enabled);
    }

    private void UpdateStartupButton(bool enabled)
    {
        startupButton.Text = (enabled ? "● 已自启" : "○ 开机自启");
        SetToggleButtonStyle(startupButton, enabled, Color.FromArgb(5, 150, 105), Color.FromArgb(236, 253, 245), Color.FromArgb(110, 231, 183));
    }

    public void SetReminderMode(bool enabled, bool persist)
    {
        if (persist && Settings.ReminderMode == enabled)
        {
            UpdateReminderButton(enabled);
            return;
        }
        if (persist)
        {
            Settings.ReminderMode = enabled;
        }
        UpdateReminderButton(enabled);
        this.ReminderModeChanged?.Invoke(enabled);
    }

    private void UpdateReminderButton(bool enabled)
    {
        reminderButton.Text = (enabled ? "● 提醒中" : "○ 提醒模式");
        SetToggleButtonStyle(reminderButton, enabled, Color.FromArgb(217, 119, 6), Color.FromArgb(255, 251, 235), Color.FromArgb(252, 211, 77));
    }

    public void PromptReminderThreshold()
    {
        ReminderThresholdDialog.Show(this, Settings.ReminderTaskThreshold, delegate (int threshold)
        {
            Settings.ReminderTaskThreshold = threshold;
            UpdateReminderThresholdButton(threshold);
            this.ReminderThresholdChanged?.Invoke(threshold);
        });
    }

    private void UpdateReminderThresholdButton(int threshold)
    {
        Button button = reminderThresholdButton;
        DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(5, 1);
        defaultInterpolatedStringHandler.AppendLiteral("≤ ");
        defaultInterpolatedStringHandler.AppendFormatted(threshold);
        defaultInterpolatedStringHandler.AppendLiteral(" 提醒");
        button.Text = defaultInterpolatedStringHandler.ToStringAndClear();
        if (footerControl != null)
        {
            Control control = footerControl;
            defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(34, 1);
            defaultInterpolatedStringHandler.AppendLiteral("提醒模式：任务完成或任务数降至 ≤");
            defaultInterpolatedStringHandler.AppendFormatted(threshold);
            defaultInterpolatedStringHandler.AppendLiteral(" 时提示 · 余额每 30 秒刷新");
            control.Text = defaultInterpolatedStringHandler.ToStringAndClear();
        }
    }

    public void SetCompactMode(bool compact, bool persist)
    {
        if (compactMode == compact)
        {
            if (persist)
            {
                Settings.CompactMode = compact;
            }
            return;
        }
        SuspendLayout();
        if (compact)
        {
            expandedClientSize = ((base.ClientSize.Width >= 700) ? base.ClientSize : expandedClientSize);
            MinimumSize = Size.Empty;
            headerPanel.Visible = false;
            listHeaderPanel.Visible = false;
            gridHostPanel.Visible = false;
            footerControl.Visible = false;
            rootLayout.Padding = new Padding(12);
            rootLayout.RowStyles[0].Height = 0f;
            rootLayout.RowStyles[1].SizeType = SizeType.Percent;
            rootLayout.RowStyles[1].Height = 100f;
            rootLayout.RowStyles[2].Height = 0f;
            rootLayout.RowStyles[3].SizeType = SizeType.Absolute;
            rootLayout.RowStyles[3].Height = 0f;
            rootLayout.RowStyles[4].Height = 0f;
            base.ClientSize = new Size(560, 154);
            MinimumSize = new Size(500, 190);
            compactExpandButton.Visible = true;
        }
        else
        {
            MinimumSize = Size.Empty;
            headerPanel.Visible = true;
            listHeaderPanel.Visible = true;
            gridHostPanel.Visible = true;
            footerControl.Visible = true;
            compactExpandButton.Visible = false;
            rootLayout.Padding = new Padding(24, 20, 24, 16);
            rootLayout.RowStyles[0].SizeType = SizeType.Absolute;
            rootLayout.RowStyles[0].Height = 64f;
            rootLayout.RowStyles[1].SizeType = SizeType.Absolute;
            rootLayout.RowStyles[1].Height = 130f;
            rootLayout.RowStyles[2].SizeType = SizeType.Absolute;
            rootLayout.RowStyles[2].Height = 48f;
            rootLayout.RowStyles[3].SizeType = SizeType.Percent;
            rootLayout.RowStyles[3].Height = 100f;
            rootLayout.RowStyles[4].SizeType = SizeType.Absolute;
            rootLayout.RowStyles[4].Height = 30f;
            base.ClientSize = expandedClientSize;
            MinimumSize = new Size(720, 500);
        }
        compactMode = compact;
        compactButton.Text = "▣ 窄窗";
        if (persist)
        {
            Settings.CompactMode = compact;
        }
        ResumeLayout(performLayout: true);
        this.CompactModeChanged?.Invoke(compact);
    }

    private static void SetToggleButtonStyle(Button button, bool enabled, Color activeText, Color activeBackground, Color activeBorder)
    {
        button.ForeColor = (enabled ? activeText : Color.FromArgb(71, 85, 105));
        button.BackColor = (enabled ? activeBackground : Color.White);
        button.FlatAppearance.BorderColor = (enabled ? activeBorder : Color.FromArgb(203, 213, 225));
    }

    private void UpdatePinButton()
    {
        pinButton.Text = (base.TopMost ? "● 已置顶" : "○ 置顶");
        pinButton.ForeColor = (base.TopMost ? Color.FromArgb(67, 56, 202) : Color.FromArgb(71, 85, 105));
        pinButton.BackColor = (base.TopMost ? Color.FromArgb(238, 242, 255) : Color.White);
        pinButton.FlatAppearance.BorderColor = (base.TopMost ? Color.FromArgb(165, 180, 252) : Color.FromArgb(203, 213, 225));
    }

    public void UpdateSnapshot(MonitorSnapshot snapshot, int completed)
    {
        projectValue.Text = snapshot.ProjectCount.ToString(CultureInfo.InvariantCulture);
        Label label = projectMeta;
        DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(8, 1);
        defaultInterpolatedStringHandler.AppendFormatted(snapshot.ActiveTasks.Count);
        defaultInterpolatedStringHandler.AppendLiteral(" 个任务正在运行");
        label.Text = defaultInterpolatedStringHandler.ToStringAndClear();
        Label label2 = statusLabel;
        string text;
        if (completed <= 0)
        {
            defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(13, 1);
            defaultInterpolatedStringHandler.AppendLiteral("实时监控中 · 最后更新 ");
            defaultInterpolatedStringHandler.AppendFormatted(snapshot.UpdatedAt, "HH:mm:ss");
            text = defaultInterpolatedStringHandler.ToStringAndClear();
        }
        else
        {
            defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(13, 2);
            defaultInterpolatedStringHandler.AppendLiteral("刚刚有 ");
            defaultInterpolatedStringHandler.AppendFormatted(completed);
            defaultInterpolatedStringHandler.AppendLiteral(" 个任务完成 · ");
            defaultInterpolatedStringHandler.AppendFormatted(snapshot.UpdatedAt, "HH:mm:ss");
            text = defaultInterpolatedStringHandler.ToStringAndClear();
        }
        label2.Text = text;
        statusLabel.ForeColor = ((completed > 0) ? Color.FromArgb(5, 150, 105) : Color.FromArgb(100, 116, 139));
        grid.Rows.Clear();
        foreach (ActiveProject activeTask in snapshot.ActiveTasks)
        {
            TimeSpan elapsed = DateTime.Now - activeTask.StartedAt;
            string text2 = activeTask.Title.Replace("\r", " ").Replace("\n", " ");
            if (text2.Length > 90)
            {
                text2 = text2.Substring(0, 90) + "…";
            }
            grid.Rows.Add(text2, activeTask.Cwd, FormatElapsed(elapsed));
        }
        emptyLabel.Visible = snapshot.ActiveTasks.Count == 0;
        if (!emptyLabel.Visible)
        {
            grid.BringToFront();
        }
        else
        {
            emptyLabel.BringToFront();
        }
    }

    public void UpdateBalance(BalanceSnapshot snapshot)
    {
        decimal? remaining = snapshot.Remaining;
        if (remaining.HasValue)
        {
            decimal valueOrDefault = remaining.GetValueOrDefault();
            Label label = balanceValue;
            string text;
            DefaultInterpolatedStringHandler defaultInterpolatedStringHandler;
            if (!(snapshot.Unit == "USD"))
            {
                defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(1, 2);
                defaultInterpolatedStringHandler.AppendFormatted(valueOrDefault, "N2");
                defaultInterpolatedStringHandler.AppendLiteral(" ");
                defaultInterpolatedStringHandler.AppendFormatted(snapshot.Unit);
                text = defaultInterpolatedStringHandler.ToStringAndClear();
            }
            else
            {
                defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(1, 1);
                defaultInterpolatedStringHandler.AppendLiteral("$");
                defaultInterpolatedStringHandler.AppendFormatted(valueOrDefault, "N2");
                text = defaultInterpolatedStringHandler.ToStringAndClear();
            }
            label.Text = text;
            remaining = snapshot.TodayCost;
            string text2;
            if (remaining.HasValue)
            {
                decimal valueOrDefault2 = remaining.GetValueOrDefault();
                text2 = snapshot.Unit == "USD"
                    ? $" · 今日 ${valueOrDefault2:N2}"
                    : $" · 今日 {valueOrDefault2:N2} {snapshot.Unit}";
            }
            else
            {
                text2 = string.Empty;
            }
            string value = text2;
            Label label2 = balanceMeta;
            defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(6, 3);
            defaultInterpolatedStringHandler.AppendFormatted(snapshot.ProviderName);
            defaultInterpolatedStringHandler.AppendFormatted(value);
            defaultInterpolatedStringHandler.AppendLiteral(" · ");
            defaultInterpolatedStringHandler.AppendFormatted(snapshot.UpdatedAt, "HH:mm");
            defaultInterpolatedStringHandler.AppendLiteral(" 更新");
            label2.Text = defaultInterpolatedStringHandler.ToStringAndClear();
            balanceValue.ForeColor = ((valueOrDefault < 10m) ? Color.FromArgb(220, 38, 38) : Color.FromArgb(15, 23, 42));
        }
        else
        {
            balanceValue.Text = (snapshot.IsConfigured ? "暂不可用" : "未配置");
            balanceValue.ForeColor = (snapshot.IsConfigured ? Color.FromArgb(217, 119, 6) : Color.FromArgb(100, 116, 139));
            balanceMeta.Text = snapshot.Error ?? snapshot.ProviderName;
        }
    }

    public void UpdateError(string error)
    {
        statusLabel.Text = "状态读取失败：" + error;
        statusLabel.ForeColor = Color.FromArgb(220, 38, 38);
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        DefaultInterpolatedStringHandler defaultInterpolatedStringHandler;
        if (elapsed.TotalDays >= 1.0)
        {
            defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(4, 2);
            defaultInterpolatedStringHandler.AppendFormatted((int)elapsed.TotalDays);
            defaultInterpolatedStringHandler.AppendLiteral("天 ");
            defaultInterpolatedStringHandler.AppendFormatted(elapsed.Hours);
            defaultInterpolatedStringHandler.AppendLiteral("小时");
            return defaultInterpolatedStringHandler.ToStringAndClear();
        }
        if (elapsed.TotalHours >= 1.0)
        {
            defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(4, 2);
            defaultInterpolatedStringHandler.AppendFormatted((int)elapsed.TotalHours);
            defaultInterpolatedStringHandler.AppendLiteral("小时 ");
            defaultInterpolatedStringHandler.AppendFormatted(elapsed.Minutes);
            defaultInterpolatedStringHandler.AppendLiteral("分");
            return defaultInterpolatedStringHandler.ToStringAndClear();
        }
        defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(3, 2);
        defaultInterpolatedStringHandler.AppendFormatted(Math.Max(0, elapsed.Minutes));
        defaultInterpolatedStringHandler.AppendLiteral("分 ");
        defaultInterpolatedStringHandler.AppendFormatted(Math.Max(0, elapsed.Seconds));
        defaultInterpolatedStringHandler.AppendLiteral("秒");
        return defaultInterpolatedStringHandler.ToStringAndClear();
    }
}
