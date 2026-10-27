using TraceMind.Core.Export;
using TraceMind.Core.Geometry;
using TraceMind.Core.KiCad;
using TraceMind.Core.Library;
using TraceMind.Core.Model;
using TraceMind.Core.Netlist;
using TraceMind.Core.Pcb;
using TraceMind.Core.Samples;
using Xunit.Abstractions;

namespace TraceMind.Tests;

public class CoreTests(ITestOutputHelper output)
{
    [Fact]
    public void ShapeDistances()
    {
        var a = Shape.Circle(new(0, 0), 1);
        var b = Shape.Circle(new(3, 0), 1);
        Assert.Equal(1, Shape.Distance(a, b), 6);
        var box = Shape.Box(new(0, 0), 2, 2, 0);
        Assert.Equal(1, Shape.Distance(box, Shape.Circle(new(3, 0), 1)), 6);
        Assert.True(Shape.Distance(box, Shape.Capsule(new(-5, 0), new(5, 0), 0.1)) < 0);
        var rotated = Shape.Box(new(0, 0), 4, 1, 90);
        Assert.Equal(0.5, Shape.Distance(rotated, Shape.Circle(new(1, 0), 0)), 6);
    }

    [Theory]
    [InlineData("R_0603", 2)] [InlineData("SOIC-8", 8)] [InlineData("QFN-32_5x5_P0.5", 33)] [InlineData("TQFP-44_10x10_P0.8", 44)]
    [InlineData("PinHeader_2x05_P2.54", 10)] [InlineData("SOT-23-5", 5)] [InlineData("DIP-14", 14)] [InlineData("JST_PH_1x02", 2)]
    [InlineData("CP_Radial_D5.0_P2.00", 2)] [InlineData("TSSOP-20", 20)] [InlineData("LQFP-64_10x10_P0.5", 64)]
    public void FootprintsGenerate(string id, int pads)
    {
        var fp = FootprintLibrary.Get(id);
        Assert.NotNull(fp);
        Assert.Equal(pads, fp!.Pads.Count);
        // Pads within a footprint must not overlap each other unless they share a number.
        for (int i = 0; i < fp.Pads.Count; i++)
            for (int j = i + 1; j < fp.Pads.Count; j++)
                if (fp.Pads[i].Number != fp.Pads[j].Number)
                    Assert.True(Shape.Distance(FootprintLibrary.PadShapeLocal(fp.Pads[i]), FootprintLibrary.PadShapeLocal(fp.Pads[j])) > 0.1, $"{id}: pads {fp.Pads[i].Number} and {fp.Pads[j].Number} too close");
    }

    [Fact]
    public void AllExampleFootprintsResolve()
    {
        foreach (var id in FootprintLibrary.Examples) Assert.True(FootprintLibrary.Get(id) != null, id);
        foreach (var (id, _) in SymbolLibrary.Catalog.Where(c => !c.Id.Contains("xN"))) Assert.True(SymbolLibrary.Get(id) != null, id);
    }

    [Fact]
    public void NetlistFromLabelsAndWires()
    {
        var p = new Project();
        var r1 = EditOps.AddComponent(p, "R", "1k");
        var r2 = EditOps.AddComponent(p, "R", "1k");
        var pinA = EditOps.FindPin(p, r1.Ref, "2")!; var pinB = EditOps.FindPin(p, r2.Ref, "1")!;
        var c1 = new Vec2(pinA.Position.X + 5.08, pinA.Position.Y); var c2 = new Vec2(c1.X, pinB.Position.Y);
        p.Schematic.Wires.Add(new Wire { A = pinA.Position, B = c1 });
        p.Schematic.Wires.Add(new Wire { A = c1, B = c2 });
        p.Schematic.Wires.Add(new Wire { A = c2, B = pinB.Position });
        EditOps.ConnectPins(p, "VIN", [(r1.Ref, "1")]);
        EditOps.ConnectPins(p, "VIN", [(r2.Ref, "2")]);
        var nl = NetlistBuilder.Build(p);
        Assert.Equal(2, nl.Nets.Count);
        Assert.Contains(nl.Nets, n => n.Name == "VIN" && n.Nodes.Count == 2);
        Assert.Contains(nl.Nets, n => n.Name.StartsWith("Net-(") && n.Nodes.Count == 2);
    }

    [Fact]
    public void SampleRoutesClean()
    {
        var p = SampleProjects.AttinyBlinker();
        var before = Drc.Run(p);
        output.WriteLine("Before: " + before.Summary());
        foreach (var v in before.Violations.Where(v => v.Severity == Severity.Error && v.Code != "unrouted")) output.WriteLine("  " + v);
        Assert.True(before.Unrouted > 10);
        var res = new AutoRouter(p).Route();
        output.WriteLine(res.Summary());
        var after = Drc.Run(p);
        output.WriteLine("After: " + after.Summary());
        foreach (var v in after.Violations.Where(v => v.Severity != Severity.Info)) output.WriteLine("  " + v);
        Assert.Empty(res.FailedNets);
        Assert.Equal(0, after.Unrouted);
        Assert.Equal(0, after.Violations.Count(v => v.Code is "clearance" or "short"));
    }

