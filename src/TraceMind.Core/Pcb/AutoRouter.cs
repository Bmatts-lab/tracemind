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
    public string Summary() => FailedNets.Count == 0
        ? $"Routed all {NetsTotal} nets in {Elapsed.TotalSeconds:0.0}s — {TrackLength:0} mm of track, {ViasAdded} vias ({Passes} pass{(Passes == 1 ? "" : "es")})."
        : $"Routed {NetsRouted}/{NetsTotal} nets in {Elapsed.TotalSeconds:0.0}s. Failed: {string.Join(", ", FailedNets)}. {TrackLength:0} mm of track, {ViasAdded} vias.";
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
    bool[] blocked = [], viaBlocked = [];
    float[] g = []; int[] cameFrom = []; int[] stamp = []; int curStamp; byte[] dirOf = [];
    readonly Dictionary<double, bool[]> edgeCache = [];

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
        var netlist = NetlistBuilder.Build(project);
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
            for (int i = 0; i < order.Count; i++)
            {
                if (sw.ElapsedMilliseconds > opt.TimeLimitMs) { res.FailedNets.AddRange(order.Skip(i)); break; }
                ct.ThrowIfCancellationRequested();
                var net = order[i];
                progress?.Report($"Pass {pass}: routing {net} ({i + 1}/{order.Count})");
                if (RouteNet(net, netlist)) res.NetsRouted++; else res.FailedNets.Add(net);
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
        blocked = new bool[n * nl]; viaBlocked = new bool[n];
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

    bool RouteNet(string net, NetlistResult netlist)
    {
        var board = project.Board; var rules = board.Rules;
        double width = rules.WidthFor(net);
        // Neck down for fine-pitch pads: never wider than the narrowest pad this net connects to.
        foreach (var pad in project.AllPads())
            if (netlist.NetOfPad(pad.Component, pad.Pad.Number) == net)
                width = Math.Min(width, Math.Max(rules.MinTrackWidth, Math.Min(pad.Pad.Size.X, pad.Pad.Size.Y)));
        double hw = width / 2;
        double viaD = rules.ViaDiameterFor(net), viaDrill = rules.ViaDrillFor(net);
        double margin = step * 0.1;
        int n = cols * rows;

        var conn = Connectivity.Analyze(project, netlist);
        if (!conn.Groups.TryGetValue(net, out var groups)) return true;
        var padGroups = groups.Where(gr => gr.Any(i => i.Kind == CopperKind.Pad)).ToList();
        if (padGroups.Count <= 1) return true;

        // ---- obstacle map ----
        Array.Clear(blocked); Array.Clear(viaBlocked);
        var edgeTrack = EdgeMask(rules.EdgeClearance + hw);
        var edgeVia = EdgeMask(rules.EdgeClearance + viaD / 2);
        for (int l = 0; l < nl; l++) Array.Copy(edgeTrack, 0, blocked, l * n, n);
        Array.Copy(edgeVia, viaBlocked, n);
        int? onlyLayer = opt.NetLayer.TryGetValue(net, out var forced) && LayerIndex(forced) >= 0 ? LayerIndex(forced) : null;
        if (onlyLayer is int ol) for (int l = 0; l < nl; l++) if (l != ol) Array.Fill(blocked, true, l * n, n);

        foreach (var item in conn.Items)
        {
            if (item.Net == net && net != "") continue;
            double clr = Math.Max(rules.ClearanceFor(net), rules.ClearanceFor(item.Net));
            MarkShape(item, clr + hw + margin, clr + viaD / 2 + margin);
        }
        foreach (var hole in board.Holes)
            MarkShape(new CopperItem { Shape = Shape.Circle(hole.Position, hole.Diameter / 2), AllLayers = true }, rules.Clearance + hw + margin, rules.Clearance + viaD / 2 + margin);
        foreach (var pad in project.AllPads().Where(p => p.Pad.IsThrough && !p.Pad.Plated))
            MarkShape(new CopperItem { Shape = Shape.Circle(pad.Position, pad.Pad.Drill / 2), AllLayers = true }, rules.Clearance + hw + margin, rules.Clearance + viaD / 2 + margin);

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
            var path = Search(tree, c => groupOf.TryGetValue(c, out var gi) && !connected[gi], targetAnchors, srcBox.Union(tgtBox).Inflate(8), onlyLayer)
                       ?? Search(tree, c => groupOf.TryGetValue(c, out var gi) && !connected[gi], targetAnchors, Rect.Empty, onlyLayer);
            if (path == null) return false;
            int hitGroup = groupOf[path[^1]];
            EmitPath(path, net, width, viaD, viaDrill, padGroups[hitGroup], tree.Contains(path[0]) ? FindItemAt(groups, path[0]) : null);
            connected[hitGroup] = true; remaining--;
            foreach (var c in path) tree.Add(c);
            foreach (var c in groupCells[hitGroup]) tree.Add(c);
            // a path cell might also touch other groups — merge them for free
            foreach (var c in path)
                if (groupOf.TryGetValue(c, out var other) && !connected[other])
                {
                    connected[other] = true; remaining--;
                    foreach (var cc in groupCells[other]) tree.Add(cc);
                }
        }
        return true;
    }

    Vec2 CellCenter(int idx) { int c = idx % (cols * rows); return Center(c % cols, c / cols); }

    void MarkShape(CopperItem item, double inflateTrack, double inflateVia)
    {
        var bb = item.Shape.Bounds.Inflate(Math.Max(inflateTrack, inflateVia));
        var (x0, y0) = ToCell(new(bb.MinX, bb.MinY)); var (x1, y1) = ToCell(new(bb.MaxX, bb.MaxY));
        x0 = Math.Max(0, x0 - 1); y0 = Math.Max(0, y0 - 1); x1 = Math.Min(cols - 1, x1 + 1); y1 = Math.Min(rows - 1, y1 + 1);
        int n = cols * rows;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var d = item.Shape.DistanceTo(Center(x, y));
                int ci = y * cols + x;
                if (d < inflateVia) viaBlocked[ci] = true;
                if (d < inflateTrack)
                    for (int l = 0; l < nl; l++)
                        if (item.OnLayer(layers[l])) blocked[l * n + ci] = true;
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
                        blocked[idx] = false; // own copper is always enterable
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
                    cells.Add(idx); blocked[idx] = false;
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

    List<int>? Search(HashSet<int> sources, Func<int, bool> isTarget, List<Vec2> targets, Rect window, int? onlyLayer)
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
                if (blocked[ni]) continue;
                bool diag = (d & 1) == 1;
                if (diag && (blocked[l * n + y * cols + nx] || blocked[l * n + ny * cols + x])) continue; // no corner cutting
                double cost = (diag ? 1.41421356 : 1.0) * step;
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
            if (opt.AllowVias && onlyLayer == null && !viaBlocked[c])
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

    void EmitPath(List<int> path, string net, double width, double viaD, double viaDrill, List<CopperItem> targetGroup, CopperItem? startPad)
    {
        int n = cols * rows;
        var board = project.Board;
        // Split into per-layer runs
        var runs = new List<(int layer, List<Vec2> pts)>();
        int curLayer = path[0] / n; var pts = new List<Vec2> { CellCenter(path[0]) };
        for (int i = 1; i < path.Count; i++)
        {
            int l = path[i] / n;
            if (l != curLayer)
            {
                runs.Add((curLayer, pts));
                var vp = CellCenter(path[i]);
                board.Vias.Add(new Via { Position = vp, Diameter = viaD, Drill = viaDrill, Net = net, Auto = true });
                curLayer = l; pts = [vp];
            }
            else pts.Add(CellCenter(path[i]));
        }
        runs.Add((curLayer, pts));

        // Connect ends to pad centres for a clean look (the segment stays inside the pad).
        var endP = CellCenter(path[^1]);
        var endPad = targetGroup.FirstOrDefault(it => it.Kind == CopperKind.Pad && it.Shape.Contains(endP) && it.OnLayer(layers[path[^1] / n]));
        if (endPad != null) runs[^1].pts.Add(endPad.Pad!.Position);
        if (startPad != null && startPad.OnLayer(layers[path[0] / n]) && startPad.Shape.Contains(CellCenter(path[0]))) runs[0].pts.Insert(0, startPad.Pad!.Position);

        foreach (var (layer, rp) in runs)
        {
            var simplified = Simplify(rp);
            for (int i = 0; i + 1 < simplified.Count; i++)
                if (!simplified[i].ApproxEquals(simplified[i + 1]))
                    board.Tracks.Add(new Track { A = simplified[i], B = simplified[i + 1], Width = width, Layer = layers[layer], Net = net, Auto = true });
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
