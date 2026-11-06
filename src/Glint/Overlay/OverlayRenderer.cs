using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Glint.GameOverlay;

public sealed record OverlayStyle(double Scale, double Background, Color? Accent, bool Compact);

/// <summary>
/// Draws the overlay with GDI+ into a premultiplied 32-bit bitmap (the layered window's DIB, or an off-screen
/// bitmap in tests). Glint's dark panel + accent gradient (#ff6a3d → #d14bff → #6c7bff). Widths are measured with
/// every digit as '0' so the panel doesn't jitter as numbers change. Not thread-safe: call from one thread.
/// </summary>
public static class OverlayRenderer
{
    static readonly Color[] Grad = { Color.FromArgb(0xff, 0x6a, 0x3d), Color.FromArgb(0xd1, 0x4b, 0xff), Color.FromArgb(0x6c, 0x7b, 0xff) };
    static readonly Color Panel = Color.FromArgb(0x0b, 0x0c, 0x11), Text = Color.FromArgb(0xf2, 0xf3, 0xf7), Dim = Color.FromArgb(0x93, 0x99, 0xb0);
    static readonly StringFormat Fmt = MakeFormat();
    static readonly Bitmap MeasureBmp = new(1, 1, PixelFormat.Format32bppPArgb);
    static readonly Graphics MeasureG = Graphics.FromImage(MeasureBmp);

    static StringFormat MakeFormat()
    {
        var f = (StringFormat)StringFormat.GenericTypographic.Clone();
        f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
        return f;
    }

    sealed class Fonts : IDisposable
    {
        public readonly double S;
        public readonly Font Label, Value, Unit, Hero;
        /// <summary>Compact (one-line) layout: same proportions as the dashboard preview (13 px base: label .82em, value 1.25em, unit .78em).</summary>
        public readonly Font CLabel, CValue, CUnit;
        public Fonts(double s)
        {
            S = s;
            Label = new Font("Segoe UI Semibold", (float)(10.5 * s), GraphicsUnit.Pixel);
            Value = new Font("Segoe UI Semibold", (float)(15 * s), GraphicsUnit.Pixel);
            Unit = new Font("Segoe UI Semibold", (float)(10.5 * s), GraphicsUnit.Pixel);
            Hero = new Font("Segoe UI", (float)(27 * s), FontStyle.Bold, GraphicsUnit.Pixel);
            CLabel = new Font("Segoe UI Semibold", (float)(10.7 * s), GraphicsUnit.Pixel);
            CValue = new Font("Segoe UI Semibold", (float)(16.25 * s), GraphicsUnit.Pixel);
            CUnit = new Font("Segoe UI Semibold", (float)(10.1 * s), GraphicsUnit.Pixel);
        }
        public void Dispose() { Label.Dispose(); Value.Dispose(); Unit.Dispose(); Hero.Dispose(); CLabel.Dispose(); CValue.Dispose(); CUnit.Dispose(); }
    }
    static Fonts? _fonts;
    /// <summary>Drop cached fonts (overlay switched off).</summary>
    public static void ReleaseCache() { _last = null; _fonts?.Dispose(); _fonts = null; }
    static Fonts F(double s) { if (_fonts?.S != s) { _fonts?.Dispose(); _fonts = new Fonts(s); } return _fonts; }

    static float W(Font f, string t) => t.Length == 0 ? 0 : MeasureG.MeasureString(t, f, PointF.Empty, Fmt).Width;
    static string Zeros(string t) => new(t.Select(c => char.IsDigit(c) ? '0' : c).ToArray());
    /// <summary>Distance from the top of a drawn string to its baseline.</summary>
    static float Ascent(Font f) => f.Size * f.FontFamily.GetCellAscent(f.Style) / f.FontFamily.GetEmHeight(f.Style);
    static float Descent(Font f) => f.Size * f.FontFamily.GetCellDescent(f.Style) / f.FontFamily.GetEmHeight(f.Style);

    readonly record struct Piece(string Text, Font Font, float X, float Baseline, Brush? Brush, Color Color, RectangleF Box);

