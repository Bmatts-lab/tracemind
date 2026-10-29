using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using TraceMind.AI;
using TraceMind.App;
using TraceMind.App.Views;
using TraceMind.Core.Samples;

// Renders the real TraceMind window off-screen and saves PNG screenshots.
var outDir = args.Length > 0 ? args[0] : "shots";
Directory.CreateDirectory(outDir);
AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

void Pump(int ms = 200)
{
    var end = DateTime.Now.AddMilliseconds(ms);
    while (DateTime.Now < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
}
void Shot(MainWindow w, string name)
{
    Pump(300);
    var frame = w.CaptureRenderedFrame();
    frame?.Save(Path.Combine(outDir, name));
    Console.WriteLine("saved " + name);
}

var w = new MainWindow { Width = 1600, Height = 960 };
w.Show();
Pump();
Shot(w, "01-empty.png");
w.LoadForAutomation(SampleProjects.AttinyBlinker());
Pump();
w.SchematicView.ZoomToFit();
Shot(w, "02-schematic.png");
var u1 = w.Session.Project.Find("U1")!;
w.SchematicView.CenterOn(u1.SchPos + new TraceMind.Core.Geometry.Vec2(20, 5), 9);
Shot(w, "02b-schematic-zoom.png");
w.ShowEditor(1);
Pump();
w.PcbView.ZoomToFit();
Shot(w, "03-pcb-unrouted.png");
var t = w.RouteForAutomation();
while (!t.IsCompleted) Pump(50);
w.PcbView.ZoomToFit();
Shot(w, "04-pcb-routed.png");
w.Session.Mutate(() => w.Session.Project.Board.Zones.Add(new TraceMind.Core.Model.Zone { Net = "GND", Layer = TraceMind.Core.Model.Layer.BCu, Outline = [.. w.Session.Project.Board.Outline] }));
w.DrcForAutomation();
Shot(w, "05-pcb-pour.png");
w.ChatView.AddUser("Add a 3.3V regulator for the ESP32 and route the board");
w.ChatView.Handle(new AgentEvent(AgentEventKind.Thinking, "The ESP32-C3 draws ~350 mA peaks during Wi-Fi TX, so a 500 mA+ LDO with low dropout is needed."));
w.ChatView.Handle(new AgentEvent(AgentEventKind.WebSearch, "Searching: AP2112K-3.3 LCSC stock"));
w.ChatView.Handle(new AgentEvent(AgentEventKind.ToolCall, "add_component", "{}"));
w.ChatView.Handle(new AgentEvent(AgentEventKind.ToolResult, "add_component", "Added U3 (AP2112K-3.3) symbol=AP2112K-3.3 footprint=SOT-23-5."));
w.ChatView.Handle(new AgentEvent(AgentEventKind.ToolCall, "autoroute", "{}"));
w.ChatView.Handle(new AgentEvent(AgentEventKind.ToolResult, "autoroute", "Routed all 11 nets in 0.9s — 300 mm of track, 9 vias (1 pass). DRC: 0 errors"));
w.ChatView.Handle(new AgentEvent(AgentEventKind.Text, "Added an **AP2112K-3.3** (LCSC `C51118`) — 600 mA, 250 mV dropout:\n- 1 µF input and output caps per the datasheet\n- EN tied to VIN\n\nThe board is routed with **0 DRC errors**."));
w.ChatView.Handle(new AgentEvent(AgentEventKind.Done, ""));
Shot(w, "06-chat.png");
w.ShowEditor(0);
w.SchematicView.ZoomToFit();
Shot(w, "07-schematic-after.png");

// ---- v0.3: single-sided board, wire links, AI board image ----
{
    var p = new TraceMind.Core.Model.Project { Name = "Single-sided demo" };
    p.Board.Outline = [new(0, 0), new(40, 0), new(40, 25), new(0, 25)];
    p.Board.CopperLayers = 1;
    var ic = TraceMind.Core.Model.EditOps.AddComponent(p, "Conn_2x04", "U1", "DIP-8", "U1"); ic.PcbPos = new(20, 12.5); ic.Placed = true;
    var j1 = TraceMind.Core.Model.EditOps.AddComponent(p, "Conn_1x04", "IN", "PinHeader_1x04_P2.54", "J1"); j1.PcbPos = new(4, 12.5); j1.Placed = true;
    var j2 = TraceMind.Core.Model.EditOps.AddComponent(p, "Conn_1x04", "OUT", "PinHeader_1x04_P2.54", "J2"); j2.PcbPos = new(36, 12.5); j2.Placed = true;
    TraceMind.Core.Model.EditOps.ConnectPins(p, "A", [("J1", "1"), ("U1", "8"), ("J2", "4")]);
    TraceMind.Core.Model.EditOps.ConnectPins(p, "B", [("J1", "4"), ("U1", "1"), ("J2", "1")]);
    TraceMind.Core.Model.EditOps.ConnectPins(p, "C", [("J1", "2"), ("U1", "5")]);
    TraceMind.Core.Model.EditOps.ConnectPins(p, "D", [("J2", "3"), ("U1", "4")]);
    p.Board.Holes.Add(new TraceMind.Core.Model.MountingHole { Position = new(3, 3), Diameter = 3.2 });
    w.LoadForAutomation(p);
    Pump();
    var r1 = w.RunToolForAutomation("autoroute", "{\"wire_links\":true}");
    while (!r1.IsCompleted) Pump(50);
    Console.WriteLine("autoroute: " + r1.Result[..Math.Min(400, r1.Result.Length)]);
    w.ShowEditor(1); w.PcbView.ZoomToFit();
    Shot(w, "08-single-sided-wirelinks.png");
    var r2 = w.RunToolForAutomation("get_board", "{\"image\":true,\"include_pads\":false}");
    while (!r2.IsCompleted) Pump(50);
    var node = System.Text.Json.Nodes.JsonNode.Parse(r2.Result)!;
    var b64 = node["data"]?["__image_png"]?.GetValue<string>();
    Console.WriteLine("board image bytes: " + (b64 == null ? 0 : Convert.FromBase64String(b64).Length));
    if (b64 != null) File.WriteAllBytes(Path.Combine(outDir, "09-ai-board-image.png"), Convert.FromBase64String(b64));
    var r3 = w.RunToolForAutomation("move_mounting_hole", "{\"reference\":\"H1\",\"x\":37,\"y\":22}");
    while (!r3.IsCompleted) Pump(50);
    Console.WriteLine("move hole: " + r3.Result);
}
