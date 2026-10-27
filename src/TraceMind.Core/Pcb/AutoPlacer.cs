using TraceMind.Core.Geometry;
using TraceMind.Core.Model;
using TraceMind.Core.Netlist;

namespace TraceMind.Core.Pcb;

public sealed class PlacerOptions
{
    /// <summary>Re-place components that are already placed (except locked ones).</summary>
    public bool ReplaceAll { get; set; }
    public double Spacing { get; set; } = 0.6;
    public double CandidateStep { get; set; } = 0.5;
    public bool AllowRotation { get; set; } = true;
    /// <summary>Only place these references (null = all eligible).</summary>
    public List<string>? Refs { get; set; }
}

/// <summary>Greedy connectivity-driven placement: most-connected parts first, each at the free spot with least wirelength.</summary>
public static class AutoPlacer
{
    public static int Place(Project project, PlacerOptions? options = null)
    {
        var opt = options ?? new PlacerOptions();
        var netlist = NetlistBuilder.Build(project);
        var board = project.Board;
        var bounds = board.OutlineBounds();
        var edgeMargin = board.Rules.EdgeClearance + 0.5;

        bool Eligible(Component c) => !c.DoNotPopulate && project.Footprint(c) != null && project.Symbol(c)?.IsPower != true;
        var movable = project.Components.Where(c => Eligible(c) && !c.Locked && (opt.ReplaceAll || !c.Placed) && (opt.Refs == null || opt.Refs.Contains(c.Ref, StringComparer.OrdinalIgnoreCase))).ToList();
        if (movable.Count == 0) return 0;
        foreach (var c in movable) c.Placed = false;

        // Net weights: big nets (GND, VCC) pull less.
        var netWeight = netlist.Nets.ToDictionary(n => n.Name, n => 1.0 / Math.Max(1, n.Nodes.Count - 1));
        var compNets = project.Components.ToDictionary(c => c, c => netlist.Nets.Where(n => n.Nodes.Any(nd => nd.Component == c)).Select(n => n.Name).ToHashSet());
        double Affinity(Component a, Component b) => compNets[a].Intersect(compNets[b]).Sum(n => netWeight[n]);

        var placed = project.Components.Where(c => Eligible(c) && c.Placed).ToList();
        var remaining = new List<Component>(movable);
        int count = 0;
        while (remaining.Count > 0)
        {
            // pick: strongest affinity to placed set; tie → most pads
            Component next = placed.Count == 0
                ? remaining.OrderByDescending(c => project.Footprint(c)!.Pads.Count).First()
                : remaining.OrderByDescending(c => placed.Sum(p => Affinity(c, p))).ThenByDescending(c => project.Footprint(c)!.Pads.Count).First();
            remaining.Remove(next);
            if (PlaceOne(project, netlist, next, placed, bounds, edgeMargin, opt)) { placed.Add(next); count++; }
        }
        return count;
    }

    static bool PlaceOne(Project project, NetlistResult netlist, Component c, List<Component> placed, Rect bounds, double edgeMargin, PlacerOptions opt)
    {
        var outline = project.Board.Outline;
        // Positions of pads (by net) already on the board
        var netPoints = new Dictionary<string, List<Vec2>>();
        foreach (var p in placed)
            foreach (var pad in project.Pads(p))
            {
                var net = netlist.NetOfPad(p, pad.Pad.Number);
                if (net == "") continue;
                if (!netPoints.TryGetValue(net, out var l)) netPoints[net] = l = [];
                l.Add(pad.Position);
            }
        var target = bounds.Center;
        if (netPoints.Count > 0)
        {
            // centroid of connected pads as a starting point
            var myNets = project.Pads(c).Select(p => netlist.NetOfPad(c, p.Pad.Number)).Where(n => n != "" && netPoints.ContainsKey(n)).ToList();
            var pts = myNets.SelectMany(n => netPoints[n].Count > 6 ? [] : netPoints[n]).ToList();
            if (pts.Count > 0) target = new Vec2(pts.Average(p => p.X), pts.Average(p => p.Y));
        }
        double bestCost = double.MaxValue; Vec2 bestPos = default; double bestRot = 0;
        var rotations = opt.AllowRotation ? new double[] { 0, 90, 180, 270 } : [c.PcbRot];
        var placedRects = placed.Where(p => p.OnBottom == c.OnBottom).Select(p => project.Courtyard(p).Inflate(opt.Spacing / 2)).ToList();
        double stepSize = opt.CandidateStep;
        int maxR = (int)Math.Ceiling(Math.Max(bounds.Width, bounds.Height) / stepSize);
        bool found = false;
        // spiral-ish search: rings around the target, stop after finding a fit and searching a bit further
        for (int r = 0; r <= maxR; r++)
        {
            if (found && r > 12 && r * stepSize > 4) break;
            for (int ix = -r; ix <= r; ix++)
                for (int iy = -r; iy <= r; iy++)
                {
                    if (Math.Max(Math.Abs(ix), Math.Abs(iy)) != r) continue;
                    var pos = (target + new Vec2(ix * stepSize, iy * stepSize)).Snap(0.05);
                    foreach (var rot in rotations)
                    {
                        c.PcbPos = pos; c.PcbRot = rot;
                        var cy = project.Courtyard(c).Inflate(opt.Spacing / 2);
                        if (!Inside(outline, cy, edgeMargin)) continue;
                        if (placedRects.Any(pr => pr.Intersects(cy))) continue;
                        double cost = 0;
                        foreach (var pad in project.Pads(c))
                        {
                            var net = netlist.NetOfPad(c, pad.Pad.Number);
                            if (net == "" || !netPoints.TryGetValue(net, out var pts)) continue;
                            double w = 1.0 / Math.Max(1, pts.Count);
                            cost += pts.Min(p => p.DistanceTo(pad.Position)) * (pts.Count > 6 ? 0.2 : 1) * (0.5 + w);
                        }
                        cost += pos.DistanceTo(target) * 0.05 + (rot == 0 ? 0 : 0.01);
                        if (cost < bestCost) { bestCost = cost; bestPos = pos; bestRot = rot; found = true; }
                    }
                }
        }
        if (!found) { c.Placed = false; return false; }
        c.PcbPos = bestPos; c.PcbRot = bestRot; c.Placed = true;
        return true;
    }

    static bool Inside(List<Vec2> outline, Rect r, double margin)
    {
        foreach (var p in new[] { new Vec2(r.MinX, r.MinY), new Vec2(r.MaxX, r.MinY), new Vec2(r.MaxX, r.MaxY), new Vec2(r.MinX, r.MaxY) })
        {
            if (!ProjectGeometry.PointInPolygon(outline, p)) return false;
            if (ProjectGeometry.DistanceToPolygonEdge(outline, p) < margin) return false;
        }
        return true;
    }
}
