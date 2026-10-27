using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TraceMind.AI;

namespace TraceMind.App.Views;

public static class Dialogs
{
    static Window Base(string title, double width, Control content)
    {
        return new Window
        {
            Title = title, Width = width, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new Border { Padding = new Thickness(18), Child = content },
            Background = new SolidColorBrush(Color.Parse("#15171C")), ShowInTaskbar = false,
        };
    }

    public static async Task<string?> Prompt(Window owner, string title, string message, string initial = "")
    {
        var box = new TextBox { Text = initial, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        string? result = null;
        var ok = new Button { Content = "OK", Classes = { "accent" }, IsDefault = true, Padding = new Thickness(18, 5) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 5) };
        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } },
            },
        };
        var w = Base(title, message.Length > 120 ? 520 : 380, panel);
        ok.Click += (_, _) => { result = box.Text; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        w.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await w.ShowDialog(owner);
        return result;
    }

    public static async Task Message(Window owner, string title, string message)
    {
        var ok = new Button { Content = "OK", Classes = { "accent" }, IsDefault = true, IsCancel = true, Padding = new Thickness(18, 5), HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Spacing = 14, Children = { new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, ok } };
        var w = Base(title, 480, panel);
        ok.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
    }

    public static async Task<bool> Confirm(Window owner, string title, string message, string yes = "Yes", string no = "Cancel")
    {
        bool result = false;
        var y = new Button { Content = yes, Classes = { "accent" }, IsDefault = true, Padding = new Thickness(18, 5) };
        var n = new Button { Content = no, IsCancel = true, Padding = new Thickness(14, 5) };
        var panel = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { n, y } },
            },
        };
        var w = Base(title, 440, panel);
        y.Click += (_, _) => { result = true; w.Close(); };
        n.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
        return result;
    }

    /// <summary>Returns: true = save, false = discard, null = cancel.</summary>
    public static async Task<bool?> SaveChanges(Window owner)
    {
        bool? result = null;
        var save = new Button { Content = "Save", Classes = { "accent" }, IsDefault = true, Padding = new Thickness(18, 5) };
        var discard = new Button { Content = "Don't save", Padding = new Thickness(14, 5) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 5) };
        var panel = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "You have unsaved changes. Save them first?", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, discard, save } },
            },
        };
        var w = Base("Unsaved changes", 420, panel);
        save.Click += (_, _) => { result = true; w.Close(); };
        discard.Click += (_, _) => { result = false; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
        return result;
    }

    public static async Task<bool> Settings(Window owner, AgentSettings s)
    {
        bool saved = false;
        bool planDefault = s.Backend == "plan" || (s.Backend == "" && string.IsNullOrWhiteSpace(s.ApiKey));
        var usePlan = new RadioButton { GroupName = "backend", IsChecked = planDefault, Content = new TextBlock { Text = "Use my Claude plan (Pro / Max) through Claude Code — no API bill", TextWrapping = TextWrapping.Wrap } };
        var useApi = new RadioButton { GroupName = "backend", IsChecked = !planDefault, Content = new TextBlock { Text = "Use an Anthropic API key (pay per use)", TextWrapping = TextWrapping.Wrap } };
        var ccStatus = new TextBlock { Text = "Checking for Claude Code…", Classes = { "hint" } };
        const string installCmd = "irm https://claude.ai/install.ps1 | iex";
        var planModel = new ComboBox { ItemsSource = new[] { "Plan default", "opus", "sonnet", "haiku" }, SelectedItem = s.PlanModel == "" ? "Plan default" : s.PlanModel, HorizontalAlignment = HorizontalAlignment.Stretch };
        var copyInstall = new Button { Content = "Copy install command", Padding = new Thickness(10, 3), FontSize = 12, IsVisible = false };
        copyInstall.Click += async (_, _) => { if (TopLevel.GetTopLevel(copyInstall)?.Clipboard is { } cb) await cb.SetTextAsync(installCmd); };
        _ = Task.Run(async () =>
        {
            var v = await TraceMind.AI.ClaudeCodeAgent.ProbeAsync(s.ClaudeCodePath);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (v != null) ccStatus.Text = $"✓ Claude Code found ({v}). If you haven't yet, run `claude` once in a terminal and sign in with your Claude account.";
                else
                {
                    ccStatus.Text = $"Claude Code isn't installed. Open PowerShell and run:\n    {installCmd}\nthen run `claude` once and sign in with your Claude account. Restart TraceMind afterwards.";
                    copyInstall.IsVisible = true;
                }
            });
        });
        var key = new TextBox { Text = s.ApiKey, PasswordChar = '•', Watermark = "sk-ant-…" };
        var reveal = new CheckBox { Content = "Show key" };
        reveal.IsCheckedChanged += (_, _) => key.PasswordChar = reveal.IsChecked == true ? '\0' : '•';
        var model = new ComboBox
        {
            ItemsSource = new[] { "claude-opus-5-5", "claude-fable-5-1", "claude-sonnet-5-5", "claude-haiku-5-5" },
            SelectedItem = s.Model, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        if (model.SelectedItem == null) model.SelectedIndex = 0;
        var effort = new ComboBox { ItemsSource = new[] { "low", "medium", "high", "xhigh", "max" }, SelectedItem = s.Effort, HorizontalAlignment = HorizontalAlignment.Stretch };
        var web = new CheckBox { Content = "Let Claude search the web for parts and datasheets", IsChecked = s.WebSearch };
        var thinking = new CheckBox { Content = "Show Claude's reasoning summaries in the chat", IsChecked = s.ShowThinking };
        var baseUrl = new TextBox { Text = s.ApiBaseUrl, Watermark = "https://api.anthropic.com (default)" };
        var save = new Button { Content = "Save", Classes = { "accent" }, IsDefault = true, Padding = new Thickness(18, 5) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 5) };
        static TextBlock H(string t) => new() { Text = t, Classes = { "section" } };
        var panel = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Claude settings", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Your Claude plan works through Claude Code (free with Pro / Max): usage counts toward your plan's normal limits. Alternatively use an API key from console.anthropic.com (stored encrypted for your Windows user).", Classes = { "hint" } },
                H("HOW TRACEMIND TALKS TO CLAUDE"), usePlan, ccStatus, copyInstall,
                H("MODEL ON YOUR PLAN"), planModel, useApi,
                H("API KEY (only for the API option)"), key, reveal,
                H("API MODEL"), model,
                new TextBlock { Text = "Opus 5.5 is the default. Fable 5.1 is the most capable (and slowest/most expensive); Sonnet and Haiku are faster and cheaper.", Classes = { "hint" } },
                H("EFFORT"), effort,
                new TextBlock { Text = "Higher effort = more careful designs, more tokens. 'high' is a good default for board design.", Classes = { "hint" } },
                H("OPTIONS"), web, thinking,
                H("API BASE URL (OPTIONAL)"), baseUrl,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 14, 0, 0), Children = { cancel, save } },
            },
        };
        var w = Base("Settings", 520, panel);
        save.Click += (_, _) =>
        {
            s.ApiKey = key.Text ?? "";
            s.Backend = usePlan.IsChecked == true ? "plan" : "api";
            s.PlanModel = planModel.SelectedItem as string is { } pm && pm != "Plan default" ? pm : "";
            s.Model = model.SelectedItem as string ?? s.Model;
            s.Effort = effort.SelectedItem as string ?? s.Effort;
            s.WebSearch = web.IsChecked == true; s.ShowThinking = thinking.IsChecked == true;
            s.ApiBaseUrl = baseUrl.Text?.Trim() ?? "";
            s.Save();
            saved = true; w.Close();
        };
        cancel.Click += (_, _) => w.Close();
        await w.ShowDialog(owner);
        return saved;
    }

    public const string Shortcuts = """
        General
          Ctrl+N / Ctrl+O / Ctrl+S   New / Open / Save
          Ctrl+Z / Ctrl+Y            Undo / Redo
          Ctrl+1 / Ctrl+2            Schematic / PCB
          F                          Zoom to fit
          Mouse wheel                Zoom    ·  Middle or right drag: pan
          Esc                        Cancel / back to Select

        Schematic
          S  Select     W  Wire (Space flips the corner)     L  Net label     Q  No-connect
          R  Rotate     M  Mirror     Del  Delete
          Click a pin or label to highlight its net (also on the PCB)

        PCB
          S  Select     X  Route     V  Via + switch layer while routing     /  Corner style
          R / Shift+R  Rotate     F  Flip to other side     L  Lock     Del  Delete / unplace
          PgUp / PgDn  Top / bottom layer
          Ctrl+R  Autoroute     Ctrl+D  Design rule check
        """;
}
