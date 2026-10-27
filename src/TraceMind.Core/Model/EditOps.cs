using TraceMind.Core.Geometry;
using TraceMind.Core.Library;
using TraceMind.Core.Netlist;

namespace TraceMind.Core.Model;

/// <summary>High-level editing operations shared by the UI, importers and the AI agent.</summary>
public static class EditOps
{
    public const double G = 2.54;

    public static Component AddComponent(Project p, string symbolId, string value, string footprintId = "", string? reference = null, Vec2? schPos = null)
    {
        var sym = p.GetSymbol(symbolId) ?? throw new ArgumentException($"Unknown symbol '{symbolId}'");
        if (string.IsNullOrEmpty(footprintId)) footprintId = SymbolLibrary.DefaultFootprint(symbolId);
        if (!string.IsNullOrEmpty(footprintId) && p.GetFootprint(footprintId) == null) throw new ArgumentException($"Unknown footprint '{footprintId}'");
        var c = new Component
        {
            SymbolId = symbolId, Value = value, FootprintId = footprintId,
            Ref = string.IsNullOrWhiteSpace(reference) ? p.NextRef(sym.RefPrefix) : reference!,
        };
        if (sym.IsPower) { c.Ref = p.NextRef("#PWR"); c.FootprintId = ""; }
        p.Components.Add(c);
        c.SchPos = schPos ?? FindFreeSchematicSpot(p, c);
        return c;
    }

    /// <summary>Find an empty spot on the schematic sheet for a component (row-major packing).</summary>
    public static Vec2 FindFreeSchematicSpot(Project p, Component c)
    {
        var sym = p.Symbol(c);
        var b = sym?.BodyBounds() ?? new Rect(-2.54, -2.54, 2.54, 2.54);
        double pad = 12.7; // leave room for labels
        var occupied = p.Components.Where(o => o != c).Select(o => p.SchBounds(o).Inflate(pad / 2)).ToList();
        for (double y = 25.4; y < 1000; y += G * 4)
            for (double x = 25.4; x < 400; x += G * 4)
            {
                var pos = new Vec2(x, y).Snap(G);
                var r = new Rect(pos.X + b.MinX, pos.Y + b.MinY, pos.X + b.MaxX, pos.Y + b.MaxY).Inflate(pad / 2);
                if (!occupied.Any(o => o.Intersects(r))) return pos;
            }
        return new Vec2(25.4, 25.4);
    }

    public static PinInstance? FindPin(Project p, string reference, string pin)
    {
        var c = p.Find(reference) ?? throw new ArgumentException($"No component {reference}");
        var pins = p.Pins(c).ToList();
        return pins.FirstOrDefault(x => x.Pin.Number == pin)
            ?? pins.FirstOrDefault(x => string.Equals(x.Pin.Name, pin, StringComparison.OrdinalIgnoreCase));
    }

    static int LabelRotationFor(Component c, PinDef pin)
    {
        var side = ProjectGeometry.RotateSide(pin.Side, c.SchRot, c.SchMirror);
        return side switch { PinSide.Left => 180, PinSide.Right => 0, PinSide.Top => 90, _ => 270 };
    }

    /// <summary>Connect pins to a named net by placing net labels at each pin tip.</summary>
    public static List<string> ConnectPins(Project p, string net, IEnumerable<(string Ref, string Pin)> pins)
    {
        var log = new List<string>();
        foreach (var (r, pinId) in pins)
        {
            var pin = FindPin(p, r, pinId);
            if (pin == null) { log.Add($"{r} has no pin '{pinId}'"); continue; }
            var existing = p.Schematic.Labels.Where(l => l.Position.ApproxEquals(pin.Position, 0.01)).ToList();
            if (existing.Any(l => l.Name == net)) continue;
            // replace a different label sitting directly on the pin (re-assigning the pin)
            foreach (var l in existing) { p.Schematic.Labels.Remove(l); log.Add($"{r}.{pin.Pin.Number} moved from {l.Name} to {net}"); }
            p.Schematic.Labels.Add(new NetLabel { Name = net, Position = pin.Position, Rotation = LabelRotationFor(pin.Component, pin.Pin) });
        }
        return log;
    }

    public static void DisconnectPin(Project p, string reference, string pinId)
    {
        var pin = FindPin(p, reference, pinId) ?? throw new ArgumentException($"{reference} has no pin {pinId}");
        p.Schematic.Labels.RemoveAll(l => l.Position.ApproxEquals(pin.Position, 0.01));
        p.Schematic.Wires.RemoveAll(w => w.A.ApproxEquals(pin.Position, 0.01) || w.B.ApproxEquals(pin.Position, 0.01));
    }

    public static void MarkNoConnect(Project p, string reference, string pinId)
    {
        var pin = FindPin(p, reference, pinId) ?? throw new ArgumentException($"{reference} has no pin {pinId}");
        if (!p.Schematic.NoConnects.Any(n => n.Position.ApproxEquals(pin.Position, 0.01)))
            p.Schematic.NoConnects.Add(new NoConnectFlag { Position = pin.Position });
    }

