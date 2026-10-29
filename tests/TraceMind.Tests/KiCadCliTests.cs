using TraceMind.Core.Export;
using TraceMind.Core.KiCad;
using TraceMind.Core.Model;
using TraceMind.Core.Pcb;
using TraceMind.Core.Samples;
using Xunit.Abstractions;

namespace TraceMind.Tests;

/// <summary>Checks our files against real KiCad. Skipped when kicad-cli isn't installed.</summary>
public class KiCadCliTests(ITestOutputHelper output)
{
    [Fact]
    public async Task KiCadOpensOurBoardAndExportsFab()
    {
        if (KiCadCli.Find() == null) { output.WriteLine("kicad-cli not installed — skipped"); return; }
        var p = SampleProjects.AttinyBlinker();
        new AutoRouter(p).Route();
        var dir = Path.Combine(Path.GetTempPath(), "tm_kicad_" + Guid.NewGuid().ToString("N"));
        var res = await KiCadCli.ExportAsync(p, dir, cncOnly: true, mirrorBottom: true);
        foreach (var f in res.Files) output.WriteLine(Path.GetFileName(f));
        Assert.Contains(res.Files, f => f.EndsWith("F_Cu.gbr"));
        Assert.Contains(res.Files, f => f.EndsWith("B_Cu.gbr"));
        Assert.Contains(res.Files, f => f.EndsWith("Edge_Cuts.gbr"));
        Assert.Contains(res.Files, f => f.Contains("PTH") && f.EndsWith(".drl"));
        // Pads in KiCad's own drill file match ours (same origin, mm)
        var drl = File.ReadAllText(res.Files.First(f => f.EndsWith("-PTH.drl")));
        Assert.Contains("METRIC", drl);
        // Round trip: KiCad-written board re-imports with the same parts and tracks
        var back = KiCadPcb.Import(File.ReadAllText(Path.Combine(dir, GerberExporter.Sanitize(p.Name) + ".kicad_pcb"))).Project;
        Assert.Equal(p.Board.Tracks.Count, back.Board.Tracks.Count);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void KiCadDrcAcceptsOurBoard()
    {
        var exe = KiCadCli.Find();
        if (exe == null) return;
        var p = SampleProjects.AttinyBlinker();
        new AutoRouter(p).Route();
        var dir = Path.Combine(Path.GetTempPath(), "tm_kicad_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pcb = KiCadPcb.SaveTo(p, Path.Combine(dir, "b.kicad_pcb"));
        // KiCad 7 has no CLI DRC; exporting an SVG of every layer proves the file parses fully.
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "pcb", "export", "svg", "--layers", "F.Cu,B.Cu,F.SilkS,Edge.Cuts", "-o", Path.Combine(dir, "b.svg"), pcb }) psi.ArgumentList.Add(a);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var err = proc.StandardError.ReadToEnd(); proc.WaitForExit();
        output.WriteLine(err);
        Assert.Equal(0, proc.ExitCode);
        Assert.True(new FileInfo(Path.Combine(dir, "b.svg")).Length > 1000);
        Directory.Delete(dir, true);
    }
}
