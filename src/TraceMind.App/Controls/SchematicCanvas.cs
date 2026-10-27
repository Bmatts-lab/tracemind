using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using TraceMind.Core.Geometry;
using TraceMind.Core.Model;
using CRect = TraceMind.Core.Geometry.Rect;

namespace TraceMind.App.Controls;

public enum SchTool { Select, Wire, Label, NoConnect, Place }

public sealed class SchematicCanvas : CanvasBase
{
    public SchTool Tool { get; private set; } = SchTool.Select;
    public event Action<SchTool>? ToolChanged;
    /// <summary>Ask the host for a net label name (returns null when cancelled).</summary>
    public Func<string, Task<string?>>? PromptLabel;
    public event Action<Component>? ComponentDoubleClicked;

    const double G = 2.54;
    // place tool
    string? placeSymbolId; string placeValue = ""; string placeFootprint = ""; int placeRot;
    // wire tool
    Vec2? wireStart; bool wireHorizontalFirst = true;
    // selection of non-component items
    readonly HashSet<Guid> selWires = [], selLabels = [];
    // dragging
    Vec2? dragStart; Dictionary<Guid, Vec2>? dragOrig; Dictionary<Guid, Vec2>? dragLabelOrig; bool dragMoved;
    Vec2? bandStart;
    PinInstance? hoverPin;
    DateTime lastClick; Vec2 lastClickPos;

    public void SetTool(SchTool t)
    {
        Tool = t; wireStart = null;
        if (t != SchTool.Place) placeSymbolId = null;
        ToolChanged?.Invoke(t);
        Cursor = t == SchTool.Select ? Cursor.Default : new Cursor(StandardCursorType.Cross);
        InvalidateVisual();
    }

    public void BeginPlace(string symbolId, string value, string footprint)
    {
        placeSymbolId = symbolId; placeValue = value; placeFootprint = footprint; placeRot = 0;
        SetTool(SchTool.Place);
        placeSymbolId = symbolId;
        Status($"Click to place {symbolId}. R rotates, Esc finishes.");
    }

    public override void ZoomToFit()
    {
        var r = CRect.Empty;
        foreach (var c in Session.Project.Components) r = r.Union(Session.Project.SchBounds(c));
        foreach (var w in Session.Project.Schematic.Wires) { r = r.Include(w.A); r = r.Include(w.B); }
        foreach (var l in Session.Project.Schematic.Labels) r = r.Include(l.Position);
        if (r.IsEmpty) r = new CRect(0, 0, 150, 100);
        ZoomToRect(r.Inflate(10));
    }

    public void ZoomToRect(CRect r) => base.ZoomToRect(r);

    // ======================= rendering =======================

