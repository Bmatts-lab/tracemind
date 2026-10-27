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
