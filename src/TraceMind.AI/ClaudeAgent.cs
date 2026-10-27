using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace TraceMind.AI;

public enum AgentEventKind { Text, Thinking, ToolCall, ToolResult, WebSearch, Status, Error, Done, Usage }

public sealed record AgentEvent(AgentEventKind Kind, string Text, string? Detail = null);

/// <summary>
/// Conversational PCB design agent. Keeps the conversation history, calls Claude with the design tools
/// plus web search / fetch, runs tool calls through the host (UI thread) and reports progress events.
/// </summary>
public sealed class ClaudeAgent
{
    readonly AgentSettings settings;
    readonly DesignTools tools;
    readonly Func<string, JsonElement, Task<string>> runTool;
    readonly List<BetaMessageParam> history = [];
    public event Action<AgentEvent>? OnEvent;
    public bool Busy { get; private set; }

    public ClaudeAgent(AgentSettings settings, DesignTools tools, Func<string, JsonElement, Task<string>> runTool)
    {
        this.settings = settings; this.tools = tools; this.runTool = runTool;
    }

    public void Reset() { history.Clear(); ConversationCost = 0; ConversationTokens = 0; }

    /// <summary>Estimated USD spent in this conversation (list prices; your invoice is authoritative).</summary>
    public double ConversationCost { get; private set; }
    public long ConversationTokens { get; private set; }
    public double TotalSessionCost { get; private set; }

    /// <summary>(input, output, cache read) $ per million tokens.</summary>
    static (double In, double Out, double Read) Prices(string model) => model switch
    {
        var m when m.StartsWith("claude-fable") || m.StartsWith("claude-mythos") => (10, 50, 0.25),
        var m when m.StartsWith("claude-opus-5-5") => (4, 20, 0.20),
        var m when m.StartsWith("claude-opus") => (5, 25, 0.50),
        var m when m.StartsWith("claude-sonnet") => (2, 10, 0.20),
        var m when m.StartsWith("claude-haiku-5") => (0.10, 0.50, 0.01),
        var m when m.StartsWith("claude-haiku") => (1, 5, 0.10),
        _ => (4, 20, 0.20),
    };

    void AddUsage(BetaMessage resp)
    {
        var u = resp.Usage;
        if (u == null) return;
        var (pin, pout, pread) = Prices(settings.Model);
        long input = u.InputTokens, output = u.OutputTokens;
        long write = u.CacheCreationInputTokens ?? 0, read = u.CacheReadInputTokens ?? 0;
        long searches = u.ServerToolUse?.WebSearchRequests ?? 0;
        double cost = (input * pin + write * pin * 1.25 + read * pread + output * pout) / 1_000_000 + searches * 0.01;
        ConversationCost += cost; TotalSessionCost += cost;
        ConversationTokens += input + output + write + read;
        OnEvent?.Invoke(new(AgentEventKind.Usage, $"≈ ${ConversationCost:0.00} this chat", $"{ConversationTokens:N0} tokens · ≈ ${TotalSessionCost:0.00} since the app started (estimate at list prices)"));
    }

    const string SystemPrompt = """
        You are TraceMind's built-in PCB design engineer. You work inside a desktop EDA app on the user's open project
        (schematic + PCB) through tools, and you can research real parts on the web.

        How the app works
        - Units are millimetres. PCB coordinates: origin at the board's top-left corner, +x right, +y down. Rotation is degrees counter-clockwise.
        - The schematic is label-based: `connect` puts net labels on pins. Every pin is addressed as REF.PIN (pin number or pin name).
        - Parts: use built-in symbols for passives/diodes/transistors/connectors. For any IC or module, pass its full pin list to
          add_component (numbers = footprint pad numbers, names from the datasheet, correct electrical types).
        - Footprints: prefer the built-in parametric ones (list_library). If the package isn't covered, build it with define_footprint
          from the datasheet's recommended land pattern — never guess pad geometry you have not looked up.
        - The autorouter is a grid maze router you steer: net classes (power widths), routing order, skipped nets (e.g. GND when a pour
          connects it), per-net layers and via cost. After routing, read failures and DRC, then fix placement/rules and route again.

        How to work
        - Start by calling get_design to see the current state; don't assume.
        - Research before choosing parts: use web search to find real, in-stock parts (prefer JLCPCB/LCSC basic parts when the user
          wants cheap assembly), check datasheets for pinouts, typical application circuits, required passives, absolute maximums.
          Record mpn, manufacturer, datasheet URL and LCSC number on each part.
        - Design like an experienced engineer: decoupling caps on every supply pin (placed right next to it), pull-ups where needed,
          bulk caps on regulators per datasheet, ESD/reverse-polarity protection on external connectors when appropriate,
          sensible LED resistor values, test points or a programming header for MCUs. Mark unused IC pins as no-connect.
        - Placement: connectors on board edges, decoupling caps within ~2 mm of their pins, keep the switching loop of regulators tight,
          crystals close to the MCU, then auto_place the rest and check for overlaps.
        - Routing: set net classes first (e.g. 0.4–0.6 mm for power), route signals, add a GND pour (usually bottom) when helpful,
          and finish with run_drc. Aim for zero errors.
        - Keep chat replies short and concrete: what you did, key part choices (with part numbers), and anything the user must decide
          or verify. Ask a question only when a real design decision is ambiguous (voltage, connector type, size constraints...).
        - Never claim a part has a property you didn't verify; say when something is an assumption.
        """;

