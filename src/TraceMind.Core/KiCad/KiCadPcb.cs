using System.Text.RegularExpressions;
using TraceMind.Core.Geometry;
using TraceMind.Core.Library;
using TraceMind.Core.Model;
using TraceMind.Core.Netlist;
using static TraceMind.Core.KiCad.SNode;

namespace TraceMind.Core.KiCad;

/// <summary>Read / write KiCad 7/8 .kicad_pcb board files.</summary>
public static partial class KiCadPcb
{
    static SNode Uuid() => L("uuid", Guid.NewGuid().ToString());
    static SNode At(Vec2 p, double rot = double.NaN) => double.IsNaN(rot) || rot == 0 ? L("at", p.X, p.Y) : L("at", p.X, p.Y, rot);
    static SNode XY(string head, Vec2 p) => L(head, p.X, p.Y);
    static SNode Stroke(double w) => L("stroke", L("width", w), L("type", Sym("solid")));
    static SNode Layer(string l) => L("layer", l);
    static double NormRot(double r) { r %= 360; if (r < 0) r += 360; return Math.Round(r, 4); }

    // ======================= EXPORT =======================

    public static string Export(Project project)
    {
        var netlist = NetlistBuilder.Build(project);
        var board = project.Board;
        var netNames = new List<string> { "" };
        netNames.AddRange(netlist.Nets.Select(n => n.Name));
        netNames.AddRange(board.Tracks.Select(t => t.Net).Concat(board.Vias.Select(v => v.Net)).Concat(board.Zones.Select(z => z.Net)).Where(n => n != "" && !netNames.Contains(n)).Distinct());
        var netIndex = netNames.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        SNode NetRef(string n) => L("net", netIndex.TryGetValue(n, out var i) ? i : 0, n);

        var root = new List<SNode>
        {
            Sym("kicad_pcb"),
            L("version", Sym("20240108")), L("generator", "tracemind"), L("generator_version", "1.0"),
            L("general", L("thickness", board.Thickness), L("legacy_teardrops", Sym("no"))),
            L("paper", "A4"),
            L("layers",
                L("0", "F.Cu", Sym("signal")),
                board.CopperLayers >= 4 ? L("1", "In1.Cu", Sym("signal")) : null,
                board.CopperLayers >= 4 ? L("2", "In2.Cu", Sym("signal")) : null,
                L("31", "B.Cu", Sym("signal")),
                L("34", "B.Paste", Sym("user")), L("35", "F.Paste", Sym("user")),
                L("36", "B.SilkS", Sym("user"), "B.Silkscreen"), L("37", "F.SilkS", Sym("user"), "F.Silkscreen"),
                L("38", "B.Mask", Sym("user")), L("39", "F.Mask", Sym("user")),
                L("44", "Edge.Cuts", Sym("user")),
                L("46", "B.CrtYd", Sym("user"), "B.Courtyard"), L("47", "F.CrtYd", Sym("user"), "F.Courtyard"),
                L("48", "B.Fab", Sym("user")), L("49", "F.Fab", Sym("user"))),
            L("setup", L("pad_to_mask_clearance", board.Rules.MaskExpansion)),
        };
        foreach (var (n, i) in netIndex.OrderBy(x => x.Value)) root.Add(L("net", i, n));

        foreach (var c in project.Components.Where(c => c.Placed && !c.DoNotPopulate))
        {
            var fp = project.Footprint(c);
            if (fp == null) continue;
            string side = c.OnBottom ? "B" : "F";
            var items = new List<SNode>
            {
                Layer(side + ".Cu"), Uuid(), At(c.PcbPos, NormRot(c.PcbRot)),
                L("property", "Reference", c.Ref, At(new(0, fp.Courtyard.MinY - 0.8), NormRot(c.PcbRot)), Layer(side + ".SilkS"), Uuid(), L("effects", L("font", L("size", 1.0, 1.0), L("thickness", 0.15)), c.OnBottom ? L("justify", Sym("mirror")) : null)),
                L("property", "Value", c.Value, At(new(0, fp.Courtyard.MaxY + 0.8), NormRot(c.PcbRot)), Layer(side + ".Fab"), Uuid(), L("effects", L("font", L("size", 1.0, 1.0), L("thickness", 0.15)), c.OnBottom ? L("justify", Sym("mirror")) : null)),
            };
            if (!string.IsNullOrEmpty(c.Datasheet)) items.Add(L("property", "Datasheet", c.Datasheet, At(Vec2.Zero), Layer(side + ".Fab"), L("hide", Sym("yes")), Uuid()));
            if (!string.IsNullOrEmpty(c.Mpn)) items.Add(L("property", "MPN", c.Mpn, At(Vec2.Zero), Layer(side + ".Fab"), L("hide", Sym("yes")), Uuid()));
            if (!string.IsNullOrEmpty(c.SupplierPart)) items.Add(L("property", "LCSC", c.SupplierPart, At(Vec2.Zero), Layer(side + ".Fab"), L("hide", Sym("yes")), Uuid()));
            if (c.FootprintId.StartsWith("MountingHole")) items.Add(L("attr", Sym("exclude_from_pos_files"), Sym("exclude_from_bom")));
            else items.Add(L("attr", Sym(fp.Pads.All(p => p.IsThrough) ? "through_hole" : "smd")));

            Vec2 Local(Vec2 p) => c.OnBottom ? new(-p.X, p.Y) : p;
            foreach (var g in fp.Silk) items.AddRange(GraphicNodes(g, Local, side + ".SilkS"));
            foreach (var g in fp.Fab) items.AddRange(GraphicNodes(g, Local, side + ".Fab"));
            var cy = fp.Courtyard;
            if (!cy.IsEmpty) items.Add(L("fp_rect", XY("start", Local(new(cy.MinX, cy.MinY))), XY("end", Local(new(cy.MaxX, cy.MaxY))), Stroke(0.05), L("fill", Sym("none")), Layer(side + ".CrtYd"), Uuid()));

            foreach (var pad in fp.Pads)
            {
                double absRot = NormRot((c.OnBottom ? -pad.Rotation : pad.Rotation) + c.PcbRot);
                string type = pad.IsThrough ? (pad.Plated ? "thru_hole" : "np_thru_hole") : "smd";
                string shape = pad.Shape switch { PadShape.Circle => "circle", PadShape.Oval => "oval", PadShape.RoundRect => "roundrect", _ => "rect" };
                SNode layers = pad.IsThrough ? L("layers", "*.Cu", "*.Mask") : L("layers", side + ".Cu", side + ".Paste", side + ".Mask");
                var net = netlist.NetOfPad(c, pad.Number);
                items.Add(L("pad", pad.Number, Sym(type), Sym(shape), L("at", Local(pad.Position).X, Local(pad.Position).Y, absRot), L("size", pad.Size.X, pad.Size.Y),
                    pad.IsThrough ? L("drill", pad.Drill) : null, layers,
                    pad.Shape == PadShape.RoundRect ? L("roundrect_rratio", pad.RoundRectRatio) : null,
                    net != "" ? NetRef(net) : null, Uuid()));
            }
            var fpNode = new List<SNode> { Sym("footprint"), new("TraceMind:" + fp.Id, true) };
            fpNode.AddRange(items);
            root.Add(new SNode(fpNode));
        }

        for (int i = 0; i < board.Outline.Count; i++)
            root.Add(L("gr_line", XY("start", board.Outline[i]), XY("end", board.Outline[(i + 1) % board.Outline.Count]), Stroke(0.1), Layer("Edge.Cuts"), Uuid()));
        foreach (var t in board.Texts)
            root.Add(L("gr_text", t.Text, At(t.Position, t.Rotation), Layer(t.Layer.KiCadName()), Uuid(), L("effects", L("font", L("size", t.Size, t.Size), L("thickness", t.Size / 8)))));
        foreach (var t in board.Tracks)
            root.Add(L("segment", XY("start", t.A), XY("end", t.B), L("width", t.Width), Layer(t.Layer.KiCadName()), L("net", netIndex.GetValueOrDefault(t.Net)), Uuid()));
        foreach (var v in board.Vias)
            root.Add(L("via", At(v.Position), L("size", v.Diameter), L("drill", v.Drill), L("layers", "F.Cu", "B.Cu"), L("net", netIndex.GetValueOrDefault(v.Net)), Uuid()));
        foreach (var z in board.Zones)
            root.Add(L("zone", L("net", netIndex.GetValueOrDefault(z.Net)), L("net_name", z.Net), Layer(z.Layer.KiCadName()), Uuid(), L("hatch", Sym("edge"), 0.5),
                L("priority", z.Priority), L("connect_pads", L("clearance", z.Clearance)), L("min_thickness", 0.25),
                L("fill", Sym("yes"), L("thermal_gap", 0.5), L("thermal_bridge_width", 0.5)),
                L("polygon", new SNode(new[] { Sym("pts") }.Concat(z.Outline.Select(p => XY("xy", p)))))));
        foreach (var h in board.Holes)
            root.Add(L("footprint", "TraceMind:MountingHole", Layer("F.Cu"), Uuid(), At(h.Position),
                L("property", "Reference", "H", At(Vec2.Zero), Layer("F.SilkS"), L("hide", Sym("yes")), Uuid()),
                L("attr", Sym("exclude_from_pos_files"), Sym("exclude_from_bom")),
                L("pad", "", Sym("np_thru_hole"), Sym("circle"), At(Vec2.Zero), L("size", h.Diameter, h.Diameter), L("drill", h.Diameter), L("layers", "*.Cu", "*.Mask"), Uuid())));
        return new SNode(root).ToString() + "\n";
    }

