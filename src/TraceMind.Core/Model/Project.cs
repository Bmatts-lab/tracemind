using System.Text.Json.Serialization;
using TraceMind.Core.Geometry;

namespace TraceMind.Core.Model;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Layer { FCu, In1Cu, In2Cu, BCu, FSilk, BSilk, FMask, BMask, FPaste, BPaste, EdgeCuts, FFab, BFab, FCrtYd, BCrtYd }

public static class Layers
{
    public static readonly Layer[] CopperOrder = [Layer.FCu, Layer.In1Cu, Layer.In2Cu, Layer.BCu];
    public static bool IsCopper(this Layer l) => l is Layer.FCu or Layer.BCu or Layer.In1Cu or Layer.In2Cu;
    public static string Display(this Layer l) => l switch
    {
        Layer.FCu => "Top copper", Layer.BCu => "Bottom copper", Layer.In1Cu => "Inner 1", Layer.In2Cu => "Inner 2",
        Layer.FSilk => "Top silk", Layer.BSilk => "Bottom silk", Layer.FMask => "Top mask", Layer.BMask => "Bottom mask",
        Layer.FPaste => "Top paste", Layer.BPaste => "Bottom paste", Layer.EdgeCuts => "Board edge",
        _ => l.ToString(),
    };
    public static string KiCadName(this Layer l) => l switch
    {
        Layer.FCu => "F.Cu", Layer.BCu => "B.Cu", Layer.In1Cu => "In1.Cu", Layer.In2Cu => "In2.Cu",
        Layer.FSilk => "F.SilkS", Layer.BSilk => "B.SilkS", Layer.FMask => "F.Mask", Layer.BMask => "B.Mask",
        Layer.FPaste => "F.Paste", Layer.BPaste => "B.Paste", Layer.EdgeCuts => "Edge.Cuts",
        Layer.FFab => "F.Fab", Layer.BFab => "B.Fab", Layer.FCrtYd => "F.CrtYd", Layer.BCrtYd => "B.CrtYd", _ => l.ToString(),
    };
    public static Layer? FromKiCad(string s) => s switch
    {
        "F.Cu" => Layer.FCu, "B.Cu" => Layer.BCu, "In1.Cu" => Layer.In1Cu, "In2.Cu" => Layer.In2Cu,
        "F.SilkS" or "F.Silkscreen" => Layer.FSilk, "B.SilkS" or "B.Silkscreen" => Layer.BSilk,
        "F.Mask" => Layer.FMask, "B.Mask" => Layer.BMask, "F.Paste" => Layer.FPaste, "B.Paste" => Layer.BPaste,
        "Edge.Cuts" => Layer.EdgeCuts, "F.Fab" => Layer.FFab, "B.Fab" => Layer.BFab,
        "F.CrtYd" or "F.Courtyard" => Layer.FCrtYd, "B.CrtYd" or "B.Courtyard" => Layer.BCrtYd, _ => null,
    };
}

public sealed class Component
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Ref { get; set; } = "U?";
    public string Value { get; set; } = "";
    public string SymbolId { get; set; } = "";
    public string FootprintId { get; set; } = "";
    public string Description { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Mpn { get; set; } = "";
    public string Datasheet { get; set; } = "";
    /// <summary>Supplier part number, e.g. LCSC "C25804".</summary>
    public string SupplierPart { get; set; } = "";
    public Dictionary<string, string> Fields { get; set; } = [];
    /// <summary>Optional symbol-pin-number → footprint-pad-number map. Missing entries map 1:1.</summary>
    public Dictionary<string, string> PinMap { get; set; } = [];
    /// <summary>Excluded from BOM / board (e.g. power flags).</summary>
    public bool DoNotPopulate { get; set; }

    // Schematic placement
    public Vec2 SchPos { get; set; }
    public int SchRot { get; set; }
    public bool SchMirror { get; set; }

    // PCB placement
    public Vec2 PcbPos { get; set; }
    public double PcbRot { get; set; }
    public bool OnBottom { get; set; }
    public bool Placed { get; set; }
    public bool Locked { get; set; }

    public string PadForPin(string pin) => PinMap.TryGetValue(pin, out var p) ? p : pin;

    public Component Clone()
    {
        var c = (Component)MemberwiseClone();
        c.Fields = new(Fields); c.PinMap = new(PinMap);
        return c;
    }
}

public sealed class Wire
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Vec2 A { get; set; }
    public Vec2 B { get; set; }
}

public sealed class NetLabel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Vec2 Position { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Text direction: 0 = text to the right of the anchor, 180 = to the left, 90 / 270 vertical.</summary>
    public int Rotation { get; set; }
}

public sealed class NoConnectFlag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Vec2 Position { get; set; }
}

public sealed class SchText
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Vec2 Position { get; set; }
    public string Text { get; set; } = "";
    public double Size { get; set; } = 1.8;
}

