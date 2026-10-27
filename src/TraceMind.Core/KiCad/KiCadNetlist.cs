using TraceMind.Core.Geometry;
using TraceMind.Core.Library;
using TraceMind.Core.Model;
using TraceMind.Core.Netlist;
using static TraceMind.Core.KiCad.SNode;

namespace TraceMind.Core.KiCad;

/// <summary>KiCad netlist (.net, "kicadsexpr" format) import / export.</summary>
public static class KiCadNetlist
{
    public static string Export(Project project)
    {
        var netlist = NetlistBuilder.Build(project);
        var comps = project.Components.Where(c => project.Symbol(c)?.IsPower != true).OrderBy(c => c.Ref).ToList();
        var compNodes = comps.Select(c => L("comp", L("ref", c.Ref), L("value", c.Value),
            L("footprint", KiCadPcb.ToKiCadLibName(c.FootprintId)), L("datasheet", string.IsNullOrEmpty(c.Datasheet) ? "~" : c.Datasheet),
            string.IsNullOrEmpty(c.Description) ? null : L("description", c.Description),
            L("fields", L("field", L("name", "MPN"), c.Mpn), L("field", L("name", "LCSC"), c.SupplierPart)),
            L("libsource", L("lib", "TraceMind"), L("part", c.SymbolId), L("description", project.Symbol(c)?.Name ?? "")),
            L("tstamps", c.Id.ToString())));
        var libparts = comps.Select(c => c.SymbolId).Distinct().Select(id => project.GetSymbol(id)).Where(s => s != null).Select(s =>
            L("libpart", L("lib", "TraceMind"), L("part", s!.Id), L("description", s.Name),
                L("pins", s.Pins.Select(pin => L("pin", L("num", pin.Number), L("name", string.IsNullOrEmpty(pin.Name) ? "~" : pin.Name), L("type", PinTypeName(pin.Type)))))));
        int code = 1;
        var nets = netlist.Nets.Select(n => L("net", L("code", (code++).ToString()), L("name", n.Name),
            n.Nodes.Select(nd => L("node", L("ref", nd.Component.Ref), L("pin", nd.Component.PadForPin(nd.Pin)),
                string.IsNullOrEmpty(nd.PinName) ? null : L("pinfunction", nd.PinName), L("pintype", PinTypeName(nd.Type))))));
        var root = L("export", L("version", "E"),
            L("design", L("source", project.Name), L("date", DateTime.Now.ToString("yyyy-MM-dd HH:mm")), L("tool", "TraceMind 1.0")),
            L("components", compNodes), L("libparts", libparts), L("nets", nets));
        return root.ToString() + "\n";
    }

    static string PinTypeName(PinType t) => t switch
    {
        PinType.Input => "input", PinType.Output => "output", PinType.Bidirectional => "bidirectional", PinType.PowerIn => "power_in",
        PinType.PowerOut => "power_out", PinType.OpenCollector => "open_collector", PinType.NoConnect => "no_connect", PinType.TriState => "tri_state", _ => "passive",
    };

    static PinType ParsePinType(string? s) => s switch
    {
        "input" => PinType.Input, "output" => PinType.Output, "bidirectional" => PinType.Bidirectional, "power_in" => PinType.PowerIn,
        "power_out" => PinType.PowerOut, "open_collector" => PinType.OpenCollector, "no_connect" => PinType.NoConnect, "tri_state" => PinType.TriState, _ => PinType.Passive,
    };