    static IEnumerable<SNode> GraphicNodes(Graphic g, Func<Vec2, Vec2> tf, string layer)
    {
        double w = g.Width > 0 ? Math.Min(g.Width, 0.3) : 0.12;
        switch (g.Kind)
        {
            case GraphicKind.Line when g.Points.Count >= 2:
                yield return L("fp_line", XY("start", tf(g.Points[0])), XY("end", tf(g.Points[1])), Stroke(w), Layer(layer), Uuid()); break;
            case GraphicKind.Rect when g.Points.Count >= 2:
                yield return L("fp_rect", XY("start", tf(g.Points[0])), XY("end", tf(g.Points[1])), Stroke(w), L("fill", Sym(g.Filled ? "solid" : "none")), Layer(layer), Uuid()); break;
            case GraphicKind.Circle when g.Points.Count >= 1:
                yield return L("fp_circle", XY("center", tf(g.Points[0])), XY("end", tf(g.Points[0] + new Vec2(g.Radius, 0))), Stroke(w), L("fill", Sym(g.Filled ? "solid" : "none")), Layer(layer), Uuid()); break;
            case GraphicKind.Polyline or GraphicKind.Polygon:
                for (int i = 0; i + 1 < g.Points.Count; i++)
                    yield return L("fp_line", XY("start", tf(g.Points[i])), XY("end", tf(g.Points[i + 1])), Stroke(w), Layer(layer), Uuid());
                break;
        }
    }

