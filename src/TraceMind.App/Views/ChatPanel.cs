using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TraceMind.AI;

namespace TraceMind.App.Views;

/// <summary>Conversation with Claude: messages, tool activity, web research and the input box.</summary>
public sealed partial class ChatPanel : UserControl
{
    readonly StackPanel messages = new() { Spacing = 8, Margin = new Thickness(10, 10, 10, 16) };
    readonly ScrollViewer scroller;
    readonly TextBox input;
    readonly Button send, stop;
    readonly TextBlock statusText, modelText;
    readonly WrapPanel suggestions;
    readonly Dictionary<string, (Border row, TextBlock text)> pendingTools = [];
    int toolSeq;

    public event Action<string>? SendRequested;
    public event Action? StopRequested;
    public event Action? NewChatRequested;
    public event Action? SettingsRequested;
    public event Action? BuildRequested;
    readonly TextBlock usageText = new() { FontSize = 11.5, Margin = new Thickness(12, 0, 12, 6) };

    static readonly string[] Starters =
    [
        "Design a USB-C powered ESP32-C3 board with a status LED and a button",
        "Review my schematic for mistakes",
        "Place and route the board, then fix DRC errors",
        "Find a 3.3 V LDO for 500 mA that JLCPCB stocks",
        "Add an I2C temperature sensor with pull-ups",
    ];

