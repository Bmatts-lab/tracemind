using System.Text.Json.Serialization;
using TraceMind.Core.Geometry;

namespace TraceMind.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PinType { Passive, Input, Output, Bidirectional, PowerIn, PowerOut, OpenCollector, NoConnect, TriState }

/// <summary>Which way the pin sticks out of the symbol body.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PinSide { Left, Right, Top, Bottom }

public sealed class PinDef
{
    public string Number { get; set; } = "1";
    public string Name { get; set; } = "";
    /// <summary>Connection point (outer tip of the pin) relative to the symbol origin.</summary>
    public Vec2 Position { get; set; }
    public PinSide Side { get; set; }
    public double Length { get; set; } = 2.54;
    public PinType Type { get; set; } = PinType.Passive;
    public bool ShowName { get; set; } = true;
    public bool ShowNumber { get; set; } = true;

    /// <summary>Point where the pin meets the body.</summary>
    [JsonIgnore]
    public Vec2 BodyPoint => Side switch
    {
        PinSide.Left => Position + new Vec2(Length, 0),
        PinSide.Right => Position - new Vec2(Length, 0),
        PinSide.Top => Position + new Vec2(0, Length),
        _ => Position - new Vec2(0, Length),
    };
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GraphicKind { Line, Rect, Circle, Arc, Polyline, Polygon, Text }

public sealed class Graphic
{
    public GraphicKind Kind { get; set; }
    public List<Vec2> Points { get; set; } = [];
    public double Radius { get; set; }
    /// <summary>Arc start / end angle in degrees (math convention, y down).</summary>
    public double StartAngle { get; set; }
    public double EndAngle { get; set; }
    public bool Filled { get; set; }
    public string Text { get; set; } = "";
    public double Width { get; set; } = 0.254;
    public double TextSize { get; set; } = 1.27;

    public static Graphic Line(Vec2 a, Vec2 b) => new() { Kind = GraphicKind.Line, Points = [a, b] };
    public static Graphic RectG(Vec2 a, Vec2 b, bool fill = false) => new() { Kind = GraphicKind.Rect, Points = [a, b], Filled = fill };
    public static Graphic CircleG(Vec2 c, double r, bool fill = false) => new() { Kind = GraphicKind.Circle, Points = [c], Radius = r, Filled = fill };
    public static Graphic Poly(bool fill, params Vec2[] p) => new() { Kind = fill ? GraphicKind.Polygon : GraphicKind.Polyline, Points = [.. p], Filled = fill };
    public static Graphic ArcG(Vec2 c, double r, double a0, double a1) => new() { Kind = GraphicKind.Arc, Points = [c], Radius = r, StartAngle = a0, EndAngle = a1 };
    public static Graphic TextG(Vec2 p, string t, double size = 1.27) => new() { Kind = GraphicKind.Text, Points = [p], Text = t, TextSize = size };
}

public sealed class SymbolDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string RefPrefix { get; set; } = "U";
    public List<Graphic> Graphics { get; set; } = [];
    public List<PinDef> Pins { get; set; } = [];
    /// <summary>Power symbols (GND, +3V3...) name the net they are attached to via the component value.</summary>
    public bool IsPower { get; set; }
    public bool HidePinNames { get; set; }
    public bool HidePinNumbers { get; set; }
    /// <summary>Where to draw reference / value text relative to origin.</summary>
    public Vec2 RefTextPos { get; set; } = new(0, -3.81);
    public Vec2 ValueTextPos { get; set; } = new(0, 3.81);

    public Rect BodyBounds()
    {
        var r = Rect.Empty;
        foreach (var g in Graphics)
        {
            foreach (var p in g.Points) r = r.Include(p);
            if (g.Kind is GraphicKind.Circle or GraphicKind.Arc && g.Points.Count > 0)
                r = r.Union(Rect.FromCenter(g.Points[0], g.Radius * 2, g.Radius * 2));
        }
        foreach (var p in Pins) { r = r.Include(p.Position); r = r.Include(p.BodyPoint); }
        return r.IsEmpty ? new Rect(-1, -1, 1, 1) : r;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PadShape { Rect, Circle, Oval, RoundRect }

public sealed class PadDef
{
    public string Number { get; set; } = "1";
    public PadShape Shape { get; set; } = PadShape.Rect;
    public Vec2 Position { get; set; }
    public Vec2 Size { get; set; } = new(1, 1);
    /// <summary>Drill diameter in mm. 0 = surface-mount pad.</summary>
    public double Drill { get; set; }
    public double Rotation { get; set; }
    /// <summary>Plated through hole (false = mechanical NPTH hole).</summary>
    public bool Plated { get; set; } = true;
    public double RoundRectRatio { get; set; } = 0.25;
    [JsonIgnore] public bool IsThrough => Drill > 0;
}

public sealed class FootprintDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<PadDef> Pads { get; set; } = [];
    /// <summary>Silkscreen graphics (lines / circles / polylines), component-local coordinates.</summary>
    public List<Graphic> Silk { get; set; } = [];
    public List<Graphic> Fab { get; set; } = [];
    /// <summary>Courtyard rectangle, component-local.</summary>
    public Rect Courtyard { get; set; }
    /// <summary>Body height in mm (for future 3D view / clearance checks).</summary>
    public double Height { get; set; } = 1;

    public Rect PadBounds()
    {
        var r = Rect.Empty;
        foreach (var p in Pads)
        {
            var ext = Math.Max(p.Size.X, p.Size.Y) / 2;
            r = r.Union(Rect.FromCenter(p.Position, ext * 2, ext * 2));
        }
        return r.IsEmpty ? new Rect(-0.5, -0.5, 0.5, 0.5) : r;
    }
}
