using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TraceMind.Core.Library;

namespace TraceMind.App.Views;

/// <summary>Browse built-in and project symbols and start placing them on the schematic.</summary>
public sealed class LibraryPanel : UserControl
{
    readonly TextBox search = new() { Watermark = "Search parts…" };
    readonly ListBox list = new() { Background = Brushes.Transparent };
    readonly TextBox value = new() { Watermark = "Value (e.g. 10k, 100nF)" };
    readonly AutoCompleteBox footprint = new() { Watermark = "Footprint", FilterMode = AutoCompleteFilterMode.ContainsOrdinal, MinimumPrefixLength = 0 };
    readonly TextBlock info = new() { Classes = { "hint" } };
    DesignSession? session;
    List<(string Id, string Desc)> items = [];

    /// <summary>symbol id, value, footprint</summary>
    public event Action<string, string, string>? PlaceRequested;

    public LibraryPanel()
    {
        var place = new Button { Content = "Place on schematic", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Classes = { "accent" }, Margin = new Thickness(0, 8, 0, 0) };
        place.Click += (_, _) => Place();
        list.DoubleTapped += (_, _) => Place();
        list.SelectionChanged += (_, _) => OnSelected();
        search.TextChanged += (_, _) => Refresh();
        footprint.ItemsSource = FootprintLibrary.Examples;

        var bottom = new StackPanel
        {
            Margin = new Thickness(10),
            Children =
            {
                new TextBlock { Text = "VALUE", Classes = { "section" } }, value,
                new TextBlock { Text = "FOOTPRINT", Classes = { "section" } }, footprint,
                place, info,
                new TextBlock { Classes = { "hint" }, Margin = new Thickness(0, 12, 0, 0),
                    Text = "Tip: for ICs and modules, ask Claude — it looks up the datasheet and builds the symbol and footprint for you." },
            },
        };
        var root = new DockPanel();
        var top = new Border { Padding = new Thickness(10, 10, 10, 6), Child = search };
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(bottom); root.Children.Add(list);
        Content = root;
        Refresh();
    }

    public void Attach(DesignSession s)
    {
        session = s;
        s.Changed += Refresh;
        Refresh();
    }

    void Refresh()
    {
        var q = search.Text?.Trim() ?? "";
        items = SymbolLibrary.Catalog.Select(c => (c.Id switch { "Conn_1xN" => "Conn_1x04", "Conn_2xN" => "Conn_2x05", _ => c.Id }, c.Description)).ToList();
        if (session != null)
            foreach (var s in session.Project.Symbols.Values.Where(s => SymbolLibrary.Get(s.Id) == null))
                items.Add((s.Id, s.Name + " (project part, " + s.Pins.Count + " pins)"));
        if (q != "") items = items.Where(i => i.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Desc.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var sel = (list.SelectedItem as ListBoxItem)?.Tag as string;
        list.ItemsSource = items.Select(i => new ListBoxItem
        {
            Tag = i.Id, Padding = new Thickness(10, 5),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = i.Id, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = i.Desc, FontSize = 11, Foreground = Palette.Muted, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
        }).ToList();
        if (session != null)
            footprint.ItemsSource = FootprintLibrary.Examples.Concat(session.Project.Footprints.Keys).Distinct().OrderBy(x => x).ToList();
    }

    void OnSelected()
    {
        if ((list.SelectedItem as ListBoxItem)?.Tag is not string id) return;
        footprint.Text = SymbolLibrary.DefaultFootprint(id);
        if (session?.Project.Symbols.TryGetValue(id, out var ps) == true && SymbolLibrary.Get(id) == null)
        {
            var used = session.Project.Components.FirstOrDefault(c => c.SymbolId == id);
            footprint.Text = used?.FootprintId ?? "";
            value.Text = used?.Value ?? ps.Name;
        }
        else value.Text = id switch { "R" => "10k", "C" => "100nF", "C_Pol" => "100uF", "L" => "10uH", "LED" => "Red", "GND" => "GND", "VCC" => "+3V3", _ => "" };
        info.Text = "";
    }

    void Place()
    {
        if ((list.SelectedItem as ListBoxItem)?.Tag is not string id) { info.Text = "Pick a part first."; return; }
        var fp = footprint.Text?.Trim() ?? "";
        if (fp != "" && FootprintLibrary.Get(fp) == null && session?.Project.Footprints.ContainsKey(fp) != true)
        {
            info.Text = $"Unknown footprint '{fp}'.";
            return;
        }
        PlaceRequested?.Invoke(id, value.Text?.Trim() ?? "", fp);
    }
}