    public override void Render(DrawingContext ctx)
    {
        var p = Session.Project;
        ctx.FillRectangle(Palette.SchBackground, new Avalonia.Rect(Bounds.Size));
        DrawGrid(ctx, G, Palette.SchGridDot);
        var nl = Session.Netlist;
        string? hi = Session.HighlightNet;
        var hiPoints = new HashSet<(long, long)>();
        if (hi != null)
        {
            foreach (var c in p.Components)
                foreach (var pin in p.Pins(c))
                    if (nl.PinNets.TryGetValue((c.Ref, pin.Pin.Number), out var n) && n == hi) hiPoints.Add(PKey(pin.Position));
            foreach (var l in p.Schematic.Labels.Where(l => l.Name == hi)) hiPoints.Add(PKey(l.Position));
        }
        double lineW = Math.Max(0.15, 1.3 / Zoom);
        var vis = VisibleWorld();

        using (ctx.PushTransform(WorldTransform))
        {
            // wires
            var wirePen = new Pen(Palette.Wire, Math.Max(0.2, 1.6 / Zoom), lineCap: PenLineCap.Round);
            var wireHiPen = new Pen(Palette.Highlight, Math.Max(0.3, 2.4 / Zoom), lineCap: PenLineCap.Round);
            var selPen = new Pen(Palette.Selection, Math.Max(0.5, 4 / Zoom), lineCap: PenLineCap.Round);
            var wireNets = hi != null ? WiresOnNet(hi) : [];
            foreach (var w in p.Schematic.Wires)
            {
                if (selWires.Contains(w.Id)) ctx.DrawLine(selPen, P(w.A), P(w.B));
                ctx.DrawLine(wireNets.Contains(w.Id) ? wireHiPen : wirePen, P(w.A), P(w.B));
            }
            // junctions
            foreach (var j in Junctions()) ctx.DrawEllipse(Palette.Wire, null, P(j), 0.45, 0.45);

            // components
            foreach (var c in p.Components)
            {
                var sym = p.Symbol(c);
                if (sym == null) continue;
                var b = p.SchBounds(c);
                if (b.MaxX < vis.Left - 5 || b.MinX > vis.Right + 5 || b.MaxY < vis.Top - 5 || b.MinY > vis.Bottom + 5) continue;
                bool sel = Session.SelectedComponents.Contains(c.Id);
                if (sel) ctx.DrawRectangle(Palette.SelectionFill, new Pen(Palette.Selection, lineW), new Avalonia.Rect(b.MinX - 0.6, b.MinY - 0.6, b.Width + 1.2, b.Height + 1.2), 0.5, 0.5);
                DrawSymbol(ctx, c, sym, sym.IsPower ? Palette.PowerLabel : Palette.SymbolStroke, lineW);
                foreach (var pin in p.Pins(c))
                {
                    bool h = hiPoints.Contains(PKey(pin.Position));
                    var pen = new Pen(h ? Palette.Highlight : Palette.PinStroke, Math.Max(0.15, (h ? 2.2 : 1.3) / Zoom));
                    ctx.DrawLine(pen, P(pin.Position), P(pin.BodyPoint));
                    if (!sym.IsPower && !nl.PinNets.ContainsKey((c.Ref, pin.Pin.Number)) && !p.Schematic.NoConnects.Any(n => n.Position.ApproxEquals(pin.Position, 0.01)))
                        ctx.DrawEllipse(null, new Pen(Palette.NoConnect, Math.Max(0.1, 1 / Zoom)), P(pin.Position), 0.35, 0.35);
                }
            }
            // no-connect flags
            var ncPen = new Pen(Palette.NoConnect, Math.Max(0.2, 1.6 / Zoom));
            foreach (var n in p.Schematic.NoConnects)
            {
                ctx.DrawLine(ncPen, P(n.Position + new Vec2(-0.8, -0.8)), P(n.Position + new Vec2(0.8, 0.8)));
                ctx.DrawLine(ncPen, P(n.Position + new Vec2(-0.8, 0.8)), P(n.Position + new Vec2(0.8, -0.8)));
            }
            // label anchors
            foreach (var l in p.Schematic.Labels)
                ctx.DrawEllipse(hiPoints.Contains(PKey(l.Position)) ? Palette.Highlight : (IsPowerNet(l.Name) ? Palette.PowerLabel : Palette.Label), null, P(l.Position), 0.35, 0.35);

            // hover pin
            if (hoverPin != null) ctx.DrawEllipse(null, new Pen(Palette.HoverPin, Math.Max(0.15, 1.5 / Zoom)), P(hoverPin.Position), 0.9, 0.9);

            // wire preview
            if (Tool == SchTool.Wire && wireStart is { } ws)
            {
                var end = SnapPoint(mouseWorld);
                var corner = wireHorizontalFirst ? new Vec2(end.X, ws.Y) : new Vec2(ws.X, end.Y);
                var prev = new Pen(Palette.Wire, Math.Max(0.2, 1.6 / Zoom), new ImmutableDashStyle([3, 3], 0));
                ctx.DrawLine(prev, P(ws), P(corner)); ctx.DrawLine(prev, P(corner), P(end));
            }
            // rubber band
            if (bandStart is { } bs)
            {
                var r = CRect.FromPoints(bs, mouseWorld);
                ctx.DrawRectangle(Palette.SelectionFill, new Pen(Palette.Selection, lineW, new ImmutableDashStyle([4, 3], 0)), new Avalonia.Rect(r.MinX, r.MinY, r.Width, r.Height));
            }
            // placement ghost
            if (Tool == SchTool.Place && placeSymbolId != null && p.GetSymbol(placeSymbolId) is { } ghost)
            {
                var gc = new Component { SchPos = SnapPoint(mouseWorld), SchRot = placeRot, SymbolId = placeSymbolId };
                using (ctx.PushOpacity(0.6)) DrawSymbol(ctx, gc, ghost, Palette.Selection, lineW);
            }
        }

        // ---------- text (screen space) ----------
        foreach (var c in p.Components)
        {
            var sym = p.Symbol(c);
            if (sym == null) continue;
            var b = p.SchBounds(c);
            if (b.MaxX < vis.Left - 5 || b.MinX > vis.Right + 5 || b.MaxY < vis.Top - 5 || b.MinY > vis.Bottom + 5) continue;
            DrawSymbolText(ctx, c, sym);
            if (!sym.IsPower)
            {
                var refPos = TextAnchor(c, sym.RefTextPos);
                var valPos = TextAnchor(c, sym.ValueTextPos);
                bool twoPinVertical = sym.Pins.Count == 2 && sym.Pins.All(pp => pp.Side is PinSide.Top or PinSide.Bottom);
                bool vertSym = twoPinVertical && c.SchRot % 180 == 0;
                int align = vertSym ? 0 : (sym.Pins.Count > 3 ? 0 : 1);
                if (twoPinVertical && c.SchRot % 180 != 0)
                {
                    // laid sideways: reference above, value below, both centred
                    var body = sym.Graphics.SelectMany(g => g.Points).Select(pt => ProjectGeometry.SchToWorld(c, pt)).Aggregate(CRect.Empty, (r, pt) => r.Include(pt));
                    if (body.IsEmpty) body = b;
                    refPos = new Vec2(body.Center.X, body.MinY - 1.4); valPos = new Vec2(body.Center.X, body.MaxY + 1.4); align = 1;
                }
                DrawText(ctx, c.Ref, refPos, 1.5, Palette.RefText, align, face: Sans);
                DrawText(ctx, c.Value, valPos, 1.3, Palette.ValueText, align);
            }
            else DrawText(ctx, c.Value, TextAnchor(c, sym.ValueTextPos), 1.3, Palette.PowerLabel, 1);
        }
        foreach (var l in p.Schematic.Labels) DrawLabel(ctx, l, hiPoints.Contains(PKey(l.Position)) || selLabels.Contains(l.Id));
        foreach (var t in p.Schematic.Texts) DrawText(ctx, t.Text, t.Position, t.Size, Palette.ValueText);
        if (Tool == SchTool.Wire && wireStart == null) DrawText(ctx, "Click a pin or grid point to start a wire", ToWorld(new Point(12, Bounds.Height - 16)), 12 / Zoom, Palette.Muted);
    }