    [Fact]
    public void AutoPlaceThenRoute()
    {
        var p = SampleProjects.AttinyBlinker();
        foreach (var c in p.Components) c.Placed = false;
        p.Board.Outline = [new(0, 0), new(50, 0), new(50, 35), new(0, 35)];
        p.Board.Holes.Clear();
        int n = AutoPlacer.Place(p);
        output.WriteLine($"placed {n}");
        Assert.True(n >= 13);
        var drc0 = Drc.Run(p);
        Assert.DoesNotContain(drc0.Violations, v => v.Code == "courtyard");
        var res = new AutoRouter(p).Route();
        output.WriteLine(res.Summary());
        var drc = Drc.Run(p);
        output.WriteLine(drc.Summary());
        foreach (var v in drc.Violations.Where(v => v.Severity == Severity.Error)) output.WriteLine("  " + v);
        Assert.Equal(0, drc.Violations.Count(v => v.Code is "clearance" or "short"));
    }

    [Fact]
    public void GerberExportAndKiCadRoundTrip()
    {
        var p = SampleProjects.AttinyBlinker();
        new AutoRouter(p).Route();
        p.Board.Zones.Add(new Zone { Net = "GND", Layer = Layer.BCu, Outline = [.. p.Board.Outline] });
        var dir = Path.Combine(Path.GetTempPath(), "tm_test_" + Guid.NewGuid().ToString("N"));
        var res = GerberExporter.Export(p, dir);
        Assert.Contains(res.Files, f => f.EndsWith("F_Cu.gtl"));
        Assert.Contains(res.Files, f => f.EndsWith("PTH.drl"));
        Assert.Contains(res.Files, f => f.EndsWith(".zip"));
        var top = File.ReadAllText(res.Files.First(f => f.EndsWith("F_Cu.gtl")));
        Assert.Contains("%FSLAX46Y46*%", top);
        Assert.EndsWith("M02*", top.TrimEnd());
        var bom = File.ReadAllText(res.Files.First(f => f.EndsWith("BOM.csv")));
        Assert.Contains("C25804", bom);

        var kicad = KiCadPcb.Export(p);
        File.WriteAllText(Path.Combine(dir, "board.kicad_pcb"), kicad);
        var imported = KiCadPcb.Import(kicad).Project;
        Assert.Equal(p.Components.Count(c => c.Placed), imported.Components.Count);
        Assert.Equal(p.Board.Holes.Count, imported.Board.Holes.Count);
        Assert.Equal(p.Board.Tracks.Count, imported.Board.Tracks.Count);
        // Pad positions survive the round trip
        var origPads = p.AllPads().Select(x => (x.Component.Ref, x.Pad.Number, x.Position)).OrderBy(x => x.Ref).ThenBy(x => x.Number).ThenBy(x => x.Position.X).ToList();
        var newPads = imported.AllPads().Select(x => (x.Component.Ref, x.Pad.Number, x.Position)).OrderBy(x => x.Ref).ThenBy(x => x.Number).ThenBy(x => x.Position.X).ToList();
        Assert.Equal(origPads.Count, newPads.Count);
        for (int i = 0; i < origPads.Count; i++) Assert.True(origPads[i].Position.ApproxEquals(newPads[i].Position, 0.001), $"{origPads[i]} vs {newPads[i]}");
        var drc = Drc.Run(imported);
        output.WriteLine("Imported DRC: " + drc.Summary());
        Assert.Equal(0, drc.Unrouted);

        var net = KiCadNetlist.Export(p);
        var (fromNet, warnings) = KiCadNetlist.Import(net);
        foreach (var w in warnings) output.WriteLine(w);
        var nl1 = NetlistBuilder.Build(p); var nl2 = NetlistBuilder.Build(fromNet);
        Assert.Equal(nl1.Nets.Count, nl2.Nets.Count);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void ProjectJsonRoundTrip()
    {
        var p = SampleProjects.AttinyBlinker();
        var json = ProjectFile.Serialize(p);
        var q = ProjectFile.Deserialize(json);
        Assert.Equal(p.Components.Count, q.Components.Count);
        Assert.Equal(NetlistBuilder.Build(p).Nets.Count, NetlistBuilder.Build(q).Nets.Count);
        Assert.Equal(p.Board.Outline, q.Board.Outline);
    }
}
