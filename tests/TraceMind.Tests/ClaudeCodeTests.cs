using TraceMind.AI;
using TraceMind.Core.Model;
using Xunit.Abstractions;

namespace TraceMind.Tests;

public class ClaudeCodeTests(ITestOutputHelper output)
{
    /// <summary>Live test against a locally installed, logged-in Claude Code. Opt in with TM_CLAUDE_CODE_TEST=1.</summary>
    [Fact]
    public async Task DrivesDesignToolsThroughClaudeCode()
    {
        if (Environment.GetEnvironmentVariable("TM_CLAUDE_CODE_TEST") != "1") return;
        var project = new Project();
        var tools = new DesignTools(() => project);
        var settings = new AgentSettings { PlanModel = "haiku", WebSearch = false };
        using var agent = new ClaudeCodeAgent(settings, (n, i) => Task.FromResult(tools.Execute(n, i)));
        var events = new List<AgentEvent>();
        agent.OnEvent += e => { lock (events) events.Add(e); output.WriteLine($"{e.Kind}: {e.Text} {e.Detail}"); };
        await agent.SendAsync("Add one 10k resistor with footprint R_0603 using add_component, then reply with just its reference.", CancellationToken.None);
        Assert.DoesNotContain(events, e => e.Kind == AgentEventKind.Error);
        Assert.Contains(events, e => e.Kind == AgentEventKind.ToolCall && e.Text == "add_component");
        Assert.Single(project.Components);
        // second turn resumes the same conversation
        await agent.SendAsync("What value did you give it? One word.", CancellationToken.None);
        Assert.Contains(events.Where(e => e.Kind == AgentEventKind.Text), e => e.Text.Contains("10k", StringComparison.OrdinalIgnoreCase));
    }
}