    public static void RemoveComponent(Project p, Component c)
    {
        foreach (var pin in p.Pins(c).ToList())
        {
            p.Schematic.Labels.RemoveAll(l => l.Position.ApproxEquals(pin.Position, 0.01));
            p.Schematic.NoConnects.RemoveAll(n => n.Position.ApproxEquals(pin.Position, 0.01));
        }
        p.Components.Remove(c);
    }

    /// <summary>Move a component on the schematic, dragging labels / wire ends attached to its pins along.</summary>
    public static void MoveSchematic(Project p, Component c, Vec2 newPos, int? rot = null, bool? mirror = null)
    {
        var before = p.Pins(c).ToDictionary(x => x.Pin.Number + "|" + x.Pin.Position, x => x.Position);
        c.SchPos = newPos;
        if (rot.HasValue) c.SchRot = ((rot.Value % 360) + 360) % 360;
        if (mirror.HasValue) c.SchMirror = mirror.Value;
        foreach (var pin in p.Pins(c))
        {
            if (!before.TryGetValue(pin.Pin.Number + "|" + pin.Pin.Position, out var old)) continue;
            foreach (var l in p.Schematic.Labels.Where(l => l.Position.ApproxEquals(old, 0.01)))
            { l.Position = pin.Position; l.Rotation = LabelRotationFor(c, pin.Pin); }
            foreach (var n in p.Schematic.NoConnects.Where(n => n.Position.ApproxEquals(old, 0.01))) n.Position = pin.Position;
            foreach (var w in p.Schematic.Wires)
            {
                if (w.A.ApproxEquals(old, 0.01)) w.A = pin.Position;
                if (w.B.ApproxEquals(old, 0.01)) w.B = pin.Position;
            }
        }
    }

    /// <summary>Update net names on board copper after the schematic changed (renames by pad overlap).</summary>
    public static int SyncBoardNets(Project p)
    {
        var netlist = NetlistBuilder.Build(p);
        var pads = p.AllPads().Select(pd => (pd, net: netlist.NetOfPad(pd.Component, pd.Pad.Number))).ToList();
        int changed = 0;
        foreach (var t in p.Board.Tracks)
        {
            var shape = ProjectGeometry.TrackShape(t);
            foreach (var (pd, net) in pads)
                if (net != "" && pd.OnLayer(t.Layer) && Shape.Distance(shape, pd.Shape) <= 0 && t.Net != net) { t.Net = net; changed++; break; }
        }
        return changed;
    }

