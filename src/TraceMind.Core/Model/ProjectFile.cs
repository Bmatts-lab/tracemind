using System.Text.Json;
using System.Text.Json.Serialization;

namespace TraceMind.Core.Model;

/// <summary>Native project format (.tmind) — indented JSON.</summary>
public static class ProjectFile
{
    public const string Extension = ".tmind";

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static string Serialize(Project p, bool indented = true) => JsonSerializer.Serialize(p, indented ? Options : Compact);
    public static Project Deserialize(string json) => JsonSerializer.Deserialize<Project>(json, Options) ?? new Project();
    public static void Save(Project p, string path) => File.WriteAllText(path, Serialize(p));
    public static Project Load(string path) => Deserialize(File.ReadAllText(path));
    public static Project Clone(Project p) => Deserialize(Serialize(p, false));
}

/// <summary>Snapshot-based undo/redo.</summary>
public sealed class UndoStack
{
    readonly List<string> undo = [], redo = [];
    public int Limit { get; set; } = 200;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    /// <summary>Record the state *before* a change.</summary>
    public void Push(Project p)
    {
        var s = ProjectFile.Serialize(p, false);
        if (undo.Count > 0 && undo[^1] == s) return;
        undo.Add(s);
        if (undo.Count > Limit) undo.RemoveAt(0);
        redo.Clear();
    }

    public Project? Undo(Project current)
    {
        if (undo.Count == 0) return null;
        redo.Add(ProjectFile.Serialize(current, false));
        var s = undo[^1]; undo.RemoveAt(undo.Count - 1);
        return ProjectFile.Deserialize(s);
    }

    public Project? Redo(Project current)
    {
        if (redo.Count == 0) return null;
        undo.Add(ProjectFile.Serialize(current, false));
        var s = redo[^1]; redo.RemoveAt(redo.Count - 1);
        return ProjectFile.Deserialize(s);
    }

    public void Clear() { undo.Clear(); redo.Clear(); }
}
