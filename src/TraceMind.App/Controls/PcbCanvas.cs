using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using TraceMind.Core.Geometry;
using TraceMind.Core.Model;
using TraceMind.Core.Pcb;
using CRect = TraceMind.Core.Geometry.Rect;

namespace TraceMind.App.Controls;

public enum PcbTool { Select, Route, Via }

public sealed class PcbCanvas : CanvasBase
{
    public PcbTool Tool { get; private set; } = PcbTool.Select;
    public Layer ActiveLayer { get; private set; } = Layer.FCu;
    public event Action<PcbTool>? ToolChanged;
    public event Action<Layer>? LayerChanged;
    public event Action<Component>? ComponentDoubleClicked;
    public double Grid { get; set; } = 0.25;
    public bool ShowRatsnest { get; set; } = true;
    public bool ShowZones { get; set; } = true;
    public bool ShowDrc { get; set; } = true;
    public bool[] LayerVisible { get; } = [true, true, true, true];

    // selection of copper
    readonly HashSet<Guid> selTracks = [], selVias = [], selHoles = [];
    Dictionary<Guid, Vec2>? holeDragOrig;
    /// <summary>Draw mm rulers along the top and left edges (used for images sent to the AI).</summary>
    public bool ShowRulers { get; set; }
    // dragging components
    Vec2? dragStart; Dictionary<Guid, Vec2>? dragOrig; bool dragMoved;
    Vec2? bandStart;
    // routing
    Vec2? routeStart; string routeNet = ""; bool diagonalFirst;
    DateTime lastClick; Vec2 lastClickPos;

    readonly Dictionary<Guid, Geometry> zoneCache = [];

    public override void Attach(DesignSession session)
    {
        base.Attach(session);
        session.Changed += () => zoneCache.Clear();
    }

    public void SetTool(PcbTool t)
    {
        Tool = t; routeStart = null;
        ToolChanged?.Invoke(t);
        Cursor = t == PcbTool.Select ? Cursor.Default : new Cursor(StandardCursorType.Cross);
        Status(t switch
        {
            PcbTool.Route => "Route: click a pad to start, click to add corners, V = via / switch layer, / = corner style, Esc = finish.",
            PcbTool.Via => "Click to place a via.",
            _ => "Select: drag to move, R rotate, F flip side, L lock, Del delete.",
        });
        InvalidateVisual();
    }

    public void SetLayer(Layer l)
    {
        ActiveLayer = l;
        LayerChanged?.Invoke(l);
        InvalidateVisual();
    }

    public override void ZoomToFit()
    {
        var r = Session.Project.Board.OutlineBounds();
        foreach (var c in Session.Project.Components.Where(c => c.Placed)) r = r.Union(Session.Project.Courtyard(c));
        if (r.IsEmpty) r = new CRect(0, 0, 60, 40);
        ZoomToRect(r.Inflate(3));
    }

    public void ZoomToRect(CRect r) => base.ZoomToRect(r);

    // ======================= rendering =======================

    static IBrush Alpha(Color c, double a) => new ImmutableSolidColorBrush(c, a);

