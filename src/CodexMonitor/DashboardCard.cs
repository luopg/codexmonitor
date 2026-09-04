using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CodexMonitor;

internal sealed class DashboardCard : Panel
{
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.FromArgb(226, 232, 240);


    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = 16;


    public DashboardCard()
    {
        BackColor = Color.White;
        DoubleBuffered = true;
        base.Resize += delegate
        {
            UpdateRegion();
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = RoundedPath(new Rectangle(0, 0, base.Width - 1, base.Height - 1), Radius);
        using Pen pen = new Pen(BorderColor);
        e.Graphics.DrawPath(pen, path);
    }

    private void UpdateRegion()
    {
        if (base.Width < 2 || base.Height < 2)
        {
            return;
        }
        using GraphicsPath path = RoundedPath(new Rectangle(0, 0, base.Width, base.Height), Radius);
        Region? previousRegion = base.Region;
        base.Region = new Region(path);
        previousRegion?.Dispose();
    }

    private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        int num = radius * 2;
        GraphicsPath graphicsPath = new GraphicsPath();
        graphicsPath.AddArc(bounds.Left, bounds.Top, num, num, 180f, 90f);
        graphicsPath.AddArc(bounds.Right - num, bounds.Top, num, num, 270f, 90f);
        graphicsPath.AddArc(bounds.Right - num, bounds.Bottom - num, num, num, 0f, 90f);
        graphicsPath.AddArc(bounds.Left, bounds.Bottom - num, num, num, 90f, 90f);
        graphicsPath.CloseFigure();
        return graphicsPath;
    }
}
