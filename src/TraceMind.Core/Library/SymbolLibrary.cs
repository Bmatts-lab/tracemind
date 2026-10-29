using TraceMind.Core.Geometry;
using TraceMind.Core.Model;

namespace TraceMind.Core.Library;

/// <summary>Built-in schematic symbols. All coordinates in mm, pins on a 2.54 mm grid.</summary>
public static class SymbolLibrary
{
    public const double G = 2.54;

    public static readonly (string Id, string Description)[] Catalog =
    [
        ("R", "Resistor"), ("C", "Capacitor (non-polarised)"), ("C_Pol", "Polarised capacitor (pin 1 = +)"),
        ("L", "Inductor / ferrite bead"), ("D", "Diode (pin 1 = K, pin 2 = A)"), ("D_Schottky", "Schottky diode (1 = K, 2 = A)"),
        ("D_Zener", "Zener diode (1 = K, 2 = A)"), ("LED", "LED (pin 1 = K, pin 2 = A)"),
        ("Q_NPN", "NPN transistor (1 = B, 2 = E, 3 = C — SOT-23 order)"), ("Q_PNP", "PNP transistor (1 = B, 2 = E, 3 = C)"),
        ("Q_NMOS", "N-channel MOSFET (1 = G, 2 = S, 3 = D — SOT-23 order)"), ("Q_PMOS", "P-channel MOSFET (1 = G, 2 = S, 3 = D)"),
        ("SW", "Push button (2 pins)"), ("Crystal", "Crystal (2 pins)"), ("Fuse", "Fuse / polyfuse"),
        ("GND", "Ground power symbol — value is the net name"), ("VCC", "Supply power symbol — value is the net name, e.g. +3V3"),
        ("Conn_1xN", "Single-row connector, e.g. Conn_1x04"), ("Conn_2xN", "Dual-row connector, e.g. Conn_2x05"),
        ("TestPoint", "Test point (1 pin)"), ("Jumper", "Wire link / jumper (2 pins, same net) — footprint WireLink_P7.62 / P10.16 / P12.7"), ("MountingHole", "Mounting hole (no pins)"),
    ];

    public static SymbolDef? Get(string id)
    {
        if (id.StartsWith("Conn_1x", StringComparison.OrdinalIgnoreCase) && int.TryParse(id[7..], out var n1)) return Conn1(n1);
        if (id.StartsWith("Conn_2x", StringComparison.OrdinalIgnoreCase) && int.TryParse(id[7..], out var n2)) return Conn2(n2);
        return id switch
        {
            "R" => Resistor(), "C" => Capacitor(false), "C_Pol" => Capacitor(true), "L" => Inductor(),
            "D" => Diode("D", "Diode", 0), "D_Schottky" => Diode("D_Schottky", "Schottky diode", 1), "D_Zener" => Diode("D_Zener", "Zener diode", 2),
            "LED" => Led(), "Q_NPN" => Bjt(true), "Q_PNP" => Bjt(false), "Q_NMOS" => Mosfet(true), "Q_PMOS" => Mosfet(false),
            "SW" => Switch(), "Crystal" => Crystal(), "Fuse" => Fuse(), "GND" => Gnd(), "VCC" => Vcc(),
            "TestPoint" => TestPoint(), "Jumper" => Jumper(), "MountingHole" => new SymbolDef { Id = "MountingHole", Name = "Mounting hole", RefPrefix = "H", Graphics = [Graphic.CircleG(Vec2.Zero, 1.27)] },
            _ => null,
        };
    }