    public override void Render(DrawingContext ctx)
    {
        var p = Session.Project; var board = p.Board;
        ctx.FillRectangle(Palette.PcbBackground, new Avalonia.Rect(Bounds.Size));
        var hi = Session.HighlightNet;
        var nl = Session.Netlist;
        var pads = p.AllPads().ToList();
        string NetOf(PadInstance pd) => nl.NetOfPad(pd.Component, pd.Pad.Number);

        using (ctx.PushTransform(WorldTransform))
        {
            // board
            if (board.Outline.Count >= 3) ctx.DrawGeometry(Palette.BoardFill, null, Poly(board.Outline));
        }
        DrawGrid(ctx, Grid >= 0.5 ? Grid : 1.0, Palette.PcbGridDot);

        using (ctx.PushTransform(WorldTransform))
        {
            // layers: draw inactive copper layers first, active last
            var copperLayers = board.CopperLayerList().ToList();
            var order = copperLayers.Where(l => l != ActiveLayer).Reverse().Append(ActiveLayer).Where(copperLayers.Contains).ToList();
            foreach (var layer in order)
            {
                int li = Array.IndexOf(Layers.CopperOrder, layer);
                if (li >= 0 && !LayerVisible[li]) continue;
                bool active = layer == ActiveLayer;
                var col = Palette.CopperColor(layer);
                double alpha = active ? 0.92 : 0.5;
                if (ShowZones)
                    foreach (var z in board.Zones.Where(z => z.Layer == layer && z.Outline.Count >= 3))
                        ctx.DrawGeometry(Alpha(col, active ? 0.32 : 0.18), new Pen(Alpha(col, 0.5), 0.05), ZoneGeometry(z, pads, NetOf));
                foreach (var t in board.Tracks.Where(t => t.Layer == layer))
                {
                    bool h = hi != null && t.Net == hi;
                    bool s = selTracks.Contains(t.Id);
                    var brush = h ? Alpha(Colors.White, 0.95) : Alpha(col, alpha);
                    ctx.DrawLine(new Pen(brush, t.Width, lineCap: PenLineCap.Round), P(t.A), P(t.B));
                    if (s) ctx.DrawLine(new Pen(Palette.Selection, Math.Max(0.05, 1.5 / Zoom), new ImmutableDashStyle([2, 2], 0), PenLineCap.Round), P(t.A), P(t.B));
                }
                // smd pads of this layer
                foreach (var pd in pads.Where(pd => !pd.Pad.IsThrough && pd.OnLayer(layer)))
                {
                    bool h = hi != null && NetOf(pd) == hi;
                    var b = h ? Alpha(Colors.White, 0.95) : (layer == Layer.FCu ? Palette.PadTop : Palette.PadBottom);
                    using (ctx.PushOpacity(active || h ? 1 : 0.55)) DrawShape(ctx, pd.Shape, b);
                }
            }
            // through-hole pads, holes, vias
            foreach (var pd in pads.Where(pd => pd.Pad.IsThrough))
            {
                bool h = hi != null && NetOf(pd) == hi;
                if (pd.Pad.Plated) DrawShape(ctx, pd.Shape, h ? Brushes.White : Palette.PadTht);
                ctx.DrawEllipse(pd.Pad.Plated ? Palette.Hole : Palette.HoleNpth, null, P(pd.Position), pd.Pad.Drill / 2, pd.Pad.Drill / 2);
            }
            foreach (var v in board.Vias)
            {
                bool h = hi != null && v.Net == hi;
                ctx.DrawEllipse(h ? Brushes.White : Palette.ViaRing, selVias.Contains(v.Id) ? new Pen(Palette.Selection, 0.08) : null, P(v.Position), v.Diameter / 2, v.Diameter / 2);
                ctx.DrawEllipse(Palette.Hole, null, P(v.Position), v.Drill / 2, v.Drill / 2);
            }
            foreach (var hole in board.Holes)
            {
                bool hs = selHoles.Contains(hole.Id);
                ctx.DrawEllipse(Palette.HoleNpth, new Pen(hs ? Palette.Selection : Palette.SilkTop, hs ? Math.Max(0.15, 2 / Zoom) : 0.1), P(hole.Position), hole.Diameter / 2, hole.Diameter / 2);
            }

            // silkscreen
            double silkW = Math.Max(board.Rules.SilkWidth, 0.6 / Zoom);
            foreach (var c in p.Components.Where(c => c.Placed && !c.DoNotPopulate))
            {
                var fp = p.Footprint(c);
                if (fp == null) continue;
                var brush = c.OnBottom ? Palette.SilkBottom : Palette.SilkTop;
                using (ctx.PushOpacity(c.OnBottom == (ActiveLayer == Layer.BCu) ? 0.95 : 0.5))
                    foreach (var g in fp.Silk) DrawGraphic(ctx, g, c, new Pen(brush, silkW, lineCap: PenLineCap.Round));
                bool sel = Session.SelectedComponents.Contains(c.Id);
                if (sel || c.Locked)
                {
                    var cy = p.Courtyard(c);
                    ctx.DrawRectangle(sel ? Palette.SelectionFill : null, new Pen(sel ? Palette.Selection : Palette.Courtyard, Math.Max(0.03, 1.2 / Zoom), c.Locked && !sel ? new ImmutableDashStyle([3, 3], 0) : null),
                        new Avalonia.Rect(cy.MinX, cy.MinY, cy.Width, cy.Height));
                }
            }

            // board edge
            if (board.Outline.Count >= 3) ctx.DrawGeometry(null, new Pen(Palette.BoardEdge, Math.Max(0.1, 1.5 / Zoom)), Poly(board.Outline));

            // ratsnest
            if (ShowRatsnest)
            {
                var rp = new Pen(Palette.Ratsnest, Math.Max(0.02, 1 / Zoom));
                var rpHi = new Pen(Brushes.White, Math.Max(0.04, 1.6 / Zoom));
                foreach (var aw in Session.Connectivity.Airwires) ctx.DrawLine(hi == aw.Net ? rpHi : rp, P(aw.A), P(aw.B));
            }

            // DRC markers
            if (ShowDrc && Session.LastDrc != null)
            {
                var pen = new Pen(Palette.DrcMarker, Math.Max(0.05, 1.6 / Zoom));
                foreach (var v in Session.LastDrc.Violations.Where(v => v.Severity == Severity.Error && v.Location != Vec2.Zero && v.Code != "unrouted"))
                {
                    double r = Math.Max(0.4, 7 / Zoom);
                    ctx.DrawEllipse(null, pen, P(v.Location), r, r);
                    ctx.DrawLine(pen, P(v.Location + new Vec2(-r * 0.6, -r * 0.6)), P(v.Location + new Vec2(r * 0.6, r * 0.6)));
                    ctx.DrawLine(pen, P(v.Location + new Vec2(-r * 0.6, r * 0.6)), P(v.Location + new Vec2(r * 0.6, -r * 0.6)));
                }
            }

            // route preview
            if (Tool == PcbTool.Route && routeStart is { } rs)
            {
                var end = SnapRoute(mouseWorld);
                var (c1, e1) = Corner45(rs, end);
                var w = board.Rules.WidthFor(routeNet);
                var pen = new Pen(Alpha(Palette.CopperColor(ActiveLayer), 0.6), w, lineCap: PenLineCap.Round);
                ctx.DrawLine(pen, P(rs), P(c1)); ctx.DrawLine(pen, P(c1), P(e1));
                var outline = new Pen(Brushes.White, Math.Max(0.02, 0.8 / Zoom), new ImmutableDashStyle([2, 2], 0));
                ctx.DrawLine(outline, P(rs), P(c1)); ctx.DrawLine(outline, P(c1), P(e1));
            }
            if (bandStart is { } bs)
            {
                var r = CRect.FromPoints(bs, mouseWorld);
                ctx.DrawRectangle(Palette.SelectionFill, new Pen(Palette.Selection, Math.Max(0.03, 1 / Zoom), new ImmutableDashStyle([4, 3], 0)), new Avalonia.Rect(r.MinX, r.MinY, r.Width, r.Height));
            }
        }

        // ---------- text ----------
        foreach (var c in p.Components.Where(c => c.Placed && !c.DoNotPopulate))
        {
            var fp = p.Footprint(c);
            if (fp == null) continue;
            var cy = p.Courtyard(c);
            DrawText(ctx, c.Ref, new Vec2(cy.Center.X, cy.MinY - 0.6), 1.0, c.OnBottom ? Palette.SilkBottom : Palette.SilkTop, 1, face: Mono, minPx: 7);
            if (Zoom > 14)
                foreach (var pd in p.Pads(c))
                {
                    var net = NetOf(pd);
                    var sz = Math.Min(pd.Pad.Size.X, pd.Pad.Size.Y);
                    if (sz * Zoom < 14) continue;
                    DrawText(ctx, pd.Pad.Number, pd.Position, Math.Min(sz * 0.45, 0.9), Brushes.Black, 1, face: Mono, minPx: 6);
                    if (net != "" && sz * Zoom > 26) DrawText(ctx, net.Length > 10 ? net[..10] : net, pd.Position + new Vec2(0, sz * 0.32), Math.Min(sz * 0.25, 0.5), new ImmutableSolidColorBrush(Color.Parse("#202020")), 1, minPx: 6);
                }
        }
        foreach (var t in board.Texts)
            DrawText(ctx, t.Text, t.Position, t.Size, t.Layer == Layer.BSilk ? Palette.SilkBottom : Palette.SilkTop, 1, face: Mono);
        board.EnsureHoleRefs();
        foreach (var hole in board.Holes)
            DrawText(ctx, hole.Ref, hole.Position + new Vec2(0, hole.Diameter / 2 + 0.9), 0.9, Palette.Muted, 1, face: Mono, minPx: 7);
        if (ShowRulers) DrawRulers(ctx);
    }