    public static (Project Project, List<string> Warnings) Import(string text, string name = "Imported")
    {
        var root = Parse(text);
        if (root.Head != "export") throw new FormatException("Not a KiCad netlist (.net) file");
        var warnings = new List<string>();
        var project = new Project { Name = name };
        // libparts → pin definitions
        var libPins = new Dictionary<string, List<SymbolLibrary.IcPin>>();
        foreach (var lp in root.First("libparts")?.All("libpart") ?? [])
        {
            var key = (lp.Get("lib") ?? "") + ":" + (lp.Get("part") ?? "");
            libPins[key] = lp.First("pins")?.All("pin").Select(pn => new SymbolLibrary.IcPin(pn.Get("num") ?? "", (pn.Get("name") ?? "") == "~" ? "" : pn.Get("name") ?? "", ParsePinType(pn.Get("type")))).ToList() ?? [];
        }
        var padNets = new Dictionary<(Component, string), string>();
        foreach (var cn in root.First("components")?.All("comp") ?? [])
        {
            var c = new Component { Ref = cn.Get("ref") ?? "U?", Value = cn.Get("value") ?? "", Datasheet = cn.Get("datasheet") is { } ds && ds != "~" ? ds : "" };
            foreach (var f in cn.First("fields")?.All("field") ?? [])
            {
                var fname = f.Get("name") ?? ""; var fval = f.Str(2) ?? "";
                if (fname.Equals("MPN", StringComparison.OrdinalIgnoreCase)) c.Mpn = fval;
                else if (fname.Equals("LCSC", StringComparison.OrdinalIgnoreCase)) c.SupplierPart = fval;
                else if (fname.Equals("Manufacturer", StringComparison.OrdinalIgnoreCase)) c.Manufacturer = fval;
                else if (fval != "") c.Fields[fname] = fval;
            }
            var fpName = cn.Get("footprint") ?? "";
            c.FootprintId = KiCadPcb.FromKiCadLibName(fpName) ?? "";
            if (fpName != "" && c.FootprintId == "") warnings.Add($"{c.Ref}: footprint '{fpName}' is not in the built-in library — assign one (or ask the AI to create it).");
            var lib = cn.First("libsource");
            var part = lib?.Get("part") ?? "";
            var key = (lib?.Get("lib") ?? "") + ":" + part;
            string prefix = new(c.Ref.TakeWhile(char.IsLetter).ToArray());
            if (SymbolLibrary.Get(part) != null) c.SymbolId = part;
            else if (part is "R_Small" or "R_US") c.SymbolId = "R";
            else if (part is "C_Small" or "C_Polarized" or "CP") c.SymbolId = part == "C_Small" ? "C" : "C_Pol";
            else if (part.StartsWith("LED")) c.SymbolId = "LED";
            else if (libPins.TryGetValue(key, out var pins) && pins.Count > 0)
            {
                var symId = "IC_" + part;
                if (!project.Symbols.ContainsKey(symId)) project.Symbols[symId] = SymbolLibrary.BuildIc(symId, part, pins, string.IsNullOrEmpty(prefix) ? "U" : prefix);
                c.SymbolId = symId;
            }
            project.Components.Add(c);
        }
        foreach (var nn in root.First("nets")?.All("net") ?? [])
        {
            var netName = nn.Get("name") ?? "";
            if (netName.StartsWith("unconnected-")) continue;
            foreach (var node in nn.All("node"))
            {
                var comp = project.Find(node.Get("ref") ?? "");
                if (comp == null) continue;
                padNets[(comp, node.Get("pin") ?? "")] = netName.TrimStart('/');
            }
        }
        // Components without a known symbol get one generated from their pad numbers.
        foreach (var c in project.Components.Where(c => string.IsNullOrEmpty(c.SymbolId)))
        {
            var pads = padNets.Keys.Where(k => k.Item1 == c).Select(k => k.Item2).ToList();
            var fp = project.Footprint(c);
            if (fp != null) pads.AddRange(fp.Pads.Select(pd => pd.Number));
            var symId = "IC_" + c.Ref;
            project.Symbols[symId] = SymbolLibrary.BuildIc(symId, c.Value, pads.Where(x => x != "").Distinct().OrderBy(x => int.TryParse(x, out var i) ? i : 9999).Select(x => new SymbolLibrary.IcPin(x, x)), new string(c.Ref.TakeWhile(char.IsLetter).ToArray()));
            c.SymbolId = symId;
        }
        SchematicSynth.FromPadNets(project, padNets);
        return (project, warnings);
    }
}
