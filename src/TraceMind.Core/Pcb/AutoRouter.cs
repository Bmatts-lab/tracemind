using System.Diagnostics;
using TraceMind.Core.Geometry;
using TraceMind.Core.Model;
using TraceMind.Core.Netlist;

namespace TraceMind.Core.Pcb;

public sealed class RouterOptions
{
    /// <summary>Routing grid in mm. 0 = choose automatically from board size and pad pitch.</summary>
    public double GridStep { get; set; }
    public int MaxPasses { get; set; } = 4;
    /// <summary>Cost of a via, in mm of equivalent track length.</summary>
    public double ViaCost { get; set; } = 4.0;
    /// <summary>Extra cost per direction change (mm equivalent).</summary>
    public double TurnCost { get; set; } = 0.6;
    /// <summary>Extra cost factor for running against the layer's preferred direction (top = horizontal, bottom = vertical).</summary>
    public double WrongWayFactor { get; set; } = 0.25;
    public bool AllowVias { get; set; } = true;
    /// <summary>Only route these nets (null = all).</summary>
    public List<string>? Nets { get; set; }
    /// <summary>Nets to route first, in this order.</summary>
    public List<string>? Priority { get; set; }
    /// <summary>Restrict a net to one layer (e.g. keep a sensitive net on top).</summary>
    public Dictionary<string, Layer> NetLayer { get; set; } = [];
    /// <summary>Nets the router should leave alone (e.g. GND when a ground pour handles it).</summary>
    public List<string> SkipNets { get; set; } = [];
    /// <summary>Remove previous autorouted copper before routing.</summary>
    public bool RipUpAutoRouted { get; set; } = true;
    public int TimeLimitMs { get; set; } = 60_000;
    /// <summary>Allow tracks to narrow in tight spots. Per-net minimum width; nets not listed may narrow down to the default track width.</summary>
    public Dictionary<string, double> NeckDown { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AllowNeckDown { get; set; } = true;
    /// <summary>Cost multiplier for running a net narrower than its class width.</summary>
    public double NeckDownCost { get; set; } = 2.5;
    /// <summary>When a net can't be routed, insert wire links (jumpers) across the blocking tracks and keep going. Meant for single-layer boards.</summary>
    public bool WireLinks { get; set; }
    /// <summary>Allowed wire-link pad spacings in mm (default 0.3", 0.4", 0.5").</summary>
    public double[] LinkSpacings { get; set; } = [7.62, 10.16, 12.7];
    public int MaxLinksPerNet { get; set; } = 6;
}

/// <summary>One place where a failed connection has to cross existing copper.</summary>
public sealed record RouteCrossing(Vec2 Entry, Vec2 Exit, string BlockedBy, double Length)
{
    public Vec2 Middle => (Entry + Exit) / 2;
}

public sealed class RouteFailure
{
    public string Net { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public List<RouteCrossing> Crossings { get; } = [];
    public string Note { get; set; } = "";
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder($"{Net}: can't connect {From} → {To}.");
        if (Crossings.Count > 0)
            sb.Append(" Shortest way through needs to cross: " + string.Join("; ", Crossings.Select(c =>
                $"{c.BlockedBy} between ({c.Entry.X:0.##},{c.Entry.Y:0.##}) and ({c.Exit.X:0.##},{c.Exit.Y:0.##}), {c.Length:0.##} mm wide")));
        if (Note != "") sb.Append(' ').Append(Note);
        return sb.ToString();
    }
}

public sealed class RouteResult
{
    public int NetsRouted { get; set; }
    public int NetsTotal { get; set; }
    public List<string> FailedNets { get; set; } = [];
    public int ViasAdded { get; set; }
    public double TrackLength { get; set; }
    public int Passes { get; set; }
    public TimeSpan Elapsed { get; set; }
    public List<RouteFailure> Failures { get; set; } = [];
    /// <summary>References of wire links the router inserted.</summary>
    public List<string> LinksAdded { get; set; } = [];
    public string Summary() => FailedNets.Count == 0
        ? $"Routed all {NetsTotal} nets in {Elapsed.TotalSeconds:0.0}s — {TrackLength:0} mm of track, {ViasAdded} vias ({Passes} pass{(Passes == 1 ? "" : "es")})."
        : $"Routed {NetsRouted}/{NetsTotal} nets in {Elapsed.TotalSeconds:0.0}s. Failed: {string.Join(", ", FailedNets)}. {TrackLength:0} mm of track, {ViasAdded} vias.";
    public string Details() => (LinksAdded.Count > 0 ? $"Inserted wire links: {string.Join(", ", LinksAdded)}. " : "")
        + string.Join("\n", Failures.Select(f => f.ToString()));
}

/// <summary>
/// Grid-based multi-layer maze router (A*), with clearance-aware obstacle maps,
/// via support, preferred layer directions and rip-up-and-reroute passes.
/// </summary>
public sealed class AutoRouter
{
    readonly Project project;
    readonly RouterOptions opt;
    readonly IProgress<string>? progress;
    readonly CancellationToken ct;

