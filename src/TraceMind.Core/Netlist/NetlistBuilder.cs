using TraceMind.Core.Geometry;
using TraceMind.Core.Model;

namespace TraceMind.Core.Netlist;

public sealed record NetNode(Component Component, string Pin, string PinName, PinType Type);

public sealed class Net
{
    public string Name { get; set; } = "";
    public List<NetNode> Nodes { get; } = [];
    /// <summary>True when the name comes from a label / power symbol rather than being auto-generated.</summary>
    public bool Named { get; set; }
}

public sealed class NetlistResult
{
    public List<Net> Nets { get; } = [];
    /// <summary>Pad key (component id + pad number) → net name.</summary>
    public Dictionary<string, string> PadNets { get; } = [];
    /// <summary>(component ref, pin number) → net name.</summary>
    public Dictionary<(string Ref, string Pin), string> PinNets { get; } = [];
    public List<PinInstance> UnconnectedPins { get; } = [];

    public string NetOfPad(Component c, string pad) => PadNets.TryGetValue(PadInstance.PadKey(c, pad), out var n) ? n : "";
    public Net? Find(string name) => Nets.FirstOrDefault(n => n.Name == name);
}

/// <summary>Extracts electrical connectivity from the schematic (wires, labels, power symbols).</summary>
public static class NetlistBuilder
{
    const double Eps = 0.01;

    sealed class UnionFind
    {
        readonly List<int> parent = [];
        public int Add() { parent.Add(parent.Count); return parent.Count - 1; }
        public int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        public void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
    }

    static long Key(Vec2 p) => ((long)Math.Round(p.X * 100) << 32) ^ (uint)(int)Math.Round(p.Y * 100);

    public static NetlistResult Build(Project project)
    {
        var uf = new UnionFind();
        var pointNodes = new Dictionary<long, int>();
        int NodeAt(Vec2 p)
        {
            var k = Key(p);
            if (!pointNodes.TryGetValue(k, out var id)) { id = uf.Add(); pointNodes[k] = id; }
            return id;
        }

        var sch = project.Schematic;
        // Wires: both endpoints connected
        foreach (var w in sch.Wires) uf.Union(NodeAt(w.A), NodeAt(w.B));

        // Collect all points of interest
        var pins = project.Components.SelectMany(c => project.Pins(c)).ToList();
        var points = new List<Vec2>();
        points.AddRange(pins.Select(p => p.Position));
        points.AddRange(sch.Labels.Select(l => l.Position));
        foreach (var w in sch.Wires) { points.Add(w.A); points.Add(w.B); }

        // Points lying on a wire's interior join that wire (T connections).
        foreach (var pt in points)
        {
            foreach (var w in sch.Wires)
            {
                if (pt.ApproxEquals(w.A, Eps) || pt.ApproxEquals(w.B, Eps)) continue;
                if (Shape.PointSegDistance(pt, w.A, w.B) < Eps) uf.Union(NodeAt(pt), NodeAt(w.A));
            }
        }

        // Labels with equal names, and power symbols with equal values, are the same net.
        var nameRoots = new Dictionary<string, int>(StringComparer.Ordinal);
        var nodeNames = new Dictionary<int, string>();
        void Name(int node, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (nameRoots.TryGetValue(name, out var other)) uf.Union(node, other); else nameRoots[name] = node;
            nodeNames.TryAdd(node, name);
        }
        foreach (var l in sch.Labels) Name(NodeAt(l.Position), l.Name.Trim());
        foreach (var pin in pins)
        {
            var sym = project.Symbol(pin.Component);
            if (sym?.IsPower == true) Name(NodeAt(pin.Position), string.IsNullOrWhiteSpace(pin.Component.Value) ? sym.Pins[0].Name : pin.Component.Value.Trim());
        }

        // Group pins by root
        var groups = new Dictionary<int, List<PinInstance>>();
        foreach (var pin in pins)
        {
            var root = uf.Find(NodeAt(pin.Position));
            if (!groups.TryGetValue(root, out var list)) groups[root] = list = [];
            list.Add(pin);
        }
        var rootNames = new Dictionary<int, string>();
        foreach (var (node, name) in nodeNames)
        {
            var r = uf.Find(node);
            // prefer power names / shorter names deterministically
            if (!rootNames.TryGetValue(r, out var existing) || string.CompareOrdinal(name, existing) < 0) rootNames[r] = name;
        }

        var result = new NetlistResult();
        var noConnects = sch.NoConnects.Select(n => Key(n.Position)).ToHashSet();
        var usedNames = new HashSet<string>();
        foreach (var kv in groups.OrderBy(g => g.Value.Select(p => p.Component.Ref + "." + p.Pin.Number).Min(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            var root = kv.Key; var list = kv.Value;
            var realPins = list.Where(p => project.Symbol(p.Component)?.IsPower != true).ToList();
            bool named = rootNames.TryGetValue(root, out var name);
            if (realPins.Count == 0) continue;
            if (!named)
            {
                if (realPins.Count == 1)
                {
                    var single = realPins[0];
                    if (!noConnects.Contains(Key(single.Position)) && single.Pin.Type != PinType.NoConnect) result.UnconnectedPins.Add(single);
                    continue; // single pin, no net
                }
                var first = realPins.OrderBy(p => p.Component.Ref, StringComparer.Ordinal).ThenBy(p => p.Pin.Number, StringComparer.Ordinal).First();
                name = $"Net-({first.Component.Ref}-Pad{first.Component.PadForPin(first.Pin.Number)})";
            }
            var baseName = name!; int k = 2;
            while (!usedNames.Add(name!)) name = $"{baseName}_{k++}";
            var net = new Net { Name = name!, Named = named };
            foreach (var p in realPins)
            {
                net.Nodes.Add(new NetNode(p.Component, p.Pin.Number, p.Pin.Name, p.Pin.Type));
                result.PinNets[(p.Component.Ref, p.Pin.Number)] = net.Name;
            }
            result.Nets.Add(net);
        }

        // Map pads → nets
        foreach (var c in project.Components)
        {
            var sym = project.Symbol(c);
            if (sym == null) continue;
            foreach (var pin in sym.Pins)
                if (result.PinNets.TryGetValue((c.Ref, pin.Number), out var n))
                    result.PadNets[PadInstance.PadKey(c, c.PadForPin(pin.Number))] = n;
        }
        result.Nets.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }
}
