using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TraceMind.Core.Library;
using TraceMind.Core.Model;

namespace TraceMind.App.Views;

/// <summary>Edit the selected component, or the board when nothing is selected.</summary>
public sealed class PropertiesPanel : UserControl
{
    DesignSession? session;
    readonly ContentControl host = new();
    Guid? shownId;
    bool rebuildQueued;

    public event Action<string>? OpenUrl;

    public PropertiesPanel()
    {
        Content = new ScrollViewer { Content = host, Padding = new Thickness(10) };
    }

    public void Attach(DesignSession s)
    {
        session = s;
        s.SelectionChanged += Rebuild;
        s.Changed += () =>
        {
            if (IsKeyboardFocusWithin || rebuildQueued) return;
            rebuildQueued = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { rebuildQueued = false; if (!IsKeyboardFocusWithin) Rebuild(); }, Avalonia.Threading.DispatcherPriority.Background);
        };
        Rebuild();
    }

    static TextBlock Section(string t) => new() { Text = t, Classes = { "section" } };
    static double ParseD(string? s, double def) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public void Rebuild()
    {
        if (session == null) return;
        var sel = session.Selection.ToList();
        if (sel.Count == 1) ShowComponent(sel[0]);
        else if (sel.Count > 1) { shownId = null; host.Content = new TextBlock { Text = $"{sel.Count} parts selected.", Classes = { "hint" } }; }
        else ShowBoard();
    }

    void ShowComponent(Component c)
    {
        shownId = c.Id;
        var p = session!.Project;
        var stack = new StackPanel { Spacing = 2 };
        TextBox Field(string label, string value)
        {
            stack.Children.Add(Section(label));
            var tb = new TextBox { Text = value };
            stack.Children.Add(tb);
            return tb;
        }
        var sym = p.Symbol(c);
        stack.Children.Add(new TextBlock { Text = c.Ref, FontSize = 20, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(new TextBlock { Text = sym?.Name ?? c.SymbolId, Classes = { "hint" } });
        var refBox = Field("REFERENCE", c.Ref);
        var valBox = Field("VALUE", c.Value);
        stack.Children.Add(Section("FOOTPRINT"));
        var fpBox = new AutoCompleteBox { Text = c.FootprintId, FilterMode = AutoCompleteFilterMode.ContainsOrdinal, MinimumPrefixLength = 0,
            ItemsSource = FootprintLibrary.Examples.Concat(p.Footprints.Keys).Distinct().OrderBy(x => x).ToList() };
        stack.Children.Add(fpBox);
        var mpnBox = Field("MANUFACTURER PART NUMBER", c.Mpn);
        var mfrBox = Field("MANUFACTURER", c.Manufacturer);
        var lcscBox = Field("SUPPLIER PART (LCSC)", c.SupplierPart);
        var dsBox = Field("DATASHEET", c.Datasheet);
        var descBox = Field("DESCRIPTION", c.Description);
        var dnp = new CheckBox { Content = "Do not populate", IsChecked = c.DoNotPopulate, Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(dnp);

        stack.Children.Add(Section("PCB PLACEMENT"));
        var placeGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*,6,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        TextBox Small(string label, string val, int col)
        {
            var l = new TextBlock { Text = label, Classes = { "hint" } }; Grid.SetColumn(l, col);
            var t = new TextBox { Text = val }; Grid.SetColumn(t, col); Grid.SetRow(t, 1);
            placeGrid.Children.Add(l); placeGrid.Children.Add(t);
            return t;
        }
        var xBox = Small("X mm", F(c.PcbPos.X), 0); var yBox = Small("Y mm", F(c.PcbPos.Y), 2); var rBox = Small("Rotation", F(c.PcbRot), 4);
        stack.Children.Add(placeGrid);
        var bottom = new CheckBox { Content = "Bottom side", IsChecked = c.OnBottom };
        var placed = new CheckBox { Content = "Placed on board", IsChecked = c.Placed };
        var locked = new CheckBox { Content = "Locked", IsChecked = c.Locked };
        stack.Children.Add(new WrapPanel { Children = { placed, bottom, locked } });

        var apply = new Button { Content = "Apply", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        var error = new TextBlock { Foreground = Palette.ChatErrorText, TextWrapping = TextWrapping.Wrap };
        apply.Click += (_, _) =>
        {
            var newRef = refBox.Text?.Trim() ?? c.Ref;
            if (newRef != c.Ref && p.Find(newRef) != null) { error.Text = $"{newRef} already exists."; return; }
            var fp = fpBox.Text?.Trim() ?? "";
            if (fp != "" && p.GetFootprint(fp) == null) { error.Text = $"Unknown footprint '{fp}'."; return; }
            error.Text = "";
            session.Mutate(() =>
            {
                c.Ref = newRef; c.Value = valBox.Text ?? ""; c.FootprintId = fp; c.Mpn = mpnBox.Text ?? ""; c.Manufacturer = mfrBox.Text ?? "";
                c.SupplierPart = lcscBox.Text ?? ""; c.Datasheet = dsBox.Text ?? ""; c.Description = descBox.Text ?? ""; c.DoNotPopulate = dnp.IsChecked == true;
                c.PcbPos = new(ParseD(xBox.Text, c.PcbPos.X), ParseD(yBox.Text, c.PcbPos.Y)); c.PcbRot = ParseD(rBox.Text, c.PcbRot);
                c.OnBottom = bottom.IsChecked == true; c.Placed = placed.IsChecked == true; c.Locked = locked.IsChecked == true;
            });
        };
        stack.Children.Add(apply);
        stack.Children.Add(error);
        if (!string.IsNullOrWhiteSpace(c.Datasheet) && c.Datasheet.StartsWith("http"))
        {
            var open = new Button { Content = "Open datasheet", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            open.Click += (_, _) => OpenUrl?.Invoke(c.Datasheet);
            stack.Children.Add(open);
        }

        // Pins and nets
        if (sym != null && sym.Pins.Count > 0)
        {
            stack.Children.Add(Section("PINS"));
            var nl = session.Netlist;
            foreach (var pin in sym.Pins.OrderBy(x => int.TryParse(x.Number, out var n) ? n : 9999).ThenBy(x => x.Number))
            {
                var net = nl.PinNets.TryGetValue((c.Ref, pin.Number), out var nn) ? nn : "—";
                var row = new DockPanel { Margin = new Thickness(0, 1) };
                var netText = new TextBlock { Text = net, Foreground = net == "—" ? Palette.Muted : Palette.Label, FontSize = 12 };
                DockPanel.SetDock(netText, Dock.Right);
                row.Children.Add(netText);
                row.Children.Add(new TextBlock { Text = $"{pin.Number}  {pin.Name}", FontSize = 12, Foreground = Palette.PinName });
                stack.Children.Add(row);
            }
        }
        host.Content = stack;
    }

    void ShowBoard()
    {
        shownId = null;
        var p = session!.Project; var b = p.Board; var r = b.Rules;
        var bb = b.OutlineBounds();
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock { Text = "Board", FontSize = 20, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(new TextBlock { Text = "Select a part to edit it. These settings apply to the whole board.", Classes = { "hint" } });
        var name = new TextBox { Text = p.Name };
        stack.Children.Add(Section("PROJECT NAME")); stack.Children.Add(name);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto") };
        int row = 0;
        TextBox Cell(string label, string val, int col)
        {
            var l = new TextBlock { Text = label, Classes = { "hint" }, Margin = new Thickness(0, 6, 0, 2) };
            Grid.SetColumn(l, col); Grid.SetRow(l, row);
            var t = new TextBox { Text = val }; Grid.SetColumn(t, col); Grid.SetRow(t, row + 1);
            grid.Children.Add(l); grid.Children.Add(t);
            return t;
        }
        var w = Cell("Width (mm)", F(bb.Width), 0); var h = Cell("Height (mm)", F(bb.Height), 2); row += 2;
        var clr = Cell("Clearance", F(r.Clearance), 0); var tw = Cell("Track width", F(r.TrackWidth), 2); row += 2;
        var vd = Cell("Via diameter", F(r.ViaDiameter), 0); var vdr = Cell("Via drill", F(r.ViaDrill), 2); row += 2;
        var mtw = Cell("Min track", F(r.MinTrackWidth), 0); var ec = Cell("Edge clearance", F(r.EdgeClearance), 2); row += 2;
        stack.Children.Add(Section("SIZE AND RULES"));
        stack.Children.Add(grid);
        var layers = new ComboBox { ItemsSource = new[] { "2 layers", "4 layers" }, SelectedIndex = b.CopperLayers >= 4 ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(layers);
        bool rectangular = b.Outline.Count == 4;
        if (!rectangular) stack.Children.Add(new TextBlock { Text = "Custom outline — changing width/height replaces it with a rectangle.", Classes = { "hint" } });

        stack.Children.Add(Section("NET CLASSES"));
        stack.Children.Add(new TextBlock { Text = "One per line:  Name: width = NET1, NET2", Classes = { "hint" } });
        var classes = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), FontSize = 12,
            Text = string.Join("\n", r.NetClasses.Select(nc => $"{nc.Name}: {F(nc.TrackWidth)} = {string.Join(", ", nc.Nets)}")),
        };
        stack.Children.Add(classes);
        var apply = new Button { Content = "Apply", Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        apply.Click += (_, _) => session.Mutate(() =>
        {
            p.Name = name.Text?.Trim() is { Length: > 0 } n ? n : p.Name;
            double nw = ParseD(w.Text, bb.Width), nh = ParseD(h.Text, bb.Height);
            if (Math.Abs(nw - bb.Width) > 1e-6 || Math.Abs(nh - bb.Height) > 1e-6)
                b.Outline = [new(bb.MinX, bb.MinY), new(bb.MinX + nw, bb.MinY), new(bb.MinX + nw, bb.MinY + nh), new(bb.MinX, bb.MinY + nh)];
            r.Clearance = ParseD(clr.Text, r.Clearance); r.TrackWidth = ParseD(tw.Text, r.TrackWidth);
            r.ViaDiameter = ParseD(vd.Text, r.ViaDiameter); r.ViaDrill = ParseD(vdr.Text, r.ViaDrill);
            r.MinTrackWidth = ParseD(mtw.Text, r.MinTrackWidth); r.EdgeClearance = ParseD(ec.Text, r.EdgeClearance);
            b.CopperLayers = layers.SelectedIndex == 1 ? 4 : 2;
            r.NetClasses.Clear();
            foreach (var line in (classes.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = line.IndexOf(':'); var eq = line.IndexOf('=');
                if (colon < 0 || eq < colon) continue;
                var nc = new NetClass
                {
                    Name = line[..colon].Trim(), TrackWidth = ParseD(line[(colon + 1)..eq].Trim(), r.TrackWidth), Clearance = r.Clearance,
                    ViaDiameter = r.ViaDiameter, ViaDrill = r.ViaDrill,
                    Nets = line[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                };
                r.NetClasses.Add(nc);
            }
        });
        stack.Children.Add(apply);

        // summary
        var nl = session.Netlist;
        stack.Children.Add(Section("SUMMARY"));
        stack.Children.Add(new TextBlock
        {
            Classes = { "hint" },
            Text = $"{p.Components.Count(c => p.Symbol(c)?.IsPower != true)} parts, {nl.Nets.Count} nets\n" +
                   $"{p.Components.Count(c => c.Placed)} placed, {b.Tracks.Count} tracks, {b.Vias.Count} vias\n" +
                   $"{session.Connectivity.Airwires.Count} unrouted connections",
        });
        host.Content = stack;
    }
}
