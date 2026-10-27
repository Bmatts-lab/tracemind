using System.Globalization;
using System.IO.Compression;
using System.Text;
using TraceMind.Core.Geometry;
using TraceMind.Core.Model;
using TraceMind.Core.Netlist;

namespace TraceMind.Core.Export;

/// <summary>RS-274X (Gerber X2) writer. Coordinates are mm, format 4.6, Y axis flipped (Gerber is Y-up).</summary>
internal sealed class GerberWriter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    readonly StringBuilder header = new(), body = new();
    readonly Dictionary<string, int> apertures = [];
    int nextD = 10;
    string? currentAperture;
    bool dark = true;

    public GerberWriter(string fileFunction, string polarity = "Positive")
    {
        header.AppendLine("%TF.GenerationSoftware,TraceMind,TraceMind,1.0*%");
        header.AppendLine($"%TF.CreationDate,{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}*%");
        header.AppendLine($"%TF.FileFunction,{fileFunction}*%");
        header.AppendLine($"%TF.FilePolarity,{polarity}*%");
        header.AppendLine("%FSLAX46Y46*%");
        header.AppendLine("%MOMM*%");
        header.AppendLine("%LPD*%");
    }

    static string N(double v) => ((long)Math.Round(v * 1_000_000)).ToString(Inv);
    static string F(double v) => v.ToString("0.######", Inv);
    static string XY(Vec2 p) => $"X{N(p.X)}Y{N(-p.Y)}";

    int Ap(string def)
    {
        if (!apertures.TryGetValue(def, out var d)) { d = nextD++; apertures[def] = d; header.AppendLine($"%ADD{d}{def}*%"); }
        return d;
    }

    void Select(string def)
    {
        var d = Ap(def);
        var key = "D" + d;
        if (currentAperture != key) { body.AppendLine($"D{d}*"); currentAperture = key; }
    }

    public void Polarity(bool isDark)
    {
        if (dark == isDark) return;
        dark = isDark;
        body.AppendLine(isDark ? "%LPD*%" : "%LPC*%");
    }

    public void Stroke(Vec2 a, Vec2 b, double width)
    {
        Select($"C,{F(Math.Max(width, 0.001))}");
        body.AppendLine($"{XY(a)}D02*");
        body.AppendLine($"{XY(b)}D01*");
    }

    public void Polyline(IReadOnlyList<Vec2> pts, double width, bool closed = false)
    {
        if (pts.Count < 2) return;
        Select($"C,{F(Math.Max(width, 0.001))}");
        body.AppendLine($"{XY(pts[0])}D02*");
        for (int i = 1; i < pts.Count; i++) body.AppendLine($"{XY(pts[i])}D01*");
        if (closed) body.AppendLine($"{XY(pts[0])}D01*");
    }

    public void FlashCircle(Vec2 c, double d)
    {
        Select($"C,{F(d)}");
        body.AppendLine($"{XY(c)}D03*");
    }

    public void Region(IReadOnlyList<Vec2> pts)
    {
        if (pts.Count < 3) return;
        body.AppendLine("G36*");
        body.AppendLine($"{XY(pts[0])}D02*");
        for (int i = 1; i < pts.Count; i++) body.AppendLine($"{XY(pts[i])}D01*");
        body.AppendLine($"{XY(pts[0])}D01*");
        body.AppendLine("G37*");
    }

    /// <summary>Draw any convex shape: flashes for simple cases, regions otherwise.</summary>
    public void Shape(Shape s)
    {
        if (s.Core.Length == 1) { FlashCircle(s.Core[0], s.Radius * 2); return; }
        if (s.Core.Length == 2) { Stroke(s.Core[0], s.Core[1], s.Radius * 2); return; }
        Region(s.ToOutline(6));
    }

    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append(header);
        sb.Append(body);
        sb.AppendLine("M02*");
        return sb.ToString();
    }
}

public sealed record ExportResult(List<string> Files, string Folder, List<string> Warnings);

