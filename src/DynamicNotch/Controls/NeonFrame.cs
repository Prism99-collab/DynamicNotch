using System.Windows;
using System.Windows.Media;

namespace DynamicNotch.Controls;

/// <summary>
/// A crisp cut-corner neon edge, drawn over a card: a thin bright line, a faint glow just inside it, and optionally a
/// few slanted stripes on the top edge. It holds no content, so it is drawn once and only again when its size or
/// colour changes; an outer glow (a soft shadow) can sit on it without ever touching what's inside the card.
/// </summary>
public sealed class NeonFrame : FrameworkElement
{
    public NeonFrame() { IsHitTestVisible = false; SnapsToDevicePixels = true; }

    /// <summary>The edge's brush (animate its colour to recolour the frame).</summary>
    public Brush? Stroke { get; set; }
    public double CutTL { get; set; }
    public double CutTR { get; set; }
    public double CutBR { get; set; }
    public double CutBL { get; set; }
    /// <summary>Slanted stripes on the top edge, near the right.</summary>
    public bool Hatch { get; set; }
    public double Thickness { get; set; } = 1.2;
    /// <summary>How strong the glow just inside the edge is (0–1).</summary>
    public double InnerGlow { get; set; } = 0.18;

    /// <summary>The outline of a box w×h with 45° cuts, inset by i (so a pen of width 2i stays inside).</summary>
    public static Geometry Outline(double w, double h, double tl, double tr, double br, double bl, double i)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(i, i + tl), true, true);
            c.LineTo(new Point(i + tl, i), true, false);
            c.LineTo(new Point(w - i - tr, i), true, false);
            c.LineTo(new Point(w - i, i + tr), true, false);
            c.LineTo(new Point(w - i, h - i - br), true, false);
            c.LineTo(new Point(w - i - br, h - i), true, false);
            c.LineTo(new Point(i + bl, h - i), true, false);
            c.LineTo(new Point(i, h - i - bl), true, false);
        }
        g.Freeze();
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 4 || h <= 4 || Stroke == null) return;
        var shape = Outline(w, h, CutTL, CutTR, CutBR, CutBL, 0);
        var line = Outline(w, h, CutTL, CutTR, CutBR, CutBL, Thickness / 2);
        if (InnerGlow > 0)
        {
            dc.PushClip(shape);
            dc.PushOpacity(InnerGlow);
            dc.DrawGeometry(null, new Pen(Stroke, 6) { LineJoin = PenLineJoin.Miter }, line);
            dc.Pop();
            dc.Pop();
        }
        dc.DrawGeometry(null, new Pen(Stroke, Thickness) { LineJoin = PenLineJoin.Miter }, line);
        if (Hatch && w > 70)
        {
            // four slanted stripes hanging from the top edge
            double right = w - CutTR - 10, hy = 3.2;
            var stripes = new StreamGeometry();
            using (var c = stripes.Open())
                for (int k = 0; k < 4; k++)
                {
                    double x = right - k * 6.5;
                    c.BeginFigure(new Point(x - 4, 0), true, true);
                    c.LineTo(new Point(x, 0), false, false);
                    c.LineTo(new Point(x - hy, hy), false, false);
                    c.LineTo(new Point(x - 4 - hy, hy), false, false);
                }
            stripes.Freeze();
            dc.PushOpacity(0.85);
            dc.DrawGeometry(Stroke, null, stripes);
            dc.Pop();
        }
    }
}