    // grid
    double step, ox, oy; int cols, rows, nl; Layer[] layers = [];
    bool[] outside = [];
    // per-net working buffers
    bool[] blocked = [], viaBlocked = [], wideBlocked = [];
    bool neckActive;
    // obstacle ownership (for diagnostics / wire links): index into obstacleItems, -1 = edge / not crossable
    int[] owner = [];
    readonly List<CopperItem> obstacleItems = [];
    NetlistResult netlist = null!;
    RouteResult current = null!;
    float[] g = []; int[] cameFrom = []; int[] stamp = []; int curStamp; byte[] dirOf = [];
    readonly Dictionary<double, bool[]> edgeCache = [];
    readonly List<string> linksAdded = [];

    static readonly int[] DX = [1, 1, 0, -1, -1, -1, 0, 1];
    static readonly int[] DY = [0, 1, 1, 1, 0, -1, -1, -1];

    public AutoRouter(Project project, RouterOptions? options = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        this.project = project; opt = options ?? new RouterOptions(); this.progress = progress; this.ct = ct;
    }

    public RouteResult Route()
    {
        var sw = Stopwatch.StartNew();
        var board = project.Board;
        var rules = board.Rules;
        netlist = NetlistBuilder.Build(project);
        SetupGrid(netlist);

        if (opt.RipUpAutoRouted)
        {
            board.Tracks.RemoveAll(t => t.Auto && !t.Locked && (opt.Nets == null || opt.Nets.Contains(t.Net)));
            board.Vias.RemoveAll(v => v.Auto && !v.Locked && (opt.Nets == null || opt.Nets.Contains(v.Net)));
        }
        var manualTracks = board.Tracks.ToList(); var manualVias = board.Vias.ToList();

        var allNets = netlist.Nets.Where(n => n.Nodes.Count >= 2)
            .Select(n => n.Name)
            .Where(n => opt.Nets == null || opt.Nets.Contains(n))
            .Where(n => !opt.SkipNets.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // Initial ordering: priority list, then small nets first by bounding box size.
        var padsByNet = project.AllPads().GroupBy(p => netlist.NetOfPad(p.Component, p.Pad.Number)).ToDictionary(g => g.Key, g => g.ToList());
        double Span(string net)
        {
            if (!padsByNet.TryGetValue(net, out var ps) || ps.Count == 0) return 0;
            var r = Rect.Empty; foreach (var p in ps) r = r.Include(p.Position);
            return r.Width + r.Height + ps.Count * 2;
        }
        var order = allNets.OrderBy(n => opt.Priority?.IndexOf(n) is int i && i >= 0 ? i : int.MaxValue).ThenBy(Span).ToList();

        linksAdded.Clear();
        RouteResult? best = null; List<Track>? bestTracks = null; List<Via>? bestVias = null;
        var failedLast = new List<string>();
        int pass;
        for (pass = 1; pass <= Math.Max(1, opt.MaxPasses); pass++)
        {
            ct.ThrowIfCancellationRequested();
            board.Tracks = [.. manualTracks]; board.Vias = [.. manualVias];
            if (pass > 1)
            {
                // Failed nets go first; nets that blocked them get a fresh chance later.
                order = failedLast.Concat(order.Where(n => !failedLast.Contains(n))).ToList();
            }
            var res = new RouteResult { NetsTotal = order.Count, Passes = pass };
            current = res;
            for (int i = 0; i < order.Count; i++)
            {
                if (sw.ElapsedMilliseconds > opt.TimeLimitMs) { res.FailedNets.AddRange(order.Skip(i)); break; }
                ct.ThrowIfCancellationRequested();
                var net = order[i];
                progress?.Report($"Pass {pass}: routing {net} ({i + 1}/{order.Count})");
                if (RouteNetWithLinks(net)) res.NetsRouted++; else res.FailedNets.Add(net);
            }
            res.ViasAdded = board.Vias.Count(v => v.Auto);
            res.TrackLength = board.Tracks.Where(t => t.Auto).Sum(t => t.A.DistanceTo(t.B));
            if (best == null || res.FailedNets.Count < best.FailedNets.Count ||
                (res.FailedNets.Count == best.FailedNets.Count && res.TrackLength + res.ViasAdded * opt.ViaCost < best.TrackLength + best.ViasAdded * opt.ViaCost))
            {
                best = res; bestTracks = [.. board.Tracks]; bestVias = [.. board.Vias];
            }
            if (res.FailedNets.Count == 0 || sw.ElapsedMilliseconds > opt.TimeLimitMs) break;
            failedLast = res.FailedNets.ToList();
        }
        board.Tracks = bestTracks!; board.Vias = bestVias!;
        best!.Elapsed = sw.Elapsed;
        best.LinksAdded = linksAdded.ToList();
        best.Passes = Math.Min(pass, opt.MaxPasses);
        return best;
    }

    // ---------------- grid setup ----------------

    void SetupGrid(NetlistResult netlist)
    {
        var board = project.Board;
        var b = board.OutlineBounds();
        if (b.IsEmpty) throw new InvalidOperationException("Board has no outline.");
        layers = board.CopperLayerList().ToArray();
        nl = layers.Length;
        step = opt.GridStep;
        if (step <= 0)
        {
            // Fine enough for the smallest pad pitch, coarse enough to stay fast.
            double minPitch = double.MaxValue;
            foreach (var c in project.Components.Where(c => c.Placed))
            {
                var pads = project.Pads(c).ToList();
                for (int i = 0; i < pads.Count; i++)
                    for (int j = i + 1; j < pads.Count; j++)
                    {
                        var d = pads[i].Position.DistanceTo(pads[j].Position);
                        if (d > 0.05 && d < minPitch) minPitch = d;
                    }
            }
            double byPitch = minPitch == double.MaxValue ? 0.25 : minPitch / 5;
            double byArea = Math.Sqrt(b.Width * b.Height / 700_000.0);
            step = Math.Clamp(Math.Max(byPitch, byArea), 0.05, 0.25);
            step = Math.Round(step / 0.025) * 0.025;
            if (step < 0.05) step = 0.05;
        }
        ox = b.MinX; oy = b.MinY;
        cols = (int)Math.Ceiling(b.Width / step) + 1;
        rows = (int)Math.Ceiling(b.Height / step) + 1;
        int n = cols * rows;
        outside = new bool[n];
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
                outside[y * cols + x] = !ProjectGeometry.PointInPolygon(board.Outline, Center(x, y));
        blocked = new bool[n * nl]; viaBlocked = new bool[n]; wideBlocked = new bool[n * nl]; owner = new int[n * nl];
        g = new float[n * nl]; cameFrom = new int[n * nl]; stamp = new int[n * nl]; dirOf = new byte[n * nl];
    }

    Vec2 Center(int x, int y) => new(ox + x * step, oy + y * step);
    int CellIndex(int layer, int x, int y) => (layer * rows + y) * cols + x;
    (int x, int y) ToCell(Vec2 p) => ((int)Math.Round((p.X - ox) / step), (int)Math.Round((p.Y - oy) / step));
    bool InGrid(int x, int y) => x >= 0 && y >= 0 && x < cols && y < rows;
    int LayerIndex(Layer l) => Array.IndexOf(layers, l);

    bool[] EdgeMask(double inflate)
    {
        inflate = Math.Round(inflate, 4);
        if (edgeCache.TryGetValue(inflate, out var m)) return m;
        var outline = project.Board.Outline;
        m = new bool[cols * rows];
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
            {
                int i = y * cols + x;
                m[i] = outside[i] || ProjectGeometry.DistanceToPolygonEdge(outline, Center(x, y)) < inflate;
            }
        edgeCache[inflate] = m;
        return m;
    }

    // ---------------- per-net routing ----------------

    /// <summary>Route a net; on failure optionally insert wire links across the blocking copper and try again.</summary>
    bool RouteNetWithLinks(string net)
    {
        for (int attempt = 0; ; attempt++)
        {
            var outcome = RouteNet(net, out var failure);
            if (outcome) return true;
            if (!opt.WireLinks || failure == null || failure.Crossings.Count == 0 || attempt >= opt.MaxLinksPerNet)
            {
                if (failure != null) current.Failures.Add(failure);
                return false;
            }
            // Remove any partial routing of this net from this attempt before trying again with links.
            int before = linksAdded.Count;
            foreach (var cr in failure.Crossings)
                if (InsertWireLink(net, cr) is { } r) linksAdded.Add(r);
            if (linksAdded.Count == before)
            {
                failure.Note += " No room for a wire link at the crossing — move parts or tracks to make space.";
                current.Failures.Add(failure);
                return false;
            }
            netlist = NetlistBuilder.Build(project);
        }
    }

    bool RouteNet(string net, out RouteFailure? failure)
    {
        failure = null;
        var board = project.Board; var rules = board.Rules;
        double width = rules.WidthFor(net);
        // Never wider than the narrowest pad this net connects to.
        foreach (var pad in project.AllPads())
            if (netlist.NetOfPad(pad.Component, pad.Pad.Number) == net)
                width = Math.Min(width, Math.Max(rules.MinTrackWidth, Math.Min(pad.Pad.Size.X, pad.Pad.Size.Y)));
        double neck = width;
        if (opt.AllowNeckDown)
        {
            neck = opt.NeckDown.TryGetValue(net, out var nd) ? nd : Math.Min(width, rules.TrackWidth);
            neck = Math.Clamp(neck, rules.MinTrackWidth, width);
        }
        neckActive = neck < width - 1e-6;
        double hw = neck / 2, hwFull = width / 2;
        double viaD = rules.ViaDiameterFor(net), viaDrill = rules.ViaDrillFor(net);
        double margin = step * 0.1;
        int n = cols * rows;

        var conn = Connectivity.Analyze(project, netlist);
        if (!conn.Groups.TryGetValue(net, out var groups)) return true;
        var padGroups = groups.Where(gr => gr.Any(i => i.Kind == CopperKind.Pad)).ToList();
        if (padGroups.Count <= 1) return true;

        // ---- obstacle maps (narrow = neck width, wide = full width) ----
        Array.Clear(blocked); Array.Clear(viaBlocked); Array.Clear(wideBlocked); Array.Fill(owner, -1);
        obstacleItems.Clear();
        var edgeTrack = EdgeMask(rules.EdgeClearance + hw);
        var edgeWide = EdgeMask(rules.EdgeClearance + hwFull);
        var edgeVia = EdgeMask(rules.EdgeClearance + viaD / 2);
        for (int l = 0; l < nl; l++) { Array.Copy(edgeTrack, 0, blocked, l * n, n); Array.Copy(edgeWide, 0, wideBlocked, l * n, n); }
        Array.Copy(edgeVia, viaBlocked, n);
        int? onlyLayer = opt.NetLayer.TryGetValue(net, out var forced) && LayerIndex(forced) >= 0 ? LayerIndex(forced) : null;
        if (onlyLayer is int ol) for (int l = 0; l < nl; l++) if (l != ol) { Array.Fill(blocked, true, l * n, n); Array.Fill(wideBlocked, true, l * n, n); }

        foreach (var item in conn.Items)
        {
            if (item.Net == net && net != "") continue;
            double clr = Math.Max(rules.ClearanceFor(net), rules.ClearanceFor(item.Net));
            obstacleItems.Add(item);
            MarkShape(item, clr + hw + margin, clr + hwFull + margin, clr + viaD / 2 + margin, obstacleItems.Count - 1);
        }
        foreach (var hole in board.Holes)
            MarkShape(new CopperItem { Shape = Shape.Circle(hole.Position, hole.Diameter / 2), AllLayers = true }, rules.Clearance + hw + margin, rules.Clearance + hwFull + margin, rules.Clearance + viaD / 2 + margin, -1);
        foreach (var pad in project.AllPads().Where(p => p.Pad.IsThrough && !p.Pad.Plated))
            MarkShape(new CopperItem { Shape = Shape.Circle(pad.Position, pad.Pad.Drill / 2), AllLayers = true }, rules.Clearance + hw + margin, rules.Clearance + hwFull + margin, rules.Clearance + viaD / 2 + margin, -1);

        // ---- group cells ----
        var groupCells = padGroups.Select(gr => CellsOf(gr, hw)).ToList();
        var groupOf = new Dictionary<int, int>();
        for (int gi = 0; gi < groupCells.Count; gi++) foreach (var c in groupCells[gi]) groupOf.TryAdd(c, gi);

        var connected = new bool[padGroups.Count]; connected[0] = true;
        var tree = new HashSet<int>(groupCells[0]);
        int remaining = padGroups.Count - 1;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            var targetAnchors = new List<Vec2>();
            for (int gi = 0; gi < padGroups.Count; gi++) if (!connected[gi]) foreach (var it in padGroups[gi]) targetAnchors.AddRange(it.Anchors);
            var srcBox = Rect.Empty; foreach (var c in tree) srcBox = srcBox.Include(CellCenter(c));
            var tgtBox = Rect.Empty; foreach (var a in targetAnchors) tgtBox = tgtBox.Include(a);
            bool IsTarget(int c) => groupOf.TryGetValue(c, out var gi) && !connected[gi];
            var path = Search(tree, IsTarget, targetAnchors, srcBox.Union(tgtBox).Inflate(8), onlyLayer, false)
                       ?? Search(tree, IsTarget, targetAnchors, Rect.Empty, onlyLayer, false);
            if (path == null)
            {
                failure = Diagnose(net, tree, IsTarget, targetAnchors, onlyLayer, padGroups, groupOf, connected);
                return false;
            }
            int hitGroup = groupOf[path[^1]];
            EmitPath(path, net, width, neck, viaD, viaDrill, padGroups[hitGroup], tree.Contains(path[0]) ? FindItemAt(groups, path[0]) : null);
            connected[hitGroup] = true; remaining--;
            foreach (var c in path) tree.Add(c);
            foreach (var c in groupCells[hitGroup]) tree.Add(c);
            foreach (var c in path)
                if (groupOf.TryGetValue(c, out var other) && !connected[other])
                {
                    connected[other] = true; remaining--;
                    foreach (var cc in groupCells[other]) tree.Add(cc);
                }
        }
        return true;
    }

