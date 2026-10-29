using System.Text.Json;
using TraceMind.AI;
using TraceMind.Core.Model;
using TraceMind.Core.Samples;
using Xunit.Abstractions;

namespace TraceMind.Tests;

public class NewToolsTests(ITestOutputHelper output)
{
    [Fact]
    public void AssistantCopperHoleAndExportTools()
    {
        var p = SampleProjects.AttinyBlinker();
        var t = new DesignTools(() => p);
        string Run(string name, string json)
        {
            var r = t.Execute(name, JsonDocument.Parse(json).RootElement);
            output.WriteLine($"{name}: {(r.Length > 300 ? r[..300] + "…" : r)}");
            Assert.DoesNotContain("\"ok\":false", r);
            return r;
        }
        // holes
        Assert.Contains("H1", Run("get_design", "{\"include_pins\":false}"));
        Run("move_mounting_hole", "{\"reference\":\"H2\",\"x\":40,\"y\":26}");
        Assert.Equal(new TraceMind.Core.Geometry.Vec2(40, 26), p.Board.FindHole("H2")!.Position);
        Run("add_mounting_hole", "{\"x\":42,\"y\":3,\"diameter\":2.2}");
        Run("delete_mounting_hole", "{\"references\":[\"H3\"]}");
        Assert.Equal(2, p.Board.Holes.Count);
        // tracks
        var drawn = Run("draw_track", "{\"net\":\"MOSI\",\"layer\":\"bottom\",\"points\":[[20,28],[30,28],[30,26]],\"widths\":[0.4,0.25]}");
        Assert.Equal(2, p.Board.Tracks.Count);
        Assert.Contains(p.Board.Tracks, x => x.Width == 0.4);
        var id = TraceMind.Core.Model.ProjectGeometry.ShortId(p.Board.Tracks[0].Id);
        Run("update_track", $"{{\"id\":\"{id}\",\"end\":[31,28]}}");
        Run("set_track_width", "{\"net\":\"MOSI\",\"width\":0.3}");
        var board = Run("get_board", "{\"include_pads\":true}");
        Assert.Contains(id, board);
        Run("delete_tracks", $"{{\"ids\":[\"{id}\"]}}");
        Assert.Single(p.Board.Tracks);
        // wire link
        Run("add_wire_link", "{\"net\":\"MOSI\",\"x\":10,\"y\":27,\"angle\":0,\"spacing\":7.62}");
        Assert.Contains(p.Components, c => c.IsWireLink());
        // single layer + routing with links
        Run("set_board", "{\"layers\":1,\"copper_side\":\"top\"}");
        Assert.All(p.Board.Tracks, x => Assert.Equal(Layer.FCu, x.Layer));
        // exports
        var dir = Path.Combine(Path.GetTempPath(), "tm_tools_" + Guid.NewGuid().ToString("N"));
        var ex = Run("export_fab", $"{{\"folder\":{JsonSerializer.Serialize(dir)},\"mode\":\"cnc\",\"mirror_bottom\":true}}");
        Assert.True(File.Exists(Directory.GetFiles(dir, "*F_Cu.gtl").Single()));
        Assert.Empty(Directory.GetFiles(dir, "*Mask*"));
        var drl = File.ReadAllText(Directory.GetFiles(dir, "*-PTH.drl").Single());
        Assert.Contains("METRIC", drl);
        Run("save_kicad", $"{{\"path\":{JsonSerializer.Serialize(Path.Combine(dir, "x.kicad_pcb"))},\"netlist\":true}}");
        Assert.True(File.Exists(Path.Combine(dir, "x.net")));
        Directory.Delete(dir, true);
    }
}