    static (long, long) PKey(Vec2 v) => ((long)Math.Round(v.X * 100), (long)Math.Round(v.Y * 100));
    static bool IsPowerNet(string n) => n.StartsWith('+') || n.StartsWith('-') || n.Contains("GND", StringComparison.OrdinalIgnoreCase) || n.StartsWith("VCC") || n.StartsWith("VDD") || n == "VBUS" || n.StartsWith("VIN");

    Vec2 TextAnchor(Component c, Vec2 local)
    {
        // Keep text readable regardless of rotation: rotate the anchor but not the text.
        return ProjectGeometry.SchToWorld(c, local);
    }

    void DrawSymbol(DrawingContext ctx, Component c, SymbolDef sym, IBrush stroke, double lineW)
    {
        foreach (var g in sym.Graphics)
        {
            var pen = new Pen(stroke, Math.Max(g.Width, lineW), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            switch (g.Kind)
            {
                case GraphicKind.Line when g.Points.Count >= 2:
                    ctx.DrawLine(pen, P(ProjectGeometry.SchToWorld(c, g.Points[0])), P(ProjectGeometry.SchToWorld(c, g.Points[1])));
                    break;
                case GraphicKind.Rect when g.Points.Count >= 2:
                    {
                        var a = ProjectGeometry.SchToWorld(c, g.Points[0]); var b = ProjectGeometry.SchToWorld(c, g.Points[1]);
                        var r = CRect.FromPoints(a, b);
                        ctx.DrawRectangle(g.Filled ? (g.Points[1].X - g.Points[0].X > 2 ? Palette.SymbolFill : stroke) : null, pen, new Avalonia.Rect(r.MinX, r.MinY, r.Width, r.Height));
                        break;
                    }
                case GraphicKind.Circle when g.Points.Count >= 1:
                    ctx.DrawEllipse(g.Filled ? stroke : null, pen, P(ProjectGeometry.SchToWorld(c, g.Points[0])), g.Radius, g.Radius);
                    break;
                case GraphicKind.Arc when g.Points.Count >= 1:
                    {
                        var geo = new StreamGeometry();
                        using (var gc = geo.Open())
                        {
                            for (int i = 0; i <= 16; i++)
                            {
                                var a = (g.StartAngle + (g.EndAngle - g.StartAngle) * i / 16.0) * Math.PI / 180;
                                var pt = ProjectGeometry.SchToWorld(c, g.Points[0] + new Vec2(Math.Cos(a), Math.Sin(a)) * g.Radius);
                                if (i == 0) gc.BeginFigure(P(pt), false); else gc.LineTo(P(pt));
                            }
                            gc.EndFigure(false);
                        }
                        ctx.DrawGeometry(null, pen, geo);
                        break;
                    }
                case GraphicKind.Polyline or GraphicKind.Polygon when g.Points.Count >= 2:
                    {
                        var geo = new StreamGeometry();
                        using (var gc = geo.Open())
                        {
                            gc.BeginFigure(P(ProjectGeometry.SchToWorld(c, g.Points[0])), g.Filled);
                            foreach (var pt in g.Points.Skip(1)) gc.LineTo(P(ProjectGeometry.SchToWorld(c, pt)));
                            gc.EndFigure(g.Kind == GraphicKind.Polygon);
                        }
                        ctx.DrawGeometry(g.Filled ? stroke : null, pen, geo);
                        break;
                    }
            }
        }
    }

    void DrawSymbolText(DrawingContext ctx, Component c, SymbolDef sym)
    {
        foreach (var pin in sym.Pins)
        {
            var side = ProjectGeometry.RotateSide(pin.Side, c.SchRot, c.SchMirror);
            var tip = ProjectGeometry.SchToWorld(c, pin.Position); var body = ProjectGeometry.SchToWorld(c, pin.BodyPoint);
            bool vertical = side is PinSide.Top or PinSide.Bottom;
            if (!sym.HidePinNumbers && pin.ShowNumber && pin.Length > 0)
            {
                var mid = (tip + body) / 2;
                var off = vertical ? new Vec2(-0.5, 0) : new Vec2(0, -0.55);
                DrawText(ctx, pin.Number, mid + off, 1.0, Palette.PinNumber, vertical ? 2 : 1, minPx: 6);
            }
            if (!sym.HidePinNames && pin.ShowName && !string.IsNullOrEmpty(pin.Name))
            {
                var name = pin.Name.Replace("~", "");
                switch (side)
                {
                    case PinSide.Left: DrawText(ctx, name, body + new Vec2(0.6, 0), 1.2, Palette.PinName, 0, minPx: 6); break;
                    case PinSide.Right: DrawText(ctx, name, body + new Vec2(-0.6, 0), 1.2, Palette.PinName, 2, minPx: 6); break;
                    case PinSide.Top: DrawText(ctx, name, body + new Vec2(0, 0.6), 1.2, Palette.PinName, 2, vertical: true, minPx: 6); break;
                    case PinSide.Bottom: DrawText(ctx, name, body + new Vec2(0, -0.6), 1.2, Palette.PinName, 0, vertical: true, minPx: 6); break;
                }
            }
        }
    }

    void DrawLabel(DrawingContext ctx, NetLabel l, bool highlighted)
    {
        double px = 1.3 * Zoom;
        if (px < 4) return;
        var brush = highlighted ? Palette.Highlight : (IsPowerNet(l.Name) ? Palette.PowerLabel : Palette.Label);
        var ft = new FormattedText(l.Name, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Sans, px, brush);
        var a = ToScreen(l.Position);
        double pad = px * 0.35, w = ft.Width + pad * 2 + px * 0.5, h = ft.Height + pad * 0.6;
        Matrix m = l.Rotation switch
        {
            180 => Matrix.CreateRotation(Math.PI),
            90 => Matrix.CreateRotation(-Math.PI / 2),
            270 => Matrix.CreateRotation(Math.PI / 2),
            _ => Matrix.Identity,
        };
        using (ctx.PushTransform(m * Matrix.CreateTranslation(a.X, a.Y)))
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(0, 0), true);
                g.LineTo(new Point(px * 0.5, -h / 2)); g.LineTo(new Point(w, -h / 2)); g.LineTo(new Point(w, h / 2)); g.LineTo(new Point(px * 0.5, h / 2));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(new ImmutableSolidColorBrush(((ISolidColorBrush)brush).Color, 0.12), new Pen(brush, 1), geo);
            bool flip = l.Rotation == 180 || l.Rotation == 90;
            if (flip)
                using (ctx.PushTransform(Matrix.CreateTranslation(-(px * 0.5 + pad + ft.Width / 2), 0) * Matrix.CreateRotation(Math.PI) * Matrix.CreateTranslation(px * 0.5 + pad + ft.Width / 2, 0)))
                    ctx.DrawText(ft, new Point(px * 0.5 + pad, -ft.Height / 2));
            else ctx.DrawText(ft, new Point(px * 0.5 + pad, -ft.Height / 2));
        }
    }

    IEnumerable<Vec2> Junctions()
    {
        var counts = new Dictionary<(long, long), (int n, Vec2 p)>();
        void Add(Vec2 v) { var k = PKey(v); counts[k] = counts.TryGetValue(k, out var e) ? (e.n + 1, v) : (1, v); }
        var wires = Session.Project.Schematic.Wires;
        foreach (var w in wires) { Add(w.A); Add(w.B); }
        foreach (var w in wires)
            foreach (var o in wires)
            {
                if (o == w) continue;
                foreach (var end in new[] { o.A, o.B })
                    if (!end.ApproxEquals(w.A, 0.01) && !end.ApproxEquals(w.B, 0.01) && Shape.PointSegDistance(end, w.A, w.B) < 0.01) Add(end);
            }
        foreach (var c in Session.Project.Components) foreach (var pin in Session.Project.Pins(c)) if (counts.ContainsKey(PKey(pin.Position))) Add(pin.Position);
        return counts.Values.Where(v => v.n >= 3).Select(v => v.p);
    }

    HashSet<Guid> WiresOnNet(string net)
    {
        var p = Session.Project; var nl = Session.Netlist;
        var seeds = new List<Vec2>();
        foreach (var c in p.Components) foreach (var pin in p.Pins(c))
                if (nl.PinNets.TryGetValue((c.Ref, pin.Pin.Number), out var n) && n == net) seeds.Add(pin.Position);
        seeds.AddRange(p.Schematic.Labels.Where(l => l.Name == net).Select(l => l.Position));
        var result = new HashSet<Guid>();
        var frontier = new Queue<Vec2>(seeds);
        while (frontier.Count > 0)
        {
            var pt = frontier.Dequeue();
            foreach (var w in p.Schematic.Wires)
            {
                if (result.Contains(w.Id)) continue;
                if (Shape.PointSegDistance(pt, w.A, w.B) < 0.01) { result.Add(w.Id); frontier.Enqueue(w.A); frontier.Enqueue(w.B); }
            }
        }
        return result;
    }

    // ======================= hit testing =======================

    Vec2 SnapPoint(Vec2 w)
    {
        var pin = PinAt(w, 1.2);
        return pin?.Position ?? w.Snap(G / 2);
    }

    PinInstance? PinAt(Vec2 w, double tol)
    {
        PinInstance? best = null; double bd = tol;
        foreach (var c in Session.Project.Components)
            foreach (var pin in Session.Project.Pins(c))
            {
                var d = pin.Position.DistanceTo(w);
                if (d < bd) { bd = d; best = pin; }
            }
        return best;
    }

    Component? ComponentAt(Vec2 w)
    {
        var p = Session.Project;
        foreach (var c in p.Components.AsEnumerable().Reverse())
            if (p.SchBounds(c).Inflate(0.5).Contains(w)) return c;
        return null;
    }

    NetLabel? LabelAt(Vec2 w)
    {
        foreach (var l in Session.Project.Schematic.Labels)
        {
            var len = l.Name.Length * 1.0 + 1.5;
            var dir = l.Rotation switch { 180 => new Vec2(-1, 0), 90 => new Vec2(0, -1), 270 => new Vec2(0, 1), _ => new Vec2(1, 0) };
            if (Shape.PointSegDistance(w, l.Position, l.Position + dir * len) < 1.0) return l;
        }
        return null;
    }

    Wire? WireAt(Vec2 w)
    {
        Wire? best = null; double bd = Math.Max(0.6, 6 / Zoom);
        foreach (var wi in Session.Project.Schematic.Wires)
        {
            var d = Shape.PointSegDistance(w, wi.A, wi.B);
            if (d < bd) { bd = d; best = wi; }
        }
        return best;
    }

    string? NetAt(Vec2 w)
    {
        var pin = PinAt(w, 1.0);
        if (pin != null && Session.Netlist.PinNets.TryGetValue((pin.Component.Ref, pin.Pin.Number), out var n)) return n;
        var l = LabelAt(w);
        if (l != null) return l.Name;
        var wire = WireAt(w);
        if (wire != null)
        {
            foreach (var net in Session.Netlist.Nets) if (WiresOnNet(net.Name).Contains(wire.Id)) return net.Name;
        }
        return null;
    }

    // ======================= interaction =======================

    protected override void OnWorldPointerMoved(PointerEventArgs e, Vec2 w)
    {
        var newHover = Tool is SchTool.Wire or SchTool.Label or SchTool.NoConnect || Tool == SchTool.Select ? PinAt(w, 1.0) : null;
        if (newHover != hoverPin)
        {
            hoverPin = newHover;
            if (hoverPin != null)
            {
                var net = Session.Netlist.PinNets.TryGetValue((hoverPin.Component.Ref, hoverPin.Pin.Number), out var n) ? n : "unconnected";
                Status($"{hoverPin.Component.Ref} pin {hoverPin.Pin.Number}{(hoverPin.Pin.Name != "" ? " (" + hoverPin.Pin.Name + ")" : "")} — net {net}");
            }
        }
        if (dragStart is { } ds && dragOrig != null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var delta = (w - ds).Snap(G / 2);
            if (!dragMoved && delta.Length > 0) { dragMoved = true; Session.PushUndo(); }
            if (dragMoved)
            {
                foreach (var c in Session.Selection)
                    if (dragOrig.TryGetValue(c.Id, out var o)) EditOps.MoveSchematic(Session.Project, c, o + delta);
                if (dragLabelOrig != null)
                    foreach (var l in Session.Project.Schematic.Labels)
                        if (dragLabelOrig.TryGetValue(l.Id, out var o)) l.Position = o + delta;
                Session.Invalidate();
            }
            return;
        }
        InvalidateVisual();
    }

    protected override async void OnWorldPointerPressed(PointerPressedEventArgs e, Vec2 w)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        bool dbl = (DateTime.Now - lastClick).TotalMilliseconds < 350 && lastClickPos.DistanceTo(w) < 1;
        lastClick = DateTime.Now; lastClickPos = w;
        var p = Session.Project;
        switch (Tool)
        {
            case SchTool.Place when placeSymbolId != null:
                {
                    var id = placeSymbolId; var pos = SnapPoint(w);
                    Session.Mutate(() =>
                    {
                        var c = EditOps.AddComponent(p, id, placeValue, placeFootprint, null, pos);
                        if (placeRot != 0) EditOps.MoveSchematic(p, c, pos, placeRot);
                        Session.Select([c]);
                    });
                    Status($"Placed {id}. Click again to place another, Esc to stop.");
                    break;
                }
            case SchTool.Wire:
                {
                    var pt = SnapPoint(w);
                    if (wireStart is not { } ws) { wireStart = pt; InvalidateVisual(); break; }
                    if (pt.ApproxEquals(ws)) { wireStart = null; InvalidateVisual(); break; }
                    var corner = wireHorizontalFirst ? new Vec2(pt.X, ws.Y) : new Vec2(ws.X, pt.Y);
                    Session.Mutate(() =>
                    {
                        if (!corner.ApproxEquals(ws)) p.Schematic.Wires.Add(new Wire { A = ws, B = corner });
                        if (!corner.ApproxEquals(pt)) p.Schematic.Wires.Add(new Wire { A = corner, B = pt });
                    });
                    bool endsOnSomething = PinAt(pt, 0.05) != null || p.Schematic.Wires.Count(x => x.A.ApproxEquals(pt) || x.B.ApproxEquals(pt) || Shape.PointSegDistance(pt, x.A, x.B) < 0.01) > 1
                                           || p.Schematic.Labels.Any(l => l.Position.ApproxEquals(pt));
                    wireStart = endsOnSomething || dbl ? null : pt;
                    break;
                }
            case SchTool.Label:
                {
                    var pin = PinAt(w, 1.2);
                    var pt = pin?.Position ?? (WireAt(w) is { } wire ? Shape.ClosestPointOnSeg(w, wire.A, wire.B).Snap(G / 2) : w.Snap(G / 2));
                    if (PromptLabel == null) break;
                    var suggestion = NetAt(pt) ?? "";
                    var name = await PromptLabel(suggestion.StartsWith("Net-(") ? "" : suggestion);
                    if (string.IsNullOrWhiteSpace(name)) break;
                    Session.Mutate(() =>
                    {
                        if (pin != null) EditOps.ConnectPins(p, name.Trim(), [(pin.Component.Ref, pin.Pin.Number)]);
                        else p.Schematic.Labels.Add(new NetLabel { Name = name.Trim(), Position = pt });
                    });
                    break;
                }
            case SchTool.NoConnect:
                {
                    var pin = PinAt(w, 1.2);
                    if (pin == null) break;
                    Session.Mutate(() =>
                    {
                        var existing = p.Schematic.NoConnects.FirstOrDefault(n => n.Position.ApproxEquals(pin.Position, 0.01));
                        if (existing != null) p.Schematic.NoConnects.Remove(existing);
                        else p.Schematic.NoConnects.Add(new NoConnectFlag { Position = pin.Position });
                    });
                    break;
                }
            case SchTool.Select:
                {
                    bool add = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    var pinHit = PinAt(w, 0.6);
                    var comp = ComponentAt(w);
                    var label = LabelAt(w);
                    if (label != null && (comp == null || pinHit != null))
                    {
                        if (!add) { selLabels.Clear(); selWires.Clear(); Session.ClearSelection(); }
                        selLabels.Add(label.Id);
                        Session.SetHighlightNet(label.Name);
                        StartDrag(w);
                        break;
                    }
                    if (comp != null)
                    {
                        if (dbl) { ComponentDoubleClicked?.Invoke(comp); break; }
                        if (!add) { selLabels.Clear(); selWires.Clear(); }
                        if (!Session.SelectedComponents.Contains(comp.Id)) Session.Select([comp], add);
                        if (pinHit != null) Session.SetHighlightNet(NetAt(pinHit.Position));
                        else Session.SetHighlightNet(null);
                        StartDrag(w);
                        break;
                    }
                    var wire = WireAt(w);
                    if (wire != null)
                    {
                        if (!add) { selWires.Clear(); selLabels.Clear(); Session.ClearSelection(); }
                        selWires.Add(wire.Id);
                        Session.SetHighlightNet(NetAt(w));
                        InvalidateVisual();
                        break;
                    }
                    if (!add) { selWires.Clear(); selLabels.Clear(); Session.ClearSelection(); }
                    bandStart = w;
                    e.Pointer.Capture(this);
                    break;
                }
        }
    }

    void StartDrag(Vec2 w)
    {
        dragStart = w; dragMoved = false;
        dragOrig = Session.Selection.ToDictionary(c => c.Id, c => c.SchPos);
        dragLabelOrig = Session.Project.Schematic.Labels.Where(l => selLabels.Contains(l.Id)).ToDictionary(l => l.Id, l => l.Position);
    }

    protected override void OnWorldPointerReleased(PointerReleasedEventArgs e, Vec2 w)
    {
        if (bandStart is { } bs)
        {
            var r = CRect.FromPoints(bs, w);
            var p = Session.Project;
            if (r.Width > 0.5 || r.Height > 0.5)
            {
                var hits = p.Components.Where(c => { var b = p.SchBounds(c); return r.Contains(new Vec2(b.MinX, b.MinY)) && r.Contains(new Vec2(b.MaxX, b.MaxY)); }).ToList();
                foreach (var wi in p.Schematic.Wires.Where(x => r.Contains(x.A) && r.Contains(x.B))) selWires.Add(wi.Id);
                foreach (var l in p.Schematic.Labels.Where(x => r.Contains(x.Position))) selLabels.Add(l.Id);
                Session.Select(hits, true);
            }
            bandStart = null;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }
        if (dragStart != null)
        {
            if (dragMoved) Session.MarkChanged();
            dragStart = null; dragOrig = null; dragLabelOrig = null; dragMoved = false;
        }
    }

    protected override void OnRightClick(Vec2 w)
    {
        if (Tool == SchTool.Wire && wireStart != null) { wireStart = null; InvalidateVisual(); return; }
        if (Tool != SchTool.Select) { SetTool(SchTool.Select); return; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var p = Session.Project;
        switch (e.Key)
        {
            case Key.Escape:
                if (Tool == SchTool.Wire && wireStart != null) wireStart = null;
                else SetTool(SchTool.Select);
                selWires.Clear(); selLabels.Clear();
                Session.ClearSelection();
                InvalidateVisual();
                e.Handled = true; break;
            case Key.Delete or Key.Back:
                DeleteSelection(); e.Handled = true; break;
            case Key.R:
                if (Tool == SchTool.Place) { placeRot = (placeRot + 90) % 360; InvalidateVisual(); }
                else if (Session.SelectedComponents.Count > 0)
                    Session.Mutate(() => { foreach (var c in Session.Selection.ToList()) EditOps.MoveSchematic(p, c, c.SchPos, c.SchRot + 90); });
                else if (selLabels.Count > 0)
                    Session.Mutate(() => { foreach (var l in p.Schematic.Labels.Where(l => selLabels.Contains(l.Id))) l.Rotation = (l.Rotation + 90) % 360; });
                e.Handled = true; break;
            case Key.M:
                if (Session.SelectedComponents.Count > 0)
                    Session.Mutate(() => { foreach (var c in Session.Selection.ToList()) EditOps.MoveSchematic(p, c, c.SchPos, null, !c.SchMirror); });
                e.Handled = true; break;
            case Key.W: SetTool(SchTool.Wire); e.Handled = true; break;
            case Key.L: SetTool(SchTool.Label); e.Handled = true; break;
            case Key.Q: SetTool(SchTool.NoConnect); e.Handled = true; break;
            case Key.S when e.KeyModifiers == KeyModifiers.None: SetTool(SchTool.Select); e.Handled = true; break;
            case Key.Space when Tool == SchTool.Wire: wireHorizontalFirst = !wireHorizontalFirst; InvalidateVisual(); e.Handled = true; break;
            case Key.F when e.KeyModifiers == KeyModifiers.None: ZoomToFit(); e.Handled = true; break;
        }
    }

    public void DeleteSelection()
    {
        var p = Session.Project;
        if (Session.SelectedComponents.Count == 0 && selWires.Count == 0 && selLabels.Count == 0) return;
        Session.Mutate(() =>
        {
            foreach (var c in Session.Selection.ToList()) EditOps.RemoveComponent(p, c);
            p.Schematic.Wires.RemoveAll(w => selWires.Contains(w.Id));
            p.Schematic.Labels.RemoveAll(l => selLabels.Contains(l.Id));
        });
        selWires.Clear(); selLabels.Clear();
        Session.ClearSelection();
    }
}