    /// <summary>
    /// The normal search failed: search again allowing the path to pass through other nets' copper at a high cost,
    /// and report each place it had to cross (that's where a wire link, a move, or a different layer is needed).
    /// </summary>
    RouteFailure Diagnose(string net, HashSet<int> tree, Func<int, bool> isTarget, List<Vec2> targets, int? onlyLayer,
        List<List<CopperItem>> padGroups, Dictionary<int, int> groupOf, bool[] connected)
    {
        int n = cols * rows;
        var f = new RouteFailure { Net = net };
        string Describe(List<CopperItem> grp) => grp.Where(i => i.Kind == CopperKind.Pad).Select(i => $"{i.Pad!.Component.Ref}.{i.Pad.Pad.Number}").FirstOrDefault() ?? "copper";
        f.From = Describe(padGroups[Array.IndexOf(connected, true)]);
        var path = Search(tree, isTarget, targets, Rect.Empty, onlyLayer, true);
        if (path == null)
        {
            var unreached = padGroups.Where((g, i) => !connected[i]).Select(Describe).ToList();
            f.To = string.Join(", ", unreached);
            f.Note = "Even crossing other copper there is no way through — a pad is boxed in by the board edge or holes, or sits outside the board.";
            return f;
        }
        f.To = groupOf.TryGetValue(path[^1], out var gi) ? Describe(padGroups[gi]) : "?";
        // Walk the path and collect runs through blocked cells.
        int i0 = -1;
        for (int i = 0; i <= path.Count; i++)
        {
            bool soft = i < path.Count && blocked[path[i]] && !tree.Contains(path[i]) && !isTarget(path[i]);
            if (soft && i0 < 0) i0 = i;
            if (!soft && i0 >= 0)
            {
                var entry = CellCenter(path[Math.Max(0, i0 - 1)]);
                var exit = CellCenter(path[Math.Min(path.Count - 1, i)]);
                var who = path.Skip(i0).Take(i - i0).Select(c => owner[c]).Where(o => o >= 0).Distinct()
                    .Select(o => obstacleItems[o]).Select(it => it.Kind switch
                    {
                        CopperKind.Pad => $"pad {it.Pad!.Component.Ref}.{it.Pad.Pad.Number} [{(it.Net == "" ? "no net" : it.Net)}]",
                        CopperKind.Via => $"via [{it.Net}]",
                        _ => $"track {ProjectGeometry.ShortId(it.Track!.Id)} [{it.Net}] on {it.Layer.Display()}",
                    }).Distinct().Take(3).ToList();
                f.Crossings.Add(new RouteCrossing(entry, exit, who.Count > 0 ? string.Join(" + ", who) : "clearance zone", entry.DistanceTo(exit)));
                i0 = -1;
            }
        }
        if (f.Crossings.Count == 0) f.Note = "The path only fails on clearance — try a smaller track width or clearance for this net.";
        return f;
    }

