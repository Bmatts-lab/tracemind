using TraceMind.Core.Geometry;
using TraceMind.Core.Model;
using TraceMind.Core.Netlist;

namespace TraceMind.Core.Pcb;

public enum CopperKind { Pad, Track, Via }

/// <summary>A piece of copper on the board with its net, layers and geometry.</summary>
public sealed class CopperItem
{
    public CopperKind Kind;
    public string Net = "";
    public Shape Shape = null!;
    public Layer Layer;           // for tracks / smd pads
    public bool AllLayers;         // vias and through-hole pads
    public PadInstance? Pad;
    public Track? Track;
    public Via? Via;
    public Vec2[] Anchors = [];
    public bool OnLayer(Layer l) => AllLayers ? l.IsCopper() : Layer == l;
    public bool SharesLayer(CopperItem o) => AllLayers || o.AllLayers || Layer == o.Layer;
    public string Describe() => Kind switch
    {
        CopperKind.Pad => $"pad {Pad!.Component.Ref}.{Pad.Pad.Number}",
        CopperKind.Track => $"track on {Layer.Display()}",
        _ => "via",
    };
}

public sealed record Airwire(string Net, Vec2 A, Vec2 B);

public sealed class ConnectivityResult
{
    public List<CopperItem> Items { get; } = [];
    public List<Airwire> Airwires { get; } = [];
    /// <summary>net → list of connected groups (each a list of items).</summary>
    public Dictionary<string, List<List<CopperItem>>> Groups { get; } = [];
    public int UnroutedNets => Airwires.Select(a => a.Net).Distinct().Count();
}

public static class Connectivity
{
    public static List<CopperItem> CollectCopper(Project project, NetlistResult netlist, bool includeTracks = true)
    {
        var items = new List<CopperItem>();
        foreach (var pad in project.AllPads())
        {
            if (!pad.Pad.Plated && pad.Pad.IsThrough) continue; // NPTH
            var net = netlist.NetOfPad(pad.Component, pad.Pad.Number);
            items.Add(new CopperItem
            {
                Kind = CopperKind.Pad, Net = net, Shape = pad.Shape, Pad = pad,
                AllLayers = pad.Pad.IsThrough, Layer = pad.OnBottom ? Layer.BCu : Layer.FCu, Anchors = [pad.Position],
            });
        }
        if (includeTracks)
        {
            foreach (var t in project.Board.Tracks)
                items.Add(new CopperItem { Kind = CopperKind.Track, Net = t.Net, Shape = ProjectGeometry.TrackShape(t), Layer = t.Layer, Track = t, Anchors = [t.A, t.B] });
            foreach (var v in project.Board.Vias)
                items.Add(new CopperItem { Kind = CopperKind.Via, Net = v.Net, Shape = ProjectGeometry.ViaShape(v), AllLayers = true, Via = v, Anchors = [v.Position] });
        }
        return items;
    }

    public static ConnectivityResult Analyze(Project project, NetlistResult netlist)
    {
        var res = new ConnectivityResult();
        res.Items.AddRange(CollectCopper(project, netlist));
        var zones = project.Board.Zones;
        foreach (var byNet in res.Items.Where(i => i.Net != "").GroupBy(i => i.Net))
        {
            var list = byNet.ToList();
            int n = list.Count;
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    var a = list[i]; var b = list[j];
                    if (!a.SharesLayer(b) || !a.Shape.Bounds.Intersects(b.Shape.Bounds)) continue;
                    if (Shape.Distance(a.Shape, b.Shape) <= 0.001) parent[Find(i)] = Find(j);
                }
            // Pads joined inside a part (same pad number, e.g. switch legs / SOT-223 tab) or by a wire link.
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    var a = list[i].Pad; var b = list[j].Pad;
                    if (a == null || b == null || a.Component != b.Component) continue;
                    if (a.Pad.Number == b.Pad.Number || a.Component.IsWireLink()) parent[Find(i)] = Find(j);
                }
            // Copper pours join everything of their net that touches them.
            foreach (var z in zones.Where(z => z.Net == byNet.Key && z.Outline.Count >= 3))
            {
                int first = -1;
                for (int i = 0; i < n; i++)
                {
                    var it = list[i];
                    if (!it.OnLayer(z.Layer)) continue;
                    if (!it.Anchors.Any(a => ProjectGeometry.PointInPolygon(z.Outline, a))) continue;
                    if (first < 0) first = i; else parent[Find(i)] = Find(first);
                }
            }
            var groups = list.Select((it, idx) => (it, root: Find(idx))).GroupBy(x => x.root).Select(g => g.Select(x => x.it).ToList()).ToList();
            res.Groups[byNet.Key] = groups;
            // Only groups with pads matter for the ratsnest (floating track stubs are ignored).
            var padGroups = groups.Where(g => g.Any(i => i.Kind == CopperKind.Pad)).ToList();
            if (padGroups.Count < 2) continue;
            // Prim's MST over groups using nearest anchors
            var inTree = new bool[padGroups.Count]; inTree[0] = true;
            var bestDist = new double[padGroups.Count]; var bestPair = new (Vec2, Vec2)[padGroups.Count];
            for (int g = 1; g < padGroups.Count; g++) { (bestDist[g], bestPair[g]) = Nearest(padGroups[0], padGroups[g]); }
            for (int step = 1; step < padGroups.Count; step++)
            {
                int pick = -1; double d = double.MaxValue;
                for (int g = 0; g < padGroups.Count; g++) if (!inTree[g] && bestDist[g] < d) { d = bestDist[g]; pick = g; }
                if (pick < 0) break;
                inTree[pick] = true;
                res.Airwires.Add(new Airwire(byNet.Key, bestPair[pick].Item1, bestPair[pick].Item2));
                for (int g = 0; g < padGroups.Count; g++)
                {
                    if (inTree[g]) continue;
                    var (dd, pp) = Nearest(padGroups[pick], padGroups[g]);
                    if (dd < bestDist[g]) { bestDist[g] = dd; bestPair[g] = pp; }
                }
            }
        }
        return res;
    }

    static (double, (Vec2, Vec2)) Nearest(List<CopperItem> a, List<CopperItem> b)
    {
        double best = double.MaxValue; (Vec2, Vec2) pair = default;
        foreach (var ia in a) foreach (var pa in ia.Anchors)
                foreach (var ib in b) foreach (var pb in ib.Anchors)
                    {
                        var d = (pa - pb).LengthSq;
                        if (d < best) { best = d; pair = (pa, pb); }
                    }
        return (Math.Sqrt(best), pair);
    }
}