    /// <summary>Lay out every string (positions in pixels) and the panel size.</summary>
    static (IReadOnlyList<OverlayRow> rows, OverlayStyle st, (List<Piece>, SizeF, float, float) result)? _last;

    /// <summary>Measure + Draw of the same frame lay it out once.</summary>
    static (List<Piece> pieces, SizeF size, float barTop, float barBottom) Layout(IReadOnlyList<OverlayRow> rows, OverlayStyle st)
    {
        if (_last is { } l && ReferenceEquals(l.rows, rows) && l.st == st) return l.result;
        var r = DoLayout(rows, st);
        _last = (rows, st, r);
        return r;
    }

    static (List<Piece> pieces, SizeF size, float barTop, float barBottom) DoLayout(IReadOnlyList<OverlayRow> rows, OverlayStyle st)
    {
        var f = F(st.Scale);
        float u = (float)st.Scale;
        var pieces = new List<Piece>();
        float left = 21 * u, padR = 13 * u, padY = 9 * u;
        if (st.Compact)
        {
            // One line, like the dashboard preview: 10 px padding, a full-height accent bar with a 10 px gap, label/value/unit
            // 6 px apart, items 16 px apart, all on one baseline. Height comes from the font's real ascent + descent.
            float pad = 10 * u, x = 22 * u, asc = Ascent(f.CValue), h = pad * 2 + asc + Descent(f.CValue), baseLine = pad + asc;
            foreach (var r in rows)
            {
                if (x > 22 * u) x += 16 * u;
                var label = r.Label.ToUpperInvariant();
                pieces.Add(new Piece(label, f.CLabel, x, baseLine, null, Dim, default));
                x += W(f.CLabel, label) + 6 * u;
                float vw = Math.Max(W(f.CValue, r.Value), W(f.CValue, Zeros(r.Value)));
                pieces.Add(new Piece(r.Value, f.CValue, x, baseLine, null, ValueColor(r, st), new RectangleF(x, baseLine - asc, vw, f.CValue.Size)));
                x += vw;
                if (r.Unit.Length > 0) { x += 6 * u; pieces.Add(new Piece(r.Unit, f.CUnit, x, baseLine, null, Dim, default)); x += W(f.CUnit, r.Unit); }
            }
            return (pieces, new SizeF(x + 14 * u, h), pad, h - pad);
        }
        // detailed: an optional hero FPS row, then label ........ value unit rows
        float y = padY, labelW = 0, valueW = 0, unitW = 0, heroW = 0;
        foreach (var r in rows)
        {
            if (r.Hero) { heroW = Math.Max(heroW, Math.Max(W(f.Hero, r.Value), W(f.Hero, Zeros(r.Value))) + 6 * u + W(f.Label, r.Label.ToUpperInvariant())); continue; }
            labelW = Math.Max(labelW, W(f.Label, r.Label.ToUpperInvariant()));
            valueW = Math.Max(valueW, Math.Max(W(f.Value, r.Value), W(f.Value, Zeros(r.Value))));
            unitW = Math.Max(unitW, W(f.Unit, r.Unit));
        }
        if (unitW > 0) unitW += 3 * u;
        float rowsW = labelW + 20 * u + valueW + unitW;
        float width = left + Math.Max(rowsW, heroW) + padR;
        float valueRight = width - padR - unitW;
        foreach (var r in rows)
        {
            if (r.Hero)
            {
                float bl = y + Ascent(f.Hero) - 2 * u;
                float vw = Math.Max(W(f.Hero, r.Value), W(f.Hero, Zeros(r.Value)));
                pieces.Add(new Piece(r.Value, f.Hero, left, bl, null, ValueColor(r, st), new RectangleF(left, bl - Ascent(f.Hero), vw, f.Hero.Size)));
                pieces.Add(new Piece(r.Label.ToUpperInvariant(), f.Label, left + vw + 6 * u, bl, null, Dim, default));
                y += 33 * u;
                continue;
            }
            float b = y + Ascent(f.Value);
            pieces.Add(new Piece(r.Label.ToUpperInvariant(), f.Label, left, b, null, Dim, default));
            float w = W(f.Value, r.Value);
            pieces.Add(new Piece(r.Value, f.Value, valueRight - w, b, null, ValueColor(r, st), new RectangleF(valueRight - w, b - Ascent(f.Value), w, f.Value.Size)));
            if (r.Unit.Length > 0) pieces.Add(new Piece(r.Unit, f.Unit, valueRight + 3 * u, b, null, Dim, default));
            y += 21 * u;
        }
        float height = y + padY - 2 * u;
        return (pieces, new SizeF(width, height), 10 * u, height - 10 * u);
    }

