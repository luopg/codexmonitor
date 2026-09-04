using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CodexMonitor;

internal static class TrayIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(int count)
    {
        using Bitmap bitmap = new Bitmap(32, 32);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using SolidBrush brush = new SolidBrush((count > 0) ? Color.FromArgb(24, 154, 92) : Color.FromArgb(82, 91, 105));
            graphics.FillEllipse(brush, 1, 1, 30, 30);
            using Pen pen = new Pen(Color.FromArgb(235, 245, 255), 2f);
            graphics.DrawEllipse(pen, 2, 2, 28, 28);
            string text = ((count > 99) ? "99+" : count.ToString(CultureInfo.InvariantCulture));
            using Font font = new Font("Segoe UI", (count > 9) ? 10 : 13, FontStyle.Bold, GraphicsUnit.Point);
            using SolidBrush brush2 = new SolidBrush(Color.White);
            SizeF sizeF = graphics.MeasureString(text, font);
            graphics.DrawString(text, font, brush2, (32f - sizeF.Width) / 2f, (32f - sizeF.Height) / 2f - 1f);
        }
        IntPtr hicon = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hicon).Clone();
        }
        finally
        {
            DestroyIcon(hicon);
        }
    }
}
