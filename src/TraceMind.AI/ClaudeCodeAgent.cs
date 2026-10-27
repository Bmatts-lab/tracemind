using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TraceMind.AI;

/// <summary>
/// Runs the design agent through the user's locally installed Claude Code CLI, so it uses their Claude
/// subscription (Pro / Max) login instead of an API key. TraceMind's design tools are exposed to Claude Code
/// through a local MCP server (<see cref="McpBridge"/>).
/// </summary>
public sealed class ClaudeCodeAgent : IDesignAgent, IDisposable
{
    readonly AgentSettings settings;
    readonly Func<string, JsonElement, Task<string>> runTool;
    McpBridge? bridge;
    string? sessionId;
    double sessionCostEquivalent;
    public event Action<AgentEvent>? OnEvent;
    public bool Busy { get; private set; }

    const string ExtraInstructions = """

        You are running inside the TraceMind desktop app. Your only way to see or change the design is the
        mcp__tracemind__* tools; use WebSearch / WebFetch for part research. You have no file system or shell access,
        and none is needed.
        """;

    public ClaudeCodeAgent(AgentSettings settings, Func<string, JsonElement, Task<string>> runTool)
    {
        this.settings = settings; this.runTool = runTool;
    }

    public void Reset() { sessionId = null; sessionCostEquivalent = 0; }

    // ---------------- locating the CLI ----------------

    public static string? FindClaude(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;
        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd", "claude.bat" } : new[] { "claude" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(home, ".local", "bin"));
        dirs.Add(Path.Combine(home, ".claude", "local"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var d in dirs)
            foreach (var n in names)
            {
                try { var p = Path.Combine(d.Trim('"'), n); if (File.Exists(p)) return p; } catch { }
            }
        return null;
    }

    static ProcessStartInfo StartInfo(string exe)
    {
        var psi = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/s"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(exe);
        }
        else psi.FileName = exe;
        return psi;
    }

    /// <summary>Returns the Claude Code version string, or null if it can't be run.</summary>
    public static async Task<string?> ProbeAsync(string? overridePath = null)
    {
        var exe = FindClaude(overridePath);
        if (exe == null) return null;
        try
        {
            var psi = StartInfo(exe);
            psi.ArgumentList.Add("--version");
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch { return null; }
    }

    // ---------------- conversation ----------------

    public async Task SendAsync(string userText, CancellationToken ct)
    {
        if (Busy) throw new InvalidOperationException("Agent is busy");
        var exe = FindClaude(settings.ClaudeCodePath);
        if (exe == null)
        {
            OnEvent?.Invoke(new(AgentEventKind.Error,
                "Claude Code isn't installed. To use your Claude plan: open PowerShell and run\n\n    irm https://claude.ai/install.ps1 | iex\n\nthen run `claude` once and log in with your Claude account. Or switch to an API key in Settings."));
            OnEvent?.Invoke(new(AgentEventKind.Done, ""));
            return;
        }
        Busy = true;
        Process? proc = null;
        var workDir = Path.Combine(Path.GetTempPath(), "TraceMind", "agent");
        try
        {
            Directory.CreateDirectory(workDir);
            if (bridge == null)
            {
                bridge = new McpBridge(runTool);
                bridge.ToolStarted += (n, input) => OnEvent?.Invoke(new(AgentEventKind.ToolCall, n, Short(input.GetRawText(), 300)));
                bridge.ToolFinished += (n, result) => OnEvent?.Invoke(new(AgentEventKind.ToolResult, n, ResultSummary(result)));
            }
            var mcpConfig = Path.Combine(workDir, "mcp.json");
            await File.WriteAllTextAsync(mcpConfig, new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    ["tracemind"] = new JsonObject { ["type"] = "http", ["url"] = bridge.Url, ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + bridge.Token } },
                },
            }.ToJsonString(), ct);
            var promptFile = Path.Combine(workDir, "system-prompt.md");
            await File.WriteAllTextAsync(promptFile, ClaudeAgent.SystemPrompt + ExtraInstructions, ct);

            var psi = StartInfo(exe);
            psi.WorkingDirectory = workDir;
            psi.RedirectStandardInput = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
            foreach (var a in new[]
            {
                "-p", "--output-format", "stream-json", "--verbose",
                "--mcp-config", mcpConfig, "--strict-mcp-config",
                "--append-system-prompt-file", promptFile,
                "--tools", settings.WebSearch ? "WebSearch,WebFetch" : "",
                "--allowedTools", "mcp__tracemind", "WebSearch", "WebFetch",
                "--permission-mode", "dontAsk",
            }) psi.ArgumentList.Add(a);
            // Model: whatever the user's Claude Code / plan defaults to, unless they picked one for plan mode.
            if (!string.IsNullOrWhiteSpace(settings.PlanModel)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(settings.PlanModel); }
            if (sessionId != null) { psi.ArgumentList.Add("--resume"); psi.ArgumentList.Add(sessionId); }
            // Make sure the subscription login is used, not a stray API key in the environment.
            psi.Environment.Remove("ANTHROPIC_API_KEY");