    // ======================= IMPORT =======================

    public sealed record ImportResult(Project Project, List<string> Warnings);

    public static ImportResult Import(string text, string name = "Imported")
    {
        var root = SNode.Parse(text);
        if (root.Head != "kicad_pcb") throw new FormatException("Not a .kicad_pcb file");
        var warnings = new List<string>();
        var project = new Project { Name = name };
        var board = project.Board;
        board.Outline = [];
        board.Thickness = root.First("general")?.First("thickness")?.Num(1, 1.6) ?? 1.6;
        var netNames = new Dictionary<int, string>();
        foreach (var n in root.All("net")) netNames[(int)n.Num(1)] = n.Str(2) ?? "";
        string NetOf(SNode? node)
        {
            var nn = node?.First("net");
            if (nn == null) return "";
            if (nn.Count >= 3) return nn.Str(2) ?? "";
            var s = nn.Str(1) ?? "";
            return int.TryParse(s, out var id) ? netNames.GetValueOrDefault(id, "") : s;
        }
        int copper = root.First("layers")?.Children!.Count(c => c.IsList && (c.Str(1) ?? "").EndsWith(".Cu")) ?? 2;
        board.CopperLayers = copper >= 4 ? 4 : 2;

        // ---- footprints ----
        var padNets = new Dictionary<(Component, string), string>();
        int fpIndex = 0;
        foreach (var fpn in root.All("footprint").Concat(root.All("module")))
        {
            fpIndex++;
            var libName = fpn.Str(1) ?? $"FP{fpIndex}";
            var shortName = libName.Contains(':') ? libName[(libName.IndexOf(':') + 1)..] : libName;
            var at = fpn.First("at");
            var pos = new Vec2(at?.Num(1) ?? 0, at?.Num(2) ?? 0);
            double rot = at?.Num(3) ?? 0;
            if (libName == "TraceMind:MountingHole")
            {
                var hp = fpn.First("pad");
                board.Holes.Add(new MountingHole { Position = pos, Diameter = hp?.First("drill")?.Num(1, 3.2) ?? 3.2 });
                continue;
            }
            bool bottom = (fpn.Get("layer") ?? "F.Cu").StartsWith("B.");
            string reference = "", value = "", datasheet = "", mpn = "", lcsc = "";
            foreach (var p in fpn.All("property"))
            {
                var key = p.Str(1) ?? ""; var val = p.Str(2) ?? "";
                if (key == "Reference") reference = val; else if (key == "Value") value = val; else if (key == "Datasheet") datasheet = val;
                else if (key.Equals("MPN", StringComparison.OrdinalIgnoreCase)) mpn = val; else if (key.Equals("LCSC", StringComparison.OrdinalIgnoreCase)) lcsc = val;
            }
            foreach (var t in fpn.All("fp_text"))
            {
                if (t.Str(1) == "reference") reference = t.Str(2) ?? reference;
                if (t.Str(1) == "value") value = t.Str(2) ?? value;
            }
            if (string.IsNullOrEmpty(reference)) reference = "U" + fpIndex;

            Vec2 Local(SNode? n, int i = 1)
            {
                var v = new Vec2(n?.Num(i) ?? 0, n?.Num(i + 1) ?? 0);
                return bottom ? new(-v.X, v.Y) : v;
            }
            var def = new FootprintDef { Id = $"{shortName}__{reference}", Name = shortName, Description = $"Imported from KiCad ({libName})" };
            var comp = new Component { Ref = reference, Value = value, Datasheet = datasheet, Mpn = mpn, SupplierPart = lcsc, PcbPos = pos, PcbRot = rot, OnBottom = bottom, Placed = true, FootprintId = def.Id };
            foreach (var pn in fpn.All("pad"))
            {
                var num = pn.Str(1) ?? ""; var type = pn.Str(2) ?? "smd"; var shape = pn.Str(3) ?? "rect";
                var pat = pn.First("at");
                double absRot = pat?.Num(3) ?? 0;
                var size = pn.First("size");
                var drillNode = pn.First("drill");
                double drill = 0;
                if (drillNode != null) drill = drillNode.Str(1) == "oval" ? Math.Min(drillNode.Num(2), drillNode.Num(3, drillNode.Num(2))) : drillNode.Num(1);
                if (type == "connect") continue;
                var pad = new PadDef
                {
                    Number = num, Position = Local(pat), Size = new(size?.Num(1, 1) ?? 1, size?.Num(2, size?.Num(1, 1) ?? 1) ?? 1),
                    Drill = type is "thru_hole" or "np_thru_hole" ? Math.Max(drill, 0.1) : 0, Plated = type != "np_thru_hole",
                    Shape = shape switch { "circle" => PadShape.Circle, "oval" => PadShape.Oval, "roundrect" => PadShape.RoundRect, _ => PadShape.Rect },
                    Rotation = NormRot(bottom ? rot - absRot : absRot - rot),
                    RoundRectRatio = pn.First("roundrect_rratio")?.Num(1, 0.25) ?? 0.25,
                };
                if (shape is "custom" or "trapezoid") warnings.Add($"{reference} pad {num}: {shape} pad approximated as rectangle");
                def.Pads.Add(pad);
                var net = NetOf(pn);
                if (net != "" && num != "" && !net.StartsWith("unconnected-")) padNets[(comp, num)] = net;
            }
            foreach (var g in fpn.Children!.Where(c => c.IsList && c.Head.StartsWith("fp_")))
            {
                var layer = g.Get("layer") ?? "";
                bool silk = layer.EndsWith(".SilkS") || layer.EndsWith(".Silkscreen");
                bool crt = layer.EndsWith(".CrtYd") || layer.EndsWith(".Courtyard");
                var w = g.First("stroke")?.First("width")?.Num(1) ?? g.First("width")?.Num(1) ?? 0.12;
                Graphic? gr = g.Head switch
                {
                    "fp_line" => new Graphic { Kind = GraphicKind.Line, Points = [Local(g.First("start")), Local(g.First("end"))], Width = w },
                    "fp_rect" => new Graphic { Kind = GraphicKind.Rect, Points = [Local(g.First("start")), Local(g.First("end"))], Width = w },
                    "fp_circle" => new Graphic { Kind = GraphicKind.Circle, Points = [Local(g.First("center"))], Radius = Local(g.First("center")).DistanceTo(Local(g.First("end"))), Width = w },
                    _ => null,
                };
                if (gr == null) continue;
                if (silk) def.Silk.Add(gr);
                else if (crt) def.Courtyard = gr.Points.Aggregate(def.Courtyard.IsEmpty ? Rect.Empty : def.Courtyard, (r, p) => r.Include(p));
                else if (layer.EndsWith(".Fab")) def.Fab.Add(gr);
            }
            if (def.Courtyard.IsEmpty || def.Courtyard.Width <= 0) def.Courtyard = def.PadBounds().Inflate(0.25);
            project.Footprints[def.Id] = def;
            project.Components.Add(comp);
        }

        // ---- board graphics ----
        var edgeSegs = new List<(Vec2 a, Vec2 b)>();
        foreach (var g in root.Children!.Where(c => c.IsList && c.Head.StartsWith("gr_")))
        {
            var layer = g.Get("layer") ?? "";
            if (layer == "Edge.Cuts")
            {
                switch (g.Head)
                {
                    case "gr_line": edgeSegs.Add((P(g.First("start")), P(g.First("end")))); break;
                    case "gr_rect":
                        var s = P(g.First("start")); var e = P(g.First("end"));
                        edgeSegs.Add((s, new(e.X, s.Y))); edgeSegs.Add((new(e.X, s.Y), e)); edgeSegs.Add((e, new(s.X, e.Y))); edgeSegs.Add((new(s.X, e.Y), s)); break;
                    case "gr_arc":
                        var a0 = P(g.First("start")); var am = P(g.First("mid") ?? g.First("start")); var a1 = P(g.First("end"));
                        var pts = ArcPoints(a0, am, a1);
                        for (int i = 0; i + 1 < pts.Count; i++) edgeSegs.Add((pts[i], pts[i + 1]));
                        break;
                    case "gr_circle":
                        var c = P(g.First("center")); var r = c.DistanceTo(P(g.First("end")));
                        for (int i = 0; i < 48; i++) edgeSegs.Add((c + new Vec2(Math.Cos(i * Math.PI / 24), Math.Sin(i * Math.PI / 24)) * r, c + new Vec2(Math.Cos((i + 1) * Math.PI / 24), Math.Sin((i + 1) * Math.PI / 24)) * r));
                        break;
                    case "gr_poly":
                        var pp = g.First("pts")?.All("xy").Select(P).ToList() ?? [];
                        for (int i = 0; i < pp.Count; i++) edgeSegs.Add((pp[i], pp[(i + 1) % pp.Count]));
                        break;
                }
            }
            else if (g.Head == "gr_text" && Layers.FromKiCad(layer) is { } tl && (tl == Model.Layer.FSilk || tl == Model.Layer.BSilk))
            {
                var at = g.First("at");
                board.Texts.Add(new BoardText { Text = g.Str(1) ?? "", Position = P(at), Rotation = at?.Num(3) ?? 0, Layer = tl, Size = g.First("effects")?.First("font")?.First("size")?.Num(1, 1) ?? 1 });
            }
        }
        board.Outline = ChainOutline(edgeSegs);
        if (board.Outline.Count < 3)
        {
            warnings.Add("No closed board outline found — using the footprint bounding box.");
            var r = Rect.Empty;
            foreach (var c in project.Components) r = r.Union(project.Courtyard(c));
            r = r.IsEmpty ? new Rect(0, 0, 50, 50) : r.Inflate(3);
            board.Outline = [new(r.MinX, r.MinY), new(r.MaxX, r.MinY), new(r.MaxX, r.MaxY), new(r.MinX, r.MaxY)];
        }

        // ---- copper ----
        foreach (var s in root.All("segment"))
        {
            var layer = Layers.FromKiCad(s.Get("layer") ?? "F.Cu") ?? Model.Layer.FCu;
            board.Tracks.Add(new Track { A = P(s.First("start")), B = P(s.First("end")), Width = s.First("width")?.Num(1, 0.25) ?? 0.25, Layer = layer, Net = NetOf(s) });
        }
        foreach (var a in root.All("arc"))
        {
            var layer = Layers.FromKiCad(a.Get("layer") ?? "F.Cu") ?? Model.Layer.FCu;
            var pts = ArcPoints(P(a.First("start")), P(a.First("mid")), P(a.First("end")));
            for (int i = 0; i + 1 < pts.Count; i++)
                board.Tracks.Add(new Track { A = pts[i], B = pts[i + 1], Width = a.First("width")?.Num(1, 0.25) ?? 0.25, Layer = layer, Net = NetOf(a) });
        }
        foreach (var v in root.All("via"))
            board.Vias.Add(new Via { Position = P(v.First("at")), Diameter = v.First("size")?.Num(1, 0.6) ?? 0.6, Drill = v.First("drill")?.Num(1, 0.3) ?? 0.3, Net = NetOf(v) });
        foreach (var z in root.All("zone"))
        {
            var pts = z.First("polygon")?.First("pts")?.All("xy").Select(P).ToList();
            if (pts == null || pts.Count < 3) continue;
            var lname = z.Get("layer") ?? z.First("layers")?.Str(1) ?? "F.Cu";
            board.Zones.Add(new Zone { Net = z.Get("net_name") ?? NetOf(z), Layer = Layers.FromKiCad(lname) ?? Model.Layer.FCu, Outline = pts, Clearance = z.First("connect_pads")?.First("clearance")?.Num(1, 0.3) ?? 0.3 });
        }

        // ---- design rules from the most common track widths ----
        if (board.Tracks.Count > 0)
            board.Rules.TrackWidth = board.Tracks.GroupBy(t => Math.Round(t.Width, 3)).OrderByDescending(g => g.Count()).First().Key;
        if (board.Vias.Count > 0)
        {
            var common = board.Vias.GroupBy(v => (Math.Round(v.Diameter, 3), Math.Round(v.Drill, 3))).OrderByDescending(g => g.Count()).First().Key;
            board.Rules.ViaDiameter = common.Item1; board.Rules.ViaDrill = common.Item2;
        }

        // ---- synthesize schematic from pad nets ----
        SchematicSynth.FromPadNets(project, padNets);
        return new ImportResult(project, warnings);

        static Vec2 P(SNode? n) => new(n?.Num(1) ?? 0, n?.Num(2) ?? 0);
    }

