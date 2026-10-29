using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TraceMind.AI;
using TraceMind.App.Controls;
using TraceMind.Core.Export;
using TraceMind.Core.Geometry;
using TraceMind.Core.KiCad;
using TraceMind.Core.Model;
using TraceMind.Core.Pcb;
using TraceMind.Core.Samples;

namespace TraceMind.App.Views;

public partial class MainWindow : Window
{
    readonly DesignSession session;
    readonly AgentSettings settings = AgentSettings.Load();
    readonly DesignTools tools;
    ClaudeAgent apiAgent = null!;
    ClaudeCodeAgent planAgent = null!;
    IDesignAgent agent => UsePlan ? planAgent : apiAgent;
    /// <summary>Use the Claude subscription via Claude Code unless the user picked the API (or only has a key).</summary>
    bool UsePlan => settings.Backend == "plan" || (settings.Backend == "" && (string.IsNullOrWhiteSpace(settings.ApiKey) || ClaudeCodeAgent.FindClaude(settings.ClaudeCodePath) != null));
    CancellationTokenSource? agentCts;
    bool routing;
    bool fitPcbPending = true, fitSchPending = true;
    bool closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        session = new DesignSession(NewProject());
        Sch.Attach(session); Pcb.Attach(session);
        Library.Attach(session); Properties.Attach(session);
        tools = new DesignTools(() => session.Project)
        {
            RenderBoardImage = RenderPcbPng,
            DefaultExportFolder = () => session.FilePath is { } fp ? Path.GetDirectoryName(fp)!
                : session.Project.KiCadSource is { Length: > 0 } ks ? Path.GetDirectoryName(ks)!
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TraceMind"),
        };
        apiAgent = new ClaudeAgent(settings, tools, RunToolAsync);
        apiAgent.OnEvent += e => Dispatcher.UIThread.Post(() => OnAgentEvent(e));
        planAgent = new ClaudeCodeAgent(settings, RunToolAsync);
        planAgent.OnEvent += e => Dispatcher.UIThread.Post(() => OnAgentEvent(e));

        session.Changed += UpdateTitleAndStats;
        session.Changed += SyncLayerChoices;
        session.SelectionChanged += () => { if (session.SelectedComponents.Count == 1) LeftTabs.SelectedIndex = 1; };

