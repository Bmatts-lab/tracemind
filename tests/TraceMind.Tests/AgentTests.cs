using System.Net;
using System.Text;
using System.Text.Json;
using TraceMind.AI;
using TraceMind.Core.Model;
using TraceMind.Core.Netlist;
using Xunit.Abstractions;

namespace TraceMind.Tests;

public class AgentTests(ITestOutputHelper output)
{
    static string Msg(string stop, string contentJson) =>
        "{\"id\":\"msg_" + Guid.NewGuid().ToString("N") + "\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5-5\",\"content\":" + contentJson +
        ",\"stop_reason\":\"" + stop + "\",\"stop_sequence\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":10}}";

    [Fact]
    public async Task AgentLoopAgainstMockApi()
    {
        var responses = new Queue<string>([
            Msg("tool_use", """
              [{"type":"thinking","thinking":"Plan: find an LDO.","signature":"sig1"},
               {"type":"text","text":"Let me research a regulator."},
               {"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"AMS1117-3.3 LCSC"}},
               {"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[{"type":"web_search_result","url":"https://lcsc.com/x","title":"AMS1117","encrypted_content":"abc","page_age":null}]},
               {"type":"tool_use","id":"toolu_1","name":"get_design","input":{}}]
            """),
            Msg("tool_use", """
              [{"type":"tool_use","id":"toolu_2","name":"add_component","input":{"name":"AMS1117-3.3","value":"AMS1117-3.3","footprint":"SOT-223","lcsc":"C6186",
                 "pins":[{"number":"1","name":"GND","type":"power_in"},{"number":"2","name":"VOUT","type":"power_out"},{"number":"3","name":"VIN","type":"power_in"}]}},
               {"type":"tool_use","id":"toolu_3","name":"add_component","input":{"symbol":"C","value":"10uF","footprint":"C_0805"}}]
            """),
            Msg("tool_use", """
              [{"type":"tool_use","id":"toolu_4","name":"connect","input":{"net":"+3V3","pins":["U1.VOUT","C1.1"]}},
               {"type":"tool_use","id":"toolu_5","name":"connect","input":{"net":"GND","pins":["U1.1","C1.2"]}}]
            """),
            Msg("end_turn", """[{"type":"text","text":"Added the regulator with its output cap."}]"""),
        ]);
        var requests = new List<string>();
        var port = Random.Shared.Next(20000, 40000);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serverTask = Task.Run(async () =>
        {
            while (responses.Count > 0)
            {
                var ctx = await listener.GetContextAsync();
                using var sr = new StreamReader(ctx.Request.InputStream);
                requests.Add(ctx.Request.Url!.AbsolutePath + " beta=" + ctx.Request.Headers["anthropic-beta"] + "\n" + await sr.ReadToEndAsync());
                var body = Encoding.UTF8.GetBytes(responses.Dequeue());
                ctx.Response.ContentType = "application/json";
                ctx.Response.StatusCode = 200;
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        });

        var project = new Project();
        var settings = new AgentSettings { ApiBaseUrl = $"http://127.0.0.1:{port}" };
        settings.ApiKey = "sk-test";
        var tools = new DesignTools(() => project);
        var agent = new ClaudeAgent(settings, tools, (name, input) => Task.FromResult(tools.Execute(name, input)));
        var events = new List<AgentEvent>();
        agent.OnEvent += e => { events.Add(e); output.WriteLine($"{e.Kind}: {e.Text} {e.Detail}"); };
        await agent.SendAsync("Add a 3.3V regulator", CancellationToken.None);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(events, e => e.Kind == AgentEventKind.Error);
        Assert.Equal(4, requests.Count);
        output.WriteLine(requests[0][..Math.Min(3000, requests[0].Length)]);
        output.WriteLine("---- request 2 ----");
        output.WriteLine(requests[1]);

        using var first = JsonDocument.Parse(requests[0][(requests[0].IndexOf('\n') + 1)..]);
        Assert.Equal("claude-opus-5-5", first.RootElement.GetProperty("model").GetString());
        Assert.Equal("default", first.RootElement.GetProperty("fallbacks").GetString());
        Assert.Contains("server-side-fallback-2026-07-01", requests[0]);
        Assert.Equal("adaptive", first.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Contains(first.RootElement.GetProperty("tools").EnumerateArray(), t => t.TryGetProperty("type", out var ty) && ty.GetString() == "web_search_20260209");

        using var second = JsonDocument.Parse(requests[1][(requests[1].IndexOf('\n') + 1)..]);
        var msgs = second.RootElement.GetProperty("messages");
        Assert.Equal(3, msgs.GetArrayLength());
        var asst = msgs[1].GetProperty("content");
        Assert.Contains(asst.EnumerateArray(), b => b.GetProperty("type").GetString() == "web_search_tool_result");
        Assert.Contains(asst.EnumerateArray(), b => b.GetProperty("type").GetString() == "server_tool_use");
        Assert.Contains(asst.EnumerateArray(), b => b.GetProperty("type").GetString() == "thinking" && b.GetProperty("signature").GetString() == "sig1");
        Assert.Equal("tool_result", msgs[2].GetProperty("content")[0].GetProperty("type").GetString());

        var nl = NetlistBuilder.Build(project);
        Assert.Equal(2, project.Components.Count);
        Assert.Equal(2, nl.Find("+3V3")!.Nodes.Count);
        Assert.Equal(2, nl.Find("GND")!.Nodes.Count);
        Assert.Contains(events, e => e.Kind == AgentEventKind.WebSearch);
    }
}