    public static string DefaultFootprint(string symbolId) => symbolId switch
    {
        "R" => "R_0603", "C" => "C_0603", "C_Pol" => "CP_Radial_D5.0_P2.00", "L" => "L_0805",
        "D" or "D_Schottky" or "D_Zener" => "D_SOD-123", "LED" => "LED_0603",
        "Q_NPN" or "Q_PNP" or "Q_NMOS" or "Q_PMOS" => "SOT-23", "SW" => "SW_Tactile_6x6", "Crystal" => "Crystal_HC49-4H",
        "Fuse" => "R_1206", "Jumper" => "WireLink_P10.16", "TestPoint" => "TestPoint_Pad_D1.5", "MountingHole" => "MountingHole_3.2mm",
        _ when symbolId.StartsWith("Conn_1x") => "PinHeader_1x" + symbolId[7..].PadLeft(2, '0') + "_P2.54",
        _ when symbolId.StartsWith("Conn_2x") => "PinHeader_2x" + symbolId[7..].PadLeft(2, '0') + "_P2.54",
        _ => "",
    };

    static PinDef Pin(string num, double x, double y, PinSide side, double len = 2.54, string name = "", PinType t = PinType.Passive)
        => new() { Number = num, Name = name, Position = new(x, y), Side = side, Length = len, Type = t, ShowName = name != "" };

    static SymbolDef TwoPinVertical(string id, string name, string prefix, double len)
        => new()
        {
            Id = id, Name = name, RefPrefix = prefix, HidePinNames = true, HidePinNumbers = true,
            Pins = [Pin("1", 0, -3.81, PinSide.Top, len), Pin("2", 0, 3.81, PinSide.Bottom, len)],
            RefTextPos = new(2.2, -0.8), ValueTextPos = new(2.2, 1.2),
        };

    static SymbolDef Resistor()
    {
        var s = TwoPinVertical("R", "Resistor", "R", 1.27);
        s.Graphics.Add(Graphic.RectG(new(-1.016, -2.54), new(1.016, 2.54)));
        return s;
    }

    static SymbolDef Capacitor(bool pol)
    {
        var s = TwoPinVertical(pol ? "C_Pol" : "C", pol ? "Polarised capacitor" : "Capacitor", "C", 3.302);
        s.Graphics.Add(new Graphic { Kind = GraphicKind.Line, Points = [new(-2.032, -0.508), new(2.032, -0.508)], Width = 0.5 });
        if (pol)
        {
            s.Graphics.Add(Graphic.RectG(new(-2.032, 0.508), new(2.032, 0.9), true));
            s.Graphics.Add(Graphic.Line(new(-1.6, -2.0), new(-0.6, -2.0)));
            s.Graphics.Add(Graphic.Line(new(-1.1, -2.5), new(-1.1, -1.5)));
        }
        else s.Graphics.Add(new Graphic { Kind = GraphicKind.Line, Points = [new(-2.032, 0.508), new(2.032, 0.508)], Width = 0.5 });
        return s;
    }

    static SymbolDef Inductor()
    {
        var s = TwoPinVertical("L", "Inductor", "L", 1.27);
        for (int i = 0; i < 4; i++) s.Graphics.Add(Graphic.ArcG(new(0, -1.905 + i * 1.27), 0.635, -90, 90));
        return s;
    }

    static SymbolDef Diode(string id, string name, int style)
    {
        var s = new SymbolDef
        {
            Id = id, Name = name, RefPrefix = "D", HidePinNames = true, HidePinNumbers = true,
            Pins = [Pin("1", 3.81, 0, PinSide.Right, 2.54), Pin("2", -3.81, 0, PinSide.Left, 2.54)],
            RefTextPos = new(0, -2.5), ValueTextPos = new(0, 2.8),
        };
        s.Graphics.Add(Graphic.Poly(false, new(-1.27, -1.27), new(1.27, 0), new(-1.27, 1.27), new(-1.27, -1.27)));
        s.Graphics.Add(Graphic.Line(new(1.27, -1.27), new(1.27, 1.27)));
        if (style == 1) { s.Graphics.Add(Graphic.Line(new(1.27, -1.27), new(1.8, -1.27))); s.Graphics.Add(Graphic.Line(new(1.27, 1.27), new(0.74, 1.27))); }
        if (style == 2) { s.Graphics.Add(Graphic.Line(new(1.27, -1.27), new(0.74, -1.6))); s.Graphics.Add(Graphic.Line(new(1.27, 1.27), new(1.8, 1.6))); }
        return s;
    }