    void DrawRulers(DrawingContext ctx)
    {
        var vis = VisibleWorld();
        double span = Math.Max(vis.Width, vis.Height);
        double stepMm = span > 150 ? 10 : span > 60 ? 5 : span > 25 ? 2 : 1;
        var bg = new ImmutableSolidColorBrush(Color.Parse("#0D0F13"), 0.85);
        ctx.FillRectangle(bg, new Avalonia.Rect(0, 0, Bounds.Width, 16));
        ctx.FillRectangle(bg, new Avalonia.Rect(0, 0, 30, Bounds.Height));
        var pen = new Pen(Palette.Muted, 1);
        for (double x = Math.Ceiling(vis.Left / stepMm) * stepMm; x <= vis.Right; x += stepMm)
        {
            var sx = ToScreen(new Vec2(x, 0)).X;
            ctx.DrawLine(pen, new Point(sx, 10), new Point(sx, 16));
            ctx.DrawText(new FormattedText(x.ToString("0.#"), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 10, Palette.Muted), new Point(sx + 2, 0));
        }
        for (double y = Math.Ceiling(vis.Top / stepMm) * stepMm; y <= vis.Bottom; y += stepMm)
        {
            var sy = ToScreen(new Vec2(0, y)).Y;
            ctx.DrawLine(pen, new Point(24, sy), new Point(30, sy));
            ctx.DrawText(new FormattedText(y.ToString("0.#"), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 10, Palette.Muted), new Point(1, sy - 6));
        }
    }