    /// <summary>Place a wire link across a crossing. Returns the new part's reference, or null when there's no room.</summary>
    string? InsertWireLink(string net, RouteCrossing cr)
    {
        var board = project.Board; var rules = board.Rules;
        var conn = Connectivity.CollectCopper(project, netlist);
        var dir = (cr.Exit - cr.Entry).Normalized();
        if (dir.LengthSq < 1e-9) dir = new Vec2(1, 0);
        const double padR = 1.0;
        double baseAngle = Math.Atan2(dir.Y, dir.X);
        foreach (var spacing in opt.LinkSpacings.OrderBy(x => x))
        {
            if (spacing < cr.Length + 2 * padR + 2 * rules.Clearance - 0.01) continue;
            foreach (var dAng in new[] { 0, 15, -15, 30, -30, 45, -45, 60, -60, 90 })
                foreach (var shift in new[] { 0.0, 0.5, -0.5, 1.0, -1.0, 1.5, -1.5, 2.0, -2.0, 3.0, -3.0 })
                {
                    double ang = baseAngle + dAng * Math.PI / 180;
                    var d = new Vec2(Math.Cos(ang), Math.Sin(ang));
                    var center = (cr.Middle + new Vec2(-d.Y, d.X) * shift).Snap(0.05);
                    var p1 = center - d * (spacing / 2); var p2 = center + d * (spacing / 2);
                    if (!PadSpotFree(p1, padR, net, conn) || !PadSpotFree(p2, padR, net, conn)) continue;
                    var c = EditOps.AddComponent(project, "Jumper", "Wire link", "WireLink_P" + spacing.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                    c.Fields["WireLink"] = "auto";
                    c.Description = $"Wire link for {net}, {spacing / 25.4:0.0#}\" spacing";
                    c.PcbPos = center; c.PcbRot = -ang * 180 / Math.PI; c.Placed = true;
                    EditOps.ConnectPins(project, net, [(c.Ref, "1"), (c.Ref, "2")]);
                    return c.Ref;
                }
        }
        return null;
    }

    bool PadSpotFree(Vec2 p, double r, string net, List<CopperItem> copper)
    {
        var board = project.Board; var rules = board.Rules;
        if (!ProjectGeometry.PointInPolygon(board.Outline, p) || ProjectGeometry.DistanceToPolygonEdge(board.Outline, p) < rules.EdgeClearance + r) return false;
        var pad = Shape.Circle(p, r);
        foreach (var it in copper)
        {
            if (it.Net == net && net != "") continue;
            var clr = Math.Max(rules.ClearanceFor(net), rules.ClearanceFor(it.Net));
            if (Shape.Distance(pad, it.Shape) < clr) return false;
        }
        foreach (var h in board.Holes) if (h.Position.DistanceTo(p) < h.Diameter / 2 + r + rules.Clearance) return false;
        return true;
    }

    Vec2 CellCenter(int idx) { int c = idx % (cols * rows); return Center(c % cols, c / cols); }

    void MarkShape(CopperItem item, double inflateTrack, double inflateWide, double inflateVia, int ownerIndex)
    {
        var bb = item.Shape.Bounds.Inflate(Math.Max(Math.Max(inflateTrack, inflateWide), inflateVia));
        var (x0, y0) = ToCell(new(bb.MinX, bb.MinY)); var (x1, y1) = ToCell(new(bb.MaxX, bb.MaxY));
        x0 = Math.Max(0, x0 - 1); y0 = Math.Max(0, y0 - 1); x1 = Math.Min(cols - 1, x1 + 1); y1 = Math.Min(rows - 1, y1 + 1);
        int n = cols * rows;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var d = item.Shape.DistanceTo(Center(x, y));
                int ci = y * cols + x;
                if (d < inflateVia) viaBlocked[ci] = true;
                for (int l = 0; l < nl; l++)
                {
                    if (!item.OnLayer(layers[l])) continue;
                    int idx = l * n + ci;
                    if (d < inflateWide) wideBlocked[idx] = true;
                    if (d < inflateTrack)
                    {
                        if (!blocked[idx] || owner[idx] >= 0) owner[idx] = blocked[idx] && owner[idx] == -1 ? -1 : ownerIndex;
                        blocked[idx] = true;
                    }
                }
            }
    }