    static SymbolDef Led()
    {
        var s = Diode("LED", "LED", 0);
        s.RefPrefix = "D";
        s.Graphics.Add(Graphic.Line(new(-0.3, -1.6), new(0.7, -2.6)));
        s.Graphics.Add(Graphic.Poly(true, new(0.7, -2.6), new(0.2, -2.45), new(0.55, -2.1)));
        s.Graphics.Add(Graphic.Line(new(0.6, -1.6), new(1.6, -2.6)));
        s.Graphics.Add(Graphic.Poly(true, new(1.6, -2.6), new(1.1, -2.45), new(1.45, -2.1)));
        s.RefTextPos = new(0, -3.5);
        return s;
    }

    static SymbolDef Bjt(bool npn)
    {
        var s = new SymbolDef
        {
            Id = npn ? "Q_NPN" : "Q_PNP", Name = npn ? "NPN transistor" : "PNP transistor", RefPrefix = "Q", HidePinNames = true, HidePinNumbers = true,
            Pins = [Pin("1", -5.08, 0, PinSide.Left, 3.81, "B"), Pin("3", 2.54, -5.08, PinSide.Top, 2.54, "C"), Pin("2", 2.54, 5.08, PinSide.Bottom, 2.54, "E")],
            RefTextPos = new(4.5, -1), ValueTextPos = new(4.5, 1.5),
        };
        s.Graphics.Add(Graphic.CircleG(new(0.6, 0), 2.8));
        s.Graphics.Add(new Graphic { Kind = GraphicKind.Line, Points = [new(-1.27, -1.9), new(-1.27, 1.9)], Width = 0.4 });
        s.Graphics.Add(Graphic.Line(new(-1.27, -0.8), new(2.54, -2.54)));
        s.Graphics.Add(Graphic.Line(new(-1.27, 0.8), new(2.54, 2.54)));
        if (npn) s.Graphics.Add(Graphic.Poly(true, new(2.54, 2.54), new(1.4, 2.4), new(1.9, 1.6)));
        else s.Graphics.Add(Graphic.Poly(true, new(-1.27, 0.8), new(-0.2, 0.9), new(-0.6, 1.7)));
        return s;
    }

    static SymbolDef Mosfet(bool n)
    {
        var s = new SymbolDef
        {
            Id = n ? "Q_NMOS" : "Q_PMOS", Name = n ? "N-MOSFET" : "P-MOSFET", RefPrefix = "Q", HidePinNames = true, HidePinNumbers = true,
            Pins = [Pin("1", -5.08, 0, PinSide.Left, 3.81, "G"), Pin("3", 2.54, -5.08, PinSide.Top, 2.54, "D"), Pin("2", 2.54, 5.08, PinSide.Bottom, 2.54, "S")],
            RefTextPos = new(4.5, -1), ValueTextPos = new(4.5, 1.5),
        };
        s.Graphics.Add(Graphic.CircleG(new(0.6, 0), 2.8));
        s.Graphics.Add(Graphic.Line(new(-1.27, -1.5), new(-1.27, 1.5)));
        s.Graphics.Add(new Graphic { Kind = GraphicKind.Line, Points = [new(-0.6, -1.8), new(-0.6, 1.8)], Width = 0.4 });
        s.Graphics.Add(Graphic.Line(new(-0.6, -1.27), new(2.54, -1.27)));
        s.Graphics.Add(Graphic.Line(new(2.54, -1.27), new(2.54, -2.54)));
        s.Graphics.Add(Graphic.Line(new(-0.6, 1.27), new(2.54, 1.27)));
        s.Graphics.Add(Graphic.Line(new(2.54, 1.27), new(2.54, 2.54)));
        s.Graphics.Add(Graphic.Line(new(-0.6, 0), new(2.54, 0)));
        s.Graphics.Add(Graphic.Line(new(2.54, 0), new(2.54, 1.27)));
        if (n) s.Graphics.Add(Graphic.Poly(true, new(-0.6, 0), new(0.4, -0.5), new(0.4, 0.5)));
        else s.Graphics.Add(Graphic.Poly(true, new(1.0, 0), new(0.0, -0.5), new(0.0, 0.5)));
        return s;
    }