    MountingHole? HoleAt(Vec2 w) => Session.Project.Board.Holes.FirstOrDefault(h => h.Position.DistanceTo(w) <= h.Diameter / 2 + 0.3);

    Geometry Poly(IReadOnlyList<Vec2> pts)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.SetFillRule(FillRule.EvenOdd);
            g.BeginFigure(P(pts[0]), true);
            for (int i = 1; i < pts.Count; i++) g.LineTo(P(pts[i]));
            g.EndFigure(true);
        }
        return geo;
    }

    void DrawShape(DrawingContext ctx, Shape s, IBrush brush)
    {
        if (s.Core.Length == 1) ctx.DrawEllipse(brush, null, P(s.Core[0]), s.Radius, s.Radius);
        else if (s.Core.Length == 2) ctx.DrawLine(new Pen(brush, s.Radius * 2, lineCap: PenLineCap.Round), P(s.Core[0]), P(s.Core[1]));
        else if (s.Radius <= 1e-9) ctx.DrawGeometry(brush, null, Poly(s.Core));
        else ctx.DrawGeometry(brush, new Pen(brush, s.Radius * 2, lineJoin: PenLineJoin.Round), Poly(s.Core));
    }

    void DrawGraphic(DrawingContext ctx, Graphic g, Component c, Pen pen)
    {
        Point T(Vec2 v) => P(ProjectGeometry.PcbToWorld(c, v));
        switch (g.Kind)
        {
            case GraphicKind.Line when g.Points.Count >= 2: ctx.DrawLine(pen, T(g.Points[0]), T(g.Points[1])); break;
            case GraphicKind.Circle when g.Points.Count >= 1:
                ctx.DrawEllipse(g.Filled ? pen.Brush : null, g.Filled ? null : pen, T(g.Points[0]), g.Radius, g.Radius); break;
            case GraphicKind.Rect when g.Points.Count >= 2:
                {
                    var a = g.Points[0]; var b = g.Points[1];
                    ctx.DrawLine(pen, T(a), T(new(b.X, a.Y))); ctx.DrawLine(pen, T(new(b.X, a.Y)), T(b));
                    ctx.DrawLine(pen, T(b), T(new(a.X, b.Y))); ctx.DrawLine(pen, T(new(a.X, b.Y)), T(a));
                    break;
                }
            case GraphicKind.Polyline or GraphicKind.Polygon:
                for (int i = 0; i + 1 < g.Points.Count; i++) ctx.DrawLine(pen, T(g.Points[i]), T(g.Points[i + 1]));
                if (g.Kind == GraphicKind.Polygon && g.Points.Count > 2) ctx.DrawLine(pen, T(g.Points[^1]), T(g.Points[0]));
                break;
        }
    }

    Geometry ZoneGeometry(Zone z, List<PadInstance> pads, Func<PadInstance, string> netOf)
    {
        if (zoneCache.TryGetValue(z.Id, out var cached)) return cached;
        var board = Session.Project.Board;
        double clr = Math.Max(z.Clearance, board.Rules.Clearance);
        var cut = new GeometryGroup { FillRule = FillRule.NonZero };
        void AddShape(Shape s)
        {
            var outline = s.ToOutline(4);
            if (outline.Count >= 3) cut.Children.Add(Poly(outline));
        }
        foreach (var t in board.Tracks.Where(t => t.Layer == z.Layer && t.Net != z.Net)) AddShape(Shape.Capsule(t.A, t.B, t.Width / 2 + clr));
        foreach (var v in board.Vias.Where(v => v.Net != z.Net)) AddShape(Shape.Circle(v.Position, v.Diameter / 2 + clr));
        foreach (var pd in pads.Where(pd => pd.OnLayer(z.Layer) || (pd.Pad.IsThrough && !pd.Pad.Plated)))
            if (netOf(pd) != z.Net || !pd.Pad.Plated) AddShape(pd.Shape.Inflate(clr));
        foreach (var h in board.Holes) AddShape(Shape.Circle(h.Position, h.Diameter / 2 + clr));
        for (int i = 0; i < board.Outline.Count; i++) AddShape(Shape.Capsule(board.Outline[i], board.Outline[(i + 1) % board.Outline.Count], board.Rules.EdgeClearance));
        Geometry geo = cut.Children.Count == 0 ? Poly(z.Outline) : new CombinedGeometry(GeometryCombineMode.Exclude, Poly(z.Outline), cut);
        zoneCache[z.Id] = geo;
        return geo;
    }

    // ======================= hit testing =======================

    PadInstance? PadAt(Vec2 w)
    {
        PadInstance? best = null; double bd = double.MaxValue;
        foreach (var pd in Session.Project.AllPads())
        {
            var d = pd.Shape.DistanceTo(w);
            if (d > 0.05) continue;
            // prefer pads on the active layer
            double score = d + (pd.OnLayer(ActiveLayer) ? 0 : 0.5);
            if (score < bd) { bd = score; best = pd; }
        }
        return best;
    }

    Track? TrackAt(Vec2 w)
    {
        Track? best = null; double bd = double.MaxValue;
        foreach (var t in Session.Project.Board.Tracks)
        {
            var d = Shape.PointSegDistance(w, t.A, t.B) - t.Width / 2;
            if (d > Math.Max(0.05, 3 / Zoom)) continue;
            double score = d + (t.Layer == ActiveLayer ? 0 : 0.3);
            if (score < bd) { bd = score; best = t; }
        }
        return best;
    }

    Via? ViaAt(Vec2 w) => Session.Project.Board.Vias.FirstOrDefault(v => v.Position.DistanceTo(w) <= v.Diameter / 2 + 0.05);

    Component? ComponentAt(Vec2 w)
    {
        var p = Session.Project;
        Component? best = null; double bestArea = double.MaxValue;
        foreach (var c in p.Components.Where(c => c.Placed && !c.DoNotPopulate))
        {
            var cy = p.Courtyard(c);
            if (!cy.Contains(w)) continue;
            var area = cy.Width * cy.Height + (c.OnBottom == (ActiveLayer == Layer.BCu) ? 0 : 1000);
            if (area < bestArea) { bestArea = area; best = c; }
        }
        return best;
    }

    string NetAt(Vec2 w)
    {
        if (PadAt(w) is { } pd) return Session.NetOfPad(pd);
        if (ViaAt(w) is { } v) return v.Net;
        if (TrackAt(w) is { } t) return t.Net;
        return "";
    }

    Vec2 SnapRoute(Vec2 w)
    {
        if (PadAt(w) is { } pd && Session.NetOfPad(pd) == routeNet) return pd.Position;
        if (ViaAt(w) is { } v && v.Net == routeNet) return v.Position;
        return w.Snap(Grid);
    }

    (Vec2 corner, Vec2 end) Corner45(Vec2 s, Vec2 e)
    {
        var d = e - s;
        double ax = Math.Abs(d.X), ay = Math.Abs(d.Y);
        double sx = Math.Sign(d.X), sy = Math.Sign(d.Y);
        Vec2 corner;
        if (!diagonalFirst)
            corner = ax > ay ? s + new Vec2(sx * (ax - ay), 0) : s + new Vec2(0, sy * (ay - ax));
        else
            corner = ax > ay ? s + new Vec2(sx * ay, sy * ay) : s + new Vec2(sx * ax, sy * ax);
        return (corner, e);
    }

    // ======================= interaction =======================

    protected override void OnWorldPointerMoved(PointerEventArgs e, Vec2 w)
    {
        if (dragStart is { } ds && dragOrig != null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var delta = (w - ds).Snap(Grid);
            if (!dragMoved && delta.Length > 0) { dragMoved = true; Session.PushUndo(); }
            if (dragMoved)
            {
                foreach (var c in Session.Selection.Where(c => !c.Locked))
                    if (dragOrig.TryGetValue(c.Id, out var o)) c.PcbPos = o + delta;
                if (holeDragOrig != null)
                    foreach (var h in Session.Project.Board.Holes)
                        if (holeDragOrig.TryGetValue(h.Id, out var ho)) h.Position = ho + delta;
                Session.Invalidate(topology: false);
            }
            return;
        }
        if (Tool == PcbTool.Select && PadAt(w) is { } pad)
        {
            var net = Session.NetOfPad(pad);
            Status($"{pad.Component.Ref} pad {pad.Pad.Number} — {(net == "" ? "no net" : net)}");
        }
        InvalidateVisual();
    }

    protected override void OnWorldPointerPressed(PointerPressedEventArgs e, Vec2 w)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        bool dbl = (DateTime.Now - lastClick).TotalMilliseconds < 350 && lastClickPos.DistanceTo(w) < 0.5;
        lastClick = DateTime.Now; lastClickPos = w;
        var p = Session.Project; var board = p.Board;
        switch (Tool)
        {
            case PcbTool.Route:
                {
                    if (routeStart is not { } rs)
                    {
                        var pd = PadAt(w);
                        if (pd != null)
                        {
                            routeNet = Session.NetOfPad(pd); routeStart = pd.Position;
                            if (!pd.Pad.IsThrough) SetLayer(pd.OnBottom ? Layer.BCu : Layer.FCu);
                        }
                        else if (ViaAt(w) is { } v) { routeNet = v.Net; routeStart = v.Position; }
                        else if (TrackAt(w) is { } t) { routeNet = t.Net; routeStart = Shape.ClosestPointOnSeg(w, t.A, t.B).Snap(0.01); SetLayer(t.Layer); }
                        else { Status("Start routing on a pad, via or track."); break; }
                        Session.SetHighlightNet(routeNet == "" ? null : routeNet);
                        break;
                    }
                    var end = SnapRoute(w);
                    var (corner, e2) = Corner45(rs, end);
                    var width = board.Rules.WidthFor(routeNet);
                    var layer = ActiveLayer;
                    Session.Mutate(() =>
                    {
                        if (!corner.ApproxEquals(rs)) board.Tracks.Add(new Track { A = rs, B = corner, Width = width, Layer = layer, Net = routeNet });
                        if (!corner.ApproxEquals(e2)) board.Tracks.Add(new Track { A = corner, B = e2, Width = width, Layer = layer, Net = routeNet });
                    });
                    bool finished = dbl || (PadAt(end) is { } endPad && Session.NetOfPad(endPad) == routeNet && endPad.Position.ApproxEquals(end));
                    routeStart = finished ? null : e2;
                    if (finished) Session.SetHighlightNet(null);
                    break;
                }
            case PcbTool.Via:
                {
                    var pos = w.Snap(Grid);
                    var net = NetAt(pos);
                    Session.Mutate(() => board.Vias.Add(new Via { Position = pos, Net = net, Diameter = board.Rules.ViaDiameterFor(net), Drill = board.Rules.ViaDrillFor(net) }));
                    break;
                }
            case PcbTool.Select:
                {
                    bool add = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    var pad = PadAt(w);
                    var comp = ComponentAt(w);
                    if (pad == null && HoleAt(w) is { } hole)
                    {
                        if (!add) { selTracks.Clear(); selVias.Clear(); selHoles.Clear(); Session.ClearSelection(); }
                        selHoles.Add(hole.Id);
                        Status($"Mounting hole {hole.Ref}: {hole.Diameter:0.##} mm at {hole.Position.X:0.###}, {hole.Position.Y:0.###}. Drag to move, Del to delete.");
                        dragStart = w; dragMoved = false;
                        dragOrig = [];
                        holeDragOrig = Session.Project.Board.Holes.Where(h => selHoles.Contains(h.Id)).ToDictionary(h => h.Id, h => h.Position);
                        break;
                    }
                    if (pad == null)
                    {
                        if (ViaAt(w) is { } v) { SelectCopper(add, null, v); Session.SetHighlightNet(v.Net == "" ? null : v.Net); break; }
                        if (TrackAt(w) is { } t && (comp == null || t.Layer == ActiveLayer)) { SelectCopper(add, t, null); Session.SetHighlightNet(t.Net == "" ? null : t.Net); break; }
                    }
                    if (comp != null)
                    {
                        if (dbl) { ComponentDoubleClicked?.Invoke(comp); break; }
                        if (!add) { selTracks.Clear(); selVias.Clear(); }
                        if (!Session.SelectedComponents.Contains(comp.Id)) Session.Select([comp], add);
                        var net = pad != null ? Session.NetOfPad(pad) : "";
                        Session.SetHighlightNet(net == "" ? null : net);
                        dragStart = w; dragMoved = false; holeDragOrig = null; selHoles.Clear();
                        dragOrig = Session.Selection.ToDictionary(c => c.Id, c => c.PcbPos);
                        break;
                    }
                    if (!add) { selTracks.Clear(); selVias.Clear(); selHoles.Clear(); Session.ClearSelection(); }
                    bandStart = w; e.Pointer.Capture(this);
                    break;
                }
        }
        InvalidateVisual();
    }

    void SelectCopper(bool add, Track? t, Via? v)
    {
        if (!add) { selTracks.Clear(); selVias.Clear(); Session.ClearSelection(); }
        if (t != null) selTracks.Add(t.Id);
        if (v != null) selVias.Add(v.Id);
        InvalidateVisual();
    }

    protected override void OnWorldPointerReleased(PointerReleasedEventArgs e, Vec2 w)
    {
        if (bandStart is { } bs)
        {
            var r = CRect.FromPoints(bs, w);
            var p = Session.Project;
            if (r.Width > 0.2 || r.Height > 0.2)
            {
                var hits = p.Components.Where(c => c.Placed && r.Contains(p.Courtyard(c).Center)).ToList();
                foreach (var t in p.Board.Tracks.Where(t => r.Contains(t.A) && r.Contains(t.B))) selTracks.Add(t.Id);
                foreach (var v in p.Board.Vias.Where(v => r.Contains(v.Position))) selVias.Add(v.Id);
                Session.Select(hits, true);
            }
            bandStart = null; e.Pointer.Capture(null);
            InvalidateVisual();
        }
        if (dragStart != null)
        {
            if (dragMoved) Session.MarkChanged();
            dragStart = null; dragOrig = null; holeDragOrig = null; dragMoved = false;
        }
    }

    protected override void OnRightClick(Vec2 w)
    {
        if (Tool == PcbTool.Route && routeStart != null) { routeStart = null; Session.SetHighlightNet(null); InvalidateVisual(); return; }
        if (Tool != PcbTool.Select) SetTool(PcbTool.Select);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var p = Session.Project; var board = p.Board;
        switch (e.Key)
        {
            case Key.Escape:
                if (routeStart != null) { routeStart = null; Session.SetHighlightNet(null); }
                else { SetTool(PcbTool.Select); selTracks.Clear(); selVias.Clear(); Session.ClearSelection(); }
                InvalidateVisual(); e.Handled = true; break;
            case Key.V when Tool == PcbTool.Route && routeStart is { } rs:
                {
                    var next = ActiveLayer == Layer.FCu ? Layer.BCu : Layer.FCu;
                    var net = routeNet;
                    Session.Mutate(() => board.Vias.Add(new Via { Position = rs, Net = net, Diameter = board.Rules.ViaDiameterFor(net), Drill = board.Rules.ViaDrillFor(net) }));
                    SetLayer(next);
                    e.Handled = true; break;
                }
            case Key.Oem2 or Key.Divide: diagonalFirst = !diagonalFirst; InvalidateVisual(); e.Handled = true; break;
            case Key.PageUp: SetLayer(Layer.FCu); e.Handled = true; break;
            case Key.PageDown: SetLayer(Layer.BCu); e.Handled = true; break;
            case Key.Delete or Key.Back: DeleteSelection(); e.Handled = true; break;
            case Key.R when Session.SelectedComponents.Count > 0:
                {
                    double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -90 : 90;
                    var sel = Session.Selection.Where(c => !c.Locked).ToList();
                    if (sel.Count == 0) break;
                    var center = sel.Count == 1 ? sel[0].PcbPos : new Vec2(sel.Average(c => c.PcbPos.X), sel.Average(c => c.PcbPos.Y)).Snap(Grid);
                    Session.Mutate(() =>
                    {
                        foreach (var c in sel) { c.PcbPos = center + (c.PcbPos - center).Rotate(step); c.PcbRot = ((c.PcbRot + step) % 360 + 360) % 360; }
                    });
                    e.Handled = true; break;
                }
            case Key.F when Session.SelectedComponents.Count > 0:
                Session.Mutate(() => { foreach (var c in Session.Selection.Where(c => !c.Locked)) c.OnBottom = !c.OnBottom; });
                e.Handled = true; break;
            case Key.F: ZoomToFit(); e.Handled = true; break;
            case Key.L when Session.SelectedComponents.Count > 0:
                Session.Mutate(() => { foreach (var c in Session.Selection) c.Locked = !c.Locked; });
                e.Handled = true; break;
            case Key.X: SetTool(PcbTool.Route); e.Handled = true; break;
            case Key.S when e.KeyModifiers == KeyModifiers.None: SetTool(PcbTool.Select); e.Handled = true; break;
        }
    }

    public void DeleteSelection()
    {
        var p = Session.Project;
        if (selTracks.Count == 0 && selVias.Count == 0 && selHoles.Count == 0 && Session.SelectedComponents.Count == 0) return;
        Session.Mutate(() =>
        {
            p.Board.Holes.RemoveAll(h => selHoles.Contains(h.Id));
            p.Board.Tracks.RemoveAll(t => selTracks.Contains(t.Id));
            p.Board.Vias.RemoveAll(v => selVias.Contains(v.Id));
            // deleting a footprint on the PCB un-places it (the part stays in the schematic)
            foreach (var c in Session.Selection.Where(c => !c.Locked)) c.Placed = false;
        });
        selTracks.Clear(); selVias.Clear(); selHoles.Clear();
        Session.ClearSelection();
    }
}