    /// <summary>Grid cells covered by a group of same-net copper (where a new track may start / end).</summary>
    List<int> CellsOf(List<CopperItem> group, double hw)
    {
        var cells = new List<int>();
        int n = cols * rows;
        foreach (var item in group)
        {
            var bb = item.Shape.Bounds;
            var (x0, y0) = ToCell(new(bb.MinX, bb.MinY)); var (x1, y1) = ToCell(new(bb.MaxX, bb.MaxY));
            int added = 0;
            for (int l = 0; l < nl; l++)
            {
                if (!item.OnLayer(layers[l])) continue;
                for (int y = Math.Max(0, y0); y <= Math.Min(rows - 1, y1); y++)
                    for (int x = Math.Max(0, x0); x <= Math.Min(cols - 1, x1); x++)
                    {
                        var p = Center(x, y);
                        // must lie inside the copper so the new track overlaps it
                        if (item.Shape.DistanceTo(p) > -Math.Min(hw * 0.3, 0.05)) continue;
                        int idx = l * n + y * cols + x;
                        cells.Add(idx); added++;
                        blocked[idx] = false; wideBlocked[idx] = false; // own copper is always enterable
                    }
            }
            if (added == 0)
            {
                // Copper smaller than a grid cell: use the nearest cell(s) to its anchor.
                var (ax, ay) = ToCell(item.Anchors[0]);
                if (!InGrid(ax, ay)) continue;
                for (int l = 0; l < nl; l++)
                {
                    if (!item.OnLayer(layers[l])) continue;
                    int idx = l * n + ay * cols + ax;
                    cells.Add(idx); blocked[idx] = false; wideBlocked[idx] = false;
                }
            }
        }
        return cells;
    }

