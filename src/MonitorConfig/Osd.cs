using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MonitorConfig;

/// <summary>
/// Brightness plate shown for about a second after a hotkey press: near the bottom of the primary
/// screen, topmost, never takes focus and lets clicks through.
/// </summary>
sealed class Osd : Form
{
    static readonly Color Bg = Color.FromArgb(29, 28, 25);
    static readonly Color Ink = Color.FromArgb(236, 232, 224);
    static readonly Color Muted = Color.FromArgb(141, 135, 124);
    static readonly Color Track = Color.FromArgb(52, 50, 46);
    static readonly Color Accent = Color.FromArgb(238, 164, 60);

    readonly System.Windows.Forms.Timer timer = new() { Interval = 30 };
    int value, ticksLeft;
    float s = 1;

    public Osd()
    {
        Text = "MonitorConfigOSD";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        DoubleBuffered = true;
        timer.Tick += (_, _) =>
        {
            if (--ticksLeft > 0) return;
            Opacity -= 0.12;
            if (Opacity <= 0.05) { timer.Stop(); Hide(); }
        };
    }

    public bool IsShowing => Visible && Opacity > 0.05;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80 | 0x20 | 0x8; // NOACTIVATE | TOOLWINDOW | TRANSPARENT | TOPMOST
            return cp;
        }
    }

    public void Pop(int percent)
    {
        value = Math.Clamp(percent, 0, 100);
        var screen = Screen.PrimaryScreen!;
        _ = Handle; // DeviceDpi is only known once the window exists
        s = DeviceDpi / 96f;
        Size = new Size((int)(232 * s), (int)(60 * s));
        using (var path = Rounded(new Rectangle(Point.Empty, Size), (int)(10 * s))) Region = new Region(path);
        var wa = screen.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - (int)(72 * s));
        Opacity = 0.97;
        if (!Visible) Show();
        Invalidate();
        ticksLeft = 1100 / timer.Interval;
        timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        float pad = 16 * s;

        // sun
        float cx = pad + 9 * s, cy = Height / 2f, r = 4 * s;
        using (var pen = new Pen(Accent, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                g.DrawLine(pen, cx + (float)Math.Cos(a) * 6.8f * s, cy + (float)Math.Sin(a) * 6.8f * s,
                                cx + (float)Math.Cos(a) * 8.6f * s, cy + (float)Math.Sin(a) * 8.6f * s);
            }
        }

        // label + value (point sizes already follow the window's DPI)
        float left = pad + 30 * s, right = Width - pad;
        using var small = new Font("Segoe UI", 9f);
        using var big = new Font("Segoe UI Semibold", 13f);
        using (var b = new SolidBrush(Muted)) g.DrawString("Яркость", small, b, left - 2 * s, 10 * s);
        string v = value.ToString();
        var size = g.MeasureString(v, big);
        using (var b = new SolidBrush(Ink)) g.DrawString(v, big, b, right - size.Width + 3 * s, 6 * s);

        // bar
        float by = Height - 18 * s, bh = 4 * s, bw = right - left;
        using (var b = new SolidBrush(Track)) FillPill(g, b, left, by, bw, bh);
        if (value > 0) using (var b = new SolidBrush(Accent)) FillPill(g, b, left, by, Math.Max(bh, bw * value / 100f), bh);
    }

    static void FillPill(Graphics g, Brush b, float x, float y, float w, float h)
    {
        using var p = new GraphicsPath();
        p.AddArc(x, y, h, h, 90, 180);
        p.AddArc(x + w - h, y, h, h, 270, 180);
        p.CloseFigure();
        g.FillPath(b, p);
    }

    static GraphicsPath Rounded(Rectangle rc, int d)
    {
        var p = new GraphicsPath();
        p.AddArc(rc.X, rc.Y, d, d, 180, 90);
        p.AddArc(rc.Right - d, rc.Y, d, d, 270, 90);
        p.AddArc(rc.Right - d, rc.Bottom - d, d, d, 0, 90);
        p.AddArc(rc.X, rc.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