    static SymbolDef Switch()
    {
        var s = new SymbolDef
        {
            Id = "SW", Name = "Push button", RefPrefix = "SW", HidePinNames = true, HidePinNumbers = true,
            Pins = [Pin("1", -5.08, 0, PinSide.Left, 2.54), Pin("2", 5.08, 0, PinSide.Right, 2.54)],
            RefTextPos = new(0, -3.5), ValueTextPos = new(0, 2.5),
        };
        s.Graphics.Add(Graphic.CircleG(new(-2.032, 0), 0.5));
        s.Graphics.Add(Graphic.CircleG(new(2.032, 0), 0.5));
        s.Graphics.Add(Graphic.Line(new(-2.54, -1.27), new(2.54, -1.27)));
        s.Graphics.Add(Graphic.Line(new(0, -1.27), new(0, -2.54)));
        return s;
    }

    static SymbolDef Crystal()
    {
        var s = new SymbolDef
        {
            Id = "Crystal", Name = "Crystal", RefPrefix = "Y", HidePinNames = true, HidePinNumbers = true,
            Pins = [Pin("1", -3.81, 0, PinSide.Left, 2.032), Pin("2", 3.81, 0, PinSide.Right, 2.032)],
            RefTextPos = new(0, -3), ValueTextPos = new(0, 3),
        };
        s.Graphics.Add(Graphic.RectG(new(-0.8, -1.6), new(0.8, 1.6)));
        s.Graphics.Add(new Graphic { Kind = GraphicKind.Line, Points = [new(-1.778, -1.27), new(-1.778, 1.27)], Width = 0.4 });
        s.Graphics.Add(new Graphic { Kind = GraphicKind.Line, Points = [new(1.778, -1.27), new(1.778, 1.27)], Width = 0.4 });
        return s;
    }

    static SymbolDef Fuse()
    {
        var s = TwoPinVertical("Fuse", "Fuse", "F", 1.27);
        s.Graphics.Add(Graphic.RectG(new(-0.9, -2.54), new(0.9, 2.54)));
        s.Graphics.Add(Graphic.Line(new(0, -2.54), new(0, 2.54)));
        return s;
    }

    static SymbolDef Gnd() => new()
    {
        Id = "GND", Name = "Ground", RefPrefix = "#PWR", IsPower = true, HidePinNames = true, HidePinNumbers = true,
        Pins = [new PinDef { Number = "1", Name = "GND", Position = Vec2.Zero, Side = PinSide.Top, Length = 0, Type = PinType.PowerIn, ShowName = false, ShowNumber = false }],
        Graphics = [Graphic.Line(new(0, 0), new(0, 1.27)), Graphic.Poly(false, new(-1.27, 1.27), new(1.27, 1.27), new(0, 2.54), new(-1.27, 1.27))],
        RefTextPos = new(0, 6), ValueTextPos = new(0, 3.6),
    };

    static SymbolDef Vcc() => new()
    {
        Id = "VCC", Name = "Power supply", RefPrefix = "#PWR", IsPower = true, HidePinNames = true, HidePinNumbers = true,
        Pins = [new PinDef { Number = "1", Name = "VCC", Position = Vec2.Zero, Side = PinSide.Bottom, Length = 0, Type = PinType.PowerIn, ShowName = false, ShowNumber = false }],
        Graphics = [Graphic.Line(new(0, 0), new(0, -1.27)), Graphic.Line(new(-1.27, -1.27), new(1.27, -1.27))],
        RefTextPos = new(0, -6), ValueTextPos = new(0, -2.6),
    };

