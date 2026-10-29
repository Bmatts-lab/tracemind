using TraceMind.Core.Model;
using TraceMind.Core.Netlist;
using TraceMind.Core.Pcb;

namespace TraceMind.App;

/// <summary>The open document: project, file path, undo history, selection and cached analysis.</summary>
public sealed class DesignSession
{
    public Project Project { get; private set; }
    public string? FilePath { get; set; }
    public bool Dirty { get; private set; }
    public UndoStack Undo { get; } = new();

    /// <summary>Raised after any change to the project (structure or geometry).</summary>
    public event Action? Changed;
    /// <summary>Raised when the selection / highlighted net changes.</summary>
    public event Action? SelectionChanged;

    public HashSet<Guid> SelectedComponents { get; } = [];
    public string? HighlightNet { get; private set; }
    public DrcReport? LastDrc { get; set; }

    NetlistResult? netlist;
    ConnectivityResult? connectivity;

    public DesignSession(Project project) { Project = project; }

    public NetlistResult Netlist => netlist ??= NetlistBuilder.Build(Project);
    public ConnectivityResult Connectivity => connectivity ??= Core.Pcb.Connectivity.Analyze(Project, Netlist);

    /// <summary>Record an undo point, apply a change, and notify views.</summary>
    public void Mutate(Action change)
    {
        Undo.Push(Project);
        change();
        MarkChanged();
    }

    public void PushUndo() => Undo.Push(Project);

    public void MarkChanged()
    {
        netlist = null; connectivity = null;
        Dirty = true;
        Changed?.Invoke();
    }

    /// <summary>Geometry-only update during drags (no undo point, keeps dirty flag).</summary>
    public void Invalidate(bool topology = true)
    {
        if (topology) { netlist = null; connectivity = null; }
        else connectivity = null;
        Changed?.Invoke();
    }

    public void Replace(Project p, string? path = null, bool clearUndo = true)
    {
        Project = p;
        if (clearUndo) { Undo.Clear(); FilePath = path; Dirty = false; }
        SelectedComponents.Clear(); HighlightNet = null; LastDrc = null;
        netlist = null; connectivity = null;
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    /// <summary>Swap in a modified copy of the project (e.g. after routing on a clone), with an undo point.</summary>
    public void ReplaceWithUndo(Project p)
    {
        Undo.Push(Project);
        Project = p;
        SelectedComponents.RemoveWhere(id => p.Components.All(c => c.Id != id));
        MarkChanged();
        SelectionChanged?.Invoke();
    }

    public void MarkSaved(string path) { FilePath = path; Dirty = false; Changed?.Invoke(); }

    public bool DoUndo()
    {
        var p = Undo.Undo(Project);
        if (p == null) return false;
        RestoreSnapshot(p);
        return true;
    }

    public bool DoRedo()
    {
        var p = Undo.Redo(Project);
        if (p == null) return false;
        RestoreSnapshot(p);
        return true;
    }

    void RestoreSnapshot(Project p)
    {
        var sel = SelectedComponents.ToList();
        Project = p;
        SelectedComponents.Clear();
        foreach (var id in sel) if (p.Components.Any(c => c.Id == id)) SelectedComponents.Add(id);
        MarkChanged();
        SelectionChanged?.Invoke();
    }

    public IEnumerable<Component> Selection => Project.Components.Where(c => SelectedComponents.Contains(c.Id));

    public void Select(IEnumerable<Component> comps, bool add = false)
    {
        if (!add) SelectedComponents.Clear();
        foreach (var c in comps) SelectedComponents.Add(c.Id);
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        if (SelectedComponents.Count == 0 && HighlightNet == null) return;
        SelectedComponents.Clear(); HighlightNet = null;
        SelectionChanged?.Invoke();
    }

    public void SetHighlightNet(string? net)
    {
        if (HighlightNet == net) return;
        HighlightNet = net;
        SelectionChanged?.Invoke();
    }

    public string NetOfPad(PadInstance pad) => Netlist.NetOfPad(pad.Component, pad.Pad.Number);
}