public static class GerberExporter
{
    /// <summary>Write a complete fabrication set (Gerbers, drill, BOM, CPL) into folder and a zip next to it.</summary>
    public static ExportResult Export(Project project, string folder, bool makeZip = true)
    {
        Directory.CreateDirectory(folder);
        var netlist = NetlistBuilder.Build(project);
        var files = new List<string>(); var warnings = new List<string>();
        var name = Sanitize(project.Name);
        var board = project.Board; var rules = board.Rules;
        var pads = project.AllPads().ToList();

        void Write(string suffix, string content) { var p = Path.Combine(folder, $"{name}-{suffix}"); File.WriteAllText(p, content); files.Add(p); }

        // ---- copper ----
        var copperLayers = board.CopperLayerList().ToList();
        for (int li = 0; li < copperLayers.Count; li++)
        {
            var layer = copperLayers[li];
            string pos = li == 0 ? "Top" : li == copperLayers.Count - 1 ? "Bot" : "Inr";
            var g = new GerberWriter($"Copper,L{li + 1},{pos}");
            DrawZones(project, netlist, g, layer, pads);
            g.Polarity(true);
            foreach (var t in board.Tracks.Where(t => t.Layer == layer)) g.Stroke(t.A, t.B, t.Width);
            foreach (var v in board.Vias) g.FlashCircle(v.Position, v.Diameter);
            foreach (var p in pads.Where(p => p.OnLayer(layer))) g.Shape(p.Shape);
            var ext = layer switch { Layer.FCu => "F_Cu.gtl", Layer.BCu => "B_Cu.gbl", Layer.In1Cu => "In1_Cu.g2", _ => "In2_Cu.g3" };
            Write(ext, g.Build());
        }

        // ---- solder mask (openings) ----
        foreach (var (layer, ext, func) in new[] { (Layer.FCu, "F_Mask.gts", "Soldermask,Top"), (Layer.BCu, "B_Mask.gbs", "Soldermask,Bot") })
        {
            var g = new GerberWriter(func, "Negative");
            foreach (var p in pads.Where(p => p.Pad.IsThrough || p.OnLayer(layer))) g.Shape(p.Shape.Inflate(rules.MaskExpansion));
            Write(ext, g.Build());
        }
        // ---- paste ----
        foreach (var (bottom, ext, func) in new[] { (false, "F_Paste.gtp", "Paste,Top"), (true, "B_Paste.gbp", "Paste,Bot") })
        {
            var g = new GerberWriter(func);
            foreach (var p in pads.Where(p => !p.Pad.IsThrough && p.OnBottom == bottom)) g.Shape(p.Shape);
            Write(ext, g.Build());
        }
        // ---- silkscreen ----
        foreach (var (bottom, ext, func) in new[] { (false, "F_Silkscreen.gto", "Legend,Top"), (true, "B_Silkscreen.gbo", "Legend,Bot") })
        {
            var g = new GerberWriter(func);
            foreach (var c in project.Components.Where(c => c.Placed && c.OnBottom == bottom && !c.DoNotPopulate))
            {
                var fp = project.Footprint(c);
                if (fp == null) continue;
                foreach (var gr in fp.Silk) DrawGraphic(g, gr, p => ProjectGeometry.PcbToWorld(c, p), rules.SilkWidth);
                var cy = project.Courtyard(c);
                var refPos = new Vec2(cy.Center.X, cy.MinY - 0.7);
                foreach (var stroke in StrokeFont.Layout(c.Ref, refPos, 1.0, 0, bottom)) g.Polyline(stroke, rules.SilkWidth);
            }
            foreach (var t in board.Texts.Where(t => t.Layer == (bottom ? Layer.BSilk : Layer.FSilk)))
                foreach (var stroke in StrokeFont.Layout(t.Text, t.Position, t.Size, t.Rotation, bottom)) g.Polyline(stroke, Math.Max(rules.SilkWidth, t.Size / 8));
            Write(ext, g.Build());
        }
        // ---- outline ----
        {
            var g = new GerberWriter("Profile,NP");
            g.Polyline(board.Outline, 0.1, closed: true);
            Write("Edge_Cuts.gm1", g.Build());
        }
        // ---- drills ----
        Write("PTH.drl", Excellon(pads.Where(p => p.Pad.IsThrough && p.Pad.Plated).Select(p => (p.Position, p.Pad.Drill))
            .Concat(board.Vias.Select(v => (v.Position, v.Drill))), plated: true));
        var npth = pads.Where(p => p.Pad.IsThrough && !p.Pad.Plated).Select(p => (p.Position, p.Pad.Drill))
            .Concat(board.Holes.Select(h => (h.Position, h.Diameter))).ToList();
        if (npth.Count > 0) Write("NPTH.drl", Excellon(npth, plated: false));

        // ---- assembly ----
        File.WriteAllText(Path.Combine(folder, $"{name}-BOM.csv"), BomExporter.Bom(project)); files.Add(Path.Combine(folder, $"{name}-BOM.csv"));
        File.WriteAllText(Path.Combine(folder, $"{name}-CPL.csv"), BomExporter.Cpl(project)); files.Add(Path.Combine(folder, $"{name}-CPL.csv"));

        if (board.Zones.Count > 0) warnings.Add("Copper pours are exported with solid connections (no thermal reliefs); isolated islands are not removed.");
        if (makeZip)
        {
            var zip = Path.Combine(folder, $"{name}-gerbers.zip");
            if (File.Exists(zip)) File.Delete(zip);
            using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
            foreach (var f in files.Where(f => !f.EndsWith(".csv"))) z.CreateEntryFromFile(f, Path.GetFileName(f));
            files.Add(zip);
        }
        return new ExportResult(files, folder, warnings);
    }

