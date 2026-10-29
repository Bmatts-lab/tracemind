using System.Globalization;
using System.Text.RegularExpressions;
using TraceMind.Core.Geometry;
using TraceMind.Core.Model;

namespace TraceMind.Core.Library;

/// <summary>
/// Parametric footprint generator. Footprints are identified by name, roughly following KiCad naming,
/// e.g. R_0603, SOIC-8, QFN-32_5x5_P0.5, PinHeader_1x04_P2.54, TQFP-48_7x7_P0.5.
/// </summary>
public static partial class FootprintLibrary
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly string[] Examples =
    [
        "R_0402", "R_0603", "R_0805", "R_1206", "R_2512", "C_0402", "C_0603", "C_0805", "C_1206", "C_1210", "L_0805", "L_1210",
        "LED_0603", "LED_0805", "LED_1206", "D_SOD-123", "D_SOD-323", "D_SMA", "D_SMB",
        "SOT-23", "SOT-23-5", "SOT-23-6", "SOT-223", "SOT-89", "TO-252", "TO-92", "TO-220-3",
        "SOIC-8", "SOIC-14", "SOIC-16", "SOIC-16W", "TSSOP-14", "TSSOP-20", "MSOP-8", "MSOP-10", "SSOP-20",
        "DIP-8", "DIP-14", "DIP-16", "DIP-28",
        "QFN-16_3x3_P0.5", "QFN-20_4x4_P0.5", "QFN-24_4x4_P0.5", "QFN-32_5x5_P0.5", "QFN-48_7x7_P0.5", "QFN-56_7x7_P0.4",
        "TQFP-32_7x7_P0.8", "TQFP-44_10x10_P0.8", "LQFP-48_7x7_P0.5", "LQFP-64_10x10_P0.5", "LQFP-100_14x14_P0.5",
        "PinHeader_1x04_P2.54", "PinHeader_2x05_P2.54", "PinHeader_1x03_P2.00", "JST_PH_1x02", "JST_XH_1x03",
        "Axial_P10.16", "CP_Radial_D5.0_P2.00", "CP_Radial_D8.0_P3.50", "LED_D5.0", "LED_D3.0",
        "Crystal_HC49-4H", "Crystal_SMD_3225-4P", "SW_Tactile_6x6", "SW_SMD_4x4", "TestPoint_Pad_D1.5", "MountingHole_3.2mm",
        "Inductor_SMD_4x4", "Inductor_SMD_6x6", "USB_Micro_B", "BarrelJack_5.5x2.1", "WireLink_P7.62", "WireLink_P10.16", "WireLink_P12.7",
    ];

    public static FootprintDef? Get(string id)
    {
        try { return Generate(id); } catch { return null; }
    }

    static FootprintDef? Generate(string id)
    {
        Match m;
        if ((m = ChipRx().Match(id)).Success) return Chip(id, m.Groups[1].Value, m.Groups[2].Value);
        switch (id)
        {
            case "D_SOD-123": return TwoPad(id, 1.635, new(0.91, 1.22), 2.7, 1.6, true);
            case "D_SOD-323": return TwoPad(id, 1.1, new(0.59, 0.45), 1.8, 1.3, true);
            case "D_SOD-523": return TwoPad(id, 0.7, new(0.6, 0.7), 1.2, 0.8, true);
            case "D_SMA": return TwoPad(id, 2.0, new(2.5, 1.7), 4.3, 2.6, true);
            case "D_SMB": return TwoPad(id, 2.15, new(2.5, 2.3), 4.3, 3.6, true);
            case "D_SMC": return TwoPad(id, 3.4, new(3.3, 3.3), 6.9, 5.9, true);
            case "SOT-23" or "SOT-23-3": return Sot23(id, 3);
            case "SOT-23-5": return Sot23(id, 5);
            case "SOT-23-6": return Sot23(id, 6);
            case "SOT-223" or "SOT-223-3": return Sot223(id);
            case "SOT-89" or "SOT-89-3": return Sot89(id);
            case "TO-252" or "TO-252-2" or "DPAK": return Dpak(id);
            case "TO-92": return InlineTht(id, 3, 1.27, 1.05, 1.5, 0.75, true);
            case "TO-220-3": return InlineTht(id, 3, 2.54, 1.8, 2.4, 1.1, false, 10.0, 4.5);
            case "Crystal_HC49-4H": return InlineTht(id, 2, 4.88, 1.5, 1.5, 0.8, false, 11.0, 4.6);
            case "Crystal_SMD_3225-4P": return Xtal3225(id);
            case "SW_Tactile_6x6": return Tactile(id);
            case "SW_SMD_4x4": return SwSmd4(id);
            case "USB_Micro_B": return UsbMicro(id);
            case "BarrelJack_5.5x2.1": return BarrelJack(id);
            case "Inductor_SMD_4x4": return TwoPad(id, 1.5, new(1.5, 3.5), 4.2, 4.2, false);
            case "Inductor_SMD_6x6": return TwoPad(id, 2.2, new(2.0, 5.5), 6.2, 6.2, false);
            case "MountingHole_2.2mm": return Hole(id, 2.2);
            case "MountingHole_3.2mm": return Hole(id, 3.2);
            case "MountingHole_4.3mm": return Hole(id, 4.3);
        }
        if ((m = SoicRx().Match(id)).Success)
        {
            var fam = m.Groups[1].Value.ToUpperInvariant(); int n = int.Parse(m.Groups[2].Value); bool wide = m.Groups[3].Success && m.Groups[3].Value != "";
            return fam switch
            {
                "SOIC" when wide => Gullwing(id, n, 1.27, 9.3, new(2.0, 0.6), 7.5),
                "SOIC" => Gullwing(id, n, 1.27, 5.4, new(1.55, 0.6), 3.9),
                "TSSOP" => Gullwing(id, n, 0.65, 5.8, new(1.5, 0.4), 4.4),
                "MSOP" => Gullwing(id, n, 0.5 + (n <= 8 ? 0.15 : 0), 4.4, new(1.45, n <= 8 ? 0.42 : 0.3), 3.0),
                "SSOP" => Gullwing(id, n, 0.65, 7.2, new(1.75, 0.45), 5.3),
                _ => null,
            };
        }
        if ((m = DipRx().Match(id)).Success) return Dip(id, int.Parse(m.Groups[1].Value), m.Groups[2].Success && m.Groups[2].Value != "" ? 15.24 : 7.62);
        if ((m = QfnRx().Match(id)).Success)
            return Qfn(id, int.Parse(m.Groups[1].Value), D(m.Groups[2].Value), D(m.Groups[3].Value), D(m.Groups[4].Value), m.Groups[5].Success && m.Groups[5].Value != "" ? D(m.Groups[5].Value) : -1);
        if ((m = QfpRx().Match(id)).Success)
            return Qfp(id, int.Parse(m.Groups[2].Value), D(m.Groups[3].Value), D(m.Groups[4].Value), D(m.Groups[5].Value));
        if ((m = HeaderRx().Match(id)).Success)
            return Header(id, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups[4].Success && m.Groups[4].Value != "" ? D(m.Groups[4].Value) : 2.54);
        if ((m = JstRx().Match(id)).Success)
            return Header(id, 1, int.Parse(m.Groups[2].Value), m.Groups[1].Value.ToUpperInvariant() switch { "PH" => 2.0, "XH" => 2.5, "EH" => 2.5, "SH" => 1.0, "GH" => 1.25, _ => 2.54 }, jst: true);
        if ((m = AxialRx().Match(id)).Success) return InlineTht(id, 2, D(m.Groups[1].Value), 1.6, 1.6, 0.8, false, D(m.Groups[1].Value) * 0.6, 2.5);
        if ((m = RadialRx().Match(id)).Success) return Radial(id, D(m.Groups[1].Value), D(m.Groups[2].Value), true);
        if ((m = LedThtRx().Match(id)).Success) return Radial(id, D(m.Groups[1].Value), 2.54, false);
        if ((m = TpRx().Match(id)).Success) return TestPad(id, D(m.Groups[1].Value));
        if ((m = LinkRx().Match(id)).Success) return WireLink(id, D(m.Groups[1].Value));
        return null;
    }

    static double D(string s) => double.Parse(s, Inv);

    [GeneratedRegex(@"^(R|C|L|LED|D|F)_(0201|0402|0603|0805|1206|1210|1812|2010|2512)", RegexOptions.IgnoreCase)] private static partial Regex ChipRx();
    [GeneratedRegex(@"^(SOIC|TSSOP|MSOP|SSOP)-(\d+)(W?)", RegexOptions.IgnoreCase)] private static partial Regex SoicRx();
    [GeneratedRegex(@"^DIP-(\d+)(W?)", RegexOptions.IgnoreCase)] private static partial Regex DipRx();
    [GeneratedRegex(@"^(?:V?QFN|DFN)-(\d+)[-_](\d+(?:\.\d+)?)x(\d+(?:\.\d+)?)[-_]P(\d+(?:\.\d+)?)(?:[-_]EP(\d+(?:\.\d+)?))?", RegexOptions.IgnoreCase)] private static partial Regex QfnRx();
    [GeneratedRegex(@"^(TQFP|LQFP|QFP)-(\d+)[-_](\d+(?:\.\d+)?)x(\d+(?:\.\d+)?)[-_]P(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)] private static partial Regex QfpRx();
    [GeneratedRegex(@"^(PinHeader|PinSocket|Header)_(\d)x(\d+)(?:_P(\d+(?:\.\d+)?))?", RegexOptions.IgnoreCase)] private static partial Regex HeaderRx();
    [GeneratedRegex(@"^JST_(PH|XH|EH|SH|GH)_1x(\d+)", RegexOptions.IgnoreCase)] private static partial Regex JstRx();
    [GeneratedRegex(@"^Axial_P(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)] private static partial Regex AxialRx();
    [GeneratedRegex(@"^CP_Radial_D(\d+(?:\.\d+)?)_P(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)] private static partial Regex RadialRx();
    [GeneratedRegex(@"^LED_D(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)] private static partial Regex LedThtRx();
    [GeneratedRegex(@"^TestPoint_Pad_D(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)] private static partial Regex TpRx();
    [GeneratedRegex(@"^WireLink_P(\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase)] private static partial Regex LinkRx();

    // ---------- helpers ----------

    static PadDef Smd(string n, double x, double y, double w, double h, PadShape s = PadShape.RoundRect) => new() { Number = n, Position = new(x, y), Size = new(w, h), Shape = s };
    static PadDef Tht(string n, double x, double y, double w, double h, double drill, PadShape s) => new() { Number = n, Position = new(x, y), Size = new(w, h), Drill = drill, Shape = s };

    static void Finish(FootprintDef f, double bodyW, double bodyH, bool pin1Mark = true, bool silkBody = true)
    {
        var pb = f.PadBounds();
        var body = Rect.FromCenter(Vec2.Zero, bodyW, bodyH);
        f.Courtyard = pb.Union(body).Inflate(0.25);
        f.Fab.Add(Graphic.RectG(new(body.MinX, body.MinY), new(body.MaxX, body.MaxY)));
        if (silkBody)
        {
            // Draw body outline on silk, skipping segments that would cross pads.
            var b = body.Inflate(0.12);
            AddSilkLine(f, new(b.MinX, b.MinY), new(b.MaxX, b.MinY));
            AddSilkLine(f, new(b.MaxX, b.MinY), new(b.MaxX, b.MaxY));
            AddSilkLine(f, new(b.MaxX, b.MaxY), new(b.MinX, b.MaxY));
            AddSilkLine(f, new(b.MinX, b.MaxY), new(b.MinX, b.MinY));
        }
        if (pin1Mark && f.Pads.Count > 0)
        {
            var p1 = f.Pads.First(p => p.Number == "1").Position;
            var dir = p1.Length < 1e-6 ? new Vec2(-1, 0) : p1.Normalized();
            var ext = Math.Max(f.Pads[0].Size.X, f.Pads[0].Size.Y) / 2 + 0.45;
            var mark = p1 + dir * ext;
            if (Math.Abs(p1.X) >= Math.Abs(p1.Y)) mark = new Vec2(p1.X + Math.Sign(p1.X == 0 ? -1 : p1.X) * ext, p1.Y);
            else mark = new Vec2(p1.X, p1.Y + Math.Sign(p1.Y) * ext);
            f.Silk.Add(new Graphic { Kind = GraphicKind.Circle, Points = [mark], Radius = 0.15, Filled = true, Width = 0.15 });
        }
    }

    /// <summary>Add a silk line, clipped so it keeps 0.2 mm away from copper pads.</summary>
    static void AddSilkLine(FootprintDef f, Vec2 a, Vec2 b)
    {
        const int steps = 64;
        bool Clear(Vec2 p) => f.Pads.All(pd => PadShapeLocal(pd).DistanceTo(p) > 0.25);
        Vec2? start = null; Vec2 last = a;
        for (int i = 0; i <= steps; i++)
        {
            var p = a + (b - a) * (i / (double)steps);
            if (Clear(p)) { start ??= p; last = p; }
            else if (start is { } s) { if (s.DistanceTo(last) > 0.2) f.Silk.Add(Graphic.Line(s, last)); start = null; }
        }
        if (start is { } s2 && s2.DistanceTo(last) > 0.2) f.Silk.Add(Graphic.Line(s2, last));
    }

    public static Shape PadShapeLocal(PadDef p) => p.Shape switch
    {
        PadShape.Circle => Shape.Circle(p.Position, p.Size.X / 2),
        PadShape.Oval => Shape.Box(p.Position, p.Size.X, p.Size.Y, p.Rotation, Math.Min(p.Size.X, p.Size.Y) / 2),
        PadShape.RoundRect => Shape.Box(p.Position, p.Size.X, p.Size.Y, p.Rotation, Math.Min(p.Size.X, p.Size.Y) * p.RoundRectRatio),
        _ => Shape.Box(p.Position, p.Size.X, p.Size.Y, p.Rotation),
    };

    // ---------- generators ----------

    static FootprintDef Chip(string id, string kind, string size)
    {
        var (x, w, h, bw, bh) = size switch
        {
            "0201" => (0.32, 0.46, 0.4, 0.6, 0.3),
            "0402" => (0.51, 0.59, 0.64, 1.0, 0.5),
            "0603" => (0.825, 0.8, 0.95, 1.6, 0.8),
            "0805" => (0.9125, 1.025, 1.4, 2.0, 1.25),
            "1206" => (1.4625, 1.125, 1.75, 3.2, 1.6),
            "1210" => (1.4625, 1.125, 2.65, 3.2, 2.5),
            "1812" => (2.1, 1.3, 3.4, 4.5, 3.2),
            "2010" => (2.4, 1.3, 2.65, 5.0, 2.5),
            _ => (3.1375, 1.525, 3.35, 6.3, 3.2),
        };
        bool polar = kind.Equals("LED", StringComparison.OrdinalIgnoreCase) || kind.Equals("D", StringComparison.OrdinalIgnoreCase);
        var f = new FootprintDef { Id = id, Name = id, Description = $"{kind} chip {size} (imperial)", Height = bh };
        f.Pads.Add(Smd("1", -x, 0, w, h)); f.Pads.Add(Smd("2", x, 0, w, h));
        Finish(f, bw, bh, pin1Mark: polar, silkBody: false);
        // Silk: two horizontal lines above/below the body (standard chip style)
        var y = Math.Max(bh / 2, h / 2) + 0.2;
        var xx = Math.Max(0.1, x - w / 2 - 0.2);
        if (xx > 0.15) { f.Silk.Add(Graphic.Line(new(-xx, -y), new(xx, -y))); f.Silk.Add(Graphic.Line(new(-xx, y), new(xx, y))); }
        return f;
    }

    static FootprintDef TwoPad(string id, double x, Vec2 pad, double bw, double bh, bool polar)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = id };
        f.Pads.Add(Smd("1", -x, 0, pad.X, pad.Y)); f.Pads.Add(Smd("2", x, 0, pad.X, pad.Y));
        Finish(f, bw, bh, pin1Mark: polar);
        if (polar) f.Silk.Add(new Graphic { Kind = GraphicKind.Line, Points = [new(-bw / 2 + 0.4, -bh / 2 - 0.1), new(-bw / 2 + 0.4, bh / 2 + 0.1)], Width = 0.2 });
        return f;
    }

    static FootprintDef Sot23(string id, int n)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = $"SOT-23 {n}-pin", Height = 1.1 };
        if (n == 3)
        {
            f.Pads.Add(Smd("1", -0.9375, -0.95, 1.475, 0.6)); f.Pads.Add(Smd("2", -0.9375, 0.95, 1.475, 0.6)); f.Pads.Add(Smd("3", 0.9375, 0, 1.475, 0.6));
            Finish(f, 1.4, 3.0);
            return f;
        }
        for (int i = 0; i < 3; i++) f.Pads.Add(Smd((i + 1).ToString(), -1.1375, -0.95 + i * 0.95, 1.325, 0.6));
        if (n == 5) { f.Pads.Add(Smd("4", 1.1375, 0.95, 1.325, 0.6)); f.Pads.Add(Smd("5", 1.1375, -0.95, 1.325, 0.6)); }
        else for (int i = 0; i < 3; i++) f.Pads.Add(Smd((i + 4).ToString(), 1.1375, 0.95 - i * 0.95, 1.325, 0.6));
        Finish(f, 1.6, 3.0);
        return f;
    }

    static FootprintDef Sot223(string id)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = "SOT-223 (tab = pin 2)", Height = 1.8 };
        f.Pads.Add(Smd("1", -3.15, -2.3, 2.0, 1.5)); f.Pads.Add(Smd("2", -3.15, 0, 2.0, 1.5)); f.Pads.Add(Smd("3", -3.15, 2.3, 2.0, 1.5));
        f.Pads.Add(Smd("2", 3.15, 0, 2.0, 3.8));
        Finish(f, 3.5, 6.5);
        return f;
    }

    static FootprintDef Sot89(string id)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = "SOT-89-3 (tab = pin 2)", Height = 1.5 };
        f.Pads.Add(Smd("1", -1.5, -1.95, 1.3, 0.9)); f.Pads.Add(Smd("2", -1.5, 0, 1.3, 0.9)); f.Pads.Add(Smd("3", -1.5, 1.95, 1.3, 0.9));
        f.Pads.Add(Smd("2", 0.8, 0, 3.0, 2.0));
        Finish(f, 2.5, 4.5);
        return f;
    }

    static FootprintDef Dpak(string id)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = "TO-252 / DPAK (tab = pin 2)", Height = 2.4 };
        f.Pads.Add(Smd("1", -4.2, -2.28, 2.2, 1.2)); f.Pads.Add(Smd("3", -4.2, 2.28, 2.2, 1.2));
        f.Pads.Add(Smd("2", 2.0, 0, 6.4, 5.8));
        Finish(f, 6.6, 6.2);
        return f;
    }

    static FootprintDef Gullwing(string id, int n, double pitch, double rowSpan, Vec2 pad, double bodyW)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = $"{n}-pin gull-wing, pitch {pitch} mm", Height = 1.5 };
        int half = n / 2; double x = rowSpan / 2; double y0 = -(half - 1) * pitch / 2;
        for (int i = 0; i < half; i++) f.Pads.Add(Smd((i + 1).ToString(), -x, y0 + i * pitch, pad.X, pad.Y));
        for (int i = 0; i < half; i++) f.Pads.Add(Smd((half + i + 1).ToString(), x, -y0 - i * pitch, pad.X, pad.Y));
        Finish(f, bodyW, (half - 1) * pitch + 1.6);
        return f;
    }

    static FootprintDef Dip(string id, int n, double span)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = $"DIP-{n} through-hole", Height = 4 };
        int half = n / 2; double x = span / 2; double y0 = -(half - 1) * 2.54 / 2;
        for (int i = 0; i < half; i++) f.Pads.Add(Tht((i + 1).ToString(), -x, y0 + i * 2.54, 1.6, 1.6, 0.8, i == 0 ? PadShape.Rect : PadShape.Oval));
        for (int i = 0; i < half; i++) f.Pads.Add(Tht((half + i + 1).ToString(), x, -y0 - i * 2.54, 1.6, 1.6, 0.8, PadShape.Oval));
        Finish(f, span - 1.3, (half - 1) * 2.54 + 2.5);
        return f;
    }

    static FootprintDef Qfn(string id, int n, double bx, double by, double pitch, double ep)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = $"QFN-{n} {bx}x{by} mm, pitch {pitch}", Height = 0.9 };
        int perSide = n / 4; double padL = 0.8, padW = pitch * 0.5;
        double y0 = -(perSide - 1) * pitch / 2;
        int num = 1;
        double ox = bx / 2 - 0.25 + padL / 2 - 0.15, oy = by / 2 - 0.25 + padL / 2 - 0.15;
        for (int i = 0; i < perSide; i++) f.Pads.Add(Smd((num++).ToString(), -ox, y0 + i * pitch, padL, padW));
        for (int i = 0; i < perSide; i++) f.Pads.Add(Smd((num++).ToString(), y0 + i * pitch, oy, padW, padL));
        for (int i = 0; i < perSide; i++) f.Pads.Add(Smd((num++).ToString(), ox, -y0 - i * pitch, padL, padW));
        for (int i = 0; i < perSide; i++) f.Pads.Add(Smd((num++).ToString(), -y0 - i * pitch, -oy, padW, padL));
        if (ep != 0)
        {
            var e = ep > 0 ? ep : Math.Round(Math.Min(bx, by) * 0.55, 2);
            f.Pads.Add(Smd((n + 1).ToString(), 0, 0, e, e, PadShape.Rect));
        }
        Finish(f, bx, by, silkBody: false);
        var hx = bx / 2 + 0.1; var hy = by / 2 + 0.1; var c = Math.Max(0.1, hx - ((perSide - 1) * pitch / 2 + padW / 2 + 0.3));
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
        {
            f.Silk.Add(Graphic.Line(new(sx * hx, sy * hy), new(sx * (hx - c), sy * hy)));
            f.Silk.Add(Graphic.Line(new(sx * hx, sy * hy), new(sx * hx, sy * (hy - c))));
        }
        return f;
    }

    static FootprintDef Qfp(string id, int n, double bx, double by, double pitch)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = $"QFP-{n} {bx}x{by} mm, pitch {pitch}", Height = 1.6 };
        int perSide = n / 4; double padL = 1.5, padW = pitch * 0.55;
        double y0 = -(perSide - 1) * pitch / 2;
        double ox = bx / 2 + 0.75, oy = by / 2 + 0.75;
        int num = 1;
        for (int i = 0; i < perSide; i++) f.Pads.Add(Smd((num++).ToString(), -ox, y0 + i * pitch, padL, padW));
        for (int i = 0; i < perSide; i++) f.Pads.Add(Smd((num++).ToString(), y0 + i * pitch, oy, padW, padL));
        for (int i = 0; i < perSide; i++) f.Pads.Add(Smd((num++).ToString(), ox, -y0 - i * pitch, padL, padW));
        for (int i = 0; i < perSide; i++) f.Pads.Add(Smd((num++).ToString(), -y0 - i * pitch, -oy, padW, padL));
        Finish(f, bx, by, silkBody: false);
        var hx = bx / 2 + 0.1; var hy = by / 2 + 0.1; var c = Math.Max(0.1, hx - ((perSide - 1) * pitch / 2 + padW / 2 + 0.3));
        foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
        {
            f.Silk.Add(Graphic.Line(new(sx * hx, sy * hy), new(sx * (hx - c), sy * hy)));
            f.Silk.Add(Graphic.Line(new(sx * hx, sy * hy), new(sx * hx, sy * (hy - c))));
        }
        return f;
    }

    static FootprintDef Header(string id, int rows, int count, double pitch, bool jst = false)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = $"{rows}x{count} header, pitch {pitch} mm", Height = 8.5 };
        double pad = pitch >= 2.5 ? 1.7 : pitch >= 2 ? 1.35 : 1.0, drill = pitch >= 2.5 ? 1.0 : pitch >= 2 ? 0.75 : 0.5;
        if (jst) { pad = pitch >= 2.5 ? 1.7 : 1.2; drill = pitch >= 2.5 ? 0.95 : 0.75; }
        double y0 = -(count - 1) * pitch / 2; double x0 = -(rows - 1) * pitch / 2;
        int num = 1;
        for (int i = 0; i < count; i++)
            for (int r = 0; r < rows; r++)
            {
                f.Pads.Add(Tht(num.ToString(), x0 + r * pitch, y0 + i * pitch, pad, pad, drill, num == 1 ? PadShape.Rect : (rows == 1 ? PadShape.Oval : PadShape.Circle)));
                num++;
            }
        Finish(f, rows * pitch + (jst ? 3.2 : 0), count * pitch + (jst ? 1.5 : 0), pin1Mark: true);
        return f;
    }

    static FootprintDef InlineTht(string id, int n, double pitch, double pw, double ph, double drill, bool oval, double bodyW = 0, double bodyH = 0)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = $"{n}-pin through-hole, pitch {pitch} mm", Height = 5 };
        double x0 = -(n - 1) * pitch / 2;
        for (int i = 0; i < n; i++) f.Pads.Add(Tht((i + 1).ToString(), x0 + i * pitch, 0, pw, ph, drill, i == 0 ? PadShape.Rect : (oval || pw != ph ? PadShape.Oval : PadShape.Circle)));
        Finish(f, bodyW > 0 ? bodyW : (n - 1) * pitch + 2.5, bodyH > 0 ? bodyH : 3.5);
        return f;
    }

    static FootprintDef Radial(string id, double dia, double pitch, bool polCap)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = polCap ? $"Radial electrolytic D{dia} P{pitch}" : $"LED THT D{dia}", Height = dia * 1.4 };
        double pad = Math.Min(1.8, pitch * 0.8);
        f.Pads.Add(Tht("1", -pitch / 2, 0, pad, pad, 0.8, PadShape.Rect));
        f.Pads.Add(Tht("2", pitch / 2, 0, pad, pad, 0.8, PadShape.Circle));
        f.Courtyard = Rect.FromCenter(Vec2.Zero, dia + 0.5, dia + 0.5);
        f.Silk.Add(Graphic.CircleG(Vec2.Zero, dia / 2 + 0.12));
        f.Fab.Add(Graphic.CircleG(Vec2.Zero, dia / 2));
        if (polCap) { f.Silk.Add(Graphic.Line(new(-dia / 2 - 0.9, -dia / 2), new(-dia / 2 + 0.1, -dia / 2))); f.Silk.Add(Graphic.Line(new(-dia / 2 - 0.4, -dia / 2 - 0.5), new(-dia / 2 - 0.4, -dia / 2 + 0.5))); }
        return f;
    }

    static FootprintDef Xtal3225(string id)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = "3.2x2.5 mm SMD crystal, 4 pads (1,3 = crystal; 2,4 = GND)", Height = 0.8 };
        f.Pads.Add(Smd("1", -1.1, 0.85, 1.4, 1.2)); f.Pads.Add(Smd("2", 1.1, 0.85, 1.4, 1.2));
        f.Pads.Add(Smd("3", 1.1, -0.85, 1.4, 1.2)); f.Pads.Add(Smd("4", -1.1, -0.85, 1.4, 1.2));
        Finish(f, 3.2, 2.5);
        return f;
    }

    static FootprintDef Tactile(string id)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = "6x6 mm tactile switch THT (1-1 and 2-2 internally connected)", Height = 5 };
        f.Pads.Add(Tht("1", -3.25, -2.25, 2.0, 2.0, 1.0, PadShape.Circle)); f.Pads.Add(Tht("1", 3.25, -2.25, 2.0, 2.0, 1.0, PadShape.Circle));
        f.Pads.Add(Tht("2", -3.25, 2.25, 2.0, 2.0, 1.0, PadShape.Circle)); f.Pads.Add(Tht("2", 3.25, 2.25, 2.0, 2.0, 1.0, PadShape.Circle));
        Finish(f, 6.0, 6.0, pin1Mark: false);
        f.Silk.Add(Graphic.CircleG(Vec2.Zero, 1.75));
        return f;
    }

    static FootprintDef SwSmd4(string id)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = "4x4 mm SMD tactile switch", Height = 1.5 };
        f.Pads.Add(Smd("1", -2.3, -1.35, 1.2, 0.9)); f.Pads.Add(Smd("1", 2.3, -1.35, 1.2, 0.9));
        f.Pads.Add(Smd("2", -2.3, 1.35, 1.2, 0.9)); f.Pads.Add(Smd("2", 2.3, 1.35, 1.2, 0.9));
        Finish(f, 4.0, 4.0, pin1Mark: false);
        return f;
    }

    static FootprintDef UsbMicro(string id)
    {
        // Generic micro-USB B receptacle (Amphenol 10118194 style): 5 signal pads, 0.65 pitch, 2 shield anchors.
        var f = new FootprintDef { Id = id, Name = id, Description = "Micro USB B receptacle (1 VBUS, 2 D-, 3 D+, 4 ID, 5 GND, 6 shield)", Height = 2.7 };
        for (int i = 0; i < 5; i++) f.Pads.Add(Smd((i + 1).ToString(), -1.3 + i * 0.65, -2.675, 0.4, 1.35, PadShape.Rect));
        f.Pads.Add(Smd("6", -2.5, -2.45, 1.4, 1.2, PadShape.Rect));
        f.Pads.Add(Smd("6", 2.5, -2.45, 1.4, 1.2, PadShape.Rect));
        f.Pads.Add(Smd("6", -1.1, 0, 1.9, 1.9, PadShape.Rect)); f.Pads.Add(Smd("6", 1.1, 0, 1.9, 1.9, PadShape.Rect));
        f.Pads.Add(Tht("6", -3.5, 0.2, 1.9, 1.9, 1.2, PadShape.Oval)); f.Pads.Add(Tht("6", 3.5, 0.2, 1.9, 1.9, 1.2, PadShape.Oval));
        Finish(f, 7.5, 5.0, pin1Mark: false);
        return f;
    }

    static FootprintDef BarrelJack(string id)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = "5.5x2.1 mm barrel jack THT (1 = centre pin +, 2 = sleeve, 3 = switch)", Height = 11 };
        f.Pads.Add(Tht("1", 0, 0, 3.5, 3.5, 2.0, PadShape.Rect));
        f.Pads.Add(Tht("2", -6.0, 0, 3.5, 3.5, 2.0, PadShape.Oval));
        f.Pads.Add(Tht("3", -3.0, 4.7, 3.5, 3.5, 2.0, PadShape.Oval));
        f.Courtyard = new Rect(-14.5, -5, 0.75 + 1.5, 6.5);
        f.Silk.Add(Graphic.RectG(new(-14.2, -4.6), new(0.8, 4.6)));
        return f;
    }

    /// <summary>Wire link / jumper: two THT pads (2.0 mm, 1.0 mm drill) joined by a wire on the component side.</summary>
    public static FootprintDef WireLink(string id, double pitch)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = $"Wire link, {pitch} mm ({pitch / 25.4:0.0#}\") spacing, 1.0 mm drill", Height = 1 };
        f.Pads.Add(Tht("1", -pitch / 2, 0, 2.0, 2.0, 1.0, PadShape.Circle));
        f.Pads.Add(Tht("2", pitch / 2, 0, 2.0, 2.0, 1.0, PadShape.Circle));
        f.Courtyard = new Rect(-pitch / 2 - 1.25, -1.25, pitch / 2 + 1.25, 1.25);
        f.Silk.Add(Graphic.Line(new(-pitch / 2 + 1.3, 0), new(pitch / 2 - 1.3, 0)));
        return f;
    }

    static FootprintDef TestPad(string id, double d)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = $"Test point SMD pad D{d}" };
        f.Pads.Add(Smd("1", 0, 0, d, d, PadShape.Circle));
        f.Courtyard = Rect.FromCenter(Vec2.Zero, d + 0.5, d + 0.5);
        f.Silk.Add(Graphic.CircleG(Vec2.Zero, d / 2 + 0.25));
        return f;
    }

    static FootprintDef Hole(string id, double d)
    {
        var f = new FootprintDef { Id = id, Name = id, Description = $"Mounting hole {d} mm (non-plated)" };
        f.Pads.Add(new PadDef { Number = "", Shape = PadShape.Circle, Size = new(d, d), Drill = d, Plated = false });
        f.Courtyard = Rect.FromCenter(Vec2.Zero, d * 1.8 + 0.5, d * 1.8 + 0.5);
        f.Silk.Add(Graphic.CircleG(Vec2.Zero, d * 0.9));
        return f;
    }

    /// <summary>Build a custom footprint from explicit pads (used by the AI agent and importers).</summary>
    public static FootprintDef Custom(string id, IEnumerable<PadDef> pads, double bodyW = 0, double bodyH = 0, string description = "")
    {
        var f = new FootprintDef { Id = id, Name = id, Description = description, Pads = [.. pads] };
        var pb = f.PadBounds();
        if (bodyW <= 0) bodyW = Math.Max(pb.Width - 0.6, 0.5);
        if (bodyH <= 0) bodyH = Math.Max(pb.Height - 0.6, 0.5);
        Finish(f, bodyW, bodyH);
        return f;
    }
}
