using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TraceMind.AI;

/// <summary>User settings, stored in %APPDATA%\TraceMind\settings.json. The API key is encrypted with Windows DPAPI.</summary>
public sealed class AgentSettings
{
    public string EncryptedApiKey { get; set; } = "";
    /// <summary>"plan" = use the Claude subscription through the local Claude Code CLI; "api" = Anthropic API key.</summary>
    public string Backend { get; set; } = "";
    /// <summary>Optional explicit path to the claude executable.</summary>
    public string ClaudeCodePath { get; set; } = "";
    /// <summary>Model alias for plan mode (opus / sonnet / haiku); empty = Claude Code's default for the plan.</summary>
    public string PlanModel { get; set; } = "";
    public string Model { get; set; } = "claude-opus-5-5";
    /// <summary>low | medium | high | xhigh | max</summary>
    public string Effort { get; set; } = "high";
    public bool WebSearch { get; set; } = true;
    public bool ShowThinking { get; set; } = true;
    public int MaxToolRounds { get; set; } = 60;
    /// <summary>Optional API base URL (proxies / gateways). Empty = api.anthropic.com.</summary>
    public string ApiBaseUrl { get; set; } = "";
    public string LastFolder { get; set; } = "";
    public List<string> RecentFiles { get; set; } = [];

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TraceMind");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public string ApiKey
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (string.IsNullOrEmpty(EncryptedApiKey)) return env ?? "";
            try
            {
                var bytes = Convert.FromBase64String(EncryptedApiKey);
                if (OperatingSystem.IsWindows()) bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return env ?? ""; }
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value)) { EncryptedApiKey = ""; return; }
            var bytes = Encoding.UTF8.GetBytes(value.Trim());
            if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            EncryptedApiKey = Convert.ToBase64String(bytes);
        }
    }

    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TraceMind.v1");

    public static AgentSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void AddRecent(string path)
    {
        RecentFiles.Remove(path);
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 10) RecentFiles.RemoveAt(10);
    }
}