    static List<BetaToolUnion> BuildTools(bool webSearch)
    {
        var list = new List<BetaToolUnion>();
        foreach (var spec in DesignTools.Specs)
        {
            using var doc = JsonDocument.Parse(spec.Schema);
            var props = new Dictionary<string, JsonElement>();
            if (doc.RootElement.TryGetProperty("properties", out var p))
                foreach (var kv in p.EnumerateObject()) props[kv.Name] = kv.Value.Clone();
            var required = doc.RootElement.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
            list.Add(new BetaTool
            {
                Name = spec.Name,
                Description = spec.Description,
                InputSchema = new() { Properties = props, Required = required },
            });
        }
        if (webSearch)
        {
            list.Add(new BetaToolUnion(new BetaWebSearchTool20260209 { MaxUses = 12 }));
            list.Add(new BetaToolUnion(new BetaWebFetchTool20260209 { MaxUses = 8 }));
        }
        return list;
    }

    public async Task SendAsync(string userText, CancellationToken ct)
    {
        if (Busy) throw new InvalidOperationException("Agent is busy");
        var key = settings.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            OnEvent?.Invoke(new(AgentEventKind.Error, "No Claude API key set. Open Settings and paste your key from console.anthropic.com."));
            OnEvent?.Invoke(new(AgentEventKind.Done, ""));
            return;
        }
        Busy = true;
        var client = string.IsNullOrWhiteSpace(settings.ApiBaseUrl)
            ? new AnthropicClient { ApiKey = key }
            : new AnthropicClient { ApiKey = key, BaseUrl = settings.ApiBaseUrl.Trim() };
        tools.Cancellation = ct;
        int startCount = history.Count;
        history.Add(new BetaUserMessageParam { Content = userText });
        var toolDefs = BuildTools(settings.WebSearch);
        try
        {
            for (int round = 0; round < settings.MaxToolRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                OnEvent?.Invoke(new(AgentEventKind.Status, round == 0 ? "Thinking…" : "Working…"));
                var parameters = new MessageCreateParams
                {
                    Model = settings.Model,
                    MaxTokens = 16000,
                    System = SystemPrompt,
                    Messages = history,
                    Tools = toolDefs,
                    Thinking = new BetaThinkingConfigAdaptive { Display = settings.ShowThinking ? Display.Summarized : Display.Omitted },
                    OutputConfig = new BetaOutputConfig { Effort = ParseEffort(settings.Effort) },
                    CacheControl = new BetaCacheControlEphemeral(),
                    Betas = ["server-side-fallback-2026-07-01"],
                    Fallbacks = new Default(),
                };
                BetaMessage resp = await client.Beta.Messages.Create(parameters, ct);
                AddUsage(resp);

                var assistant = new List<BetaContentBlockParam>();
                var toolUses = new List<BetaToolUseBlock>();
                foreach (var block in resp.Content)
                {
                    if (block.TryPickText(out var text))
                    {
                        assistant.Add(new BetaTextBlockParam { Text = text.Text });
                        if (!string.IsNullOrWhiteSpace(text.Text)) OnEvent?.Invoke(new(AgentEventKind.Text, text.Text));
                    }
                    else if (block.TryPickThinking(out var th))
                    {
                        assistant.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                        if (!string.IsNullOrWhiteSpace(th.Thinking)) OnEvent?.Invoke(new(AgentEventKind.Thinking, th.Thinking));
                    }
                    else if (block.TryPickRedactedThinking(out var rt))
                        assistant.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
                    else if (block.TryPickToolUse(out var tu))
                    {
                        assistant.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                        toolUses.Add(tu);
                    }
                    else
                    {
                        // Server tool blocks (web search / fetch results, fallback markers…) are echoed back verbatim.
                        assistant.Add(PassThrough(block));
                        if (block.TryPickServerToolUse(out var stu))
                        {
                            var q = stu.Input.TryGetValue("query", out var qv) ? qv.ToString() : stu.Input.TryGetValue("url", out var uv) ? uv.ToString() : "";
                            OnEvent?.Invoke(new(AgentEventKind.WebSearch, (stu.Name.ToString().Contains("fetch") ? "Reading " : "Searching: ") + q));
                        }
                    }
                }
                history.Add(new BetaAssistantMessageParam { Content = assistant });

                var stop = resp.StopReason?.ToString() ?? "";
                if (stop.Contains("refusal"))
                {
                    OnEvent?.Invoke(new(AgentEventKind.Error, "Claude declined this request." + (resp.StopDetails is { } sd ? " " + sd.Explanation : "")));
                    break;
                }
                if (stop.Contains("pause_turn")) continue;
                if (toolUses.Count == 0)
                {
                    if (stop.Contains("max_tokens")) OnEvent?.Invoke(new(AgentEventKind.Error, "Response was cut off (max tokens). Ask Claude to continue."));
                    break;
                }

                var results = new List<BetaContentBlockParam>();
                foreach (var tu in toolUses)
                {
                    ct.ThrowIfCancellationRequested();
                    var input = JsonSerializer.SerializeToElement(tu.Input);
                    OnEvent?.Invoke(new(AgentEventKind.ToolCall, tu.Name, Summarize(input)));
                    string result;
                    try { result = await runTool(tu.Name, input); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result = JsonSerializer.Serialize(new { ok = false, error = ex.Message }); }
                    bool isError = result.StartsWith("{\"ok\":false");
                    OnEvent?.Invoke(new(AgentEventKind.ToolResult, tu.Name, ResultSummary(result)));
                    results.Add(new BetaToolResultBlockParam { ToolUseID = tu.ID, Content = result, IsError = isError });
                }
                history.Add(new BetaUserMessageParam { Content = results });
            }
        }
        catch (OperationCanceledException)
        {
            OnEvent?.Invoke(new(AgentEventKind.Status, "Stopped."));
            RepairHistory();
        }
        catch (AnthropicApiException ex)
        {
            OnEvent?.Invoke(new(AgentEventKind.Error, "Claude API error: " + ex.Message));
            RepairHistory();
        }
        catch (Exception ex)
        {
            OnEvent?.Invoke(new(AgentEventKind.Error, ex.GetType().Name + ": " + ex.Message));
            RepairHistory();
        }
        finally
        {
            Busy = false;
            OnEvent?.Invoke(new(AgentEventKind.Done, ""));
        }
    }

    /// <summary>After an interruption, make sure the history ends in a state the API accepts (no dangling tool_use).</summary>
    void RepairHistory()
    {
        while (history.Count > 0)
        {
            var last = history[^1];
            var json = JsonSerializer.Serialize(last);
            bool assistantWithTools = json.Contains("\"role\":\"assistant\"") && json.Contains("\"type\":\"tool_use\"");
            bool userText = json.Contains("\"role\":\"user\"") && !json.Contains("tool_result");
            if (assistantWithTools || userText) { history.RemoveAt(history.Count - 1); continue; }
            break;
        }
    }

    static BetaContentBlockParam PassThrough(BetaContentBlock block)
    {
        var json = JsonSerializer.SerializeToElement(block);
        return JsonSerializer.Deserialize<BetaContentBlockParam>(json.GetRawText())!;
    }

    static Effort ParseEffort(string s) => s switch
    {
        "low" => Effort.Low, "medium" => Effort.Medium, "xhigh" => Effort.Xhigh, "max" => Effort.Max, _ => Effort.High,
    };

    static string Summarize(JsonElement input)
    {
        var s = input.GetRawText();
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    static string ResultSummary(string result)
    {
        try
        {
            using var doc = JsonDocument.Parse(result);
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("error", out var e)) return "Error: " + e.GetString();
        }
        catch { }
        return result.Length > 200 ? result[..200] + "…" : result;
    }
}
