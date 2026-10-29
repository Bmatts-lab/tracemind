using TraceMind.Core.Geometry;
using TraceMind.Core.Library;

namespace TraceMind.Core.Model;

public sealed record PinInstance(Component Component, PinDef Pin, Vec2 Position, Vec2 BodyPoint);

public sealed record PadInstance(Component Component, PadDef Pad, Vec2 Position, double Rotation, Shape Shape, bool OnBottom)
{
    public string Key => PadKey(Component, Pad.Number);
    public static string PadKey(Component c, string pad) => c.Id.ToString("N") + ":" + pad;
    public bool OnLayer(Layer l) => Pad.IsThrough ? (Pad.Plated && l.IsCopper()) : l == (OnBottom ? Layer.BCu : Layer.FCu);
}

public static class ProjectGeometry
{
    // ---------- library resolution ----------

    public static SymbolDef? GetSymbol(this Project p, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (p.Symbols.TryGetValue(id, out var s)) return s;
        s = SymbolLibrary.Get(id);
        if (s != null) p.Symbols[id] = s;
        return s;
    }

    public static FootprintDef? GetFootprint(this Project p, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (p.Footprints.TryGetValue(id, out var f)) return f;
        f = FootprintLibrary.Get(id);
        if (f != null) p.Footprints[id] = f;
        return f;
    }

    public static SymbolDef? Symbol(this Project p, Component c) => p.GetSymbol(c.SymbolId);
    public static FootprintDef? Footprint(this Project p, Component c) => p.GetFootprint(c.FootprintId);

    // ---------- schematic transforms ----------

    public static Vec2 SchToWorld(Component c, Vec2 local)
    {
        if (c.SchMirror) local = new(-local.X, local.Y);
        return c.SchPos + local.Rotate(c.SchRot);
    }

    public static PinSide RotateSide(PinSide s, int rot, bool mirror)
    {
        if (mirror) s = s switch { PinSide.Left => PinSide.Right, PinSide.Right => PinSide.Left, _ => s };
        int steps = ((rot % 360) + 360) % 360 / 90;
        for (int i = 0; i < steps; i++)
            s = s switch { PinSide.Left => PinSide.Bottom, PinSide.Bottom => PinSide.Right, PinSide.Right => PinSide.Top, _ => PinSide.Left };
        return s;
    }

    public static IEnumerable<PinInstance> Pins(this Project p, Component c)
    {
        var sym = p.Symbol(c);
        if (sym == null) yield break;
        foreach (var pin in sym.Pins) yield return new PinInstance(c, pin, SchToWorld(c, pin.Position), SchToWorld(c, pin.BodyPoint));
    }

    public static Rect SchBounds(this Project p, Component c)
    {
        var sym = p.Symbol(c);
        if (sym == null) return Rect.FromCenter(c.SchPos, 2, 2);
        var b = sym.BodyBounds();
        return Rect.FromPoints(SchToWorld(c, new(b.MinX, b.MinY)), SchToWorld(c, new(b.MaxX, b.MaxY)));
    }

    // ---------- PCB transforms ----------

    public static Vec2 PcbToWorld(Component c, Vec2 local)
    {
        if (c.OnBottom) local = new(-local.X, local.Y);
        return c.PcbPos + local.Rotate(c.PcbRot);
    }

    public static Vec2 WorldToPcbLocal(Component c, Vec2 world)
    {
        var l = (world - c.PcbPos).Rotate(-c.PcbRot);
        return c.OnBottom ? new(-l.X, l.Y) : l;
    }

    public static IEnumerable<PadInstance> Pads(this Project p, Component c)
    {
        var fp = p.Footprint(c);
        if (fp == null || c.DoNotPopulate) yield break;
        foreach (var pad in fp.Pads)
        {
            var pos = PcbToWorld(c, pad.Position);
            double rot = (c.OnBottom ? -pad.Rotation : pad.Rotation) + c.PcbRot;
            var shape = pad.Shape switch
            {
                PadShape.Circle => Shape.Circle(pos, pad.Size.X / 2),
                PadShape.Oval => Shape.Box(pos, pad.Size.X, pad.Size.Y, rot, Math.Min(pad.Size.X, pad.Size.Y) / 2),
                PadShape.RoundRect => Shape.Box(pos, pad.Size.X, pad.Size.Y, rot, Math.Min(pad.Size.X, pad.Size.Y) * pad.RoundRectRatio),
                _ => Shape.Box(pos, pad.Size.X, pad.Size.Y, rot),
            };
            yield return new PadInstance(c, pad, pos, rot, shape, c.OnBottom);
        }
    }

    public static IEnumerable<PadInstance> AllPads(this Project p)
        => p.Components.Where(c => c.Placed).SelectMany(c => p.Pads(c));

    /// <summary>Courtyard of a placed footprint as a world-space rectangle (axis aligned bounding box).</summary>
    public static Rect Courtyard(this Project p, Component c)
    {
        var fp = p.Footprint(c);
        var cy = fp?.Courtyard ?? new Rect(-1, -1, 1, 1);
        if (cy.IsEmpty) cy = fp?.PadBounds().Inflate(0.25) ?? new Rect(-1, -1, 1, 1);
        var r = Rect.Empty;
        foreach (var corner in new[] { new Vec2(cy.MinX, cy.MinY), new Vec2(cy.MaxX, cy.MinY), new Vec2(cy.MaxX, cy.MaxY), new Vec2(cy.MinX, cy.MaxY) })
            r = r.Include(PcbToWorld(c, corner));
        return r;
    }

    /// <summary>Short, human/AI friendly id for a track or via (first 6 hex digits of its GUID).</summary>
    public static string ShortId(Guid id) => id.ToString("N")[..6];

    /// <summary>Wire links (jumpers) are parts whose pads are joined by a wire above the board.</summary>
    public static bool IsWireLink(this Component c) => c.Fields.ContainsKey("WireLink") || c.SymbolId == "Jumper";

    public static Shape TrackShape(Track t) => Shape.Capsule(t.A, t.B, t.Width / 2);
    public static Shape ViaShape(Via v) => Shape.Circle(v.Position, v.Diameter / 2);

    /// <summary>Next free reference designator for a prefix, e.g. R → R7.</summary>
    public static string NextRef(this Project p, string prefix)
    {
        int max = 0;
        foreach (var c in p.Components)
        {
            if (!c.Ref.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(c.Ref[prefix.Length..], out var n) && n > max) max = n;
        }
        return prefix + (max + 1);
    }

    public static bool PointInPolygon(IReadOnlyList<Vec2> poly, Vec2 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    public static double DistanceToPolygonEdge(IReadOnlyList<Vec2> poly, Vec2 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i < poly.Count; i++) best = Math.Min(best, Shape.PointSegDistance(p, poly[i], poly[(i + 1) % poly.Count]));
        return best;
    }
}
