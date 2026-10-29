using System.Diagnostics;
using TraceMind.Core.KiCad;
using TraceMind.Core.Model;

namespace TraceMind.Core.Export;

/// <summary>Optional: produce Gerbers / drill files with KiCad's own exporter when KiCad (7+) is installed.</summary>
public static class KiCadCli
{
    public static string? Find()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "kicad-cli.exe" } : new[] { "kicad-cli" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Programs" })
            {
                var kicad = Path.Combine(root, "KiCad");
                if (Directory.Exists(kicad))
                    dirs.AddRange(Directory.GetDirectories(kicad).OrderByDescending(d => d).Select(d => Path.Combine(d, "bin")));
            }
        }
        else dirs.AddRange(["/usr/bin", "/usr/local/bin", "/Applications/KiCad/KiCad.app/Contents/MacOS"]);
        foreach (var d in dirs)
            foreach (var n in names)
            {
                try { var p = Path.Combine(d.Trim('"'), n); if (File.Exists(p)) return p; } catch { }
            }
        return null;
    }

    /// <summary>Export via kicad-cli: Gerbers for every copper layer + Edge.Cuts, and an Excellon drill file in mm (same origin).</summary>
    public static async Task<ExportResult> ExportAsync(Project project, string folder, bool cncOnly, bool mirrorBottom, CancellationToken ct = default)
    {
        var exe = Find() ?? throw new InvalidOperationException("kicad-cli was not found. Install KiCad 7 or newer, or use TraceMind's built-in exporter.");
        Directory.CreateDirectory(folder);
        var name = GerberExporter.Sanitize(project.Name);
        var pcb = Path.Combine(folder, name + ".kicad_pcb");
        await File.WriteAllTextAsync(pcb, KiCadPcb.Export(project), ct);
        var layers = string.Join(",", project.Board.CopperLayerList().Select(l => l.KiCadName()).Append("Edge.Cuts")
            .Concat(cncOnly ? [] : new[] { "F.Mask", "B.Mask", "F.SilkS", "B.SilkS", "F.Paste", "B.Paste" }));
        var log = new List<string>();
        await Run(exe, ct, log, "pcb", "export", "gerbers", "--output", folder + Path.DirectorySeparatorChar, "--layers", layers, "--no-protel-ext", pcb);
        await Run(exe, ct, log, "pcb", "export", "drill", "--output", folder + Path.DirectorySeparatorChar, "--format", "excellon", "--excellon-units", "mm",
            "--drill-origin", "absolute", "--excellon-separate-th", pcb);
        if (mirrorBottom)
        {
            var mdir = Path.Combine(folder, "mirrored");
            Directory.CreateDirectory(mdir);
            await Run(exe, ct, log, "pcb", "export", "drill", "--output", mdir + Path.DirectorySeparatorChar, "--format", "excellon", "--excellon-units", "mm",
                "--drill-origin", "absolute", "--excellon-mirror-y", "--excellon-separate-th", pcb);
            log.Add("kicad-cli can mirror drill files only; for a mirrored bottom-copper Gerber use TraceMind's built-in exporter (mirror option).");
        }
        var files = Directory.GetFiles(folder).Concat(mirrorBottom ? Directory.GetFiles(Path.Combine(folder, "mirrored")) : []).ToList();
        return new ExportResult(files, folder, log);
    }

    static async Task Run(string exe, CancellationToken ct, List<string> log, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"kicad-cli {string.Join(' ', args.Take(3))} failed: {(await stderr).Trim()} {(await stdout).Trim()}");
    }
}
