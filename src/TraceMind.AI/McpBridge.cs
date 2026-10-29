using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TraceMind.AI;

/// <summary>
/// Minimal MCP server (Streamable HTTP transport, JSON responses) on 127.0.0.1 that exposes TraceMind's
/// design tools to a local Claude Code process.
/// </summary>
public sealed class McpBridge : IDisposable
{
    readonly HttpListener listener = new();
    readonly Func<string, JsonElement, Task<string>> runTool;
    readonly CancellationTokenSource stop = new();
    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/mcp";
    /// <summary>Random secret the client must send, so other local programs can't drive the design.</summary>
    public string Token { get; } = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
    public event Action<string, JsonElement>? ToolStarted;
    public event Action<string, string>? ToolFinished;

    public McpBridge(Func<string, JsonElement, Task<string>> runTool)
    {
        this.runTool = runTool;
        Port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();
        _ = Task.Run(Loop);
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    async Task Loop()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.Headers["Authorization"] != "Bearer " + Token) { ctx.Response.StatusCode = 401; ctx.Response.Close(); return; }
            if (ctx.Request.HttpMethod == "DELETE") { ctx.Response.StatusCode = 200; ctx.Response.Close(); return; }
            if (ctx.Request.HttpMethod != "POST") { ctx.Response.StatusCode = 405; ctx.Response.Close(); return; }
            string body;
            using (var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await sr.ReadToEndAsync();
            var node = JsonNode.Parse(body);
            JsonNode? response;
            if (node is JsonArray batch)
            {
                var arr = new JsonArray();
                foreach (var m in batch) if (await Process(m!) is { } r) arr.Add(r);
                response = arr.Count > 0 ? arr : null;
            }
            else response = await Process(node!);
            if (response == null) { ctx.Response.StatusCode = 202; ctx.Response.Close(); return; }
            var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers["Mcp-Session-Id"] = "tracemind";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    async Task<JsonNode?> Process(JsonNode msg)
    {
        var method = msg["method"]?.GetValue<string>();
        var id = msg["id"]?.DeepClone();
        if (id == null) return null; // notification
        JsonNode Result(JsonNode r) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = r };
        switch (method)
        {
            case "initialize":
                var pv = msg["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18";
                return Result(new JsonObject
                {
                    ["protocolVersion"] = pv,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "tracemind", ["version"] = "1.0" },
                    ["instructions"] = "Tools that read and edit the PCB project open in TraceMind.",
                });
            case "ping":
                return Result(new JsonObject());
            case "tools/list":
                var tools = new JsonArray();
                foreach (var t in DesignTools.Specs)
                    tools.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = JsonNode.Parse(t.Schema) });
                return Result(new JsonObject { ["tools"] = tools });
            case "tools/call":
                var name = msg["params"]?["name"]?.GetValue<string>() ?? "";
                var args = msg["params"]?["arguments"] is JsonNode a ? JsonSerializer.SerializeToElement(a) : JsonDocument.Parse("{}").RootElement;
                ToolStarted?.Invoke(name, args);
                string text;
                try { text = await runTool(name, args); }
                catch (Exception ex) { text = JsonSerializer.Serialize(new { ok = false, error = ex.Message }); }
                var (plain, png) = ClaudeAgent.SplitImage(text);
                ToolFinished?.Invoke(name, plain);
                var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = plain });
                if (png != null) content.Add(new JsonObject { ["type"] = "image", ["data"] = png, ["mimeType"] = "image/png" });
                return Result(new JsonObject
                {
                    ["content"] = content,
                    ["isError"] = text.StartsWith("{\"ok\":false"),
                });
            default:
                return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found: " + method } };
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        try { listener.Stop(); listener.Close(); } catch { }
    }
}