    /// <summary>
    /// Arrange the schematic: each IC / connector forms a cluster with the small parts that connect to it
    /// through signal nets (decoupling caps, pull-ups, LEDs…); clusters are packed into rows.
    /// </summary>
    public static void TidySchematic(Project p)
    {
        var nl = NetlistBuilder.Build(p);
        var parts = p.Components.Where(c => p.Symbol(c) is { IsPower: false }).ToList();
        if (parts.Count == 0) return;
        int Pins(Component c) => p.Symbol(c)!.Pins.Count;
        static bool PowerNet(string n) => n.StartsWith('+') || n.StartsWith('-') || n.Contains("GND", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("VCC") || n.StartsWith("VDD") || n is "VBUS" or "VIN" or "VBAT" or "VSYS";
        var netsOf = parts.ToDictionary(c => c, c => nl.Nets.Where(n => n.Nodes.Any(x => x.Component == c)).ToList());
        var hubs = parts.Where(c => Pins(c) >= 3).OrderByDescending(Pins).ThenBy(c => c.Ref, StringComparer.Ordinal).ToList();
        var small = parts.Where(c => Pins(c) < 3).ToList();
        var cluster = hubs.ToDictionary(h => h, h => new List<Component>());
        var misc = new List<Component>();
        // assign small parts: prefer a hub reached through a signal net, then through a chain of small parts, then power nets
        var assigned = new Dictionary<Component, Component>();
        for (int pass = 0; pass < 3; pass++)
            foreach (var c in small.Where(c => !assigned.ContainsKey(c)))
            {
                Component? hub = null;
                foreach (var n in netsOf[c].Where(n => pass == 2 || !PowerNet(n.Name)).OrderBy(n => n.Nodes.Count))
                {
                    hub = n.Nodes.Select(x => x.Component).FirstOrDefault(o => o != c && cluster.ContainsKey(o));
                    if (hub == null && pass >= 1)
                        hub = n.Nodes.Select(x => x.Component).Where(o => o != c && assigned.ContainsKey(o)).Select(o => assigned[o]).FirstOrDefault();
                    if (hub != null) break;
                }
                if (hub != null) { assigned[c] = hub; cluster[hub].Add(c); }
            }
        misc.AddRange(small.Where(c => !assigned.ContainsKey(c)));

        double x = 25.4, y = 25.4, rowH = 0;
        // Aim for a landscape sheet: row width from the total area of all blocks.
        double area = 0, widest = 0;
        foreach (var h in hubs)
        {
            var hb = p.Symbol(h)!.BodyBounds();
            double w = hb.Width + 25.4 + Math.Ceiling(cluster[h].Count / 3.0) * 15.24, hh = Math.Max(hb.Height + 12.7, 3 * 15.24);
            area += (w + 10) * (hh + 8); widest = Math.Max(widest, w);
        }
        area += misc.Count * 15.24 * 15.24 * 1.3;
        double maxRowW = Math.Max(widest, Math.Sqrt(area * 1.6));
        // Small two-pin parts are laid horizontally so their net labels point left/right and never collide.
        int RotFor(Component m)
        {
            var sym = p.Symbol(m)!;
            return sym.Pins.Count == 2 && sym.Pins.All(pp => pp.Side is PinSide.Top or PinSide.Bottom) ? 90 : 0;
        }
        int LabelChars(Component m) => netsOf[m].Select(n => n.Name.Length).DefaultIfEmpty(4).Max();
        void PlaceBlock(Component? hub, List<Component> members)
        {
            var hb = hub != null ? p.Symbol(hub)!.BodyBounds() : new Rect(0, 0, 0, 0);
            double labelRoom = hub != null ? Math.Ceiling((LabelChars(hub) * 1.0 + 4) / G) * G : 0;
            double hubW = hub != null ? hb.Width + labelRoom * 2 : 0;
            const double rowPitch = 10.16;
            int perCol = Math.Max(3, (int)Math.Floor(Math.Max(hb.Height + 7.62, 22.86) / rowPitch));
            int cols = (int)Math.Ceiling(members.Count / (double)perCol);
            double colW = members.Count == 0 ? 0 : Math.Ceiling((members.Max(m => { var b = p.Symbol(m)!.BodyBounds(); return RotFor(m) == 90 ? b.Height : b.Width; }) + 2 * (members.Max(LabelChars) * 1.0 + 3.5)) / G) * G;
            double memW = cols * colW;
            double blockW = hubW + memW + (members.Count > 0 && hub != null ? 2.54 : 0);
            double blockH = Math.Max(hub != null ? hb.Height + 12.7 : 0, Math.Min(members.Count, perCol) * rowPitch) + 7.62;
            if (x + blockW > maxRowW + 25.4 && x > 25.4) { x = 25.4; y += rowH + 7.62; rowH = 0; }
            if (hub != null) MoveSchematic(p, hub, new Vec2(x + labelRoom - hb.MinX, y + 7.62 - hb.MinY).Snap(G), 0, false);
            double mx = x + hubW + (hub != null ? 2.54 : 0);
            var ordered = members.OrderBy(m => new string(m.Ref.TakeWhile(char.IsLetter).ToArray())).ThenBy(m => RefNum(m.Ref)).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var m = ordered[i];
                int col = i / perCol, row = i % perCol;
                var pos = new Vec2(mx + col * colW + colW / 2, y + 7.62 + 2.54 + row * rowPitch).Snap(G);
                MoveSchematic(p, m, pos, RotFor(m), false);
            }
            x += blockW + 7.62; rowH = Math.Max(rowH, blockH);
        }
        foreach (var h in hubs) PlaceBlock(h, cluster[h]);
        if (misc.Count > 0) PlaceBlock(null, misc);
        // power symbols, if any, go next to the first pin they touch — leave them where they are otherwise
    }

    static int RefNum(string r) => int.TryParse(new string(r.SkipWhile(char.IsLetter).ToArray()), out var n) ? n : 0;
}

/// <summary>Builds a label-based schematic for designs that arrive without one (KiCad board / netlist import).</summary>
public static class SchematicSynth
{
    public static void FromPadNets(Project p, Dictionary<(Component, string), string> padNets)
    {
        foreach (var c in p.Components)
        {
            if (!string.IsNullOrEmpty(c.SymbolId)) continue;
            var fp = p.Footprint(c);
            var pins = fp?.Pads.Select(pd => pd.Number).Where(n => n != "").Distinct().OrderBy(n => int.TryParse(n, out var i) ? i : 9999).ThenBy(n => n).ToList() ?? [];
            string prefix = new(c.Ref.TakeWhile(char.IsLetter).ToArray());
            string symId;
            if (pins.Count == 2 && prefix is "R" or "C" or "L" or "F") symId = prefix == "F" ? "Fuse" : prefix;
            else if (pins.Count == 2 && prefix == "D") symId = c.FootprintId.Contains("LED", StringComparison.OrdinalIgnoreCase) ? "LED" : "D";
            else
            {
                symId = "IC_" + c.Ref;
                var sym = SymbolLibrary.BuildIc(symId, c.Value, pins.Select(n => new SymbolLibrary.IcPin(n, n)), string.IsNullOrEmpty(prefix) ? "U" : prefix);
                p.Symbols[symId] = sym;
            }
            c.SymbolId = symId;
        }
        EditOps.TidySchematic(p);
        foreach (var ((comp, pad), net) in padNets)
        {
            var pin = p.Pins(comp).FirstOrDefault(x => comp.PadForPin(x.Pin.Number) == pad);
            if (pin == null) continue;
            EditOps.ConnectPins(p, net, [(comp.Ref, pin.Pin.Number)]);
        }
    }
}
