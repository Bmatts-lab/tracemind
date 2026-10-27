using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TraceMind.Core.Geometry;
using TraceMind.Core.Library;
using TraceMind.Core.Model;
using TraceMind.Core.Netlist;
using TraceMind.Core.Pcb;

namespace TraceMind.AI;

public sealed record ToolSpec(string Name, string Description, string Schema);

/// <summary>
/// The design tools Claude can call. Each tool reads / edits the open <see cref="Project"/> and returns a JSON string.
/// Tools are pure model operations — the host runs them on the UI thread and records undo snapshots.
/// </summary>
public sealed partial class DesignTools(Func<Project> getProject)
{
    Project P => getProject();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    static string R(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);

    public static readonly ToolSpec[] Specs =
    [
        new("get_design", "Get the current design: board outline/size, layers, design rules, every component (ref, value, symbol, footprint, part numbers, placement, pins with their nets) and the net list. Call this first and after big changes.",
            """{"type":"object","properties":{"include_pins":{"type":"boolean","description":"Include per-pin net assignments (default true)."}}}"""),
        new("list_library", "List built-in schematic symbols and footprint naming patterns (parametric footprints such as R_0603, SOIC-8, QFN-32_5x5_P0.5, PinHeader_1x04_P2.54, TQFP-48_7x7_P0.5).",
            """{"type":"object","properties":{}}"""),
        new("add_component", "Add a part to the schematic (it appears on the PCB as unplaced). Use a built-in `symbol` for simple parts (R, C, LED, ...), or give `pins` for any IC/module — the symbol is generated from the pin list. Pin numbers must match the footprint pad numbers.",
            """
              {"type":"object","properties":{
              "symbol":{"type":"string","description":"Built-in symbol id (R, C, C_Pol, L, D, LED, Q_NPN, Q_NMOS, SW, Crystal, Fuse, Conn_1xNN, Conn_2xNN, TestPoint). Omit when giving pins."},
              "pins":{"type":"array","description":"Pin list for a custom part symbol.","items":{"type":"object","properties":{
                  "number":{"type":"string"},"name":{"type":"string"},
                  "type":{"type":"string","enum":["passive","input","output","bidirectional","power_in","power_out","open_collector","no_connect","tri_state"]},
                  "side":{"type":"string","enum":["left","right","top","bottom"]}},"required":["number","name"]}},
              "name":{"type":"string","description":"Symbol name for custom parts, e.g. the part family (STM32G031K8)."},
              "value":{"type":"string"},"footprint":{"type":"string","description":"Footprint id (built-in pattern or one created with define_footprint)."},
              "reference":{"type":"string","description":"Optional reference designator; auto-assigned if omitted."},
              "mpn":{"type":"string"},"manufacturer":{"type":"string"},"datasheet":{"type":"string"},"lcsc":{"type":"string","description":"LCSC / JLCPCB part number, e.g. C25804"},
              "description":{"type":"string"},"prefix":{"type":"string","description":"Reference prefix for custom parts (U, J, Q, ...)."}},
              "required":["value"]}
            """),
        new("define_footprint", "Create a custom footprint from a datasheet land pattern when no built-in pattern fits. Coordinates in mm relative to the footprint centre, +y down. Pad numbers must match the symbol pin numbers.",
            """
              {"type":"object","properties":{
              "id":{"type":"string"},"description":{"type":"string"},
              "pads":{"type":"array","items":{"type":"object","properties":{
                "number":{"type":"string"},"x":{"type":"number"},"y":{"type":"number"},"width":{"type":"number"},"height":{"type":"number"},
                "shape":{"type":"string","enum":["rect","roundrect","circle","oval"]},"drill":{"type":"number","description":"Hole diameter for through-hole pads; omit for SMD."},
                "rotation":{"type":"number"}},"required":["number","x","y","width","height"]}},
              "body_width":{"type":"number"},"body_height":{"type":"number"},"height":{"type":"number","description":"Component height in mm"}},
              "required":["id","pads"]}
            """),
        new("update_component", "Change properties of an existing component.",
            """
              {"type":"object","properties":{"reference":{"type":"string"},"new_reference":{"type":"string"},"value":{"type":"string"},"footprint":{"type":"string"},
              "mpn":{"type":"string"},"manufacturer":{"type":"string"},"datasheet":{"type":"string"},"lcsc":{"type":"string"},"description":{"type":"string"},
              "pin_map":{"type":"object","description":"Symbol pin number → footprint pad number overrides.","additionalProperties":{"type":"string"}},
              "do_not_populate":{"type":"boolean"}},"required":["reference"]}
            """),
        new("remove_component", "Delete components and their connections.",
            """{"type":"object","properties":{"references":{"type":"array","items":{"type":"string"}}},"required":["references"]}"""),
        new("connect", "Connect pins to a named net (creates net labels in the schematic). Pins are written REF.PIN where PIN is the pin number or pin name, e.g. U1.8 or U1.VCC. Connecting a pin that is on another net moves it.",
            """{"type":"object","properties":{"net":{"type":"string","description":"Net name, e.g. GND, +3V3, SDA"},"pins":{"type":"array","items":{"type":"string"}}},"required":["net","pins"]}"""),
        new("disconnect", "Remove pins from their nets.",
            """{"type":"object","properties":{"pins":{"type":"array","items":{"type":"string"}}},"required":["pins"]}"""),
        new("mark_no_connect", "Mark pins as intentionally unconnected.",
            """{"type":"object","properties":{"pins":{"type":"array","items":{"type":"string"}}},"required":["pins"]}"""),
        new("set_board", "Set board outline and stack-up. Either width+height (rectangle with optional corner radius, origin at 0,0) or an explicit outline polygon.",
            """
              {"type":"object","properties":{"width":{"type":"number"},"height":{"type":"number"},"corner_radius":{"type":"number"},
              "outline":{"type":"array","items":{"type":"array","items":{"type":"number"},"minItems":2,"maxItems":2}},
              "layers":{"type":"integer","enum":[2,4]},"thickness":{"type":"number"}}}
            """),
        new("set_design_rules", "Set manufacturing rules and net classes (e.g. wide tracks for power nets). Values in mm. Typical JLCPCB minimums: clearance 0.127, track 0.127, via 0.45/0.25.",
            """
              {"type":"object","properties":{"clearance":{"type":"number"},"track_width":{"type":"number"},"min_track_width":{"type":"number"},
              "via_diameter":{"type":"number"},"via_drill":{"type":"number"},"edge_clearance":{"type":"number"},
              "net_classes":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"track_width":{"type":"number"},"clearance":{"type":"number"},
                "via_diameter":{"type":"number"},"via_drill":{"type":"number"},"nets":{"type":"array","items":{"type":"string"}}},"required":["name","nets"]}}}}
            """),
        new("place_components", "Place components on the PCB at explicit positions (mm, board coordinates, +y down). Rotation in degrees counter-clockwise. Use this for parts whose position matters (connectors on edges, decoupling caps next to IC power pins, LEDs and buttons where the user can reach them).",
            """
              {"type":"object","properties":{"placements":{"type":"array","items":{"type":"object","properties":{
              "reference":{"type":"string"},"x":{"type":"number"},"y":{"type":"number"},"rotation":{"type":"number"},"side":{"type":"string","enum":["top","bottom"]},"lock":{"type":"boolean"}},
              "required":["reference","x","y"]}}},"required":["placements"]}
            """),
        new("auto_place", "Automatically place unplaced components near the parts they connect to. Pre-place important parts first with place_components.",
            """{"type":"object","properties":{"references":{"type":"array","items":{"type":"string"}},"replace_all":{"type":"boolean","description":"Also move already-placed (unlocked) parts."},"spacing":{"type":"number"}}}"""),
        new("autoroute", "Run the autorouter. You control the strategy: net order, nets to skip (e.g. GND when a pour will connect it), per-net layer locks, via cost. Returns which nets failed so you can adjust placement or rules and retry.",
            """
              {"type":"object","properties":{"nets":{"type":"array","items":{"type":"string"},"description":"Only route these nets."},
              "priority":{"type":"array","items":{"type":"string"},"description":"Route these first, in order (critical signals, then power)."},
              "skip_nets":{"type":"array","items":{"type":"string"}},
              "net_layers":{"type":"object","additionalProperties":{"type":"string","enum":["top","bottom"]}},
              "via_cost":{"type":"number","description":"mm-equivalent cost of a via (default 4)."},
              "grid":{"type":"number","description":"Routing grid in mm (default automatic)."},
              "passes":{"type":"integer"},"keep_existing":{"type":"boolean","description":"Keep previously auto-routed tracks."}}}
            """),
        new("clear_routing", "Remove tracks and vias (all, or only for some nets).",
            """{"type":"object","properties":{"nets":{"type":"array","items":{"type":"string"}},"only_auto":{"type":"boolean"}}}"""),
        new("add_copper_pour", "Add a copper pour (e.g. a GND plane) covering the board on a layer.",
            """{"type":"object","properties":{"net":{"type":"string"},"layer":{"type":"string","enum":["top","bottom"]},"clearance":{"type":"number"}},"required":["net","layer"]}"""),
        new("add_mounting_holes", "Add mounting holes in the board corners.",
            """{"type":"object","properties":{"diameter":{"type":"number"},"inset":{"type":"number"}}}"""),
        new("add_board_text", "Add silkscreen text to the board.",
            """{"type":"object","properties":{"text":{"type":"string"},"x":{"type":"number"},"y":{"type":"number"},"size":{"type":"number"},"layer":{"type":"string","enum":["top","bottom"]}},"required":["text","x","y"]}"""),
        new("run_drc", "Run design-rule and electrical checks. Returns errors/warnings with locations.",
            """{"type":"object","properties":{"max_items":{"type":"integer"}}}"""),
        new("tidy_schematic", "Re-arrange schematic symbols into a clean grid.", """{"type":"object","properties":{}}"""),
    ];