    public ChatPanel()
    {
        var title = new TextBlock { Text = "Claude", FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        modelText = new TextBlock { Foreground = Palette.Muted, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0) };
        var newChat = new Button { Content = "New chat", Padding = new Thickness(8, 2), FontSize = 12 };
        newChat.Click += (_, _) => { Clear(); NewChatRequested?.Invoke(); };
        var settings = new Button { Content = "⚙", Padding = new Thickness(8, 2), FontSize = 12, Margin = new Thickness(4, 0, 0, 0) };
        settings.Click += (_, _) => SettingsRequested?.Invoke();
        var build = new Button { Content = "⚡ Build a board", Padding = new Thickness(10, 2), FontSize = 12, Margin = new Thickness(0, 0, 4, 0), Classes = { "accent" } };
        ToolTip.SetTip(build, "Describe a board and Claude designs everything: parts, schematic, layout, routing, checks.");
        build.Click += (_, _) => BuildRequested?.Invoke();
        var headerRight = new StackPanel { Orientation = Orientation.Horizontal, Children = { build, newChat, settings } };
        var header = new DockPanel { Margin = new Thickness(12, 8) };
        DockPanel.SetDock(headerRight, Dock.Right);
        header.Children.Add(headerRight);
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { title, modelText } });

        suggestions = new WrapPanel { Margin = new Thickness(10, 0, 10, 0) };
        foreach (var s in Starters)
        {
            var b = new Button { Content = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, FontSize = 12 }, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 6), MaxWidth = 360, HorizontalContentAlignment = HorizontalAlignment.Left };
            b.Click += (_, _) => { input!.Text = s; Send(); };
            suggestions.Children.Add(b);
        }
        var intro = new TextBlock
        {
            Text = "Describe what you want to build. Claude researches real parts on the web, draws the schematic, places parts, routes the board and checks it — you can edit everything by hand at any time.",
            TextWrapping = TextWrapping.Wrap, Foreground = Palette.Muted, Margin = new Thickness(12, 4, 12, 10), FontSize = 12.5,
        };
        messages.Children.Add(intro);
        messages.Children.Add(suggestions);

        scroller = new ScrollViewer { Content = messages, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        input = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 200,
            Watermark = "Ask Claude to design, find parts, route… (Enter to send, Shift+Enter for a new line)",
            FontSize = 13,
        };
        input.AddHandler(KeyDownEvent, (s, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { e.Handled = true; Send(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        send = new Button { Content = "Send", Padding = new Thickness(16, 6), Classes = { "accent" } };
        send.Click += (_, _) => Send();
        stop = new Button { Content = "Stop", Padding = new Thickness(16, 6), IsVisible = false };
        stop.Click += (_, _) => StopRequested?.Invoke();
        statusText = new TextBlock { Foreground = Palette.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var buttons = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var btnStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { stop, send } };
        DockPanel.SetDock(btnStack, Dock.Right);
        buttons.Children.Add(btnStack);
        buttons.Children.Add(statusText);
        var bottom = new StackPanel { Margin = new Thickness(10, 6, 10, 10), Children = { input, buttons } };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(header);
        usageText.Foreground = Palette.Muted;
        usageText.Text = "Pay-as-you-go with your own API key — no monthly cap.";
        DockPanel.SetDock(usageText, Dock.Top);
        root.Children.Add(usageText);
        root.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#232730")), [DockPanel.DockProperty] = Dock.Top });
        root.Children.Add(bottom);
        root.Children.Add(scroller);
        Content = root;
    }

    public void SetModelLabel(string s) => modelText.Text = s;

    public void Clear()
    {
        messages.Children.Clear();
        messages.Children.Add(suggestions);
        pendingTools.Clear();
    }

    void Send()
    {
        var text = input.Text?.Trim();
        if (string.IsNullOrEmpty(text) || stop.IsVisible) return;
        input.Text = "";
        SendRequested?.Invoke(text);
    }

    public void Submit(string text)
    {
        input.Text = text;
        Send();
    }

    public void SetBusy(bool busy)
    {
        stop.IsVisible = busy; send.IsEnabled = !busy;
        if (!busy) statusText.Text = "";
    }

    public void AddUser(string text)
    {
        suggestions.IsVisible = false;
        messages.Children.Add(new Border
        {
            Background = Palette.ChatUser, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8),
            HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 340, Margin = new Thickness(40, 4, 0, 0),
            Child = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
        });
        ScrollToEnd();
    }

    public void Handle(AgentEvent e)
    {
        switch (e.Kind)
        {
            case AgentEventKind.Text:
                messages.Children.Add(new Border
                {
                    Background = Palette.ChatAssistant, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8),
                    Child = Markdown(e.Text),
                });
                break;
            case AgentEventKind.Thinking:
                var body = new SelectableTextBlock { Text = e.Text, TextWrapping = TextWrapping.Wrap, Foreground = Palette.ChatThinking, FontStyle = FontStyle.Italic, FontSize = 12 };
                messages.Children.Add(new Expander
                {
                    Header = new TextBlock { Text = "Thinking", Foreground = Palette.ChatThinking, FontSize = 12 },
                    Content = body, Padding = new Thickness(8, 4), HorizontalAlignment = HorizontalAlignment.Stretch,
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                });
                break;
            case AgentEventKind.ToolCall:
                {
                    var key = e.Text + "#" + (++toolSeq);
                    var text = new TextBlock { Text = $"⚙ {Friendly(e.Text)} …", Foreground = Palette.ChatToolText, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                    var row = new Border { Background = Palette.ChatTool, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4), Child = text };
                    ToolTip.SetTip(row, e.Detail);
                    messages.Children.Add(row);
                    pendingTools[e.Text] = (row, text);
                    statusText.Text = Friendly(e.Text) + "…";
                    break;
                }
            case AgentEventKind.ToolResult:
                if (pendingTools.Remove(e.Text, out var pt))
                {
                    var failed = (e.Detail ?? "").StartsWith("Error");
                    pt.text.Text = $"{(failed ? "✗" : "✓")} {Friendly(e.Text)} — {Short(e.Detail)}";
                    if (failed) pt.text.Foreground = Palette.ChatErrorText;
                }
                break;
            case AgentEventKind.WebSearch:
                messages.Children.Add(new Border
                {
                    Background = Palette.ChatSearch, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4),
                    Child = new TextBlock { Text = "🔎 " + e.Text, Foreground = Palette.ChatSearchText, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                });
                break;
            case AgentEventKind.Error:
                messages.Children.Add(new Border
                {
                    Background = Palette.ChatError, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6),
                    Child = new SelectableTextBlock { Text = e.Text, Foreground = Palette.ChatErrorText, TextWrapping = TextWrapping.Wrap },
                });
                break;
            case AgentEventKind.Status:
                statusText.Text = e.Text;
                break;
            case AgentEventKind.Usage:
                usageText.Text = e.Text;
                ToolTip.SetTip(usageText, e.Detail);
                return;
            case AgentEventKind.Done:
                SetBusy(false);
                break;
        }
        ScrollToEnd();
    }

    public void SetStatus(string s) => statusText.Text = s;

    void ScrollToEnd() => Dispatcher.UIThread.Post(() => scroller.ScrollToEnd(), DispatcherPriority.Background);

    static string Short(string? s) => s == null ? "" : s.Length > 220 ? s[..220] + "…" : s;

    static string Friendly(string tool) => tool switch
    {
        "get_design" => "Reading the design", "list_library" => "Checking the part library", "add_component" => "Adding a part",
        "define_footprint" => "Creating a footprint", "update_component" => "Updating a part", "remove_component" => "Removing parts",
        "connect" => "Connecting pins", "disconnect" => "Disconnecting pins", "mark_no_connect" => "Marking unused pins",
        "set_board" => "Setting up the board", "set_design_rules" => "Setting design rules", "place_components" => "Placing parts",
        "auto_place" => "Auto-placing parts", "autoroute" => "Routing the board", "clear_routing" => "Clearing tracks",
        "add_copper_pour" => "Adding a copper pour", "add_mounting_holes" => "Adding mounting holes", "add_board_text" => "Adding text",
        "run_drc" => "Running design checks", "tidy_schematic" => "Tidying the schematic", _ => tool,
    };

    // ---------- tiny markdown renderer ----------

    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`)")] private static partial Regex InlineRx();

    static Control Markdown(string md)
    {
        var panel = new StackPanel { Spacing = 3 };
        bool inCode = false; var code = new List<string>();
        foreach (var raw in md.Replace("\r", "").Split('\n'))
        {
            var line = raw;
            if (line.TrimStart().StartsWith("```"))
            {
                if (inCode)
                {
                    panel.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#111317")), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4),
                        Child = new SelectableTextBlock { Text = string.Join("\n", code), FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    });
                    code.Clear();
                }
                inCode = !inCode; continue;
            }
            if (inCode) { code.Add(line); continue; }
            if (string.IsNullOrWhiteSpace(line)) { panel.Children.Add(new Border { Height = 2 }); continue; }
            double indent = 0; string bullet = "";
            var t = line.TrimStart();
            if (t.StartsWith("- ") || t.StartsWith("* ")) { bullet = "•  "; t = t[2..]; indent = 6 + (line.Length - line.TrimStart().Length) * 4; }
            else if (Regex.IsMatch(t, @"^\d+\.\s")) { var m = Regex.Match(t, @"^(\d+\.)\s"); bullet = m.Groups[1].Value + " "; t = t[m.Length..]; indent = 6; }
            bool heading = false;
            if (t.StartsWith('#')) { t = t.TrimStart('#').Trim(); heading = true; }
            var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(indent, heading ? 4 : 0, 0, 0), FontWeight = heading ? FontWeight.SemiBold : FontWeight.Normal };
            tb.Inlines = [];
            if (bullet != "") tb.Inlines.Add(new Run(bullet) { Foreground = Palette.Muted });
            foreach (var part in InlineRx().Split(t))
            {
                if (part.Length == 0) continue;
                if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4) tb.Inlines.Add(new Run(part[2..^2]) { FontWeight = FontWeight.SemiBold });
                else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2) tb.Inlines.Add(new Run(part[1..^1]) { FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), Foreground = new SolidColorBrush(Color.Parse("#F0C674")) });
                else tb.Inlines.Add(new Run(part));
            }
            panel.Children.Add(tb);
        }
        if (inCode && code.Count > 0) panel.Children.Add(new SelectableTextBlock { Text = string.Join("\n", code), FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace") });
        return panel;
    }
}