    CopperItem? FindItemAt(List<List<CopperItem>> groups, int cell)
    {
        var p = CellCenter(cell);
        foreach (var gr in groups) foreach (var it in gr) if (it.Kind == CopperKind.Pad && it.Shape.Contains(p)) return it;
        return null;
    }

    /// <param name="soft">Diagnostic mode: other nets' copper may be crossed at a high cost (edge / holes stay hard).</param>
    List<int>? Search(HashSet<int> sources, Func<int, bool> isTarget, List<Vec2> targets, Rect window, int? onlyLayer, bool soft)
    {
        int n = cols * rows;
        curStamp++;
        if (curStamp == int.MaxValue) { Array.Clear(stamp); curStamp = 1; }
        int wx0 = 0, wy0 = 0, wx1 = cols - 1, wy1 = rows - 1;
        if (!window.IsEmpty)
        {
            (wx0, wy0) = ToCell(new(window.MinX, window.MinY)); (wx1, wy1) = ToCell(new(window.MaxX, window.MaxY));
            wx0 = Math.Max(0, wx0); wy0 = Math.Max(0, wy0); wx1 = Math.Min(cols - 1, wx1); wy1 = Math.Min(rows - 1, wy1);
        }
        var tcells = targets.Select(t => ToCell(t)).Distinct().ToArray();
        double H(int x, int y)
        {
            double best = double.MaxValue;
            foreach (var (tx, ty) in tcells)
            {
                int dx = Math.Abs(tx - x), dy = Math.Abs(ty - y);
                double d = (Math.Max(dx, dy) - Math.Min(dx, dy)) + Math.Min(dx, dy) * 1.41421356;
                if (d < best) best = d;
            }
            return best * step;
        }
        var pq = new PriorityQueue<int, double>();
        foreach (var s in sources)
        {
            int l = s / n, c = s % n, x = c % cols, y = c / cols;
            if (onlyLayer is int ol && l != ol) continue;
            stamp[s] = curStamp; g[s] = 0; cameFrom[s] = -1; dirOf[s] = 255;
            pq.Enqueue(s, H(x, y));
        }
        int expanded = 0, limit = n * nl;
        while (pq.TryDequeue(out var cur, out var f))
        {
            if (++expanded > limit) break;
            int l = cur / n, c = cur % n, x = c % cols, y = c / cols;
            double gc = g[cur];
            if (f - H(x, y) > gc + 1e-6) continue; // stale entry
            if (isTarget(cur) && !sources.Contains(cur)) return Reconstruct(cur);
            var lay = layers[l];
            bool horizPref = l == 0 || (nl > 2 && l == 1);
            for (int d = 0; d < 8; d++)
            {
                int nx = x + DX[d], ny = y + DY[d];
                if (nx < wx0 || ny < wy0 || nx > wx1 || ny > wy1) continue;
                int ni = l * n + ny * cols + nx;
                bool diag = (d & 1) == 1;
                double extra = 0;
                if (blocked[ni])
                {
                    if (!soft || owner[ni] < 0 || (onlyLayer is int lo && l != lo)) continue;
                    // crossing someone else's copper: expensive to enter, cheaper to continue (keeps crossings short and few)
                    extra = (blocked[cur] && owner[cur] >= 0 ? 3 : 40) * step + 2.0 * step;
                }
                else if (diag && (blocked[l * n + y * cols + nx] || blocked[l * n + ny * cols + x]) && !soft) continue; // no corner cutting
                double cost = (diag ? 1.41421356 : 1.0) * step + extra;
                if (neckActive && wideBlocked[ni]) cost *= opt.NeckDownCost;
                bool wrong = horizPref ? DY[d] != 0 && DX[d] == 0 : DX[d] != 0 && DY[d] == 0;
                if (wrong && nl > 1) cost *= 1 + opt.WrongWayFactor;
                if (dirOf[cur] != 255 && dirOf[cur] != d)
                {
                    int turn = Math.Abs(dirOf[cur] - d); turn = Math.Min(turn, 8 - turn);
                    cost += opt.TurnCost * (turn >= 2 ? 2 : 1) * step * 4;
                }
                Relax(cur, ni, gc + cost, (byte)d, nx, ny, pq, H);
            }
            // layer change (via)
            if (opt.AllowVias && onlyLayer == null && !viaBlocked[c] && !soft)
            {
                for (int l2 = 0; l2 < nl; l2++)
                {
                    if (l2 == l) continue;
                    int ni = l2 * n + c;
                    if (blocked[ni]) continue;
                    Relax(cur, ni, gc + opt.ViaCost, 255, x, y, pq, H);
                }
            }
        }
        return null;
    }