    static void DrawZones(Project project, NetlistResult netlist, GerberWriter g, Layer layer, List<PadInstance> pads)
    {
        var board = project.Board; var rules = board.Rules;
        foreach (var z in board.Zones.Where(z => z.Layer == layer && z.Outline.Count >= 3).OrderBy(z => z.Priority))
        {
            g.Polarity(true);
            g.Region(z.Outline);
            g.Polarity(false);
            double clr = Math.Max(z.Clearance, rules.Clearance);
            foreach (var t in board.Tracks.Where(t => t.Layer == layer && t.Net != z.Net)) g.Stroke(t.A, t.B, t.Width + 2 * clr);
            foreach (var v in board.Vias.Where(v => v.Net != z.Net)) g.FlashCircle(v.Position, v.Diameter + 2 * clr);
            foreach (var p in pads.Where(p => p.OnLayer(layer) || (p.Pad.IsThrough && !p.Pad.Plated)))
                if (netlist.NetOfPad(p.Component, p.Pad.Number) != z.Net || !p.Pad.Plated) g.Shape(p.Shape.Inflate(clr));
            foreach (var h in board.Holes) g.FlashCircle(h.Position, h.Diameter + 2 * clr);
            // keep copper off the board edge
            g.Polyline(board.Outline, 2 * rules.EdgeClearance, closed: true);
        }
        g.Polarity(true);
    }

    internal static void DrawGraphic(GerberWriter g, Graphic gr, Func<Vec2, Vec2> tf, double defaultWidth)
    {
        double w = Math.Max(gr.Width, defaultWidth * 0.8);
        switch (gr.Kind)
        {
            case GraphicKind.Line when gr.Points.Count >= 2: g.Stroke(tf(gr.Points[0]), tf(gr.Points[1]), w); break;
            case GraphicKind.Polyline or GraphicKind.Polygon: g.Polyline(gr.Points.Select(tf).ToList(), w, gr.Kind == GraphicKind.Polygon); break;
            case GraphicKind.Rect when gr.Points.Count >= 2:
                var a = gr.Points[0]; var b = gr.Points[1];
                g.Polyline([tf(a), tf(new(b.X, a.Y)), tf(b), tf(new(a.X, b.Y))], w, closed: true); break;
            case GraphicKind.Circle when gr.Points.Count >= 1:
                if (gr.Filled) g.FlashCircle(tf(gr.Points[0]), gr.Radius * 2 + w);
                else
                {
                    var pts = new List<Vec2>();
                    for (int i = 0; i < 36; i++) pts.Add(tf(gr.Points[0] + new Vec2(Math.Cos(i * Math.PI / 18), Math.Sin(i * Math.PI / 18)) * gr.Radius));
                    g.Polyline(pts, w, closed: true);
                }
                break;
        }
    }

