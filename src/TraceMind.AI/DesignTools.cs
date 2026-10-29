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
              "layers":{"type":"integer","enum":[1,2,4],"description":"1 = single-sided board (e.g. CNC-milled)"},
              "copper_side":{"type":"string","enum":["top","bottom"],"description":"For 1-layer boards: which side has copper (default bottom)."},
              "thickness":{"type":"number"}}}
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
              "passes":{"type":"integer"},"keep_existing":{"type":"boolean","description":"Keep previously auto-routed tracks."},
              "wire_links":{"type":"boolean","description":"Single-layer boards: when a net is blocked, insert a wire link (jumper, 2 THT pads, 1.0 mm drill) across the blocking track and continue."},
              "link_spacings":{"type":"array","items":{"type":"number"},"description":"Allowed wire-link pad spacings in mm (default 7.62, 10.16, 12.7)."},
              "neck_down":{"type":"object","additionalProperties":{"type":"number"},"description":"Per-net minimum width: the net uses its class width where there is room and narrows to this in tight spots, e.g. {\"+5V\":0.5}. Default: down to the default track width."},
              "allow_neck_down":{"type":"boolean","description":"Default true."}}}
            """),
        new("clear_routing", "Remove tracks and vias (all, or only for some nets).",
            """{"type":"object","properties":{"nets":{"type":"array","items":{"type":"string"}},"only_auto":{"type":"boolean"}}}"""),
        new("add_copper_pour", "Add a copper pour (e.g. a GND plane) covering the board on a layer.",
            """{"type":"object","properties":{"net":{"type":"string"},"layer":{"type":"string","enum":["top","bottom"]},"clearance":{"type":"number"}},"required":["net","layer"]}"""),
        new("add_mounting_holes", "Add four mounting holes in the board corners (replaces existing holes unless replace=false).",
            """{"type":"object","properties":{"diameter":{"type":"number"},"inset":{"type":"number"},"replace":{"type":"boolean"}}}"""),
        new("add_mounting_hole", "Add one mounting hole (non-plated) at a position.",
            """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"diameter":{"type":"number"},"reference":{"type":"string"}},"required":["x","y"]}"""),
        new("move_mounting_hole", "Move and/or resize one mounting hole by reference (H1, H2…; see get_design / get_board).",
            """{"type":"object","properties":{"reference":{"type":"string"},"x":{"type":"number"},"y":{"type":"number"},"diameter":{"type":"number"}},"required":["reference"]}"""),
        new("delete_mounting_hole", "Delete mounting holes by reference.",
            """{"type":"object","properties":{"references":{"type":"array","items":{"type":"string"}}},"required":["references"]}"""),
        new("get_board", "See the PCB: outline, every track (id, net, layer, width, points), via, pad (ref.pad, net, position, size, drill), mounting hole and unrouted connection. Optionally limit to a region, and/or get a rendered image of the board (with a mm ruler) to look at.",
            """
              {"type":"object","properties":{"region":{"type":"object","properties":{"x1":{"type":"number"},"y1":{"type":"number"},"x2":{"type":"number"},"y2":{"type":"number"}}},
              "include_pads":{"type":"boolean","description":"Default true."},
              "image":{"type":"boolean","description":"Also return a rendered PNG of the board (or region)."}}}
            """),
        new("draw_track", "Draw a track by hand as a polyline of points on one layer and net. Width is per segment: give `width` for all, or `widths` (one per segment). Returns the new track ids and any clearance problems.",
            """
              {"type":"object","properties":{"net":{"type":"string"},"layer":{"type":"string","enum":["top","bottom","in1","in2"]},
              "points":{"type":"array","minItems":2,"items":{"type":"array","items":{"type":"number"},"minItems":2,"maxItems":2},"description":"[[x,y],[x,y],…] in mm"},
              "width":{"type":"number"},"widths":{"type":"array","items":{"type":"number"}},"lock":{"type":"boolean","description":"Protect from the autorouter's rip-up (default true)."}},
              "required":["net","points"]}
            """),
        new("update_track", "Move a track segment's end points, or change its width / layer / net.",
            """{"type":"object","properties":{"id":{"type":"string"},"start":{"type":"array","items":{"type":"number"}},"end":{"type":"array","items":{"type":"number"}},"width":{"type":"number"},"layer":{"type":"string","enum":["top","bottom","in1","in2"]},"net":{"type":"string"}},"required":["id"]}"""),
        new("delete_tracks", "Delete tracks and/or vias by id, or all copper of a net / layer / inside a circle.",
            """
              {"type":"object","properties":{"ids":{"type":"array","items":{"type":"string"}},"net":{"type":"string"},"layer":{"type":"string","enum":["top","bottom","in1","in2"]},
              "near":{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"radius":{"type":"number"}}},"include_locked":{"type":"boolean"}}}
            """),
        new("set_track_width", "Change the width of existing track segments (by id, or a whole net, optionally only inside a rectangle). Reports clearance problems so you can keep tight spots narrow.",
            """
              {"type":"object","properties":{"width":{"type":"number"},"ids":{"type":"array","items":{"type":"string"}},"net":{"type":"string"},
              "region":{"type":"object","properties":{"x1":{"type":"number"},"y1":{"type":"number"},"x2":{"type":"number"},"y2":{"type":"number"}}},
              "only_where_clear":{"type":"boolean","description":"Skip segments that would violate clearance at the new width (default true)."}},"required":["width"]}
            """),
        new("add_via", "Place a via (multi-layer boards).",
            """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"net":{"type":"string"},"diameter":{"type":"number"},"drill":{"type":"number"}},"required":["x","y","net"]}"""),
        new("add_wire_link", "Place a wire link (jumper: two THT pads 2.0 mm, 1.0 mm drill, joined by a wire on the component side) to hop a net over other copper. Give the two pad positions, or a centre + angle + spacing (7.62 / 10.16 / 12.7 mm).",
            """
              {"type":"object","properties":{"net":{"type":"string"},"x1":{"type":"number"},"y1":{"type":"number"},"x2":{"type":"number"},"y2":{"type":"number"},
              "x":{"type":"number"},"y":{"type":"number"},"angle":{"type":"number","description":"Degrees, 0 = along +x"},"spacing":{"type":"number"}},"required":["net"]}
            """),
        new("save_kicad", "Save the board as a KiCad .kicad_pcb (and optionally a KiCad netlist). With no path it overwrites the KiCad file the project was imported from, keeping a .bak backup.",
            """{"type":"object","properties":{"path":{"type":"string"},"netlist":{"type":"boolean","description":"Also write a .net next to it."}}}"""),
        new("export_fab", "Write manufacturing files: Gerbers for each copper layer and Edge.Cuts plus an Excellon drill file in mm, all on the same origin. mode 'cnc' = copper/outline/drill only (for milling); 'fab' adds mask, silk, paste, BOM, CPL. Can mirror the bottom side for milling, or use KiCad's kicad-cli if installed.",
            """
              {"type":"object","properties":{"folder":{"type":"string","description":"Output folder (default: next to the project / Documents\\TraceMind)."},
              "mode":{"type":"string","enum":["fab","cnc"]},"origin":{"type":"string","enum":["absolute","lower_left"],"description":"lower_left puts the board's lower-left corner at 0,0 (handy for CNC)."},
              "mirror_bottom":{"type":"boolean"},"use_kicad":{"type":"boolean","description":"Use kicad-cli if installed."}}}
            """),
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
                "add_mounting_hole" => AddHole(input),
                "move_mounting_hole" => MoveHole(input),
                "delete_mounting_hole" => DeleteHoles(input),
                "get_board" => GetBoard(input),
                "draw_track" => DrawTrack(input),
                "update_track" => UpdateTrack(input),
                "delete_tracks" => DeleteTracks(input),
                "set_track_width" => SetTrackWidth(input),
                "add_via" => AddVia(input),
                "add_wire_link" => AddWireLink(input),
                "export_fab" => ExportFab(input),
                "save_kicad" => SaveKiCad(input),
                "add_board_text" => AddText(input),
                "run_drc" => RunDrc(Int(input, "max_items") ?? 60),
                "tidy_schematic" => Do(() => { EditOps.TidySchematic(P); return Ok("Schematic tidied."); }),
                _ => Err($"Unknown tool {name}"),
            };
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    /// <summary>Tools that only read the design (no undo snapshot needed).</summary>
    public static bool IsReadOnly(string name) => name is "get_design" or "list_library" or "run_drc" or "get_board" or "export_fab" or "save_kicad";

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
                ["copper"] = p.Board.CopperLayers <= 1 ? $"single-sided, copper on {p.Board.SingleSideLayer.Display()}" : $"{p.Board.CopperLayers} layers",
                ["mounting_holes"] = new JsonArray(Holes().Select(h => (JsonNode)$"{h.Ref}: {R(h.Diameter)} mm at {R(h.Position.X)},{R(h.Position.Y)}").ToArray()),
                ["wire_links"] = new JsonArray(p.Components.Where(c => c.IsWireLink()).Select(c => (JsonNode)$"{c.Ref} {c.FootprintId}").ToArray()),
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
        if (Int(e, "layers") is { } l) b.CopperLayers = l >= 4 ? 4 : l <= 1 ? 1 : 2;
        if (Str(e, "copper_side") is { } side) b.SingleSideLayer = side == "top" ? Layer.FCu : Layer.BCu;
        if (b.CopperLayers <= 1)
        {
            // Move tracks onto the single copper layer and drop vias (they can't exist on a 1-layer board).
            foreach (var tr in b.Tracks) tr.Layer = b.SingleSideLayer;
            b.Vias.Clear();
            foreach (var z in b.Zones) z.Layer = b.SingleSideLayer;
        }
        if (Num(e, "thickness") is { } t) b.Thickness = t;
        var bb = b.OutlineBounds();
        return Ok($"Board is {R(bb.Width)} x {R(bb.Height)} mm, " + (b.CopperLayers <= 1 ? $"single-sided ({b.SingleSideLayer.Display()})." : $"{b.CopperLayers} layers."));
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
        opt.WireLinks = Bool(e, "wire_links") ?? false;
        if (e.TryGetProperty("link_spacings", out var ls) && ls.ValueKind == JsonValueKind.Array && ls.GetArrayLength() > 0)
            opt.LinkSpacings = ls.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToArray();
        opt.AllowNeckDown = Bool(e, "allow_neck_down") ?? true;
        if (e.TryGetProperty("neck_down", out var nd) && nd.ValueKind == JsonValueKind.Object)
            foreach (var kv in nd.EnumerateObject()) if (kv.Value.ValueKind == JsonValueKind.Number) opt.NeckDown[kv.Name] = kv.Value.GetDouble();
        var res = new AutoRouter(P, opt, Progress, Cancellation).Route();
        var drc = Drc.Run(P);
        return Ok(res.Summary() + " DRC: " + drc.Summary(), new
        {
            failed = res.FailedNets,
            wire_links_added = res.LinksAdded,
            // where each failed connection is blocked — place a wire link / move parts there
            failures = res.Failures.Select(f => new
            {
                net = f.Net, from = f.From, to = f.To, note = f.Note,
                crossings = f.Crossings.Select(c => new { blocked_by = c.BlockedBy, entry = new[] { Math.Round(c.Entry.X, 2), Math.Round(c.Entry.Y, 2) }, exit = new[] { Math.Round(c.Exit.X, 2), Math.Round(c.Exit.Y, 2) }, length_mm = Math.Round(c.Length, 2) }),
            }),
            vias = res.ViasAdded, length_mm = Math.Round(res.TrackLength),
        });
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
        if (Bool(e, "replace") ?? true) P.Board.Holes.Clear();
        foreach (var pt in new[] { new Vec2(b.MinX + inset, b.MinY + inset), new Vec2(b.MaxX - inset, b.MinY + inset), new Vec2(b.MaxX - inset, b.MaxY - inset), new Vec2(b.MinX + inset, b.MaxY - inset) })
            P.Board.Holes.Add(new MountingHole { Position = pt, Diameter = d });
        P.Board.EnsureHoleRefs();
        return Ok($"Added 4 x {d} mm mounting holes, {inset} mm from the corners: " + string.Join(", ", Holes().Select(h => $"{h.Ref} @{R(h.Position.X)},{R(h.Position.Y)}")));
    }

    List<MountingHole> Holes() { P.Board.EnsureHoleRefs(); return P.Board.Holes; }

    string AddHole(JsonElement e)
    {
        var h = new MountingHole { Position = new(Num(e, "x") ?? 0, Num(e, "y") ?? 0), Diameter = Num(e, "diameter") ?? 3.2, Ref = Str(e, "reference") ?? "" };
        if (h.Ref != "" && P.Board.FindHole(h.Ref) != null) return Err($"{h.Ref} already exists.");
        P.Board.Holes.Add(h); P.Board.EnsureHoleRefs();
        return Ok($"Added {h.Ref}: {R(h.Diameter)} mm at {R(h.Position.X)},{R(h.Position.Y)}." + HoleProblems(h));
    }

    string MoveHole(JsonElement e)
    {
        var h = P.Board.FindHole(Str(e, "reference") ?? "");
        if (h == null) return Err($"No mounting hole '{Str(e, "reference")}'. Holes: {string.Join(", ", Holes().Select(x => x.Ref))}");
        h.Position = new(Num(e, "x") ?? h.Position.X, Num(e, "y") ?? h.Position.Y);
        if (Num(e, "diameter") is { } d) h.Diameter = d;
        return Ok($"{h.Ref} is now {R(h.Diameter)} mm at {R(h.Position.X)},{R(h.Position.Y)}." + HoleProblems(h));
    }

    string DeleteHoles(JsonElement e)
    {
        var refs = StrList(e, "references");
        var gone = Holes().Where(h => refs.Contains(h.Ref, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var h in gone) P.Board.Holes.Remove(h);
        return gone.Count == 0 ? Err("No matching holes.") : Ok($"Deleted {string.Join(", ", gone.Select(h => h.Ref))}.");
    }

    string HoleProblems(MountingHole h)
    {
        var nl = NetlistBuilder.Build(P);
        var shape = Shape.Circle(h.Position, h.Diameter / 2);
        var hits = Connectivity.CollectCopper(P, nl).Where(c => Shape.Distance(c.Shape, shape) < P.Board.Rules.Clearance).Select(c => c.Describe()).Distinct().Take(5).ToList();
        if (!ProjectGeometry.PointInPolygon(P.Board.Outline, h.Position)) hits.Insert(0, "outside the board");
        return hits.Count > 0 ? " Warning — too close to: " + string.Join(", ", hits) : "";
    }

    // ======================= board view & manual copper =======================

    /// <summary>Host-provided renderer for board images (PNG). Arguments: region or null.</summary>
    public Func<TraceMind.Core.Geometry.Rect?, byte[]?>? RenderBoardImage { get; set; }
    /// <summary>Host-provided default output folder for exports.</summary>
    public Func<string>? DefaultExportFolder { get; set; }

    static Layer ParseLayer(string? s, Layer def) => s switch { "top" => Layer.FCu, "bottom" => Layer.BCu, "in1" => Layer.In1Cu, "in2" => Layer.In2Cu, _ => def };
    Layer DefaultLayer => P.Board.CopperLayers <= 1 ? P.Board.SingleSideLayer : Layer.FCu;
    static string Id(Guid g) => ProjectGeometry.ShortId(g);
    static string LayerName(Layer l) => l switch { Layer.FCu => "top", Layer.BCu => "bottom", Layer.In1Cu => "in1", _ => "in2" };

    static TraceMind.Core.Geometry.Rect? Region(JsonElement e)
    {
        if (!e.TryGetProperty("region", out var r) || r.ValueKind != JsonValueKind.Object) return null;
        return TraceMind.Core.Geometry.Rect.FromPoints(new(Num(r, "x1") ?? 0, Num(r, "y1") ?? 0), new(Num(r, "x2") ?? 0, Num(r, "y2") ?? 0));
    }

    string GetBoard(JsonElement e)
    {
        var p = P; var b = p.Board;
        var region = Region(e);
        bool In(Vec2 v) => region is not { } r || r.Contains(v);
        bool SegIn(Vec2 a, Vec2 c) => region is not { } r || r.Contains(a) || r.Contains(c) || r.Contains((a + c) / 2);
        var nl = NetlistBuilder.Build(p);
        var conn = Connectivity.Analyze(p, nl);
        var o = new JsonObject
        {
            ["units"] = "mm, origin top-left, +y down",
            ["copper"] = b.CopperLayers <= 1 ? $"single-sided ({LayerName(b.SingleSideLayer)})" : $"{b.CopperLayers} layers",
            ["outline"] = string.Join(" ", b.Outline.Select(v => $"{R(v.X)},{R(v.Y)}")),
            ["rules"] = $"clearance {b.Rules.Clearance}, track {b.Rules.TrackWidth}, min track {b.Rules.MinTrackWidth}, edge {b.Rules.EdgeClearance}",
            // track: id net layer width x1,y1 x2,y2
            ["tracks"] = new JsonArray(b.Tracks.Where(t => SegIn(t.A, t.B)).Select(t =>
                (JsonNode)$"{Id(t.Id)} {t.Net} {LayerName(t.Layer)} w{R(t.Width)} {R(t.A.X)},{R(t.A.Y)} {R(t.B.X)},{R(t.B.Y)}{(t.Locked ? " locked" : "")}").ToArray()),
            ["vias"] = new JsonArray(b.Vias.Where(v => In(v.Position)).Select(v => (JsonNode)$"{Id(v.Id)} {v.Net} {R(v.Position.X)},{R(v.Position.Y)} d{R(v.Diameter)}/{R(v.Drill)}").ToArray()),
            ["holes"] = new JsonArray(Holes().Where(h => In(h.Position)).Select(h => (JsonNode)$"{h.Ref} {R(h.Position.X)},{R(h.Position.Y)} d{R(h.Diameter)}").ToArray()),
            ["unrouted"] = new JsonArray(conn.Airwires.Where(a => SegIn(a.A, a.B)).Select(a => (JsonNode)$"{a.Net}: {R(a.A.X)},{R(a.A.Y)} -> {R(a.B.X)},{R(a.B.Y)}").ToArray()),
        };
        if (Bool(e, "include_pads") ?? true)
            o["pads"] = new JsonArray(p.AllPads().Where(pd => In(pd.Position)).Select(pd =>
            {
                var net = nl.NetOfPad(pd.Component, pd.Pad.Number);
                var kind = pd.Pad.IsThrough ? $"tht d{R(pd.Pad.Drill)}" : (pd.OnBottom ? "smd bottom" : "smd top");
                return (JsonNode)$"{pd.Component.Ref}.{pd.Pad.Number} {(net == "" ? "-" : net)} {R(pd.Position.X)},{R(pd.Position.Y)} {R(pd.Pad.Size.X)}x{R(pd.Pad.Size.Y)} {pd.Pad.Shape.ToString().ToLowerInvariant()} {kind}";
            }).ToArray());
        if ((Bool(e, "image") ?? false))
        {
            var png = RenderBoardImage?.Invoke(region);
            if (png != null) o["__image_png"] = Convert.ToBase64String(png);
            else o["image_note"] = "Image rendering isn't available here.";
        }
        return Ok($"{b.Tracks.Count} tracks, {b.Vias.Count} vias, {b.Holes.Count} holes, {conn.Airwires.Count} unrouted connections.", o);
    }

    /// <summary>Clearance problems for some new/changed copper (list of short messages).</summary>
    List<string> ClearanceProblems(IEnumerable<Track> tracks, IEnumerable<Via>? vias = null)
    {
        var p = P; var rules = p.Board.Rules;
        var nl = NetlistBuilder.Build(p);
        var copper = Connectivity.CollectCopper(p, nl);
        var probs = new List<string>();
        void Check(Shape shape, string net, Func<CopperItem, bool> sameLayer, string what, Guid selfId)
        {
            foreach (var it in copper)
            {
                if ((it.Track?.Id ?? it.Via?.Id) == selfId) continue;
                if (it.Net == net && net != "") continue;
                if (!sameLayer(it)) continue;
                var clr = Math.Max(rules.ClearanceFor(net), rules.ClearanceFor(it.Net));
                var d = Shape.Distance(shape, it.Shape);
                if (d < clr - 1e-4) probs.Add($"{what} is {R(Math.Max(0, d))} mm from {it.Describe()} [{(it.Net == "" ? "no net" : it.Net)}] (needs {R(clr)})");
            }
            foreach (var h in p.Board.Holes)
                if (Shape.Distance(shape, Shape.Circle(h.Position, h.Diameter / 2)) < rules.Clearance) probs.Add($"{what} touches mounting hole {h.Ref}");
            var pts = shape.ToOutline(2);
            if (pts.Any(pt => !ProjectGeometry.PointInPolygon(p.Board.Outline, pt))) probs.Add($"{what} leaves the board");
        }
        foreach (var t in tracks) Check(ProjectGeometry.TrackShape(t), t.Net, it => it.OnLayer(t.Layer), $"track {Id(t.Id)}", t.Id);
        foreach (var v in vias ?? []) Check(ProjectGeometry.ViaShape(v), v.Net, _ => true, $"via {Id(v.Id)}", v.Id);
        return probs.Distinct().Take(20).ToList();
    }

    string DrawTrack(JsonElement e)
    {
        var net = Str(e, "net") ?? "";
        var layer = ParseLayer(Str(e, "layer"), DefaultLayer);
        if (!P.Board.CopperLayerList().Contains(layer)) return Err($"This board has no {LayerName(layer)} copper layer.");
        var pts = e.GetProperty("points").EnumerateArray().Select(pt => new Vec2(pt[0].GetDouble(), pt[1].GetDouble())).ToList();
        if (pts.Count < 2) return Err("Need at least two points.");
        var widths = e.TryGetProperty("widths", out var ws) && ws.ValueKind == JsonValueKind.Array ? ws.EnumerateArray().Select(x => x.GetDouble()).ToList() : [];
        double w0 = Num(e, "width") ?? P.Board.Rules.WidthFor(net);
        bool lk = Bool(e, "lock") ?? true;
        var added = new List<Track>();
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            if (pts[i].ApproxEquals(pts[i + 1])) continue;
            var w = i < widths.Count ? widths[i] : w0;
            if (w < P.Board.Rules.MinTrackWidth - 1e-6) return Err($"Segment {i + 1} width {w} is below the minimum track width {P.Board.Rules.MinTrackWidth}.");
            added.Add(new Track { A = pts[i], B = pts[i + 1], Width = w, Layer = layer, Net = net, Locked = lk });
        }
        P.Board.Tracks.AddRange(added);
        var probs = ClearanceProblems(added);
        var nl = NetlistBuilder.Build(P);
        if (net != "" && nl.Find(net) == null) probs.Insert(0, $"note: no schematic net is called '{net}'");
        return Ok($"Drew {added.Count} segments on {LayerName(layer)} for {net}: {string.Join(" ", added.Select(t => Id(t.Id)))}." +
            (probs.Count > 0 ? " Problems: " + string.Join("; ", probs) : " No clearance problems."));
    }

    Track? FindTrack(string id) => P.Board.Tracks.FirstOrDefault(t => Id(t.Id).Equals(id, StringComparison.OrdinalIgnoreCase) || t.Id.ToString("N").StartsWith(id, StringComparison.OrdinalIgnoreCase));

    string UpdateTrack(JsonElement e)
    {
        var t = FindTrack(Str(e, "id") ?? "");
        if (t == null) return Err($"No track '{Str(e, "id")}'. Use get_board to list track ids.");
        if (e.TryGetProperty("start", out var a) && a.ValueKind == JsonValueKind.Array) t.A = new(a[0].GetDouble(), a[1].GetDouble());
        if (e.TryGetProperty("end", out var b) && b.ValueKind == JsonValueKind.Array) t.B = new(b[0].GetDouble(), b[1].GetDouble());
        if (Num(e, "width") is { } w) t.Width = w;
        if (Str(e, "layer") is { } l) t.Layer = ParseLayer(l, t.Layer);
        if (Str(e, "net") is { } n) t.Net = n;
        t.Auto = false;
        var probs = ClearanceProblems([t]);
        return Ok($"Track {Id(t.Id)}: {R(t.A.X)},{R(t.A.Y)} -> {R(t.B.X)},{R(t.B.Y)} w{R(t.Width)} {LayerName(t.Layer)}." + (probs.Count > 0 ? " Problems: " + string.Join("; ", probs) : ""));
    }

    string DeleteTracks(JsonElement e)
    {
        var ids = StrList(e, "ids");
        var net = Str(e, "net"); var layerS = Str(e, "layer");
        bool locked = Bool(e, "include_locked") ?? true;
        Vec2? c = null; double rad = 0;
        if (e.TryGetProperty("near", out var nr) && nr.ValueKind == JsonValueKind.Object) { c = new(Num(nr, "x") ?? 0, Num(nr, "y") ?? 0); rad = Num(nr, "radius") ?? 1; }
        if (ids.Count == 0 && net == null && layerS == null && c == null) return Err("Say which copper to delete: ids, net, layer and/or near.");
        bool Match(Guid id, string n, Layer? l, Func<Vec2, double> dist, bool isLocked) =>
            (ids.Count == 0 || ids.Any(x => Id(id).Equals(x, StringComparison.OrdinalIgnoreCase))) && (net == null || n == net)
            && (layerS == null || l == null || l == ParseLayer(layerS, l.Value)) && (c == null || dist(c.Value) <= rad) && (locked || !isLocked);
        int t = P.Board.Tracks.RemoveAll(x => Match(x.Id, x.Net, x.Layer, v => Shape.PointSegDistance(v, x.A, x.B), x.Locked));
        int vcount = P.Board.Vias.RemoveAll(x => Match(x.Id, x.Net, null, v => v.DistanceTo(x.Position), x.Locked));
        return Ok($"Deleted {t} tracks and {vcount} vias.");
    }

    string SetTrackWidth(JsonElement e)
    {
        var w = Num(e, "width") ?? 0;
        if (w < P.Board.Rules.MinTrackWidth - 1e-6) return Err($"Width below minimum {P.Board.Rules.MinTrackWidth}.");
        var ids = StrList(e, "ids"); var net = Str(e, "net"); var region = Region(e);
        bool onlyClear = Bool(e, "only_where_clear") ?? true;
        var sel = P.Board.Tracks.Where(t => (ids.Count == 0 || ids.Any(x => Id(t.Id).Equals(x, StringComparison.OrdinalIgnoreCase))) && (net == null || t.Net == net)
            && (region == null || region.Value.Contains(t.A) || region.Value.Contains(t.B))).ToList();
        if (ids.Count == 0 && net == null) return Err("Give track ids or a net.");
        int changed = 0; var kept = new List<string>();
        foreach (var t in sel)
        {
            var old = t.Width; t.Width = w;
            if (onlyClear && w > old && ClearanceProblems([t]).Count > 0) { t.Width = old; kept.Add(Id(t.Id)); }
            else changed++;
        }
        var probs = onlyClear ? [] : ClearanceProblems(sel);
        return Ok($"Set {changed} segments to {R(w)} mm." + (kept.Count > 0 ? $" Kept {kept.Count} narrower where {R(w)} mm would break clearance: {string.Join(" ", kept.Take(30))}." : "")
            + (probs.Count > 0 ? " Problems: " + string.Join("; ", probs) : ""));
    }

    string AddVia(JsonElement e)
    {
        if (P.Board.CopperLayers <= 1) return Err("Single-sided board: use add_wire_link instead of a via.");
        var net = Str(e, "net") ?? "";
        var v = new Via { Position = new(Num(e, "x") ?? 0, Num(e, "y") ?? 0), Net = net, Diameter = Num(e, "diameter") ?? P.Board.Rules.ViaDiameterFor(net), Drill = Num(e, "drill") ?? P.Board.Rules.ViaDrillFor(net) };
        P.Board.Vias.Add(v);
        var probs = ClearanceProblems([], [v]);
        return Ok($"Via {Id(v.Id)} at {R(v.Position.X)},{R(v.Position.Y)}." + (probs.Count > 0 ? " Problems: " + string.Join("; ", probs) : ""));
    }

    string AddWireLink(JsonElement e)
    {
        var net = Str(e, "net") ?? "";
        if (net == "") return Err("net required");
        Vec2 a, b;
        if (Num(e, "x1") is { } x1 && Num(e, "y1") is { } y1 && Num(e, "x2") is { } x2 && Num(e, "y2") is { } y2) { a = new(x1, y1); b = new(x2, y2); }
        else
        {
            var c = new Vec2(Num(e, "x") ?? 0, Num(e, "y") ?? 0);
            var ang = (Num(e, "angle") ?? 0) * Math.PI / 180; var sp = Num(e, "spacing") ?? 10.16;
            var d = new Vec2(Math.Cos(ang), Math.Sin(ang)) * (sp / 2);
            a = c - d; b = c + d;
        }
        double spacing = Math.Round(a.DistanceTo(b), 3);
        if (spacing < 3) return Err("Pads are too close for a wire link (min 3 mm).");
        var comp = EditOps.AddComponent(P, "Jumper", "Wire link", "WireLink_P" + spacing.ToString("0.###", CultureInfo.InvariantCulture));
        comp.Fields["WireLink"] = "manual";
        comp.PcbPos = (a + b) / 2; comp.PcbRot = -Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI; comp.Placed = true;
        EditOps.ConnectPins(P, net, [(comp.Ref, "1"), (comp.Ref, "2")]);
        var nl = NetlistBuilder.Build(P);
        var copper = Connectivity.CollectCopper(P, nl);
        var probs = new List<string>();
        foreach (var pad in P.Pads(comp))
            foreach (var it in copper.Where(it => it.Pad?.Component != comp && it.Net != net))
                if (Shape.Distance(pad.Shape, it.Shape) < P.Board.Rules.Clearance) probs.Add($"pad {pad.Pad.Number} hits {it.Describe()} [{it.Net}]");
        return Ok($"Added wire link {comp.Ref} for {net}: pads at {R(a.X)},{R(a.Y)} and {R(b.X)},{R(b.Y)} ({spacing} mm). Route each side to its pad." +
            (probs.Count > 0 ? " Problems: " + string.Join("; ", probs.Distinct().Take(10)) : ""));
    }

    string SaveKiCad(JsonElement e)
    {
        var path = Str(e, "path");
        if (string.IsNullOrWhiteSpace(path)) path = P.KiCadSource;
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(DefaultExportFolder?.Invoke() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TraceMind"),
                TraceMind.Core.Export.GerberExporter.Sanitize(P.Name) + ".kicad_pcb");
        if (!path.EndsWith(".kicad_pcb", StringComparison.OrdinalIgnoreCase)) path += ".kicad_pcb";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bool existed = File.Exists(path);
        TraceMind.Core.KiCad.KiCadPcb.SaveTo(P, path);
        var msg = $"Saved {path}" + (existed ? " (previous version kept as .bak)." : ".");
        if (Bool(e, "netlist") ?? false)
        {
            var net = Path.ChangeExtension(path, ".net");
            File.WriteAllText(net, TraceMind.Core.KiCad.KiCadNetlist.Export(P));
            msg += $" Netlist: {net}.";
        }
        return Ok(msg);
    }

    string ExportFab(JsonElement e)
    {
        var folder = Str(e, "folder");
        if (string.IsNullOrWhiteSpace(folder))
            folder = Path.Combine(DefaultExportFolder?.Invoke() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TraceMind"),
                TraceMind.Core.Export.GerberExporter.Sanitize(P.Name) + "-fab");
        bool cnc = Str(e, "mode") == "cnc";
        bool mirror = Bool(e, "mirror_bottom") ?? false;
        if (Bool(e, "use_kicad") ?? false)
        {
            if (TraceMind.Core.Export.KiCadCli.Find() == null) return Err("kicad-cli isn't installed (KiCad 7+). Call export_fab without use_kicad to use the built-in exporter.");
            var kr = Task.Run(() => TraceMind.Core.Export.KiCadCli.ExportAsync(P, folder, cnc, mirror)).GetAwaiter().GetResult();
            return Ok($"kicad-cli wrote {kr.Files.Count} files to {folder}.", new { files = kr.Files.Select(Path.GetFileName), notes = kr.Warnings });
        }
        var res = TraceMind.Core.Export.GerberExporter.Export(P, folder, new TraceMind.Core.Export.ExportOptions
        {
            Assembly = !cnc, MirrorBottom = mirror, Origin = Str(e, "origin") ?? (cnc ? "lower_left" : "absolute"),
        });
        return Ok($"Wrote {res.Files.Count} files to {folder}.", new { files = res.Files.Select(Path.GetFileName), notes = res.Warnings });
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
