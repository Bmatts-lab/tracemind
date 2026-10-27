using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TraceMind.Core.Geometry;
using CRect = TraceMind.Core.Geometry.Rect;

namespace TraceMind.App.Controls;

/// <summary>Shared pan / zoom / coordinate handling for the schematic and PCB editors.</summary>
public abstract class CanvasBase : Control
{
    protected DesignSession Session { get; private set; } = null!;
    /// <summary>Pixels per millimetre.</summary>
    public double Zoom { get; protected set; } = 8;
    /// <summary>World coordinate at the top-left of the control.</summary>
    public Vec2 Offset { get; protected set; } = new(-10, -10);

    protected Point? panStart; Vec2 panOffsetStart;
    protected Vec2 mouseWorld;
    public event Action<Vec2>? CursorMoved;
    public event Action<string>? StatusMessage;

    protected CanvasBase()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public virtual void Attach(DesignSession session)
    {
        Session = session;
        session.Changed += InvalidateVisual;
        session.SelectionChanged += InvalidateVisual;
    }

    public Vec2 ToWorld(Point p) => new(p.X / Zoom + Offset.X, p.Y / Zoom + Offset.Y);
    public Point ToScreen(Vec2 w) => new((w.X - Offset.X) * Zoom, (w.Y - Offset.Y) * Zoom);
    protected Matrix WorldTransform => Matrix.CreateTranslation(-Offset.X, -Offset.Y) * Matrix.CreateScale(Zoom, Zoom);

    protected void Status(string s) => StatusMessage?.Invoke(s);

    public void ZoomToRect(CRect r, double margin = 0.08)
    {
        if (r.IsEmpty || Bounds.Width < 10 || Bounds.Height < 10) return;
        var w = Math.Max(r.Width, 1); var h = Math.Max(r.Height, 1);
        Zoom = Math.Clamp(Math.Min(Bounds.Width / (w * (1 + 2 * margin)), Bounds.Height / (h * (1 + 2 * margin))), 0.5, 400);
        var c = r.Center;
        Offset = new Vec2(c.X - Bounds.Width / 2 / Zoom, c.Y - Bounds.Height / 2 / Zoom);
        InvalidateVisual();
    }

    public void CenterOn(Vec2 world, double? zoom = null)
    {
        if (zoom.HasValue) Zoom = zoom.Value;
        Offset = new Vec2(world.X - Bounds.Width / 2 / Zoom, world.Y - Bounds.Height / 2 / Zoom);
        InvalidateVisual();
    }

    public abstract void ZoomToFit();

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        var before = ToWorld(p);
        var factor = Math.Pow(1.18, e.Delta.Y);
        Zoom = Math.Clamp(Zoom * factor, 0.5, 400);
        var after = ToWorld(p);
        Offset += before - after;
        InvalidateVisual();
        e.Handled = true;
    }

    protected bool HandlePanPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
        {
            panStart = e.GetPosition(this); panOffsetStart = Offset;
            e.Pointer.Capture(this);
            e.Handled = true;
            return true;
        }
        return false;
    }

    /// <summary>Right-drag pans too; a right click without movement is reported as a click.</summary>
    protected Point? rightDown;
    protected bool rightDragged;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        mouseWorld = ToWorld(p);
        CursorMoved?.Invoke(mouseWorld);
        if (panStart is { } ps)
        {
            Offset = panOffsetStart - new Vec2((p.X - ps.X) / Zoom, (p.Y - ps.Y) / Zoom);
            InvalidateVisual();
            return;
        }
        if (rightDown is { } rd && e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if (!rightDragged && (Math.Abs(p.X - rd.X) + Math.Abs(p.Y - rd.Y)) > 4) { rightDragged = true; panStart = rd; panOffsetStart = Offset; }
            if (rightDragged && panStart is { } ps2) { Offset = panOffsetStart - new Vec2((p.X - ps2.X) / Zoom, (p.Y - ps2.Y) / Zoom); InvalidateVisual(); }
            return;
        }
        OnWorldPointerMoved(e, mouseWorld);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (HandlePanPressed(e)) return;
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed) { rightDown = e.GetPosition(this); rightDragged = false; e.Pointer.Capture(this); return; }
        OnWorldPointerPressed(e, ToWorld(e.GetPosition(this)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (rightDown != null && e.InitialPressMouseButton == MouseButton.Right)
        {
            bool wasDrag = rightDragged;
            rightDown = null; rightDragged = false; panStart = null;
            e.Pointer.Capture(null);
            if (!wasDrag) OnRightClick(ToWorld(e.GetPosition(this)));
            return;
        }
        if (panStart != null) { panStart = null; e.Pointer.Capture(null); return; }
        OnWorldPointerReleased(e, ToWorld(e.GetPosition(this)));
    }

    protected virtual void OnWorldPointerMoved(PointerEventArgs e, Vec2 w) { }
    protected virtual void OnWorldPointerPressed(PointerPressedEventArgs e, Vec2 w) { }
    protected virtual void OnWorldPointerReleased(PointerReleasedEventArgs e, Vec2 w) { }
    protected virtual void OnRightClick(Vec2 w) { }

    // ---------- drawing helpers ----------

    protected void DrawGrid(DrawingContext ctx, double grid, IBrush dot)
    {
        double spacing = grid;
        while (spacing * Zoom < 9) spacing *= 5;
        var tl = ToWorld(new Point(0, 0)); var br = ToWorld(new Point(Bounds.Width, Bounds.Height));
        double x0 = Math.Floor(tl.X / spacing) * spacing, y0 = Math.Floor(tl.Y / spacing) * spacing;
        double r = Math.Max(0.6, Math.Min(1.3, Zoom * 0.08));
        int count = 0;
        for (double x = x0; x <= br.X; x += spacing)
            for (double y = y0; y <= br.Y; y += spacing)
            {
                if (++count > 40000) return;
                var sp = ToScreen(new Vec2(x, y));
                ctx.FillRectangle(dot, new Avalonia.Rect(sp.X - r / 2, sp.Y - r / 2, r, r));
            }
    }

    protected static Typeface Mono = new("Cascadia Mono, Consolas, Menlo, monospace");
    protected static Typeface Sans = new("Inter, Segoe UI, sans-serif");

    /// <summary>Draw text in world units (size in mm) at a world position; align: 0 left, 1 centre, 2 right; vertical centre.</summary>
    protected void DrawText(DrawingContext ctx, string text, Vec2 pos, double sizeMm, IBrush brush, int align = 0, bool vertical = false, Typeface? face = null, double minPx = 5)
    {
        if (string.IsNullOrEmpty(text)) return;
        double px = sizeMm * Zoom;
        if (px < minPx) return;
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face ?? Sans, px, brush);
        var sp = ToScreen(pos);
        double dx = align switch { 1 => -ft.Width / 2, 2 => -ft.Width, _ => 0 };
        if (vertical)
        {
            using (ctx.PushTransform(Matrix.CreateTranslation(-sp.X, -sp.Y) * Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(sp.X, sp.Y)))
                ctx.DrawText(ft, new Point(sp.X + dx, sp.Y - ft.Height / 2));
        }
        else ctx.DrawText(ft, new Point(sp.X + dx, sp.Y - ft.Height / 2));
    }

    protected Avalonia.Rect VisibleWorld()
    {
        var tl = ToWorld(new Point(0, 0)); var br = ToWorld(new Point(Bounds.Width, Bounds.Height));
        return new Avalonia.Rect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
    }

    protected static Point P(Vec2 v) => new(v.X, v.Y);
}