        WireMenus();
        WireToolbar();
        WireChat();
        UpdateTitleAndStats();
        UpdateModelLabel();
        EditorTabs.SelectionChanged += (_, _) => OnEditorTabChanged();
        Opened += async (_, _) =>
        {
            FitToScreen();
            await RestoreOrReopenAsync();
            Dispatcher.UIThread.Post(() => { Sch.ZoomToFit(); fitSchPending = false; }, DispatcherPriority.Background);
            if (UsePlan && ClaudeCodeAgent.FindClaude(settings.ClaudeCodePath) == null)
                Chat.Handle(new AgentEvent(AgentEventKind.Status, "To use your Claude plan, install Claude Code — see Settings (⚙)."));
            else if (!UsePlan && string.IsNullOrWhiteSpace(settings.ApiKey))
                Chat.Handle(new AgentEvent(AgentEventKind.Status, "Add your Claude API key in Settings (⚙) to enable the AI."));
        };
        Closing += OnClosing;
        Closed += (_, _) => planAgent.Dispose();
        Sch.CursorMoved += w => StCursor.Text = $"X {w.X,8:0.00}  Y {w.Y,8:0.00} mm";
        Pcb.CursorMoved += w => StCursor.Text = $"X {w.X,8:0.000}  Y {w.Y,8:0.000} mm";
        Sch.StatusMessage += s => StMessage.Text = s;
        Pcb.StatusMessage += s => StMessage.Text = s;
        Sch.PromptLabel = name => Dialogs.Prompt(this, "Net label", "Net name (e.g. GND, +3V3, SDA):", name);
        Sch.ComponentDoubleClicked += _ => LeftTabs.SelectedIndex = 1;
        Pcb.ComponentDoubleClicked += _ => LeftTabs.SelectedIndex = 1;
        Library.PlaceRequested += (id, value, fp) => { EditorTabs.SelectedIndex = 0; Sch.BeginPlace(id, value, fp); Sch.Focus(); };
        Properties.OpenUrl += url => _ = Launcher.LaunchUriAsync(new Uri(url));
        Sch.ToolChanged += t => { TbSchSelect.IsChecked = t == SchTool.Select; TbSchWire.IsChecked = t == SchTool.Wire; TbSchLabel.IsChecked = t == SchTool.Label; TbSchNc.IsChecked = t == SchTool.NoConnect; };
        Pcb.ToolChanged += t => { TbPcbSelect.IsChecked = t == PcbTool.Select; TbPcbRoute.IsChecked = t == PcbTool.Route; TbPcbVia.IsChecked = t == PcbTool.Via; };
        Pcb.LayerChanged += l => { if (CbLayer.SelectedItem as string != l.Display()) CbLayer.SelectedItem = l.Display(); };
    }

    public static string AppVersion =>
        (System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0] ?? "dev";

    static Project NewProject() => new() { Name = "Untitled" };

    // ---- automation hooks (used by the screenshot tool and future scripting) ----
    public DesignSession Session => session;
    public void LoadForAutomation(Project p) => LoadProject(p, null);
    public void ShowEditor(int index) => EditorTabs.SelectedIndex = index;
    public Task RouteForAutomation() => AutorouteAsync();
    public void DrcForAutomation() => RunDrc();
    public ChatPanel ChatView => Chat;
    public SchematicCanvas SchematicView => Sch;
    public PcbCanvas PcbView => Pcb;
    public Task<string> RunToolForAutomation(string name, string json) => RunToolAsync(name, JsonDocument.Parse(json).RootElement.Clone());

    bool openedFromArgs;
    readonly DispatcherTimer autosaveTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    static string AutosaveDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TraceMind", "autosave");
    static string AutosaveFile => Path.Combine(AutosaveDir, "autosave" + ProjectFile.Extension);
    static string AutosaveMeta => Path.Combine(AutosaveDir, "autosave.json");

    sealed record AutosaveInfo(string? OriginalPath, string Name, DateTime Saved);

    /// <summary>Write a recovery copy of unsaved work.</summary>
    void Autosave()
    {
        if (!session.Dirty) return;
        try
        {
            Directory.CreateDirectory(AutosaveDir);
            ProjectFile.Save(session.Project, AutosaveFile);
            File.WriteAllText(AutosaveMeta, JsonSerializer.Serialize(new AutosaveInfo(session.FilePath, session.Project.Name, DateTime.Now)));
        }
        catch { /* never interrupt work for an autosave problem */ }
    }

    static void ClearAutosave()
    {
        try { if (File.Exists(AutosaveFile)) File.Delete(AutosaveFile); if (File.Exists(AutosaveMeta)) File.Delete(AutosaveMeta); } catch { }
    }

    /// <summary>On startup: offer to restore unsaved work, otherwise reopen the last project.</summary>
    async Task RestoreOrReopenAsync()
    {
        autosaveTimer.Tick += (_, _) => Autosave();
        autosaveTimer.Start();
        if (File.Exists(AutosaveFile) && File.Exists(AutosaveMeta))
        {
            AutosaveInfo? info = null;
            try { info = JsonSerializer.Deserialize<AutosaveInfo>(File.ReadAllText(AutosaveMeta)); } catch { }
            var when = info?.Saved.ToString("g") ?? File.GetLastWriteTime(AutosaveFile).ToString("g");
            if (await Dialogs.Confirm(this, "Restore unsaved work?",
                    $"TraceMind has unsaved changes to \"{info?.Name ?? "a project"}\" from {when} (it was closed without saving, or stopped unexpectedly).\n\nRestore them?", "Restore", "Discard"))
            {
                try
                {
                    var p = ProjectFile.Load(AutosaveFile);
                    LoadProject(p, info?.OriginalPath != null && File.Exists(info.OriginalPath) ? info.OriginalPath : null);
                    session.MarkChanged(); // still unsaved
                    StMessage.Text = "Restored unsaved work — save it with Ctrl+S.";
                    return;
                }
                catch (Exception ex) { await Dialogs.Message(this, "Restore failed", ex.Message); }
            }
            ClearAutosave();
        }
        if (openedFromArgs || !settings.ReopenLastProject) return;
        var last = settings.RecentFiles.FirstOrDefault(File.Exists);
        if (last != null) { OpenPath(last); StMessage.Text = "Reopened " + last; }
    }

    public void OpenPath(string path)
    {
        openedFromArgs = true;
        try
        {
            Project p = path.EndsWith(".kicad_pcb", StringComparison.OrdinalIgnoreCase) ? KiCadPcb.Import(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path)).Project.WithKiCadSource(path)
                : path.EndsWith(".net", StringComparison.OrdinalIgnoreCase) ? KiCadNetlist.Import(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path)).Project
                : ProjectFile.Load(path);
            bool native = path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase);
            LoadProject(p, native ? path : null);
            if (native) { settings.AddRecent(path); settings.Save(); BuildRecentMenu(); }
        }
        catch (Exception ex) { _ = Dialogs.Message(this, "Could not open file", ex.Message); }
    }

    void LoadProject(Project p, string? path)
    {
        session.Replace(p, path);
        apiAgent.Reset(); planAgent.Reset();
        Chat.Clear();
        fitSchPending = true; fitPcbPending = true;
        Dispatcher.UIThread.Post(() => { if (EditorTabs.SelectedIndex == 0) { Sch.ZoomToFit(); fitSchPending = false; } else { Pcb.ZoomToFit(); fitPcbPending = false; } }, DispatcherPriority.Background);
        Problems.ItemsSource = null;
        UpdateTitleAndStats();
    }

    // ======================= menus & toolbar =======================

    void WireMenus()
    {
        MiNew.Click += async (_, _) => { if (await ConfirmDiscard()) LoadProject(NewProject(), null); };
        MiExample.Click += async (_, _) => { if (await ConfirmDiscard()) LoadProject(SampleProjects.AttinyBlinker(), null); };
        MiOpen.Click += async (_, _) => await OpenAsync();
        MiSave.Click += async (_, _) => await SaveAsync(false);
        MiSaveAs.Click += async (_, _) => await SaveAsync(true);
        MiImportPcb.Click += async (_, _) => await ImportAsync("KiCad board", "*.kicad_pcb");
        MiImportNet.Click += async (_, _) => await ImportAsync("KiCad netlist", "*.net");
        MiExportFab.Click += async (_, _) => await ExportFabAsync();
        MiExportPcb.Click += async (_, _) => await ExportTextAsync("KiCad board", ".kicad_pcb", () => KiCadPcb.Export(session.Project));
        MiExportNet.Click += async (_, _) => await ExportTextAsync("KiCad netlist", ".net", () => KiCadNetlist.Export(session.Project));
        MiExportBom.Click += async (_, _) => await ExportTextAsync("BOM", ".csv", () => BomExporter.Bom(session.Project));
        MiExit.Click += (_, _) => Close();
        MiUndo.Click += (_, _) => session.DoUndo();
        MiRedo.Click += (_, _) => session.DoRedo();
        MiDelete.Click += (_, _) => { if (EditorTabs.SelectedIndex == 0) Sch.DeleteSelection(); else Pcb.DeleteSelection(); };
        MiTidy.Click += (_, _) => { session.Mutate(() => EditOps.TidySchematic(session.Project)); Sch.ZoomToFit(); };
        MiFit.Click += (_, _) => CurrentCanvas.ZoomToFit();
        MiViewSch.Click += (_, _) => EditorTabs.SelectedIndex = 0;
        MiViewPcb.Click += (_, _) => EditorTabs.SelectedIndex = 1;
        MiRatsnest.Click += (_, _) => { Pcb.ShowRatsnest = MiRatsnest.IsChecked; Pcb.InvalidateVisual(); };
        MiZones.Click += (_, _) => { Pcb.ShowZones = MiZones.IsChecked; Pcb.InvalidateVisual(); };
        MiDrcMarkers.Click += (_, _) => { Pcb.ShowDrc = MiDrcMarkers.IsChecked; Pcb.InvalidateVisual(); };
        MiBoardSetup.Click += (_, _) => { session.ClearSelection(); LeftTabs.SelectedIndex = 1; Properties.Rebuild(); };
        MiAutoPlace.Click += (_, _) => AutoPlace(false);
        MiAutoPlaceAll.Click += (_, _) => AutoPlace(true);
        MiAutoroute.Click += async (_, _) => await AutorouteAsync();
        MiAutorouteLinks.Click += async (_, _) => await AutorouteAsync(true);
        MiExportCnc.Click += async (_, _) => await ExportCncAsync();
        MiSaveKiCad.Click += async (_, _) => await SaveBackToKiCadAsync();
        MiClearRouting.Click += (_, _) => session.Mutate(() => { session.Project.Board.Tracks.RemoveAll(t => !t.Locked); session.Project.Board.Vias.RemoveAll(v => !v.Locked); });
        MiGndPour.Click += (_, _) => session.Mutate(() =>
        {
            var b = session.Project.Board;
            b.Zones.RemoveAll(z => z.Layer == Layer.BCu && z.Net == "GND");
            b.Zones.Add(new Zone { Net = "GND", Layer = Layer.BCu, Outline = [.. b.Outline] });
        });
        MiHoles.Click += (_, _) => session.Mutate(() => tools.Execute("add_mounting_holes", JsonDocument.Parse("{}").RootElement));
        MiDrc.Click += (_, _) => RunDrc();
        MiSyncNets.Click += (_, _) => { int n = 0; session.Mutate(() => n = EditOps.SyncBoardNets(session.Project)); StMessage.Text = $"Updated the net of {n} tracks."; };
        MiAiBuild.Click += async (_, _) => await BuildBoardAsync();
        MiAiReview.Click += (_, _) => Chat.Submit("Review my design: check the schematic for electrical mistakes (missing decoupling, floating inputs, wrong pull-ups, power issues), check part choices and the PCB layout, and run DRC. List concrete problems and fix the clear-cut ones.");
        MiAiRoute.Click += (_, _) => Chat.Submit("Place the parts sensibly on the board (connectors on edges, decoupling next to power pins), set net classes for power, route the board, and get DRC to zero errors.");
        MiAiParts.Click += (_, _) => Chat.Submit("For every part that is missing a manufacturer part number or LCSC number, find a suitable in-stock part (prefer JLCPCB basic parts) and fill in mpn, manufacturer, lcsc and datasheet.");
        MiSettings.Click += async (_, _) => await OpenSettings();
        MiShortcuts.Click += async (_, _) => await Dialogs.Message(this, "Keyboard shortcuts", Dialogs.Shortcuts);
        MiAbout.Click += async (_, _) => await Dialogs.Message(this, "About TraceMind",
            $"TraceMind {AppVersion} — AI-assisted PCB design.\n\nSchematic capture, PCB layout, a grid autorouter, DRC, Gerber/Excellon/BOM/CPL export and KiCad interchange, with Claude as a built-in design engineer.\n\nClaude is a product of Anthropic. TraceMind uses your Claude plan (via Claude Code) or your own API key.");
        BuildRecentMenu();

        KeyDown += OnWindowKeyDown;
    }

    void BuildRecentMenu()
    {
        var items = settings.RecentFiles.Where(File.Exists).Select(f =>
        {
            var mi = new MenuItem { Header = Path.GetFileName(f) + "  —  " + Path.GetDirectoryName(f) };
            mi.Click += async (_, _) => { if (await ConfirmDiscard()) OpenPath(f); };
            return (object)mi;
        }).ToList();
        if (items.Count == 0) items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        MiRecent.ItemsSource = items;
    }

    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.Z: session.DoUndo(); e.Handled = true; break;
                case Key.Y: session.DoRedo(); e.Handled = true; break;
                case Key.S: _ = SaveAsync(false); e.Handled = true; break;
                case Key.O: _ = OpenAsync(); e.Handled = true; break;
                case Key.N: MiNew.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); e.Handled = true; break;
                case Key.R: _ = AutorouteAsync(); e.Handled = true; break;
                case Key.D: RunDrc(); e.Handled = true; break;
                case Key.D1: EditorTabs.SelectedIndex = 0; e.Handled = true; break;
                case Key.D2: EditorTabs.SelectedIndex = 1; e.Handled = true; break;
            }
        }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.S) { _ = SaveAsync(true); e.Handled = true; }
        else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.Z) { session.DoRedo(); e.Handled = true; }
    }

    void WireToolbar()
    {
        TbNew.Click += async (_, _) => { if (await ConfirmDiscard()) LoadProject(NewProject(), null); };
        TbOpen.Click += async (_, _) => await OpenAsync();
        TbSave.Click += async (_, _) => await SaveAsync(false);
        TbUndo.Click += (_, _) => session.DoUndo();
        TbRedo.Click += (_, _) => session.DoRedo();
        TbSchSelect.Click += (_, _) => Sch.SetTool(SchTool.Select);
        TbSchWire.Click += (_, _) => Sch.SetTool(SchTool.Wire);
        TbSchLabel.Click += (_, _) => Sch.SetTool(SchTool.Label);
        TbSchNc.Click += (_, _) => Sch.SetTool(SchTool.NoConnect);
        TbPcbSelect.Click += (_, _) => Pcb.SetTool(PcbTool.Select);
        TbPcbRoute.Click += (_, _) => Pcb.SetTool(PcbTool.Route);
        TbPcbVia.Click += (_, _) => Pcb.SetTool(PcbTool.Via);
        TbAutoPlace.Click += (_, _) => AutoPlace(false);
        TbAutoroute.Click += async (_, _) => await AutorouteAsync();
        TbDrc.Click += (_, _) => RunDrc();
        BtnRunDrc.Click += (_, _) => RunDrc();
        TbExport.Click += async (_, _) => await ExportFabAsync();
        TbSettings.Click += async (_, _) => await OpenSettings();
        SyncLayerChoices();
        CbLayer.SelectionChanged += (_, _) =>
        {
            if (CbLayer.SelectedItem is string name && session.Project.Board.CopperLayerList().FirstOrDefault(l => l.Display() == name) is var l && l.IsCopper()) Pcb.SetLayer(l);
        };
        var grids = new[] { 0.05, 0.1, 0.125, 0.25, 0.5, 1.0, 1.27, 2.54 };
        CbGrid.ItemsSource = grids.Select(g => g.ToString("0.###") + " mm").ToList();
        CbGrid.SelectedIndex = 3;
        CbGrid.SelectionChanged += (_, _) => { if (CbGrid.SelectedIndex >= 0) Pcb.Grid = grids[CbGrid.SelectedIndex]; Pcb.InvalidateVisual(); };
        Problems.DoubleTapped += (_, _) => NavigateToProblem();
    }

    CanvasBase CurrentCanvas => EditorTabs.SelectedIndex == 1 ? Pcb : Sch;

    void OnEditorTabChanged()
    {
        bool pcb = EditorTabs.SelectedIndex == 1;
        SchTools.IsVisible = !pcb; PcbTools.IsVisible = pcb;
        if (pcb && fitPcbPending) Dispatcher.UIThread.Post(() => { Pcb.ZoomToFit(); fitPcbPending = false; }, DispatcherPriority.Background);
        if (!pcb && fitSchPending) Dispatcher.UIThread.Post(() => { Sch.ZoomToFit(); fitSchPending = false; }, DispatcherPriority.Background);
        (pcb ? (Control)Pcb : Sch).Focus();
    }

    void UpdateTitleAndStats()
    {
        var p = session.Project;
        var name = session.FilePath != null ? Path.GetFileName(session.FilePath) : p.Name;
        Title = $"{name}{(session.Dirty ? " •" : "")} — TraceMind v{AppVersion}";
        int parts = p.Components.Count(c => p.Symbol(c)?.IsPower != true);
        int unplaced = p.Components.Count(c => !c.Placed && !c.DoNotPopulate && p.Footprint(c) != null && p.Symbol(c)?.IsPower != true);
        var stats = $"{parts} parts · {session.Netlist.Nets.Count} nets · {session.Connectivity.Airwires.Count} unrouted";
        if (unplaced > 0) stats += $" · {unplaced} unplaced";
        if (session.LastDrc is { } d) stats += $" · DRC {d.Errors} errors";
        StStats.Text = stats;
    }

    // ======================= files =======================

    static readonly FilePickerFileType TmindType = new("TraceMind project") { Patterns = ["*" + ProjectFile.Extension] };

    async Task<bool> ConfirmDiscard()
    {
        if (!session.Dirty) return true;
        var r = await Dialogs.SaveChanges(this);
        if (r == null) return false;
        if (r == true) return await SaveAsync(false);
        return true;
    }

    async Task OpenAsync()
    {
        if (!await ConfirmDiscard()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open project", AllowMultiple = false,
            FileTypeFilter = [TmindType, new FilePickerFileType("KiCad board / netlist") { Patterns = ["*.kicad_pcb", "*.net"] }],
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path != null) OpenPath(path);
    }

    async Task<bool> SaveAsync(bool saveAs)
    {
        var path = session.FilePath;
        if (saveAs || path == null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save project", DefaultExtension = ProjectFile.Extension, FileTypeChoices = [TmindType],
                SuggestedFileName = GerberExporter.Sanitize(session.Project.Name) + ProjectFile.Extension,
            });
            path = file?.TryGetLocalPath();
            if (path == null) return false;
        }
        try
        {
            ProjectFile.Save(session.Project, path);
            session.MarkSaved(path);
            ClearAutosave();
            settings.AddRecent(path); settings.Save(); BuildRecentMenu();
            StMessage.Text = "Saved " + path;
            return true;
        }
        catch (Exception ex) { await Dialogs.Message(this, "Save failed", ex.Message); return false; }
    }

    async Task ImportAsync(string title, string pattern)
    {
        if (!await ConfirmDiscard()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import " + title, FileTypeFilter = [new FilePickerFileType(title) { Patterns = [pattern] }] });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path == null) return;
        try
        {
            var text = await File.ReadAllTextAsync(path);
            List<string> warnings;
            Project p;
            if (pattern.Contains("kicad_pcb")) { var r = KiCadPcb.Import(text, Path.GetFileNameWithoutExtension(path)); p = r.Project; p.KiCadSource = path; warnings = r.Warnings; }
            else { var r = KiCadNetlist.Import(text, Path.GetFileNameWithoutExtension(path)); p = r.Project; warnings = r.Warnings; }
            LoadProject(p, null);
            session.MarkChanged();
            if (pattern.Contains("kicad_pcb")) EditorTabs.SelectedIndex = 1;
            if (warnings.Count > 0) await Dialogs.Message(this, "Imported with notes", string.Join("\n", warnings.Take(30)));
        }
        catch (Exception ex) { await Dialogs.Message(this, "Import failed", ex.Message); }
    }

    async Task ExportTextAsync(string title, string ext, Func<string> content)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export " + title, DefaultExtension = ext, SuggestedFileName = GerberExporter.Sanitize(session.Project.Name) + ext,
            FileTypeChoices = [new FilePickerFileType(title) { Patterns = ["*" + ext] }],
        });
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        try { await File.WriteAllTextAsync(path, content()); StMessage.Text = "Exported " + path; }
        catch (Exception ex) { await Dialogs.Message(this, "Export failed", ex.Message); }
    }

    async Task ExportFabAsync()
    {
        var drc = Drc.Run(session.Project);
        session.LastDrc = drc; ShowProblems(drc);
        if (drc.Errors > 0 && !await Dialogs.Confirm(this, "DRC errors", $"The board has {drc.Summary()}.\nExport anyway?", "Export anyway"))
            return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose an output folder for fabrication files" });
        var dir = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (dir == null) return;
        try
        {
            var outDir = Path.Combine(dir, GerberExporter.Sanitize(session.Project.Name) + "-fab");
            var res = GerberExporter.Export(session.Project, outDir);
            await Dialogs.Message(this, "Fabrication files exported",
                $"Wrote {res.Files.Count} files to\n{outDir}\n\nUpload the *-gerbers.zip to your PCB maker (JLCPCB, PCBWay, OSH Park…). For assembly, also upload the BOM and CPL .csv files." +
                (res.Warnings.Count > 0 ? "\n\nNotes:\n- " + string.Join("\n- ", res.Warnings) : ""));
            _ = Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(outDir));
        }
        catch (Exception ex) { await Dialogs.Message(this, "Export failed", ex.Message); }
    }

    async Task ExportCncAsync()
    {
        var mirror = await Dialogs.Confirm(this, "CNC milling files",
            "Writes copper, board outline and drill files (mm) with the board's lower-left corner at 0,0.\n\nAlso write mirrored bottom-side files for milling from the back?", "Yes, include mirrored", "No");
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose an output folder for CNC files" });
        var dir = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (dir == null) return;
        try
        {
            var outDir = Path.Combine(dir, GerberExporter.Sanitize(session.Project.Name) + "-cnc");
            var res = GerberExporter.Export(session.Project, outDir, new ExportOptions { Assembly = false, Origin = "lower_left", MirrorBottom = mirror, Zip = false });
            await Dialogs.Message(this, "CNC files exported", $"Wrote {res.Files.Count} files to\n{outDir}\n\n- " + string.Join("\n- ", res.Files.Select(Path.GetFileName)) +
                (res.Warnings.Count > 0 ? "\n\nNotes:\n- " + string.Join("\n- ", res.Warnings) : ""));
            _ = Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(outDir));
        }
        catch (Exception ex) { await Dialogs.Message(this, "Export failed", ex.Message); }
    }

    async Task SaveBackToKiCadAsync()
    {
        var path = session.Project.KiCadSource;
        if (string.IsNullOrEmpty(path))
        {
            await ExportTextAsync("KiCad board", ".kicad_pcb", () => KiCadPcb.Export(session.Project));
            return;
        }
        if (!await Dialogs.Confirm(this, "Save to KiCad", $"Overwrite\n{path}\nwith the current board? The old file is kept as .kicad_pcb.bak.", "Save")) return;
        try { KiCadPcb.SaveTo(session.Project, path); StMessage.Text = "Saved " + path; }
        catch (Exception ex) { await Dialogs.Message(this, "Save failed", ex.Message); }
    }

    /// <summary>
    /// Make sure the whole window (including the title-bar buttons) is on screen: restore the last size/position if it
    /// still fits a monitor, otherwise maximize when the default size is larger than the screen (small / high-DPI laptops).
    /// </summary>
    void FitToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        double sc = screen.Scaling;
        var wa = screen.WorkingArea;
        double maxW = wa.Width / sc, maxH = wa.Height / sc;
        if (settings.WindowMaximized) { WindowState = WindowState.Maximized; return; }
        if (settings.WindowWidth > 0 && settings.WindowHeight > 0)
        {
            var px = new PixelPoint((int)settings.WindowX, (int)settings.WindowY);
            var target = Screens.All.FirstOrDefault(s => s.WorkingArea.Contains(px) && s.WorkingArea.Contains(new PixelPoint(px.X + (int)(settings.WindowWidth * s.Scaling) - 1, px.Y + 40)));
            if (target != null && settings.WindowWidth <= target.WorkingArea.Width / target.Scaling && settings.WindowHeight <= target.WorkingArea.Height / target.Scaling)
            {
                Width = settings.WindowWidth; Height = settings.WindowHeight; Position = px;
                return;
            }
        }
        if (Width > maxW - 8 || Height > maxH - 8)
        {
            Width = Math.Min(Width, maxW - 8); Height = Math.Min(Height, maxH - 8);
            Position = new PixelPoint(wa.X + 4, wa.Y + 4);
            WindowState = WindowState.Maximized;
        }
    }

    void SaveWindowPlacement()
    {
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            settings.WindowX = Position.X; settings.WindowY = Position.Y;
            settings.WindowWidth = Bounds.Width; settings.WindowHeight = Bounds.Height;
        }
        try { settings.Save(); } catch { }
    }

    async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        SaveWindowPlacement();
        if (closingConfirmed || !session.Dirty) { agentCts?.Cancel(); if (!session.Dirty) ClearAutosave(); return; }
        e.Cancel = true;
        if (await ConfirmDiscard()) { closingConfirmed = true; ClearAutosave(); Close(); }
    }

    // ======================= PCB commands =======================

    void AutoPlace(bool all)
    {
        int n = 0;
        session.Mutate(() => n = AutoPlacer.Place(session.Project, new PlacerOptions { ReplaceAll = all }));
        StMessage.Text = $"Auto-placed {n} parts.";
        EditorTabs.SelectedIndex = 1;
        Pcb.ZoomToFit();
    }

    async Task AutorouteAsync() => await AutorouteAsync(false);

    async Task AutorouteAsync(bool wireLinks)
    {
        if (routing) return;
        routing = true;
        TbAutoroute.IsEnabled = false;
        EditorTabs.SelectedIndex = 1;
        try
        {
            var clone = ProjectFile.Clone(session.Project);
            var progress = new Progress<string>(s => StMessage.Text = s);
            var res = await Task.Run(() => new AutoRouter(clone, new RouterOptions { WireLinks = wireLinks }, progress).Route());
            ApplyRouted(clone);
            RunDrc();
            StMessage.Text = res.Summary() + (res.LinksAdded.Count > 0 ? $" Wire links added: {string.Join(", ", res.LinksAdded)}." : "");
            ShowRouteFailures(res);
        }
        catch (Exception ex) { await Dialogs.Message(this, "Autorouter", ex.Message); }
        finally { routing = false; TbAutoroute.IsEnabled = true; }
    }

    /// <summary>Bring the result of routing on a copy back into the open project.</summary>
    void ApplyRouted(Project clone)
    {
        bool newParts = clone.Components.Count != session.Project.Components.Count;
        if (newParts) session.ReplaceWithUndo(clone); // the router added wire links
        else session.Mutate(() => { session.Project.Board.Tracks = clone.Board.Tracks; session.Project.Board.Vias = clone.Board.Vias; });
    }

    /// <summary>List where failed nets are blocked at the top of the Problems panel.</summary>
    void ShowRouteFailures(RouteResult res)
    {
        if (res.Failures.Count == 0) return;
        var items = (Problems.ItemsSource as IEnumerable<ListBoxItem>)?.ToList() ?? [];
        var extra = res.Failures.Select(f => new ListBoxItem
        {
            Tag = new DrcViolation(Severity.Error, "route", f.ToString(), f.Crossings.FirstOrDefault()?.Middle ?? Vec2.Zero),
            Padding = new Thickness(10, 2),
            Content = new TextBlock { Text = "⛔  " + f, Foreground = Palette.ChatErrorText, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
        });
        Problems.ItemsSource = extra.Concat(items).ToList();
    }

    byte[]? RenderPcbPng(TraceMind.Core.Geometry.Rect? region)
    {
        try
        {
            var area = region ?? session.Project.Board.OutlineBounds().Inflate(2);
            double aspect = Math.Clamp(area.Width / Math.Max(area.Height, 1), 0.4, 2.5);
            int w = 1400, h = (int)Math.Round(w / aspect);
            if (h > 1400) { h = 1400; w = (int)Math.Round(h * aspect); }
            var canvas = new PcbCanvas { Width = w, Height = h, ShowRulers = true };
            canvas.Attach(new DesignSession(session.Project));
            canvas.SetLayer(Pcb.ActiveLayer);
            canvas.Measure(new Size(w, h)); canvas.Arrange(new Avalonia.Rect(0, 0, w, h));
            if (region is { } r) canvas.ZoomToRect(r); else canvas.ZoomToFit();
            using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            bmp.Render(canvas);
            using var ms = new MemoryStream();
            bmp.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    void SyncLayerChoices()
    {
        var layers = session.Project.Board.CopperLayerList().Select(l => l.Display()).ToList();
        if (CbLayer.ItemsSource is IEnumerable<string> cur && cur.SequenceEqual(layers)) return;
        CbLayer.ItemsSource = layers;
        var active = session.Project.Board.CopperLayerList().Contains(Pcb.ActiveLayer) ? Pcb.ActiveLayer : session.Project.Board.CopperLayerList().First();
        CbLayer.SelectedItem = active.Display();
        Pcb.SetLayer(active);
    }

    void RunDrc()
    {
        var drc = Drc.Run(session.Project);
        session.LastDrc = drc;
        ShowProblems(drc);
        Pcb.InvalidateVisual();
        UpdateTitleAndStats();
        StMessage.Text = "DRC: " + drc.Summary();
    }

    void ShowProblems(DrcReport drc)
    {
        ProblemsTitle.Text = $"Problems — {drc.Summary()}";
        Problems.ItemsSource = drc.Violations.OrderBy(v => v.Severity).ThenBy(v => v.Code).Select(v => new ListBoxItem
        {
            Tag = v, Padding = new Thickness(10, 2),
            Content = new TextBlock
            {
                Text = (v.Severity switch { Severity.Error => "●  ", Severity.Warning => "▲  ", _ => "ℹ  " }) + v.Message,
                Foreground = v.Severity switch { Severity.Error => Palette.ChatErrorText, Severity.Warning => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F0C674")), _ => Palette.Muted },
            },
        }).ToList();
    }

    void NavigateToProblem()
    {
        if ((Problems.SelectedItem as ListBoxItem)?.Tag is not DrcViolation v) return;
        if (v.Code.StartsWith("erc"))
        {
            EditorTabs.SelectedIndex = 0;
            if (v.Location != Vec2.Zero) Sch.CenterOn(v.Location, Math.Max(Sch.Zoom, 10));
            if (v.Ref != null && session.Project.Find(v.Ref) is { } c) session.Select([c]);
            return;
        }
        EditorTabs.SelectedIndex = 1;
        if (v.Location != Vec2.Zero) Pcb.CenterOn(v.Location, Math.Max(Pcb.Zoom, 25));
        else if (v.Ref != null && session.Project.Find(v.Ref) is { } c) session.Select([c]);
    }

    // ======================= AI =======================

    void WireChat()
    {
        Chat.SendRequested += async text =>
        {
            Chat.AddUser(text);
            Chat.SetBusy(true);
            agentCts = new CancellationTokenSource();
            var ct = agentCts.Token;
            await Task.Run(() => agent.SendAsync(text, ct));
        };
        Chat.StopRequested += () => agentCts?.Cancel();
        Chat.NewChatRequested += () => { apiAgent.Reset(); planAgent.Reset(); };
        Chat.SettingsRequested += async () => await OpenSettings();
        Chat.BuildRequested += async () => await BuildBoardAsync();
    }

    async Task BuildBoardAsync()
    {
        var desc = await Dialogs.Prompt(this, "Build a board",
            "Describe the board you want. Include what it does, how it's powered, connectors, size limits, and whether you want JLCPCB assembly.\n\nExample: \"ESP32-C3 sensor board, USB-C powered, BME280 over I2C, status LED, reset + boot buttons, max 40x30 mm, JLCPCB assembly\"");
        if (string.IsNullOrWhiteSpace(desc)) return;
        if (session.Project.Components.Count > 0 &&
            await Dialogs.Confirm(this, "Build a board", "Start from a new empty project? (Choose 'Keep' to build into the current design.)", "New project", "Keep"))
        {
            if (!await ConfirmDiscard()) return;
            LoadProject(NewProject(), null);
        }
        Chat.Submit($"""
            Build this board end to end: {desc.Trim()}

            Work through every step without stopping unless a key requirement is genuinely missing:
            1. Research and choose real parts (prefer JLCPCB basic/extended parts; record MPN, manufacturer, LCSC number, datasheet).
            2. Build the complete schematic, including every support part the datasheets call for (decoupling, pull-ups, protection, programming/boot access). Mark unused pins no-connect.
            3. Size the board, set design rules and net classes for power.
            4. Place parts thoughtfully (connectors on edges, decoupling at the pins), then auto-place the rest.
            5. Route the board and add a GND pour; iterate until routing completes.
            6. Run DRC and fix all errors.
            7. Finish with a short summary: what the board does, the BOM with part numbers, and anything I should double-check before ordering.
            """);
    }

    async Task OpenSettings()
    {
        var backendBefore = UsePlan;
        if (await Dialogs.Settings(this, settings))
        {
            UpdateModelLabel();
            if (backendBefore != UsePlan) { apiAgent.Reset(); planAgent.Reset(); Chat.Clear(); }
        }
    }

    void UpdateModelLabel() => Chat.SetModelLabel(UsePlan
        ? "your Claude plan" + (settings.PlanModel != "" ? " · " + settings.PlanModel : "")
        : $"API · {settings.Model} · {settings.Effort}" + (string.IsNullOrWhiteSpace(settings.ApiKey) ? " · no key" : ""));

    void OnAgentEvent(AgentEvent e)
    {
        Chat.Handle(e);
        if (e.Kind == AgentEventKind.Done) { UpdateTitleAndStats(); }
    }

    /// <summary>Execute an AI tool call against the open design (UI thread), with undo support.</summary>
    async Task<string> RunToolAsync(string name, JsonElement input)
    {
        if (name == "autoroute")
        {
            // Route on a copy in the background so the UI stays responsive, then apply.
            var clone = await Dispatcher.UIThread.InvokeAsync(() => ProjectFile.Clone(session.Project));
            var cloneTools = new DesignTools(() => clone)
            {
                Progress = new Progress<string>(s => Dispatcher.UIThread.Post(() => { StMessage.Text = s; Chat.SetStatus(s); })),
                Cancellation = agentCts?.Token ?? default,
            };
            var result = await Task.Run(() => cloneTools.Execute(name, input));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyRouted(clone);
                if (EditorTabs.SelectedIndex != 1) EditorTabs.SelectedIndex = 1;
                session.LastDrc = Drc.Run(session.Project);
                ShowProblems(session.LastDrc);
            });
            return result;
        }
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            bool readOnly = DesignTools.IsReadOnly(name);
            if (!readOnly) session.PushUndo();
            var result = tools.Execute(name, input);
            if (name == "run_drc") { session.LastDrc = Drc.Run(session.Project); ShowProblems(session.LastDrc); Pcb.InvalidateVisual(); }
            if (!readOnly)
            {
                session.MarkChanged();
                // Follow along: show the editor that the tool affects.
                if (name is "add_component" or "connect" or "tidy_schematic" or "mark_no_connect") { if (EditorTabs.SelectedIndex != 0) EditorTabs.SelectedIndex = 0; Sch.ZoomToFit(); }
                else if (name is "place_components" or "auto_place" or "set_board" or "add_copper_pour" or "add_mounting_holes") { if (EditorTabs.SelectedIndex != 1) EditorTabs.SelectedIndex = 1; Pcb.ZoomToFit(); }
            }
            return result;
        });
    }
}