    static SymbolDef Jumper()
    {
        var s = new SymbolDef
        {
            Id = "Jumper", Name = "Wire link", RefPrefix = "W", HidePinNames = true, HidePinNumbers = true,
            Pins = [Pin("1", -3.81, 0, PinSide.Left, 1.778), Pin("2", 3.81, 0, PinSide.Right, 1.778)],
            RefTextPos = new(0, -2.2), ValueTextPos = new(0, 2.4),
        };
        s.Graphics.Add(Graphic.CircleG(new(-1.778, 0), 0.4));
        s.Graphics.Add(Graphic.CircleG(new(1.778, 0), 0.4));
        s.Graphics.Add(Graphic.ArcG(new(0, 0.2), 1.75, 190, 350));
        return s;
    }

    static SymbolDef TestPoint() => new()
    {
        Id = "TestPoint", Name = "Test point", RefPrefix = "TP", HidePinNames = true, HidePinNumbers = true,
        Pins = [Pin("1", 0, 2.54, PinSide.Bottom, 1.778)],
        Graphics = [Graphic.CircleG(new(0, 0), 0.762)],
        RefTextPos = new(2, -0.5), ValueTextPos = new(2, 1.5),
    };

    static SymbolDef Conn1(int n)
    {
        n = Math.Clamp(n, 1, 80);
        var s = new SymbolDef { Id = $"Conn_1x{n:00}", Name = $"Connector 1x{n}", RefPrefix = "J", HidePinNames = true };
        double top = -(n - 1) * G / 2; top = Math.Round(top / G) * G;
        for (int i = 0; i < n; i++) s.Pins.Add(Pin((i + 1).ToString(), -5.08, top + i * G, PinSide.Left, 3.81));
        s.Graphics.Add(Graphic.RectG(new(-1.27, top - 1.27), new(1.27, top + (n - 1) * G + 1.27)));
        for (int i = 0; i < n; i++) s.Graphics.Add(Graphic.RectG(new(-1.27, top + i * G - 0.2), new(-0.5, top + i * G + 0.2), true));
        s.RefTextPos = new(0, top - 2.5); s.ValueTextPos = new(0, top + (n - 1) * G + 2.8);
        return s;
    }

    static SymbolDef Conn2(int rows)
    {
        rows = Math.Clamp(rows, 1, 40);
        var s = new SymbolDef { Id = $"Conn_2x{rows:00}", Name = $"Connector 2x{rows}", RefPrefix = "J", HidePinNames = true };
        double top = Math.Round(-(rows - 1) * G / 2 / G) * G;
        for (int i = 0; i < rows; i++)
        {
            s.Pins.Add(Pin((2 * i + 1).ToString(), -5.08, top + i * G, PinSide.Left, 3.81));
            s.Pins.Add(Pin((2 * i + 2).ToString(), 5.08, top + i * G, PinSide.Right, 3.81));
        }
        s.Graphics.Add(Graphic.RectG(new(-1.27, top - 1.27), new(1.27, top + (rows - 1) * G + 1.27)));
        s.RefTextPos = new(0, top - 2.5); s.ValueTextPos = new(0, top + (rows - 1) * G + 2.8);
        return s;
    }

    public sealed record IcPin(string Number, string Name, PinType Type = PinType.Passive, PinSide? Side = null);