    /// <summary>Hero values (FPS) get the accent gradient; others white unless the item has a colour.</summary>
    static Color ValueColor(OverlayRow r, OverlayStyle st) =>
        r.Color is { } c && Core.Limits.Color.IsMatch(c) ? ColorTranslator.FromHtml(c) : r.Hero ? Color.Empty : Text;

    public static Size Measure(IReadOnlyList<OverlayRow> rows, OverlayStyle st)
    {
        if (rows.Count == 0) return Size.Empty;
        var s = Layout(rows, st).size;
        return new Size((int)Math.Ceiling(s.Width), (int)Math.Ceiling(s.Height));
    }

    public static void Draw(Graphics g, IReadOnlyList<OverlayRow> rows, OverlayStyle st)
    {
        if (rows.Count == 0) return;
        var (pieces, size, barTop, barBottom) = Layout(rows, st);
        float u = (float)st.Scale;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;   // ClearType can't blend onto alpha
        var rect = new RectangleF(0.5f, 0.5f, size.Width - 1, size.Height - 1);
        if (st.Background > 0.01)
        {
            using var path = Round(rect, 10 * u);
            using var fill = new SolidBrush(Color.FromArgb((int)(Math.Clamp(st.Background, 0, 1) * 255), Panel));
            g.FillPath(fill, path);
            using var border = new Pen(Color.FromArgb((int)(28 * Math.Min(1, st.Background / 0.5)), 255, 255, 255), Math.Max(1, u));
            g.DrawPath(border, path);
        }
        // accent bar
        var bar = new RectangleF(9 * u, barTop, 3 * u, Math.Max(4 * u, barBottom - barTop));
        using (var bp = Round(bar, 1.5f * u))
        using (var bb = AccentBrush(bar, st, 90))
            g.FillPath(bb, bp);
        bool shadow = st.Background < 0.4;
        using var sb = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        foreach (var p in pieces)
        {
            float top = p.Baseline - Ascent(p.Font);
            if (shadow) g.DrawString(p.Text, p.Font, sb, p.X + u, top + u, Fmt);
            if (p.Color.IsEmpty)
            {
                using var gb = AccentBrush(p.Box, st, 0);
                g.DrawString(p.Text, p.Font, gb, p.X, top, Fmt);
            }
            else
            {
                using var b = new SolidBrush(p.Color);
                g.DrawString(p.Text, p.Font, b, p.X, top, Fmt);
            }
        }
    }

    /// <summary>Off-screen render (tests, README screenshot).</summary>
    public static Bitmap Render(IReadOnlyList<OverlayRow> rows, OverlayStyle st)
    {
        var sz = Measure(rows, st);
        var bmp = new Bitmap(Math.Max(1, sz.Width), Math.Max(1, sz.Height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        Draw(g, rows, st);
        return bmp;
    }

    static Brush AccentBrush(RectangleF r, OverlayStyle st, float angle)
    {
        if (st.Accent is { } a) return new SolidBrush(a);
        var rr = new RectangleF(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height));
        var b = new LinearGradientBrush(rr, Grad[0], Grad[2], angle) { WrapMode = WrapMode.TileFlipXY };
        b.InterpolationColors = new ColorBlend { Colors = Grad, Positions = new[] { 0f, 0.55f, 1f } };
        return b;
    }

    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