    public static string Excellon(IEnumerable<(Vec2 pos, double drill)> holes, bool plated)
    {
        var inv = CultureInfo.InvariantCulture;
        var list = holes.Where(h => h.drill > 0).ToList();
        var tools = list.Select(h => Math.Round(h.drill, 3)).Distinct().OrderBy(d => d).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("M48");
        sb.AppendLine($"; DRILL file TraceMind, {(plated ? "plated" : "non-plated")} holes");
        sb.AppendLine($"; #@! TF.FileFunction,{(plated ? "Plated,1,2,PTH" : "NonPlated,1,2,NPTH")}");
        sb.AppendLine("FMAT,2");
        sb.AppendLine("METRIC");
        for (int i = 0; i < tools.Count; i++) sb.AppendLine($"T{i + 1}C{tools[i].ToString("0.000", inv)}");
        sb.AppendLine("%");
        sb.AppendLine("G90");
        sb.AppendLine("G05");
        for (int i = 0; i < tools.Count; i++)
        {
            sb.AppendLine($"T{i + 1}");
            foreach (var h in list.Where(h => Math.Round(h.drill, 3) == tools[i]))
                sb.AppendLine($"X{h.pos.X.ToString("0.###", inv)}Y{(-h.pos.Y).ToString("0.###", inv)}");
        }
        sb.AppendLine("M30");
        return sb.ToString();
    }

    public static string Sanitize(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var r = new string(s.Select(c => bad.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(r) ? "board" : r;
    }
}

public static class BomExporter
{
    static string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    public static string Bom(Project project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Comment,Designator,Footprint,Quantity,Manufacturer,MPN,LCSC Part #,Description,Datasheet");
        var rows = project.Components
            .Where(c => !c.DoNotPopulate && project.Symbol(c)?.IsPower != true && !string.IsNullOrEmpty(c.FootprintId) && !c.FootprintId.StartsWith("MountingHole") && !c.FootprintId.StartsWith("TestPoint"))
            .GroupBy(c => (c.Value, c.FootprintId, c.Mpn, c.SupplierPart))
            .OrderBy(g => g.Min(c => c.Ref));
        foreach (var g in rows)
        {
            var refs = string.Join(",", g.Select(c => c.Ref).OrderBy(r => new string(r.TakeWhile(char.IsLetter).ToArray())).ThenBy(r => int.TryParse(new string(r.SkipWhile(char.IsLetter).ToArray()), out var n) ? n : 0));
            var f = g.First();
            sb.AppendLine(string.Join(",", Q(f.Value), Q(refs), Q(f.FootprintId), g.Count(), Q(f.Manufacturer), Q(f.Mpn), Q(f.SupplierPart), Q(f.Description), Q(f.Datasheet)));
        }
        return sb.ToString();
    }

    public static string Cpl(Project project)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("Designator,Mid X,Mid Y,Layer,Rotation");
        foreach (var c in project.Components.Where(c => c.Placed && !c.DoNotPopulate && project.Footprint(c) is { } fp && fp.Pads.Any(p => !p.IsThrough || true)).OrderBy(c => c.Ref))
        {
            if (c.FootprintId.StartsWith("MountingHole")) continue;
            var rot = ((c.PcbRot % 360) + 360) % 360;
            sb.AppendLine($"{c.Ref},{c.PcbPos.X.ToString("0.###", inv)}mm,{(-c.PcbPos.Y).ToString("0.###", inv)}mm,{(c.OnBottom ? "Bottom" : "Top")},{rot.ToString("0.#", inv)}");
        }
        return sb.ToString();
    }
}