    /// <summary>Build a rectangular IC symbol from a pin list. Pins without a side are distributed automatically.</summary>
    public static SymbolDef BuildIc(string id, string name, IEnumerable<IcPin> pins, string prefix = "U")
    {
        var left = new List<IcPin>(); var right = new List<IcPin>(); var top = new List<IcPin>(); var bottom = new List<IcPin>();
        var auto = new List<IcPin>();
        foreach (var p in pins)
        {
            if (p.Side is { } sd) (sd switch { PinSide.Left => left, PinSide.Right => right, PinSide.Top => top, _ => bottom }).Add(p);
            else if (IsGroundName(p.Name)) bottom.Add(p);
            else if (p.Type is PinType.PowerIn or PinType.PowerOut || IsSupplyName(p.Name)) top.Add(p);
            else auto.Add(p);
        }
        // Inputs left, outputs right, the rest balanced.
        foreach (var p in auto.Where(p => p.Type == PinType.Input)) left.Add(p);
        foreach (var p in auto.Where(p => p.Type is PinType.Output or PinType.OpenCollector or PinType.TriState)) right.Add(p);
        foreach (var p in auto.Where(p => p.Type is not (PinType.Input or PinType.Output or PinType.OpenCollector or PinType.TriState)))
            (left.Count <= right.Count ? left : right).Add(p);
        if (top.Count > 8) { foreach (var p in top.Skip(8).ToList()) { top.Remove(p); (left.Count <= right.Count ? left : right).Add(p); } }
        if (bottom.Count > 8) { foreach (var p in bottom.Skip(8).ToList()) { bottom.Remove(p); (left.Count <= right.Count ? left : right).Add(p); } }

        int rowsV = Math.Max(Math.Max(left.Count, right.Count), 2);
        int colsH = Math.Max(Math.Max(top.Count, bottom.Count), 1);
        int maxNameLeft = left.Concat(right).Select(p => p.Name.Length).DefaultIfEmpty(0).Max();
        double halfW = Math.Max((colsH + 1) * G / 2, Math.Ceiling((maxNameLeft * 1.27 * 0.75 * 2 + 3) / (2 * G)) * G);
        halfW = Math.Max(halfW, 2 * G);
        halfW = Math.Ceiling(halfW / G) * G;
        double halfH = Math.Ceiling((rowsV + 1) * G / 2 / G) * G;
        if (top.Count > 0 || bottom.Count > 0) halfH = Math.Max(halfH, 2 * G);
        var s = new SymbolDef { Id = id, Name = name, RefPrefix = prefix };
        s.Graphics.Add(new Graphic { Kind = GraphicKind.Rect, Points = [new(-halfW, -halfH), new(halfW, halfH)], Filled = true, Width = 0.254 });
        const double len = 2.54;
        double y0 = -halfH + G;
        for (int i = 0; i < left.Count; i++) s.Pins.Add(Pin(left[i].Number, -halfW - len, y0 + i * G, PinSide.Left, len, left[i].Name, left[i].Type));
        for (int i = 0; i < right.Count; i++) s.Pins.Add(Pin(right[i].Number, halfW + len, y0 + i * G, PinSide.Right, len, right[i].Name, right[i].Type));
        double x0t = -(top.Count - 1) * G / 2; x0t = Math.Round(x0t / G) * G;
        for (int i = 0; i < top.Count; i++) s.Pins.Add(Pin(top[i].Number, x0t + i * G, -halfH - len, PinSide.Top, len, top[i].Name, top[i].Type));
        double x0b = Math.Round(-(bottom.Count - 1) * G / 2 / G) * G;
        for (int i = 0; i < bottom.Count; i++) s.Pins.Add(Pin(bottom[i].Number, x0b + i * G, halfH + len, PinSide.Bottom, len, bottom[i].Name, bottom[i].Type));
        foreach (var p in s.Pins) p.ShowName = !string.IsNullOrEmpty(p.Name) && p.Name != p.Number;
        s.RefTextPos = new(-halfW, -halfH - (top.Count > 0 ? 4.2 : 1.2));
        s.ValueTextPos = new(-halfW, halfH + (bottom.Count > 0 ? 5.2 : 2.2));
        return s;
    }

    public static bool IsGroundName(string n) => n.ToUpperInvariant() is "GND" or "VSS" or "AGND" or "DGND" or "PGND" or "EP" or "GNDA" or "GNDD" or "V-" or "VEE";
    public static bool IsSupplyName(string n)
    {
        var u = n.ToUpperInvariant();
        return u is "VCC" or "VDD" or "VIN" or "VBAT" or "VBUS" or "AVCC" or "AVDD" or "DVDD" or "VDDA" or "VDDIO" or "V+" or "3V3" or "5V" or "VREF" || u.StartsWith("VDD") || u.StartsWith("VCC");
    }
}