public sealed class Schematic
{
    public List<Wire> Wires { get; set; } = [];
    public List<NetLabel> Labels { get; set; } = [];
    public List<NoConnectFlag> NoConnects { get; set; } = [];
    public List<SchText> Texts { get; set; } = [];
    public double Grid { get; set; } = 2.54;
}

public sealed class Track
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Vec2 A { get; set; }
    public Vec2 B { get; set; }
    public double Width { get; set; } = 0.25;
    public Layer Layer { get; set; } = Layer.FCu;
    public string Net { get; set; } = "";
    /// <summary>Created by the autorouter (can be ripped up automatically).</summary>
    public bool Auto { get; set; }
    public bool Locked { get; set; }
}

public sealed class Via
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Vec2 Position { get; set; }
    public double Diameter { get; set; } = 0.6;
    public double Drill { get; set; } = 0.3;
    public string Net { get; set; } = "";
    public bool Auto { get; set; }
    public bool Locked { get; set; }
}

/// <summary>Copper pour. Filled at export/render time: everything of other nets is cut out with clearance.</summary>
public sealed class Zone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Net { get; set; } = "GND";
    public Layer Layer { get; set; } = Layer.BCu;
    public List<Vec2> Outline { get; set; } = [];
    public double Clearance { get; set; } = 0.3;
    public int Priority { get; set; }
}

public sealed class BoardText
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Vec2 Position { get; set; }
    public string Text { get; set; } = "";
    public double Size { get; set; } = 1.2;
    public Layer Layer { get; set; } = Layer.FSilk;
    public double Rotation { get; set; }
}

public sealed class NetClass
{
    public string Name { get; set; } = "Default";
    public double TrackWidth { get; set; } = 0.25;
    public double Clearance { get; set; } = 0.2;
    public double ViaDiameter { get; set; } = 0.6;
    public double ViaDrill { get; set; } = 0.3;
    public List<string> Nets { get; set; } = [];
}

public sealed class DesignRules
{
    public double Clearance { get; set; } = 0.2;
    public double TrackWidth { get; set; } = 0.25;
    public double MinTrackWidth { get; set; } = 0.127;
    public double ViaDiameter { get; set; } = 0.6;
    public double ViaDrill { get; set; } = 0.3;
    public double MinDrill { get; set; } = 0.2;
    public double EdgeClearance { get; set; } = 0.3;
    public double SilkWidth { get; set; } = 0.15;
    public double MaskExpansion { get; set; } = 0.05;
    public List<NetClass> NetClasses { get; set; } = [];

    public NetClass? ClassFor(string net) => NetClasses.FirstOrDefault(c => c.Nets.Contains(net, StringComparer.OrdinalIgnoreCase));
    public double WidthFor(string net) => ClassFor(net)?.TrackWidth ?? TrackWidth;
    public double ClearanceFor(string net) => Math.Max(Clearance, ClassFor(net)?.Clearance ?? 0);
    public double ViaDiameterFor(string net) => ClassFor(net)?.ViaDiameter ?? ViaDiameter;
    public double ViaDrillFor(string net) => ClassFor(net)?.ViaDrill ?? ViaDrill;
}

public sealed class Board
{
    /// <summary>Closed polygon of the board edge.</summary>
    public List<Vec2> Outline { get; set; } = [new(0, 0), new(60, 0), new(60, 40), new(0, 40)];
    public int CopperLayers { get; set; } = 2;
    public List<Track> Tracks { get; set; } = [];
    public List<Via> Vias { get; set; } = [];
    public List<Zone> Zones { get; set; } = [];
    public List<BoardText> Texts { get; set; } = [];
    /// <summary>Mounting holes etc. (non-plated) — position + diameter.</summary>
    public List<MountingHole> Holes { get; set; } = [];
    public DesignRules Rules { get; set; } = new();
    public double Thickness { get; set; } = 1.6;

    public IEnumerable<Layer> CopperLayerList()
    {
        yield return Layer.FCu;
        if (CopperLayers >= 4) { yield return Layer.In1Cu; yield return Layer.In2Cu; }
        yield return Layer.BCu;
    }

    public Rect OutlineBounds()
    {
        var r = Rect.Empty;
        foreach (var p in Outline) r = r.Include(p);
        return r;
    }
}

public sealed class MountingHole
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Vec2 Position { get; set; }
    public double Diameter { get; set; } = 3.2;
}

public sealed class Project
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "Untitled";
    public string Notes { get; set; } = "";
    public List<Component> Components { get; set; } = [];
    public Schematic Schematic { get; set; } = new();
    public Board Board { get; set; } = new();
    /// <summary>Symbols used by this project (copied from the built-in library or created by the AI).</summary>
    public Dictionary<string, SymbolDef> Symbols { get; set; } = [];
    public Dictionary<string, FootprintDef> Footprints { get; set; } = [];

    public Component? Find(string reference) => Components.FirstOrDefault(c => string.Equals(c.Ref, reference, StringComparison.OrdinalIgnoreCase));
}
