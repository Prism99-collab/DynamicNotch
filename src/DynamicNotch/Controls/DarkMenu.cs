using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DynamicNotch.Controls;

/// <summary>Dark, rounded tray menu that matches the notch instead of the default white Windows menu.</summary>
public static class DarkMenu
{
    static readonly Color Back = Color.FromArgb(28, 28, 31);
    static readonly Color Hover = Color.FromArgb(48, 48, 54);
    static readonly Color Line = Color.FromArgb(58, 58, 64);
    static readonly Color Text = Color.FromArgb(240, 240, 245);
    static readonly Color Dim = Color.FromArgb(140, 140, 150);
    static readonly Color Check = Color.FromArgb(48, 209, 88);
    static readonly Color Danger = Color.FromArgb(255, 105, 97);

    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new Renderer();
        menu.BackColor = Back;
        menu.ForeColor = Text;
        menu.Font = new Font("Segoe UI Variable Text", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        menu.Padding = new Padding(4, 6, 4, 6);
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = true;
        menu.DropShadowEnabled = false; // Windows 11 draws its own soft shadow for rounded menus
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripSeparator) continue;
            item.Padding = new Padding(2, 5, 8, 5);
            item.ForeColor = item.Tag as string == "danger" ? Danger : item.Enabled ? Text : Dim;
        }
        menu.Opened += (_, _) => RoundCorners(menu.Handle);
    }

    /// <summary>Non-clickable title row at the top of the menu.</summary>
    public static ToolStripLabel Header(string text) => new(text)
    {
        Font = new Font("Segoe UI Variable Display", 10.5f, FontStyle.Bold, GraphicsUnit.Point),
        ForeColor = Text,
        Padding = new Padding(8, 6, 8, 4),
    };

    static void RoundCorners(IntPtr hwnd)
    {
        try
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            int border = ColorTranslator.ToWin32(Line);
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int)); // DWMWA_BORDER_COLOR
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    sealed class Renderer : ToolStripProfessionalRenderer
    {
        public Renderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Back);

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { } // DWM draws the rounded border

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using var path = RoundRect(r, 6);
            using var b = new SolidBrush(Hover);
            g.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Tag as string == "danger" ? Danger : e.Item.Enabled ? Text : Dim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = Math.Min(r.Width, r.Height) * 0.42f;
            using var pen = new Pen(Check, Math.Max(1.6f, s * 0.3f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[] { new PointF(cx - s * 0.8f, cy), new PointF(cx - s * 0.2f, cy + s * 0.6f), new PointF(cx + s * 0.9f, cy - s * 0.6f) });
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(Line);
            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Back;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Hover;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Back;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
    }
}
