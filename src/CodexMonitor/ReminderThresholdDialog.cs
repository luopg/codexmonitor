using System;
using System.Drawing;
using System.Windows.Forms;

namespace CodexMonitor;

internal sealed class ReminderThresholdDialog : Form
{
    private static ReminderThresholdDialog? activeDialog;

    private readonly NumericUpDown thresholdInput = new NumericUpDown();

    private ReminderThresholdDialog(Form owner, int current, Action<int> saved)
    {
        Action<int> saved2 = saved;
        ReminderThresholdDialog reminderThresholdDialog = this;
        Text = "设置任务数提醒阈值";
        base.ClientSize = new Size(390, 185);
        base.FormBorderStyle = FormBorderStyle.FixedDialog;
        base.MaximizeBox = false;
        base.MinimizeBox = false;
        base.StartPosition = FormStartPosition.CenterParent;
        base.ShowInTaskbar = false;
        base.TopMost = owner.TopMost;
        BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        Label label = new Label
        {
            AutoSize = true,
            Text = "运行中任务数 ≤ 多少时提醒？",
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 41, 59),
            Location = new Point(24, 20)
        };
        Label label2 = new Label
        {
            AutoSize = false,
            Text = "例如设置为 5：任务数从 6 降到 5 时提醒一次。\n启动时已经处于阈值内不会立即提醒。",
            ForeColor = Color.FromArgb(100, 116, 139),
            Location = new Point(24, 51),
            Size = new Size(340, 48)
        };
        thresholdInput.Minimum = 0m;
        thresholdInput.Maximum = 99m;
        thresholdInput.Value = Math.Clamp(current, 0, 99);
        thresholdInput.Location = new Point(24, 108);
        thresholdInput.Size = new Size(90, 30);
        thresholdInput.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
        Button button = new Button
        {
            Text = "取消",
            Location = new Point(205, 108),
            Size = new Size(72, 32)
        };
        Button button2 = new Button
        {
            Text = "保存",
            Location = new Point(288, 108),
            Size = new Size(72, 32)
        };
        button2.BackColor = Color.FromArgb(79, 70, 229);
        button2.ForeColor = Color.White;
        button2.FlatStyle = FlatStyle.Flat;
        button2.FlatAppearance.BorderSize = 0;
        button2.Click += delegate
        {
            saved2((int)reminderThresholdDialog.thresholdInput.Value);
            reminderThresholdDialog.Close();
        };
        button.Click += delegate
        {
            reminderThresholdDialog.Close();
        };
        base.AcceptButton = button2;
        base.CancelButton = button;
        base.FormClosed += delegate
        {
            activeDialog = null;
        };
        base.Shown += delegate
        {
            reminderThresholdDialog.BringToFront();
            reminderThresholdDialog.Activate();
            reminderThresholdDialog.thresholdInput.Focus();
        };
        base.Controls.AddRange(new Control[5] { label, label2, thresholdInput, button, button2 });
    }

    public static void Show(Form owner, int current, Action<int> saved)
    {
        if (activeDialog != null && !activeDialog!.IsDisposed)
        {
            activeDialog!.TopMost = owner.TopMost;
            activeDialog!.BringToFront();
            activeDialog!.Activate();
        }
        else
        {
            activeDialog = new ReminderThresholdDialog(owner, current, saved);
            activeDialog!.Show(owner);
        }
    }
}