    // ======================= dispatch =======================

    public string Execute(string name, JsonElement input)
    {
        try
        {
            return name switch
            {
                "get_design" => GetDesign(Bool(input, "include_pins") ?? true),
                "list_library" => ListLibrary(),
                "add_component" => AddComponent(input),
                "define_footprint" => DefineFootprint(input),
                "update_component" => UpdateComponent(input),
                "remove_component" => RemoveComponents(input),
                "connect" => Connect(input),
                "disconnect" => Disconnect(input),
                "mark_no_connect" => NoConnect(input),
                "set_board" => SetBoard(input),
                "set_design_rules" => SetRules(input),
                "place_components" => PlaceComponents(input),
                "auto_place" => AutoPlace(input),
                "autoroute" => Autoroute(input),
                "clear_routing" => ClearRouting(input),
                "add_copper_pour" => AddPour(input),
                "add_mounting_holes" => AddHoles(input),
                "add_board_text" => AddText(input),
                "run_drc" => RunDrc(Int(input, "max_items") ?? 60),
                "tidy_schematic" => Do(() => { EditOps.TidySchematic(P); return Ok("Schematic tidied."); }),
                _ => Err($"Unknown tool {name}"),
            };
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    /// <summary>Tools that only read the design (no undo snapshot needed).</summary>
    public static bool IsReadOnly(string name) => name is "get_design" or "list_library" or "run_drc";

    static string Do(Func<string> f) => f();
    static string Ok(string msg, object? extra = null)
    {
        var o = new JsonObject { ["ok"] = true, ["message"] = msg };
        if (extra != null) o["data"] = JsonSerializer.SerializeToNode(extra, Json);
        return o.ToJsonString();
    }
    static string Err(string msg) => new JsonObject { ["ok"] = false, ["error"] = msg }.ToJsonString();

    // ---------- input helpers ----------
    static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double? Num(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    static int? Int(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    static bool? Bool(JsonElement e, string k) => e.TryGetProperty(k, out var v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False) ? v.GetBoolean() : null;
    static List<string> StrList(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
        ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];

    static (string Ref, string Pin) SplitPin(string s)
    {
        s = s.Trim();
        int i = s.IndexOf('.');
        if (i <= 0) throw new ArgumentException($"Pin '{s}' must be written REF.PIN, e.g. U1.3");
        return (s[..i], s[(i + 1)..]);
    }

    // ======================= tools =======================

    string GetDesign(bool includePins)
    {
        var p = P;
        var nl = NetlistBuilder.Build(p);
        var b = p.Board.OutlineBounds();
        var conn = Connectivity.Analyze(p, nl);
        var comps = new JsonArray();
        foreach (var c in p.Components.OrderBy(c => c.Ref))
        {
            var sym = p.Symbol(c);
            if (sym?.IsPower == true) continue;
            var o = new JsonObject
            {
                ["ref"] = c.Ref, ["value"] = c.Value, ["symbol"] = c.SymbolId, ["footprint"] = c.FootprintId,
                ["placed"] = c.Placed,
            };
            if (c.Mpn != "") o["mpn"] = c.Mpn;
            if (c.SupplierPart != "") o["lcsc"] = c.SupplierPart;
            if (c.Placed)
            {
                o["x"] = Math.Round(c.PcbPos.X, 3); o["y"] = Math.Round(c.PcbPos.Y, 3); o["rot"] = Math.Round(c.PcbRot, 1);
                if (c.OnBottom) o["side"] = "bottom";
                var cy = p.Courtyard(c);
                o["courtyard"] = $"{R(cy.MinX)},{R(cy.MinY)} to {R(cy.MaxX)},{R(cy.MaxY)}";
                if (c.Locked) o["locked"] = true;
            }
            else if (p.Footprint(c) is { } fp)
            {
                var pb = fp.Courtyard;
                o["size"] = $"{R(pb.Width)}x{R(pb.Height)} mm";
            }
            if (includePins && sym != null)
            {
                var pins = new JsonArray();
                foreach (var pin in sym.Pins)
                {
                    var net = nl.PinNets.TryGetValue((c.Ref, pin.Number), out var n) ? n : "";
                    var s = pin.Number + (pin.Name != "" && pin.Name != pin.Number ? ":" + pin.Name : "") + "=" + (net == "" ? "-" : net);
                    if (c.Placed && p.Pads(c).FirstOrDefault(x => x.Pad.Number == c.PadForPin(pin.Number)) is { } pad) s += $" @{R(pad.Position.X)},{R(pad.Position.Y)}";
                    pins.Add(s);
                }
                o["pins"] = pins;
            }
            comps.Add(o);
        }
        var nets = new JsonArray();
        foreach (var n in nl.Nets) nets.Add($"{n.Name} ({n.Nodes.Count}): " + string.Join(" ", n.Nodes.Select(x => x.Component.Ref + "." + x.Pin)));
        var rules = p.Board.Rules;
        var root = new JsonObject
        {
            ["project"] = p.Name,
            ["board"] = new JsonObject
            {
                ["size"] = $"{R(b.Width)} x {R(b.Height)} mm", ["origin"] = $"{R(b.MinX)},{R(b.MinY)}", ["layers"] = p.Board.CopperLayers,
                ["outline_points"] = p.Board.Outline.Count, ["tracks"] = p.Board.Tracks.Count, ["vias"] = p.Board.Vias.Count,
                ["pours"] = new JsonArray(p.Board.Zones.Select(z => (JsonNode)$"{z.Net} on {z.Layer.Display()}").ToArray()),
                ["unrouted_connections"] = conn.Airwires.Count,
            },
            ["rules"] = new JsonObject
            {
                ["clearance"] = rules.Clearance, ["track_width"] = rules.TrackWidth, ["via"] = $"{rules.ViaDiameter}/{rules.ViaDrill}",
                ["net_classes"] = new JsonArray(rules.NetClasses.Select(nc => (JsonNode)$"{nc.Name}: {nc.TrackWidth} mm [{string.Join(",", nc.Nets)}]").ToArray()),
            },
            ["components"] = comps,
            ["nets"] = nets,
            ["unconnected_pins"] = new JsonArray(nl.UnconnectedPins.Take(80).Select(x => (JsonNode)$"{x.Component.Ref}.{x.Pin.Number}{(x.Pin.Name != "" ? ":" + x.Pin.Name : "")}").ToArray()),
        };
        return root.ToJsonString();
    }

    static string ListLibrary()
    {
        var o = new JsonObject
        {
            ["symbols"] = new JsonArray(SymbolLibrary.Catalog.Select(s => (JsonNode)$"{s.Id}: {s.Description}").ToArray()),
            ["footprint_examples"] = new JsonArray(FootprintLibrary.Examples.Select(s => (JsonNode)s).ToArray()),
            ["footprint_patterns"] = new JsonArray(
                "R_/C_/L_/LED_/D_ + imperial size: 0201 0402 0603 0805 1206 1210 1812 2010 2512",
                "SOIC-N, SOIC-NW (wide), TSSOP-N, MSOP-N, SSOP-N, DIP-N",
                "QFN-N_WxH_Ppitch[_EPsize] e.g. QFN-24_4x4_P0.5 (exposed pad = pin N+1)",
                "TQFP-N_WxH_Ppitch / LQFP-N_WxH_Ppitch",
                "PinHeader_RxNN_Ppitch e.g. PinHeader_1x06_P2.54, JST_PH_1xN, JST_XH_1xN",
                "SOT-23, SOT-23-5, SOT-23-6, SOT-223 (tab=2), SOT-89, TO-252, TO-92, TO-220-3",
                "CP_Radial_Dd_Pp, LED_D5.0, Axial_P10.16, Crystal_HC49-4H, Crystal_SMD_3225-4P, SW_Tactile_6x6, SW_SMD_4x4",
                "USB_Micro_B, BarrelJack_5.5x2.1, TestPoint_Pad_D1.5, MountingHole_3.2mm, Inductor_SMD_4x4/6x6",
                "Anything else: use define_footprint with the datasheet's recommended land pattern."),
        };
        return o.ToJsonString();
    }

    string AddComponent(JsonElement e)
    {
        var p = P;
        var value = Str(e, "value") ?? "";
        var fp = Str(e, "footprint") ?? "";
        string symbolId;
        if (e.TryGetProperty("pins", out var pinsEl) && pinsEl.ValueKind == JsonValueKind.Array && pinsEl.GetArrayLength() > 0)
        {
            var name = Str(e, "name") ?? Str(e, "mpn") ?? value;
            symbolId = SafeId(name);
            var pins = pinsEl.EnumerateArray().Select(x => new SymbolLibrary.IcPin(
                Str(x, "number") ?? "", Str(x, "name") ?? "", ParsePinType(Str(x, "type")),
                Str(x, "side") switch { "left" => PinSide.Left, "right" => PinSide.Right, "top" => PinSide.Top, "bottom" => PinSide.Bottom, _ => null })).ToList();
            var dup = pins.GroupBy(x => x.Number).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) return Err($"Pin number {dup.Key} is used twice. Give every pin a unique number (use the footprint pad numbers).");
            var existing = p.GetSymbol(symbolId);
            if (existing == null || existing.Pins.Count != pins.Count || existing.Pins.Any(ep => !pins.Any(np => np.Number == ep.Number && np.Name == ep.Name)))
            {
                if (existing != null) symbolId = SafeId(name + "_" + Guid.NewGuid().ToString("N")[..4]);
                p.Symbols[symbolId] = SymbolLibrary.BuildIc(symbolId, name, pins, Str(e, "prefix") ?? "U");
            }
        }
        else
        {
            symbolId = Str(e, "symbol") ?? throw new ArgumentException("Give either a built-in `symbol` or a `pins` list.");
            if (Regex.Match(symbolId, @"^Conn_([12])x(\d+)$") is { Success: true } m) symbolId = $"Conn_{m.Groups[1].Value}x{int.Parse(m.Groups[2].Value):00}";
        }
        if (fp != "" && p.GetFootprint(fp) == null) return Err($"Unknown footprint '{fp}'. Use list_library for patterns or define_footprint to create it first.");
        var c = EditOps.AddComponent(p, symbolId, value, fp, Str(e, "reference"));
        c.Mpn = Str(e, "mpn") ?? ""; c.Manufacturer = Str(e, "manufacturer") ?? ""; c.Datasheet = Str(e, "datasheet") ?? "";
        c.SupplierPart = Str(e, "lcsc") ?? ""; c.Description = Str(e, "description") ?? "";
        var fpDef = p.Footprint(c);
        var sym = p.Symbol(c)!;
        string warn = "";
        if (fpDef != null)
        {
            var padNums = fpDef.Pads.Select(x => x.Number).ToHashSet();
            var missing = sym.Pins.Where(x => !padNums.Contains(c.PadForPin(x.Number))).Select(x => x.Number).ToList();
            if (missing.Count > 0) warn = $" Warning: pins {string.Join(",", missing)} have no matching pad on {fpDef.Id}.";
        }
        return Ok($"Added {c.Ref} ({value}) symbol={symbolId} footprint={(c.FootprintId == "" ? "none" : c.FootprintId)}.{warn}",
            new { reference = c.Ref, pins = sym.Pins.Select(x => x.Number + (x.Name != "" ? ":" + x.Name : "")).ToArray() });
    }

    static string SafeId(string s)
    {
        var r = new string(s.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray());
        return string.IsNullOrEmpty(r) ? "Part" : r;
    }

    static PinType ParsePinType(string? s) => s switch
    {
        "input" => PinType.Input, "output" => PinType.Output, "bidirectional" => PinType.Bidirectional, "power_in" => PinType.PowerIn,
        "power_out" => PinType.PowerOut, "open_collector" => PinType.OpenCollector, "no_connect" => PinType.NoConnect, "tri_state" => PinType.TriState, _ => PinType.Passive,
    };

    string DefineFootprint(JsonElement e)
    {
        var id = SafeId(Str(e, "id") ?? throw new ArgumentException("id required"));
        var pads = e.GetProperty("pads").EnumerateArray().Select(x => new PadDef
        {
            Number = Str(x, "number") ?? "",
            Position = new(Num(x, "x") ?? 0, Num(x, "y") ?? 0),
            Size = new(Num(x, "width") ?? 1, Num(x, "height") ?? 1),
            Drill = Num(x, "drill") ?? 0,
            Rotation = Num(x, "rotation") ?? 0,
            Shape = Str(x, "shape") switch { "circle" => PadShape.Circle, "oval" => PadShape.Oval, "rect" => PadShape.Rect, _ => (Num(x, "drill") ?? 0) > 0 ? PadShape.Circle : PadShape.RoundRect },
        }).ToList();
        if (pads.Count == 0) return Err("No pads given.");
        // sanity: overlapping pads with different numbers
        for (int i = 0; i < pads.Count; i++)
            for (int j = i + 1; j < pads.Count; j++)
                if (pads[i].Number != pads[j].Number && Shape.Distance(FootprintLibrary.PadShapeLocal(pads[i]), FootprintLibrary.PadShapeLocal(pads[j])) < 0.05)
                    return Err($"Pads {pads[i].Number} and {pads[j].Number} overlap or are closer than 0.05 mm — check the land pattern dimensions.");
        var fp = FootprintLibrary.Custom(id, pads, Num(e, "body_width") ?? 0, Num(e, "body_height") ?? 0, Str(e, "description") ?? "");
        fp.Height = Num(e, "height") ?? 1;
        P.Footprints[id] = fp;
        var pb = fp.PadBounds();
        return Ok($"Footprint {id} defined with {pads.Count} pads, extent {R(pb.Width)} x {R(pb.Height)} mm.");
    }

    string UpdateComponent(JsonElement e)
    {
        var p = P;
        var c = p.Find(Str(e, "reference") ?? "") ?? throw new ArgumentException("No such component");
        if (Str(e, "new_reference") is { } nr && nr != c.Ref)
        {
            if (p.Find(nr) != null) return Err($"{nr} already exists");
            c.Ref = nr;
        }
        if (Str(e, "value") is { } v) c.Value = v;
        if (Str(e, "footprint") is { } f)
        {
            if (f != "" && p.GetFootprint(f) == null) return Err($"Unknown footprint {f}");
            c.FootprintId = f;
        }
        if (Str(e, "mpn") is { } m) c.Mpn = m;
        if (Str(e, "manufacturer") is { } mf) c.Manufacturer = mf;
        if (Str(e, "datasheet") is { } ds) c.Datasheet = ds;
        if (Str(e, "lcsc") is { } l) c.SupplierPart = l;
        if (Str(e, "description") is { } d) c.Description = d;
        if (Bool(e, "do_not_populate") is { } dnp) c.DoNotPopulate = dnp;
        if (e.TryGetProperty("pin_map", out var pm) && pm.ValueKind == JsonValueKind.Object)
            foreach (var kv in pm.EnumerateObject()) c.PinMap[kv.Name] = kv.Value.GetString() ?? kv.Name;
        return Ok($"Updated {c.Ref}.");
    }

    string RemoveComponents(JsonElement e)
    {
        var removed = new List<string>();
        foreach (var r in StrList(e, "references"))
            if (P.Find(r) is { } c) { EditOps.RemoveComponent(P, c); removed.Add(r); }
        return Ok($"Removed {string.Join(", ", removed)}.");
    }

    string Connect(JsonElement e)
    {
        var net = (Str(e, "net") ?? "").Trim();
        if (net == "") return Err("net required");
        var pins = StrList(e, "pins").Select(SplitPin).ToList();
        var log = EditOps.ConnectPins(P, net, pins);
        var errors = log.Where(l => l.Contains("has no pin")).ToList();
        var nl = NetlistBuilder.Build(P);
        var n = nl.Find(net);
        return errors.Count > 0
            ? Err(string.Join("; ", errors) + $". Net {net} now has {n?.Nodes.Count ?? 0} pins.")
            : Ok($"Net {net}: {n?.Nodes.Count ?? 0} pins ({string.Join(" ", n?.Nodes.Select(x => x.Component.Ref + "." + x.Pin) ?? [])}).{(log.Count > 0 ? " " + string.Join("; ", log) : "")}");
    }

    string Disconnect(JsonElement e)
    {
        foreach (var (r, pin) in StrList(e, "pins").Select(SplitPin)) EditOps.DisconnectPin(P, r, pin);
        return Ok("Disconnected.");
    }

    string NoConnect(JsonElement e)
    {
        foreach (var (r, pin) in StrList(e, "pins").Select(SplitPin)) EditOps.MarkNoConnect(P, r, pin);
        return Ok("Marked as no-connect.");
    }

    string SetBoard(JsonElement e)
    {
        var b = P.Board;
        if (e.TryGetProperty("outline", out var ol) && ol.ValueKind == JsonValueKind.Array && ol.GetArrayLength() >= 3)
            b.Outline = ol.EnumerateArray().Select(pt => new Vec2(pt[0].GetDouble(), pt[1].GetDouble())).ToList();
        else if (Num(e, "width") is { } w && Num(e, "height") is { } h)
        {
            var r = Num(e, "corner_radius") ?? 0;
            b.Outline = RoundedRect(w, h, r);
        }
        if (Int(e, "layers") is { } l) b.CopperLayers = l >= 4 ? 4 : 2;
        if (Num(e, "thickness") is { } t) b.Thickness = t;
        var bb = b.OutlineBounds();
        return Ok($"Board is {R(bb.Width)} x {R(bb.Height)} mm, {b.CopperLayers} layers.");
    }

    public static List<Vec2> RoundedRect(double w, double h, double r)
    {
        if (r <= 0) return [new(0, 0), new(w, 0), new(w, h), new(0, h)];
        r = Math.Min(r, Math.Min(w, h) / 2);
        var pts = new List<Vec2>();
        void Arc(Vec2 c, double a0)
        {
            for (int i = 0; i <= 6; i++) { var a = (a0 + i * 15) * Math.PI / 180; pts.Add(c + new Vec2(Math.Cos(a), Math.Sin(a)) * r); }
        }
        Arc(new(r, r), 180); Arc(new(w - r, r), 270); Arc(new(w - r, h - r), 0); Arc(new(r, h - r), 90);
        return pts;
    }

    string SetRules(JsonElement e)
    {
        var r = P.Board.Rules;
        if (Num(e, "clearance") is { } c) r.Clearance = c;
        if (Num(e, "track_width") is { } tw) r.TrackWidth = tw;
        if (Num(e, "min_track_width") is { } mtw) r.MinTrackWidth = mtw;
        if (Num(e, "via_diameter") is { } vd) r.ViaDiameter = vd;
        if (Num(e, "via_drill") is { } vdr) r.ViaDrill = vdr;
        if (Num(e, "edge_clearance") is { } ec) r.EdgeClearance = ec;
        if (e.TryGetProperty("net_classes", out var ncs) && ncs.ValueKind == JsonValueKind.Array)
        {
            foreach (var nc in ncs.EnumerateArray())
            {
                var name = Str(nc, "name") ?? "Class";
                var cls = r.NetClasses.FirstOrDefault(x => x.Name == name) ?? new NetClass { Name = name, TrackWidth = r.TrackWidth, Clearance = r.Clearance, ViaDiameter = r.ViaDiameter, ViaDrill = r.ViaDrill };
                if (!r.NetClasses.Contains(cls)) r.NetClasses.Add(cls);
                if (Num(nc, "track_width") is { } w) cls.TrackWidth = w;
                if (Num(nc, "clearance") is { } cl) cls.Clearance = cl;
                if (Num(nc, "via_diameter") is { } d) cls.ViaDiameter = d;
                if (Num(nc, "via_drill") is { } dr) cls.ViaDrill = dr;
                var nets = StrList(nc, "nets");
                foreach (var other in r.NetClasses.Where(x => x != cls)) other.Nets.RemoveAll(nets.Contains);
                cls.Nets = nets;
            }
            r.NetClasses.RemoveAll(x => x.Nets.Count == 0);
        }
        return Ok($"Rules: clearance {r.Clearance}, track {r.TrackWidth}, via {r.ViaDiameter}/{r.ViaDrill}; classes: {string.Join("; ", r.NetClasses.Select(x => $"{x.Name} {x.TrackWidth}mm [{string.Join(",", x.Nets)}]"))}");
    }

    string PlaceComponents(JsonElement e)
    {
        var p = P;
        var done = new List<string>(); var problems = new List<string>();
        foreach (var pl in e.GetProperty("placements").EnumerateArray())
        {
            var c = p.Find(Str(pl, "reference") ?? "");
            if (c == null) { problems.Add($"{Str(pl, "reference")} not found"); continue; }
            if (p.Footprint(c) == null) { problems.Add($"{c.Ref} has no footprint"); continue; }
            c.PcbPos = new Vec2(Num(pl, "x") ?? 0, Num(pl, "y") ?? 0);
            if (Num(pl, "rotation") is { } rot) c.PcbRot = rot;
            if (Str(pl, "side") is { } side) c.OnBottom = side == "bottom";
            if (Bool(pl, "lock") is { } lk) c.Locked = lk;
            c.Placed = true;
            done.Add(c.Ref);
        }
        // report overlaps / out-of-board for the placed parts
        var outline = p.Board.Outline;
        var placedAll = p.Components.Where(o => o.Placed && p.Footprint(o) != null && !o.DoNotPopulate).ToList();
        var reported = new HashSet<string>();
        foreach (var r in done)
        {
            var c = p.Find(r)!; var cy = p.Courtyard(c);
            foreach (var corner in new[] { new Vec2(cy.MinX, cy.MinY), new Vec2(cy.MaxX, cy.MaxY), new Vec2(cy.MinX, cy.MaxY), new Vec2(cy.MaxX, cy.MinY) })
                if (!ProjectGeometry.PointInPolygon(outline, corner)) { problems.Add($"{r} extends outside the board"); break; }
            foreach (var o in placedAll)
            {
                if (o == c || o.OnBottom != c.OnBottom) continue;
                var key = string.CompareOrdinal(c.Ref, o.Ref) < 0 ? c.Ref + "|" + o.Ref : o.Ref + "|" + c.Ref;
                if (!reported.Add(key)) continue;
                var oc = p.Courtyard(o);
                if (Math.Min(oc.MaxX, cy.MaxX) - Math.Max(oc.MinX, cy.MinX) > 0.05 && Math.Min(oc.MaxY, cy.MaxY) - Math.Max(oc.MinY, cy.MinY) > 0.05)
                    problems.Add($"{r} overlaps {o.Ref}");
            }
        }
        return Ok($"Placed {done.Count} components." + (problems.Count > 0 ? " Problems: " + string.Join("; ", problems.Distinct()) : ""));
    }

    string AutoPlace(JsonElement e)
    {
        var refs = StrList(e, "references");
        int n = AutoPlacer.Place(P, new PlacerOptions { Refs = refs.Count > 0 ? refs : null, ReplaceAll = Bool(e, "replace_all") ?? false, Spacing = Num(e, "spacing") ?? 0.6 });
        var unplaced = P.Components.Where(c => !c.Placed && P.Footprint(c) != null && P.Symbol(c)?.IsPower != true && !c.DoNotPopulate).Select(c => c.Ref).ToList();
        return Ok($"Auto-placed {n} components." + (unplaced.Count > 0 ? $" Could not fit: {string.Join(", ", unplaced)} — enlarge the board or move parts." : ""));
    }

    /// <summary>Set by the host so long router runs can report progress.</summary>
    public IProgress<string>? Progress { get; set; }
    public CancellationToken Cancellation { get; set; }

    string Autoroute(JsonElement e)
    {
        var opt = new RouterOptions
        {
            Nets = StrList(e, "nets") is { Count: > 0 } n ? n : null,
            Priority = StrList(e, "priority"),
            SkipNets = StrList(e, "skip_nets"),
            ViaCost = Num(e, "via_cost") ?? 4,
            GridStep = Num(e, "grid") ?? 0,
            MaxPasses = Int(e, "passes") ?? 4,
            RipUpAutoRouted = !(Bool(e, "keep_existing") ?? false),
        };
        if (e.TryGetProperty("net_layers", out var nlEl) && nlEl.ValueKind == JsonValueKind.Object)
            foreach (var kv in nlEl.EnumerateObject()) opt.NetLayer[kv.Name] = kv.Value.GetString() == "bottom" ? Layer.BCu : Layer.FCu;
        var res = new AutoRouter(P, opt, Progress, Cancellation).Route();
        var drc = Drc.Run(P);
        return Ok(res.Summary() + " DRC: " + drc.Summary(), new { failed = res.FailedNets, vias = res.ViasAdded, length_mm = Math.Round(res.TrackLength) });
    }

    string ClearRouting(JsonElement e)
    {
        var nets = StrList(e, "nets"); bool onlyAuto = Bool(e, "only_auto") ?? false;
        int t = P.Board.Tracks.RemoveAll(x => (nets.Count == 0 || nets.Contains(x.Net)) && (!onlyAuto || x.Auto) && !x.Locked);
        int v = P.Board.Vias.RemoveAll(x => (nets.Count == 0 || nets.Contains(x.Net)) && (!onlyAuto || x.Auto) && !x.Locked);
        return Ok($"Removed {t} tracks and {v} vias.");
    }

    string AddPour(JsonElement e)
    {
        var net = Str(e, "net") ?? "GND";
        var layer = Str(e, "layer") == "top" ? Layer.FCu : Layer.BCu;
        P.Board.Zones.RemoveAll(z => z.Layer == layer && z.Net == net);
        P.Board.Zones.Add(new Zone { Net = net, Layer = layer, Outline = [.. P.Board.Outline], Clearance = Num(e, "clearance") ?? 0.3 });
        return Ok($"Added {net} pour on {layer.Display()}. Pads of {net} on that layer now count as connected through the pour.");
    }

    string AddHoles(JsonElement e)
    {
        var d = Num(e, "diameter") ?? 3.2; var inset = Num(e, "inset") ?? 3.5;
        var b = P.Board.OutlineBounds();
        P.Board.Holes.Clear();
        foreach (var pt in new[] { new Vec2(b.MinX + inset, b.MinY + inset), new Vec2(b.MaxX - inset, b.MinY + inset), new Vec2(b.MaxX - inset, b.MaxY - inset), new Vec2(b.MinX + inset, b.MaxY - inset) })
            P.Board.Holes.Add(new MountingHole { Position = pt, Diameter = d });
        return Ok($"Added 4 x {d} mm mounting holes, {inset} mm from the corners.");
    }

    string AddText(JsonElement e)
    {
        P.Board.Texts.Add(new BoardText { Text = Str(e, "text") ?? "", Position = new(Num(e, "x") ?? 0, Num(e, "y") ?? 0), Size = Num(e, "size") ?? 1.2, Layer = Str(e, "layer") == "bottom" ? Layer.BSilk : Layer.FSilk });
        return Ok("Text added.");
    }

    string RunDrc(int max)
    {
        var rep = Drc.Run(P);
        var items = rep.Violations.OrderBy(v => v.Severity).Take(max).Select(v => $"{v.Severity} {v.Code}: {v.Message}{(v.Location != Vec2.Zero ? $" @{R(v.Location.X)},{R(v.Location.Y)}" : "")}").ToArray();
        return Ok(rep.Summary(), new { violations = items, total = rep.Violations.Count });
    }
}
