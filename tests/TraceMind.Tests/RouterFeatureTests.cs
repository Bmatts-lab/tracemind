using TraceMind.Core.Geometry;
using TraceMind.Core.Library;
using TraceMind.Core.Model;
using TraceMind.Core.Netlist;
using TraceMind.Core.Pcb;
using Xunit.Abstractions;

namespace TraceMind.Tests;

public class RouterFeatureTests(ITestOutputHelper output)
{
    static Component AddPart(Project p, string reference, string fpId, IEnumerable<PadDef> pads, Vec2 pos)
    {
        var padList = pads.ToList();
        p.Footprints[fpId] = FootprintLibrary.Custom(fpId, padList);
        var symId = "SYM_" + fpId;
        p.Symbols[symId] = SymbolLibrary.BuildIc(symId, fpId, padList.Select(x => x.Number).Distinct().Select(n => new SymbolLibrary.IcPin(n, n)));
        var c = EditOps.AddComponent(p, symId, fpId, fpId, reference);
        c.PcbPos = pos; c.Placed = true;
        return c;
    }

    static PadDef Tht(string n, double x, double y) => new() { Number = n, Position = new(x, y), Size = new(1.7, 1.7), Drill = 1.0, Shape = PadShape.Circle };

    /// <summary>Two connectors hugging the left/right edges of a single-layer board; nets X and Y must cross.</summary>
    static Project CrossingBoard()
    {
        var p = new Project { Name = "crossing" };
        p.Board.Outline = [new(0, 0), new(30, 0), new(30, 12), new(0, 12)];
        p.Board.CopperLayers = 1; p.Board.SingleSideLayer = Layer.BCu;
        AddPart(p, "J1", "EdgeConnL", [Tht("1", 0, -4.8), Tht("2", 0, 4.8)], new(1.2, 6));
        AddPart(p, "J2", "EdgeConnR", [Tht("1", 0, -4.8), Tht("2", 0, 4.8)], new(28.8, 6));
        EditOps.ConnectPins(p, "X", [("J1", "1"), ("J2", "2")]);
        EditOps.ConnectPins(p, "Y", [("J1", "2"), ("J2", "1")]);
        return p;
    }

    [Fact]
    public void SingleLayerFailureIsDiagnosed()
    {
        var p = CrossingBoard();
        var res = new AutoRouter(p).Route();
        output.WriteLine(res.Summary());
        output.WriteLine(res.Details());
        Assert.Single(res.FailedNets);
        Assert.Contains(res.Failures, f => f.Crossings.Count >= 1 && f.Crossings[0].BlockedBy.Contains("track"));
        Assert.All(p.Board.Tracks, t => Assert.Equal(Layer.BCu, t.Layer));
        Assert.Empty(p.Board.Vias);
    }

    [Fact]
    public void SingleLayerWireLinkInserted()
    {
        var p = CrossingBoard();
        var res = new AutoRouter(p, new RouterOptions { WireLinks = true }).Route();
        output.WriteLine(res.Summary());
        output.WriteLine(res.Details());
        Assert.Empty(res.FailedNets);
        Assert.NotEmpty(res.LinksAdded);
        var link = p.Find(res.LinksAdded[0])!;
        Assert.StartsWith("WireLink_P", link.FootprintId);
        var fp = p.Footprint(link)!;
        Assert.All(fp.Pads, pad => { Assert.Equal(1.0, pad.Drill); Assert.Equal(2.0, pad.Size.X); });
        var drc = Drc.Run(p);
        output.WriteLine(drc.Summary());
        foreach (var v in drc.Violations.Where(v => v.Severity == Severity.Error)) output.WriteLine("  " + v);
        Assert.Equal(0, drc.Unrouted);
        Assert.Equal(0, drc.Violations.Count(v => v.Code is "clearance" or "short"));
    }

    [Fact]
    public void PowerNetNecksDownOnlyInTheGap()
    {
        var p = new Project { Name = "neck" };
        p.Board.Outline = [new(0, 0), new(50, 0), new(50, 20), new(0, 20)];
        p.Board.CopperLayers = 1;
        p.Board.Rules.NetClasses.Add(new NetClass { Name = "Power", TrackWidth = 1.2, Nets = ["+5V"] });
        AddPart(p, "A", "PadA", [Tht("1", 0, 0)], new(5, 10));
        AddPart(p, "B", "PadB", [Tht("1", 0, 0)], new(45, 10));
        // a wall of unconnected pads across the board with one 1.5 mm opening
        var wall = new List<PadDef>(); int k = 1;
        foreach (var y in new[] { -9.0, -7.0, -5.0, -3.0, -1.0, 2.2, 4.2, 6.2, 8.2, 10.2 }) wall.Add(Tht((k++).ToString(), 0, y - 0.6));
        AddPart(p, "W", "Wall", wall, new(25, 10));
        EditOps.ConnectPins(p, "+5V", [("A", "1"), ("B", "1")]);
        var res = new AutoRouter(p).Route();
        output.WriteLine(res.Summary() + " " + res.Details());
        Assert.Empty(res.FailedNets);
        var widths = p.Board.Tracks.Select(t => Math.Round(t.Width, 3)).Distinct().OrderBy(x => x).ToList();
        output.WriteLine("widths: " + string.Join(", ", widths));
        Assert.Contains(1.2, widths);
        Assert.True(widths[0] < 1.2);
        var drc = Drc.Run(p);
        output.WriteLine(drc.Summary());
        Assert.Equal(0, drc.Violations.Count(v => v.Code is "clearance" or "short"));
        Assert.Equal(0, drc.Unrouted);
    }
}
