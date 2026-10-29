using TraceMind.Core.Geometry;
using TraceMind.Core.Model;
using TraceMind.Core.Netlist;

namespace TraceMind.Core.Pcb;

public enum Severity { Error, Warning, Info }

public sealed record DrcViolation(Severity Severity, string Code, string Message, Vec2 Location, string? Ref = null)
{
    public override string ToString() => $"[{Severity}] {Code}: {Message} @ {Location}";
}

public sealed class DrcReport
{
    public List<DrcViolation> Violations { get; } = [];
    public int Errors => Violations.Count(v => v.Severity == Severity.Error);
    public int Warnings => Violations.Count(v => v.Severity == Severity.Warning);
    public int Unrouted { get; set; }
    public string Summary() => $"{Errors} errors, {Warnings} warnings, {Unrouted} unrouted connections";
}

public static class Drc
{
    public static DrcReport Run(Project project, NetlistResult? netlist = null)
    {
        netlist ??= NetlistBuilder.Build(project);
        var rules = project.Board.Rules;
        var report = new DrcReport();
        var items = Connectivity.CollectCopper(project, netlist);

        // ---- Clearance (spatial hash for speed) ----
        const double cell = 2.0;
        var buckets = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < items.Count; i++)
        {
            var b = items[i].Shape.Bounds.Inflate(rules.Clearance);
            for (int x = (int)Math.Floor(b.MinX / cell); x <= (int)Math.Floor(b.MaxX / cell); x++)
                for (int y = (int)Math.Floor(b.MinY / cell); y <= (int)Math.Floor(b.MaxY / cell); y++)
                {
                    if (!buckets.TryGetValue((x, y), out var l)) buckets[(x, y)] = l = [];
                    l.Add(i);
                }
        }
        var checkedPairs = new HashSet<(int, int)>();
        foreach (var bucket in buckets.Values)
            for (int a = 0; a < bucket.Count; a++)
                for (int b = a + 1; b < bucket.Count; b++)
                {
                    int i = Math.Min(bucket[a], bucket[b]), j = Math.Max(bucket[a], bucket[b]);
                    if (!checkedPairs.Add((i, j))) continue;
                    var A = items[i]; var B = items[j];
                    if (A.Net != "" && A.Net == B.Net) continue;
                    if (!A.SharesLayer(B)) continue;
                    if (A.Kind == CopperKind.Pad && B.Kind == CopperKind.Pad && A.Pad!.Component == B.Pad!.Component) continue;
                    // same-number pads of one component (e.g. tab) are fine
                    var clr = Math.Max(rules.ClearanceFor(A.Net), rules.ClearanceFor(B.Net));
                    if (!A.Shape.Bounds.Inflate(clr).Intersects(B.Shape.Bounds)) continue;
                    var d = Shape.Distance(A.Shape, B.Shape);
                    if (d < clr - 1e-4)
                    {
                        var loc = Midpoint(A, B);
                        var sev = Severity.Error;
                        var kind = d <= 0 ? "Short circuit" : "Clearance";
                        report.Violations.Add(new DrcViolation(sev, d <= 0 ? "short" : "clearance",
                            $"{kind}: {A.Describe()} [{NetLabel(A.Net)}] and {B.Describe()} [{NetLabel(B.Net)}] are {Math.Max(d, 0):0.###} mm apart (min {clr:0.###})", loc,
                            A.Pad?.Component.Ref ?? B.Pad?.Component.Ref));
                    }
                }

        // ---- Track / via rules ----
        foreach (var t in project.Board.Tracks)
        {
            if (t.Width < rules.MinTrackWidth - 1e-6)
                report.Violations.Add(new(Severity.Error, "track_width", $"Track width {t.Width:0.###} mm is below minimum {rules.MinTrackWidth:0.###} mm", (t.A + t.B) / 2));
            if (string.IsNullOrEmpty(t.Net))
                report.Violations.Add(new(Severity.Warning, "no_net", "Track has no net", (t.A + t.B) / 2));
        }
        foreach (var v in project.Board.Vias)
        {
            if (v.Drill < rules.MinDrill - 1e-6) report.Violations.Add(new(Severity.Error, "drill", $"Via drill {v.Drill:0.###} mm below minimum {rules.MinDrill:0.###}", v.Position));
            if ((v.Diameter - v.Drill) / 2 < 0.1 - 1e-6) report.Violations.Add(new(Severity.Error, "annular", $"Via annular ring {(v.Diameter - v.Drill) / 2:0.###} mm is below 0.1 mm", v.Position));
        }

