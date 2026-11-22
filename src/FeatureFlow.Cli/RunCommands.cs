using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Cli;

/// <summary>
/// <c>ff run</c>: the CLI over the API (docs/02 section 1; M3). The API's address comes from <c>--api</c> or
/// <c>FF_API_URL</c>; the local token from <c>--token-file</c> or <c>FF_API_TOKEN</c>, never from an argument.
/// </summary>
public static class RunCommands
{
    public const string Usage = """
               ff run start <template> [--param <name>=<value>]... [--repo <owner/name>]... [--parent-branch <branch>] [--tracker-issue <key>] [--dry-run] [--continue-as-new-events <n>]
               ff run show <run id> [--timeline]
               ff run wait <run id> [--for end | wait] [--timeout <seconds>]
               ff run answer <run id> <wait id> <outcome | approve | reject> [--reason <text>] [--artifact <name>=<file>]...
               ff run findings <run id>
               ff run plan <run id>
               ff run artifact <run id> <reference>
               ff run recover <run id> <wait id> retry | go_to <step> [--clean-up] | replan <step> | grant_attempts <n> | end <status> [--reason <text>]
               ff run abort <run id> --reason <text> [--close-pull-requests]
               ff run override <run id> <key> [--reason <text>] [--step <step id>] [--change <name>=<value> | <name>=@<file>]...
                 (key: breakpoint, skip, amend, change_role, grant_attempts or override_library; the run applies a
                 protected or beyond-ceiling variant where your roles hold it; @<file> sends the file's text, @@ a literal @)
               (a reason is needed for an outcome that routes the step as a failure, for end and for abort; the API says where)
               (every ff run command takes [--api <url>] [--token-file <file>])
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--dry-run" or "--timeline" or "--clean-up" or "--close-pull-requests")
                flags.Add(args[i]);
            else if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                if (!options.TryGetValue(args[i], out var list))
                    options[args[i]] = list = [];
                list.Add(args[++i]);
            }
            else
                positional.Add(args[i]);
        }

        string? Option(string name) => options.GetValueOrDefault(name)?.LastOrDefault();
        var api = Option("--api") ?? Environment.GetEnvironmentVariable("FF_API_URL") ?? "http://127.0.0.1:8480";
        var token = Option("--token-file") is { } file ? File.ReadAllText(file).Trim() : Environment.GetEnvironmentVariable("FF_API_TOKEN");
        if (token is null)
        {
            error.WriteLine("no API token: give --token-file <file> or set FF_API_TOKEN");
            return 2;
        }

        using var http = new HttpClient { BaseAddress = new Uri(api.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var client = new Client(http, output, error);
        try
        {
            switch (positional)
            {
                case ["start", var template]:
                    return await client.StartAsync(template, options.GetValueOrDefault("--param") ?? [], options.GetValueOrDefault("--repo") ?? [],
                        flags.Contains("--dry-run"), Option("--continue-as-new-events"), Option("--parent-branch"), Option("--tracker-issue"));
                case ["show", var id]:
                    return await client.ShowAsync(id, flags.Contains("--timeline"));
                case ["wait", var id]:
                    return await client.WaitAsync(id, Option("--for") ?? "end", TimeSpan.FromSeconds(double.Parse(Option("--timeout") ?? "300", System.Globalization.CultureInfo.InvariantCulture)));
                case ["answer", var id, var wait, var outcome]:
                    return await client.AnswerAsync(id, Answer(wait, outcome, Option("--reason"), options.GetValueOrDefault("--artifact") ?? []));
                case ["findings", var id]:
                    return await client.PrintAsync($"runs/{id}/findings");
                case ["plan", var id]:
                    return await client.PlanAsync(id);
                case ["artifact", var id, var reference]:
                    return await client.PrintTextAsync($"runs/{id}/artifacts/{Uri.EscapeDataString(reference)}");
                case ["recover", var id, var wait, "retry"]:
                    return await client.AnswerAsync(id, new AnswerRequest(wait, Action: "retry", Reason: Option("--reason")));
                case ["recover", var id, var wait, var action, var step] when action is "go_to" or "replan":
                    return await client.AnswerAsync(id, new AnswerRequest(wait, Action: action, StepId: step, CleanUp: flags.Contains("--clean-up"), Reason: Option("--reason")));
                case ["recover", var id, var wait, "grant_attempts", var n] when long.TryParse(n, out var attempts):
                    return await client.AnswerAsync(id, new AnswerRequest(wait, Action: "grant_attempts", Attempts: attempts, Reason: Option("--reason")));
                case ["recover", var id, var wait, "end", var status]:
                    return await client.AnswerAsync(id, new AnswerRequest(wait, Action: "end", Status: status, Reason: Option("--reason")));
                case ["abort", var id] when Option("--reason") is { } reason:
                    return await client.SendAsync($"runs/{id}/abort", new AbortRequest(reason, flags.Contains("--close-pull-requests")));
                case ["override", var id, var key]:
                    return await client.OverrideAsync(id, key, Option("--reason"), Option("--step"), options.GetValueOrDefault("--change") ?? []);
            }
        }
        catch (HttpRequestException e)
        {
            error.WriteLine($"the API at {api} did not answer: {e.Message}");
            return 3;
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            error.WriteLine(e.Message);
            return 2;
        }

        error.WriteLine("usage: " + Usage.TrimStart());
        return 2;
    }

    /// <summary>A decision's answer with the person's reason, if any; each <c>--artifact name=file</c> gives what the person produced, such as a triage (LOG 248).</summary>
    static AnswerRequest Answer(string wait, string outcome, string? reason, List<string> artifacts)
    {
        var given = artifacts.Select(a => a.Split('=', 2)).ToDictionary(
            p => p[0], p => File.ReadAllText(p.Length == 2 ? p[1] : throw new ArgumentException($"--artifact takes <name>=<file>, not {p[0]}")), StringComparer.Ordinal);
        return new AnswerRequest(wait, outcome, Reason: reason, Artifacts: given.Count == 0 ? null : given);
    }

    /// <summary>The API's calls, with the API's records as bodies and replies (docs/02 section 7).</summary>
    sealed class Client(HttpClient http, TextWriter output, TextWriter error)
    {
        /// <summary>A GET's reply as it came, for a document such as the findings ledger.</summary>
        public async Task<int> PrintAsync(string path)
        {
            var reply = await GetAsync<JsonNode>(path);
            if (reply is null)
                return 1;
            output.WriteLine(reply.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        /// <summary>A GET's reply as text, for an artifact (YAML, Markdown or JSON as it was written).</summary>
        public async Task<int> PrintTextAsync(string path)
        {
            using var response = await http.GetAsync(path);
            if (!response.IsSuccessStatusCode)
            {
                await ReadAsync<JsonNode>(response, quiet: false);
                return 1;
            }

            output.Write(await response.Content.ReadAsStringAsync());
            return 0;
        }

        public async Task<int> StartAsync(string template, List<string> parameters, List<string> repositories, bool dryRun, string? handOver, string? parentBranch = null,
            string? trackerIssue = null)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in parameters)
            {
                var eq = p.IndexOf('=');
                if (eq <= 0)
                {
                    error.WriteLine($"--param takes <name>=<value>, not '{p}'");
                    return 2;
                }

                // A value is read as YAML, so true, 3 and text keep their types; the API takes each as JSON text.
                var node = YamlText.Parse(p[(eq + 1)..], "--param", new DiagnosticBag());
                map[p[..eq]] = node is null ? JsonSerializer.Serialize(p[(eq + 1)..]) : JsonNode.Parse(YamlWriter.Write(node))?.ToJsonString() ?? "null";
            }

            var body = new StartRunRequest(template, repositories, map, dryRun, parentBranch, trackerIssue,
                handOver is null ? null : long.Parse(handOver, System.Globalization.CultureInfo.InvariantCulture));
            var reply = await PostAsync<StartRunRequest, StartRunResponse>("runs", body);
            if (reply is null)
                return 1;
            output.WriteLine($"started {reply.RunId}");
            return 0;
        }

        public async Task<int> ShowAsync(string id, bool timeline)
        {
            var view = await GetAsync<RunDetail>($"runs/{id}");
            if (view is null)
                return 1;
            var run = view.Run;
            output.WriteLine($"run {run.RunId} {run.Status} {run.Template}{(run.DryRun ? " (dry run)" : "")}");
            if (run.Detail is { } detail)
                output.WriteLine($"  {detail}");
            foreach (var wait in view.Waits.Where(w => w.Open))
                output.WriteLine($"wait {wait.WaitId} {KindName(wait.Kind)} at {wait.StepId} ({wait.DecisionType}): {wait.Prompt}; answer {string.Join(" | ", wait.Choices)}");
            foreach (var child in view.Children)
                output.WriteLine($"child {child.RunId} {child.Status} {child.Template}");
            if (timeline)
                foreach (var entry in view.Timeline)
                    output.WriteLine($"  {entry.Sequence,5} {entry.Kind} {entry.StepId}{(entry.Detail is { } d ? ": " + d : "")}");
            return 0;
        }

        /// <summary>A wait's kind as the API names it (<c>patch_approval</c>).</summary>
        static string KindName(WaitKind kind) => JsonSerializer.Serialize(kind, ApiJson.Options).Trim('"');

        /// <summary>Polls the record store through the API until the run ends, or until it has an open wait.</summary>
        public async Task<int> WaitAsync(string id, string until, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < timeout)
            {
                var view = await GetAsync<RunDetail>($"runs/{id}", quiet: true);
                var status = view?.Run.Status;
                if (until == "wait" && view?.Waits.FirstOrDefault(w => w.Open) is { } wait)
                {
                    output.WriteLine($"wait {wait.WaitId} {KindName(wait.Kind)} at {wait.StepId}");
                    return 0;
                }

                if (status is not (null or "starting" or "running"))
                {
                    output.WriteLine($"run {id} {status}");
                    return status == "completed" ? 0 : 1;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            error.WriteLine($"run {id} did not {(until == "wait" ? "open a wait" : "end")} within {timeout.TotalSeconds:0} s");
            return 4;
        }

        public async Task<int> AnswerAsync(string id, AnswerRequest body) => await SendAsync($"runs/{id}/answers", body);

        /// <summary>A run-time override (docs/05 section 5): its key, reason, step and its arguments by name, each given as <c>name=value</c>.</summary>
        public async Task<int> OverrideAsync(string id, string key, string? reason, string? step, List<string> changes)
        {
            var change = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in changes)
            {
                var eq = c.IndexOf('=');
                if (eq <= 0)
                {
                    error.WriteLine($"--change takes <name>=<value>, not '{c}'");
                    return 2;
                }

                change[c[..eq]] = ChangeValue(c[..eq], c[(eq + 1)..]);
            }

            var reply = await PostAsync<OverrideRequest, OverrideResponse>($"runs/{id}/overrides", new OverrideRequest(key, reason, step, change));
            if (reply is null)
                return 1;
            output.WriteLine(reply.Result);
            return 0;
        }

        /// <summary>
        /// An override argument's value: <c>@file</c> is the file's text, such as an <c>amend</c> patch or an
        /// <c>override_library</c> replacement, and <c>@@</c> a value that starts with a literal <c>@</c> (LOG 406).
        /// </summary>
        static string ChangeValue(string name, string value)
        {
            if (value.StartsWith("@@", StringComparison.Ordinal))
                return value[1..];
            if (!value.StartsWith('@'))
                return value;
            var file = value[1..];
            return File.Exists(file) ? File.ReadAllText(file) : throw new ArgumentException($"--change {name}=@{file}: there is no file {file}");
        }

        /// <summary>The run's current plan revision: its number and its readable form; none yet is said so (204, LOG 493).</summary>
        public async Task<int> PlanAsync(string id)
        {
            using var response = await http.GetAsync($"runs/{id}/plan");
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
            {
                output.WriteLine($"run {id} has recorded no plan revision yet");
                return 0;
            }

            var plan = await ReadAsync<PlanView>(response, quiet: false);
            if (plan is null)
                return 1;
            output.WriteLine($"revision {plan.Revision} of {plan.Template}");
            output.Write(plan.Readable);
            return 0;
        }

        public async Task<int> SendAsync<TRequest>(string path, TRequest body)
        {
            var reply = await PostAsync<TRequest, AnswerResponse>(path, body);
            if (reply is null)
                return 1;
            output.WriteLine(reply.Result);
            return 0;
        }

        async Task<TResponse?> PostAsync<TRequest, TResponse>(string path, TRequest body) where TResponse : class
        {
            using var response = await http.PostAsync(path, new StringContent(JsonSerializer.Serialize(body, ApiJson.Options), Encoding.UTF8, "application/json"));
            return await ReadAsync<TResponse>(response, quiet: false);
        }

        async Task<T?> GetAsync<T>(string path, bool quiet = false) where T : class
        {
            using var response = await http.GetAsync(path);
            return await ReadAsync<T>(response, quiet);
        }

        async Task<T?> ReadAsync<T>(HttpResponseMessage response, bool quiet) where T : class
        {
            var text = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                return JsonSerializer.Deserialize<T>(text, ApiJson.Options);
            if (!quiet)
                error.WriteLine($"{(int)response.StatusCode}: {ErrorOf(text)}");
            return null;
        }
    }

    /// <summary>The message of an API refusal (<see cref="ErrorResponse"/>), else the reply's text as it came.</summary>
    internal static string ErrorOf(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<ErrorResponse>(text, ApiJson.Options)?.Error ?? text;
        }
        catch (JsonException)
        {
            return text;
        }
    }
}