            OnEvent?.Invoke(new(AgentEventKind.Status, "Starting Claude Code…"));
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Claude Code");
            await proc.StandardInput.WriteAsync(userText);
            proc.StandardInput.Close();
            var stderr = new StringBuilder();
            var errTask = Task.Run(async () => { string? l; while ((l = await proc.StandardError.ReadLineAsync()) != null) stderr.AppendLine(l); });
            using var reg = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });

            bool gotResult = false;
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || line[0] != '{') continue;
                JsonNode? ev;
                try { ev = JsonNode.Parse(line); } catch { continue; }
                if (ev == null) continue;
                switch (ev["type"]?.GetValue<string>())
                {
                    case "system" when ev["subtype"]?.GetValue<string>() == "init":
                        sessionId = ev["session_id"]?.GetValue<string>() ?? sessionId;
                        var mcp = ev["mcp_servers"]?.AsArray().FirstOrDefault(s => s?["name"]?.GetValue<string>() == "tracemind");
                        var status = mcp?["status"]?.GetValue<string>();
                        if (status != null && status != "connected")
                            OnEvent?.Invoke(new(AgentEventKind.Error, $"Claude Code could not connect to TraceMind's tools (status: {status})."));
                        OnEvent?.Invoke(new(AgentEventKind.Status, "Thinking…"));
                        break;
                    case "assistant":
                        foreach (var block in ev["message"]?["content"]?.AsArray() ?? [])
                        {
                            var kind = block?["type"]?.GetValue<string>();
                            if (kind == "text" && block!["text"]?.GetValue<string>() is { Length: > 0 } t && !string.IsNullOrWhiteSpace(t))
                                OnEvent?.Invoke(new(AgentEventKind.Text, t));
                            else if (kind == "thinking" && settings.ShowThinking && block!["thinking"]?.GetValue<string>() is { Length: > 0 } th)
                                OnEvent?.Invoke(new(AgentEventKind.Thinking, th));
                            else if (kind == "tool_use")
                            {
                                var name = block!["name"]?.GetValue<string>() ?? "";
                                var input = block["input"];
                                if (name == "WebSearch") OnEvent?.Invoke(new(AgentEventKind.WebSearch, "Searching: " + input?["query"]?.GetValue<string>()));
                                else if (name == "WebFetch") OnEvent?.Invoke(new(AgentEventKind.WebSearch, "Reading " + input?["url"]?.GetValue<string>()));
                            }
                        }
                        break;
                    case "result":
                        gotResult = true;
                        sessionId = ev["session_id"]?.GetValue<string>() ?? sessionId;
                        bool isError = ev["is_error"]?.GetValue<bool>() ?? false;
                        var sub = ev["subtype"]?.GetValue<string>() ?? "";
                        if (isError || sub.StartsWith("error"))
                        {
                            var msg = ev["result"]?.GetValue<string>() ?? sub;
                            OnEvent?.Invoke(new(AgentEventKind.Error, FriendlyError(msg)));
                        }
                        var cost = ev["total_cost_usd"]?.GetValue<double>() ?? 0;
                        sessionCostEquivalent += cost;
                        OnEvent?.Invoke(new(AgentEventKind.Usage, "Using your Claude plan — no API charges",
                            $"Counts toward your plan's usage limits. (API-equivalent value of this chat: ≈ ${sessionCostEquivalent:0.00})"));
                        break;
                }
            }
            await proc.WaitForExitAsync(CancellationToken.None);
            await errTask;
            if (ct.IsCancellationRequested) OnEvent?.Invoke(new(AgentEventKind.Status, "Stopped."));
            else if (!gotResult)
            {
                var err = stderr.ToString().Trim();
                OnEvent?.Invoke(new(AgentEventKind.Error, FriendlyError(err.Length > 0 ? err : $"Claude Code exited with code {proc.ExitCode}.")));
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            OnEvent?.Invoke(new(AgentEventKind.Error, "Claude Code: " + ex.Message));
        }
        catch { OnEvent?.Invoke(new(AgentEventKind.Status, "Stopped.")); }
        finally
        {
            proc?.Dispose();
            Busy = false;
            OnEvent?.Invoke(new(AgentEventKind.Done, ""));
        }
    }

    static string FriendlyError(string msg)
    {
        var m = msg.ToLowerInvariant();
        if (m.Contains("log in") || m.Contains("login") || m.Contains("not authenticated") || m.Contains("invalid api key") || m.Contains("oauth"))
            return "Claude Code isn't logged in. Open a terminal (PowerShell), run `claude`, and sign in with your Claude account. Then try again.\n\nDetails: " + Short(msg, 400);
        if (m.Contains("usage limit") || m.Contains("rate limit") || m.Contains("limit reached"))
            return "You've hit your Claude plan's usage limit for now. It resets after a while — or switch to an API key in Settings.\n\nDetails: " + Short(msg, 400);
        return Short(msg, 1200);
    }

    static string Short(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    static string ResultSummary(string result)
    {
        try
        {
            using var doc = JsonDocument.Parse(result);
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("error", out var e)) return "Error: " + e.GetString();
        }
        catch { }
        return Short(result, 200);
    }

    public void Dispose() => bridge?.Dispose();
}