        // ---- Board edge ----
        var outline = project.Board.Outline;
        if (outline.Count >= 3)
        {
            foreach (var it in items)
            {
                var pts = it.Shape.ToOutline(2);
                bool outside = pts.Any(p => !ProjectGeometry.PointInPolygon(outline, p));
                double dEdge = double.MaxValue;
                for (int k = 0; k < outline.Count; k++)
                {
                    var e = Shape.Capsule(outline[k], outline[(k + 1) % outline.Count], 0);
                    dEdge = Math.Min(dEdge, Shape.Distance(it.Shape, e));
                }
                if (outside || dEdge < rules.EdgeClearance - 1e-4)
                    report.Violations.Add(new(outside ? Severity.Error : Severity.Warning, "edge",
                        outside ? $"{it.Describe()} is outside the board outline" : $"{it.Describe()} is {Math.Max(0, dEdge):0.###} mm from the board edge (min {rules.EdgeClearance})",
                        it.Anchors[0], it.Pad?.Component.Ref));
            }
        }
        else report.Violations.Add(new(Severity.Error, "outline", "Board has no outline", Vec2.Zero));

        // ---- Placement ----
        var placed = project.Components.Where(c => c.Placed && !c.DoNotPopulate && project.Footprint(c) != null).ToList();
        foreach (var c in project.Components.Where(c => !c.Placed && !c.DoNotPopulate && project.Footprint(c) != null && project.Symbol(c)?.IsPower != true))
            report.Violations.Add(new(Severity.Warning, "unplaced", $"{c.Ref} is not placed on the board", Vec2.Zero, c.Ref));
        foreach (var c in project.Components.Where(c => !c.DoNotPopulate && string.IsNullOrEmpty(c.FootprintId) && project.Symbol(c) is { IsPower: false } s && s.Pins.Count > 0))
            report.Violations.Add(new(Severity.Warning, "no_footprint", $"{c.Ref} has no footprint assigned", Vec2.Zero, c.Ref));
        for (int i = 0; i < placed.Count; i++)
            for (int j = i + 1; j < placed.Count; j++)
            {
                if (placed[i].OnBottom != placed[j].OnBottom) continue;
                if (placed[i].IsWireLink() || placed[j].IsWireLink()) continue; // a link is a wire above the board
                var a = project.Courtyard(placed[i]); var b = project.Courtyard(placed[j]);
                if (a.Intersects(b))
                {
                    var ov = new Rect(Math.Max(a.MinX, b.MinX), Math.Max(a.MinY, b.MinY), Math.Min(a.MaxX, b.MaxX), Math.Min(a.MaxY, b.MaxY));
                    if (ov.Width > 0.05 && ov.Height > 0.05)
                        report.Violations.Add(new(Severity.Warning, "courtyard", $"Courtyards of {placed[i].Ref} and {placed[j].Ref} overlap", ov.Center, placed[i].Ref));
                }
            }

        // ---- Connectivity ----
        var conn = Connectivity.Analyze(project, netlist);
        foreach (var aw in conn.Airwires)
            report.Violations.Add(new(Severity.Error, "unrouted", $"Unrouted connection on net {aw.Net}", (aw.A + aw.B) / 2));
        report.Unrouted = conn.Airwires.Count;

        // ---- Schematic checks (ERC-lite) ----
        foreach (var p in netlist.UnconnectedPins)
        {
            if (p.Pin.Type is PinType.PowerIn)
                report.Violations.Add(new(Severity.Error, "erc_power", $"Power pin {p.Component.Ref}.{p.Pin.Number} ({p.Pin.Name}) is not connected", p.Position, p.Component.Ref));
            else
                report.Violations.Add(new(Severity.Info, "erc_unconnected", $"Pin {p.Component.Ref}.{p.Pin.Number} {p.Pin.Name} is unconnected (add a no-connect flag if intended)", p.Position, p.Component.Ref));
        }
        foreach (var net in netlist.Nets)
        {
            var drivers = net.Nodes.Count(n => n.Type is PinType.Output or PinType.PowerOut);
            if (drivers > 1 && net.Nodes.All(n => n.Type != PinType.OpenCollector))
                report.Violations.Add(new(Severity.Warning, "erc_drivers", $"Net {net.Name} has {drivers} output pins driving it", Vec2.Zero));
            if (net.Nodes.Count == 1 && net.Named)
                report.Violations.Add(new(Severity.Warning, "erc_single", $"Net {net.Name} only connects to one pin ({net.Nodes[0].Component.Ref}.{net.Nodes[0].Pin})", Vec2.Zero));
        }
        var dupRefs = project.Components.Where(c => !c.Ref.StartsWith('#')).GroupBy(c => c.Ref).Where(g => g.Count() > 1);
        foreach (var g in dupRefs) report.Violations.Add(new(Severity.Error, "dup_ref", $"Duplicate reference designator {g.Key}", Vec2.Zero, g.Key));
        return report;
    }

    static string NetLabel(string n) => n == "" ? "no net" : n;

    static Vec2 Midpoint(CopperItem a, CopperItem b)
    {
        Vec2 pa = a.Anchors[0], pb = b.Anchors[0];
        if (a.Kind == CopperKind.Track) pa = Shape.ClosestPointOnSeg(pb, a.Track!.A, a.Track.B);
        if (b.Kind == CopperKind.Track) pb = Shape.ClosestPointOnSeg(pa, b.Track!.A, b.Track.B);
        return (pa + pb) / 2;
    }
}