    void Relax(int from, int to, double ng, byte dir, int x, int y, PriorityQueue<int, double> pq, Func<int, int, double> h)
    {
        if (stamp[to] == curStamp && g[to] <= ng) return;
        stamp[to] = curStamp; g[to] = (float)ng; cameFrom[to] = from; dirOf[to] = dir;
        pq.Enqueue(to, ng + h(x, y));
    }

    List<int> Reconstruct(int end)
    {
        var path = new List<int>();
        for (int c = end; c != -1; c = cameFrom[c]) path.Add(c);
        path.Reverse();
        return path;
    }

    void EmitPath(List<int> path, string net, double width, double neck, double viaD, double viaDrill, List<CopperItem> targetGroup, CopperItem? startPad)
    {
        int n = cols * rows;
        var board = project.Board;
        // Split into runs of (layer, narrow?) so each segment gets the widest width that fits there.
        var runs = new List<(int layer, bool narrow, List<Vec2> pts)>();
        // A cell is "narrow" if the full width doesn't fit there or right next to it (covers diagonal moves).
        bool Narrow(int cell)
        {
            if (!neckActive) return false;
            if (wideBlocked[cell]) return true;
            int l = cell / n, c = cell % n, x = c % cols, y = c / cols;
            for (int d = 0; d < 8; d += 2)
            {
                int nx = x + DX[d], ny = y + DY[d];
                if (InGrid(nx, ny) && wideBlocked[l * n + ny * cols + nx]) return true;
            }
            return false;
        }
        int curLayer = path[0] / n; bool curNarrow = Narrow(path[0]);
        var pts = new List<Vec2> { CellCenter(path[0]) };
        for (int i = 1; i < path.Count; i++)
        {
            int l = path[i] / n;
            var p = CellCenter(path[i]);
            if (l != curLayer)
            {
                runs.Add((curLayer, curNarrow, pts));
                board.Vias.Add(new Via { Position = p, Diameter = viaD, Drill = viaDrill, Net = net, Auto = true });
                curLayer = l; curNarrow = Narrow(path[i]); pts = [p];
                continue;
            }
            bool nw = Narrow(path[i]);
            if (nw != curNarrow)
            {
                // The transition point always sits on a cell where the full width still fits.
                if (nw)
                {
                    var last = pts[^1];
                    runs.Add((curLayer, curNarrow, pts));
                    pts = [last, p];
                }
                else
                {
                    pts.Add(p);
                    runs.Add((curLayer, curNarrow, pts));
                    pts = [p];
                }
                curNarrow = nw;
            }
            else pts.Add(p);
        }
        runs.Add((curLayer, curNarrow, pts));

        var endP = CellCenter(path[^1]);
        var endPad = targetGroup.FirstOrDefault(it => it.Kind == CopperKind.Pad && it.Shape.Contains(endP) && it.OnLayer(layers[path[^1] / n]));
        if (endPad != null) runs[^1].pts.Add(endPad.Pad!.Position);
        if (startPad != null && startPad.OnLayer(layers[path[0] / n]) && startPad.Shape.Contains(CellCenter(path[0]))) runs[0].pts.Insert(0, startPad.Pad!.Position);

        foreach (var (layer, narrow, rp) in runs)
        {
            var simplified = Simplify(rp);
            for (int i = 0; i + 1 < simplified.Count; i++)
                if (!simplified[i].ApproxEquals(simplified[i + 1]))
                    board.Tracks.Add(new Track { A = simplified[i], B = simplified[i + 1], Width = narrow ? neck : width, Layer = layers[layer], Net = net, Auto = true });
        }
    }

    static List<Vec2> Simplify(List<Vec2> pts)
    {
        if (pts.Count < 3) return pts;
        var res = new List<Vec2> { pts[0] };
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var a = res[^1]; var b = pts[i]; var c = pts[i + 1];
            var d1 = (b - a).Normalized(); var d2 = (c - b).Normalized();
            if (d1.ApproxEquals(d2, 1e-6)) continue;
            res.Add(b);
        }
        res.Add(pts[^1]);
        return res;
    }
}
