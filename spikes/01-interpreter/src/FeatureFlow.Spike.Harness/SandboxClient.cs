using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeatureFlow.Spike.Harness;

/// <summary>Reads and drives the fake sandbox. The harness's only view of it.</summary>
public sealed class SandboxClient
{
    private readonly HttpClient http;
    private readonly string baseUrl;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public SandboxClient(string baseUrl)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task ResetAsync()
    {
        var response = await http.PostAsync(baseUrl + "/admin/reset", null);
        response.EnsureSuccessStatusCode();
    }

    public async Task<SandboxState> StateAsync()
    {
        var body = await http.GetStringAsync(baseUrl + "/admin/state");
        return JsonSerializer.Deserialize<SandboxState>(body, Json) ?? new SandboxState();
    }

    public async Task<CommandView?> CommandAsync(string handle)
    {
        var response = await http.GetAsync($"{baseUrl}/commands/{handle}");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<CommandView>(body, Json);
    }

    public async Task StallAsync(string handle)
    {
        var response = await http.PostAsync($"{baseUrl}/commands/{handle}/stall", null);
        response.EnsureSuccessStatusCode();
    }

    public async Task BreakConnectionAsync(string handle, int seconds)
    {
        var response = await http.PostAsync(
            $"{baseUrl}/commands/{handle}/break-connection?seconds={seconds}", null);
        response.EnsureSuccessStatusCode();
    }

    public async Task FailHookAsync(string handle)
    {
        var response = await http.PostAsync($"{baseUrl}/commands/{handle}/fail-hook", null);
        response.EnsureSuccessStatusCode();
    }

    public async Task RestoreHookAsync(string stepId)
    {
        var response = await http.PostAsync($"{baseUrl}/hook/{stepId}/restore", null);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> HookUpAsync(string stepId)
    {
        var response = await http.GetAsync($"{baseUrl}/hook/{stepId}");
        return response.IsSuccessStatusCode;
    }
}

public sealed class SandboxState
{
    public List<CommandView> Commands { get; set; } = new();

    public List<string> HookDownStepIds { get; set; } = new();
}

public sealed class CommandView
{
    public string Handle { get; set; } = "";
    public string Status { get; set; } = "";
    public string? SessionRef { get; set; }
    public string? LastCommit { get; set; }
    public int SessionTurns { get; set; }
    public double ElapsedSeconds { get; set; }
    public string? SandboxKey { get; set; }
    public bool ConnectionBroken { get; set; }
    public int ResumeCount { get; set; }

    public string OneLine() =>
        $"handle={Handle} status={Status} session={SessionRef} turns={SessionTurns} " +
        $"elapsed={ElapsedSeconds:F2}s commit={LastCommit ?? "-"} key={SandboxKey} " +
        $"resumeCount={ResumeCount} broken={ConnectionBroken}";
}
