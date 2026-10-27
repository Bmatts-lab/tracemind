using Avalonia.Media;
using Avalonia.Media.Immutable;
using TraceMind.Core.Model;

namespace TraceMind.App;

/// <summary>Colours used by the editors. Dark, high-contrast palette tuned for long sessions.</summary>
public static class Palette
{
    static IImmutableSolidColorBrush B(string hex, double opacity = 1) => new ImmutableSolidColorBrush(Color.Parse(hex), opacity);
    static ImmutablePen P(string hex, double w, double opacity = 1) => new(B(hex, opacity), w);

    public static readonly Color Accent = Color.Parse("#3DDC97");

    // ---- schematic ----
    public static readonly IBrush SchBackground = B("#15171C");
    public static readonly IBrush SchGridDot = B("#2B303A");
    public static readonly IBrush SchSheetBorder = B("#30353F");
    public static readonly IBrush Wire = B("#3DDC97");
    public static readonly IBrush SymbolStroke = B("#E2B254");
    public static readonly IBrush SymbolFill = B("#2A2419");
    public static readonly IBrush PinStroke = B("#C9A050");
    public static readonly IBrush PinName = B("#B4BED0");
    public static readonly IBrush PinNumber = B("#6E7A8E");
    public static readonly IBrush RefText = B("#6FC3FF");
    public static readonly IBrush ValueText = B("#AEB8C8");
    public static readonly IBrush Label = B("#FF9F43");
    public static readonly IBrush PowerLabel = B("#FF6B6B");
    public static readonly IBrush NoConnect = B("#E05555");
    public static readonly IBrush Selection = B("#4DA3FF");
    public static readonly IBrush SelectionFill = B("#4DA3FF", 0.12);
    public static readonly IBrush Highlight = B("#FFFFFF");
    public static readonly IBrush HoverPin = B("#FFFFFF", 0.9);

    // ---- pcb ----
    public static readonly IBrush PcbBackground = B("#0D0F13");
    public static readonly IBrush BoardFill = B("#0F1D17");
    public static readonly IBrush BoardEdge = B("#E8D44D");
    public static readonly IBrush PcbGridDot = B("#1E242C");
    public static readonly IBrush PadTop = B("#D9A441");
    public static readonly IBrush PadBottom = B("#5E8FD8");
    public static readonly IBrush PadTht = B("#C9A23F");
    public static readonly IBrush Hole = B("#0A0B0D");
    public static readonly IBrush ViaRing = B("#B9BEC6");
    public static readonly IBrush SilkTop = B("#EDEDED");
    public static readonly IBrush SilkBottom = B("#A796E0");
    public static readonly IBrush Courtyard = B("#FF4FD8", 0.55);
    public static readonly IBrush Ratsnest = B("#9AA7B8", 0.85);
    public static readonly IBrush DrcMarker = B("#FF3B3B");
    public static readonly IBrush HoleNpth = B("#2A2F37");

    public static Color CopperColor(Layer l) => l switch
    {
        Layer.FCu => Color.Parse("#E0533F"),
        Layer.BCu => Color.Parse("#3F84E0"),
        Layer.In1Cu => Color.Parse("#D6C04A"),
        Layer.In2Cu => Color.Parse("#3FD0B0"),
        _ => Colors.Gray,
    };

    // ---- chat ----
    public static readonly IBrush ChatUser = B("#243447");
    public static readonly IBrush ChatAssistant = B("#1C2026");
    public static readonly IBrush ChatTool = B("#16231D");
    public static readonly IBrush ChatToolText = B("#7FD8B0");
    public static readonly IBrush ChatSearch = B("#1D2233");
    public static readonly IBrush ChatSearchText = B("#9DB4FF");
    public static readonly IBrush ChatThinking = B("#8A93A3");
    public static readonly IBrush ChatError = B("#3A1C1F");
    public static readonly IBrush ChatErrorText = B("#FF8A8A");
    public static readonly IBrush Muted = B("#8A93A3");

    public static readonly ImmutablePen ThinGrid = P("#2B303A", 1);
}