    static List<Vec2> ArcPoints(Vec2 a, Vec2 m, Vec2 b, int segs = 12)
    {
        // circle through 3 points
        double ax = a.X, ay = a.Y, bx = m.X, by = m.Y, cx = b.X, cy = b.Y;
        double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
        if (Math.Abs(d) < 1e-9) return [a, b];
        double ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / d;
        double uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / d;
        var c = new Vec2(ux, uy); double r = c.DistanceTo(a);
        double t0 = Math.Atan2(a.Y - c.Y, a.X - c.X), tm = Math.Atan2(m.Y - c.Y, m.X - c.X), t1 = Math.Atan2(b.Y - c.Y, b.X - c.X);
        double Norm(double x) { while (x < 0) x += 2 * Math.PI; while (x >= 2 * Math.PI) x -= 2 * Math.PI; return x; }
        double sweep = Norm(t1 - t0); if (Norm(tm - t0) > sweep) sweep -= 2 * Math.PI;
        var pts = new List<Vec2>();
        for (int i = 0; i <= segs; i++) { var t = t0 + sweep * i / segs; pts.Add(c + new Vec2(Math.Cos(t), Math.Sin(t)) * r); }
        return pts;
    }

    /// <summary>Join loose edge segments into the largest closed polygon.</summary>
    public static List<Vec2> ChainOutline(List<(Vec2 a, Vec2 b)> segs)
    {
        var remaining = new List<(Vec2 a, Vec2 b)>(segs);
        var best = new List<Vec2>();
        while (remaining.Count > 0)
        {
            var poly = new List<Vec2> { remaining[0].a, remaining[0].b };
            remaining.RemoveAt(0);
            bool progress = true;
            while (progress && !poly[^1].ApproxEquals(poly[0], 0.01))
            {
                progress = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    var (a, b) = remaining[i];
                    if (a.ApproxEquals(poly[^1], 0.01)) { poly.Add(b); remaining.RemoveAt(i); progress = true; break; }
                    if (b.ApproxEquals(poly[^1], 0.01)) { poly.Add(a); remaining.RemoveAt(i); progress = true; break; }
                }
            }
            if (poly[^1].ApproxEquals(poly[0], 0.01)) poly.RemoveAt(poly.Count - 1);
            if (Area(poly) > Area(best)) best = poly;
        }
        return best;
    }

    static double Area(List<Vec2> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++) a += p[i].Cross(p[(i + 1) % p.Count]);
        return Math.Abs(a / 2);
    }

    // ---------- footprint name mapping to KiCad standard libraries ----------

    public static string ToKiCadLibName(string id)
    {
        Match m;
        if ((m = Regex.Match(id, @"^(R|C|L)_(\d{4})$")).Success)
        {
            var metric = m.Groups[2].Value switch { "0201" => "0603", "0402" => "1005", "0603" => "1608", "0805" => "2012", "1206" => "3216", "1210" => "3225", "1812" => "4532", "2010" => "5025", _ => "6332" };
            var lib = m.Groups[1].Value switch { "R" => "Resistor_SMD", "C" => "Capacitor_SMD", _ => "Inductor_SMD" };
            return $"{lib}:{m.Groups[1].Value}_{m.Groups[2].Value}_{metric}Metric";
        }
        if ((m = Regex.Match(id, @"^LED_(\d{4})$")).Success)
        {
            var metric = m.Groups[1].Value switch { "0402" => "1005", "0603" => "1608", "0805" => "2012", _ => "3216" };
            return $"LED_SMD:LED_{m.Groups[1].Value}_{metric}Metric";
        }
        if ((m = Regex.Match(id, @"^SOIC-(\d+)$")).Success) return $"Package_SO:SOIC-{m.Groups[1].Value}_3.9x{(int.Parse(m.Groups[1].Value) / 2 * 1.27 - 0.2):0.#}mm_P1.27mm";
        if ((m = Regex.Match(id, @"^PinHeader_(\d)x(\d+)_P2\.54$")).Success) return $"Connector_PinHeader_2.54mm:PinHeader_{m.Groups[1].Value}x{m.Groups[2].Value}_P2.54mm_Vertical";
        if ((m = Regex.Match(id, @"^DIP-(\d+)$")).Success) return $"Package_DIP:DIP-{m.Groups[1].Value}_W7.62mm";
        return id switch
        {
            "SOT-23" => "Package_TO_SOT_SMD:SOT-23", "SOT-23-5" => "Package_TO_SOT_SMD:SOT-23-5", "SOT-23-6" => "Package_TO_SOT_SMD:SOT-23-6",
            "SOT-223" => "Package_TO_SOT_SMD:SOT-223-3_TabPin2", "D_SOD-123" => "Diode_SMD:D_SOD-123", "D_SMA" => "Diode_SMD:D_SMA",
            "SW_Tactile_6x6" => "Button_Switch_THT:SW_PUSH_6mm", "Crystal_HC49-4H" => "Crystal:Crystal_HC49-4H_Vertical",
            "MountingHole_3.2mm" => "MountingHole:MountingHole_3.2mm_M3", _ => "TraceMind:" + id,
        };
    }

    /// <summary>Map a KiCad library footprint name to a TraceMind parametric footprint id (best effort).</summary>
    public static string? FromKiCadLibName(string name)
    {
        var n = name.Contains(':') ? name[(name.IndexOf(':') + 1)..] : name;
        Match m;
        if ((m = Regex.Match(n, @"^(R|C|L|LED|D)_(\d{4})_\d{4}Metric")).Success) return $"{m.Groups[1].Value}_{m.Groups[2].Value}";
        if ((m = Regex.Match(n, @"^(SOIC|TSSOP|MSOP|SSOP)-(\d+)_")).Success) return $"{m.Groups[1].Value}-{m.Groups[2].Value}";
        if ((m = Regex.Match(n, @"^(?:PinHeader|PinSocket)_(\d)x(\d+)_P(\d+\.\d+)mm")).Success) return $"PinHeader_{m.Groups[1].Value}x{m.Groups[2].Value}_P{m.Groups[3].Value}";
        if ((m = Regex.Match(n, @"^DIP-(\d+)_W7\.62mm")).Success) return $"DIP-{m.Groups[1].Value}";
        if ((m = Regex.Match(n, @"^(?:V?QFN)-(\d+)-1EP_(\d+(?:\.\d+)?)x(\d+(?:\.\d+)?)mm_P(\d+(?:\.\d+)?)mm")).Success) return $"QFN-{m.Groups[1].Value}_{m.Groups[2].Value}x{m.Groups[3].Value}_P{m.Groups[4].Value}";
        if ((m = Regex.Match(n, @"^(TQFP|LQFP)-(\d+)_(\d+(?:\.\d+)?)x(\d+(?:\.\d+)?)mm_P(\d+(?:\.\d+)?)mm")).Success) return $"{m.Groups[1].Value}-{m.Groups[2].Value}_{m.Groups[3].Value}x{m.Groups[4].Value}_P{m.Groups[5].Value}";
        if (n.StartsWith("SOT-23-5")) return "SOT-23-5";
        if (n.StartsWith("SOT-23-6")) return "SOT-23-6";
        if (n.StartsWith("SOT-23")) return "SOT-23";
        if (n.StartsWith("SOT-223")) return "SOT-223";
        if (n.StartsWith("D_SOD-123")) return "D_SOD-123";
        if (n.StartsWith("D_SOD-323")) return "D_SOD-323";
        if (n.StartsWith("D_SMA")) return "D_SMA";
        if (n.StartsWith("D_SMB")) return "D_SMB";
        if (n.StartsWith("SW_PUSH_6mm")) return "SW_Tactile_6x6";
        if (n.StartsWith("MountingHole_3.2mm")) return "MountingHole_3.2mm";
        return FootprintLibrary.Get(n) != null ? n : null;
    }
}
