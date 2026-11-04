using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
               ff run answer <run id> <wait id> <outcome | approve | reject> [--artifact <name>=<file>]...
               ff run findings <run id>
               ff run recover <run id> <wait id> retry | go_to <step> [--clean-up] | replan <step> | grant_attempts <n> | end <status>
               ff run abort <run id> [--reason <text>] [--close-pull-requests]
               ff run override <run id> <key> --reason <text> [--step <step id>] [--change <name>=<value>]...
                 (key: breakpoint, skip, amend, change_role, grant_attempts or override_library; the run applies a
                 protected or beyond-ceiling variant where your roles hold it)
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
                    return await client.AnswerAsync(id, Answer(wait, outcome, options.GetValueOrDefault("--artifact") ?? []));
                case ["findings", var id]:
                    return await client.PrintAsync($"runs/{id}/findings");
                case ["recover", var id, var wait, "retry"]:
                    return await client.AnswerAsync(id, new JsonObject { ["wait_id"] = wait, ["action"] = "retry" });
                case ["recover", var id, var wait, var action, var step] when action is "go_to" or "replan":
                    return await client.AnswerAsync(id, new JsonObject { ["wait_id"] = wait, ["action"] = action, ["step_id"] = step, ["clean_up"] = flags.Contains("--clean-up") });
                case ["recover", var id, var wait, "grant_attempts", var n] when long.TryParse(n, out var attempts):
                    return await client.AnswerAsync(id, new JsonObject { ["wait_id"] = wait, ["action"] = "grant_attempts", ["attempts"] = attempts });
                case ["recover", var id, var wait, "end", var status]:
                    return await client.AnswerAsync(id, new JsonObject { ["wait_id"] = wait, ["action"] = "end", ["status"] = status });
                case ["abort", var id]:
                    return await client.SendAsync($"runs/{id}/abort", new JsonObject
                    {
                        ["reason"] = Option("--reason") ?? "aborted from the CLI",
                        ["close_pull_requests"] = flags.Contains("--close-pull-requests"),
                    });
                case ["override", var id, var key] when Option("--reason") is { } reason:
                    return await client.OverrideAsync(id, key, reason, Option("--step"), options.GetValueOrDefault("--change") ?? []);
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

    /// <summary>A decision's answer; each <c>--artifact name=file</c> gives what the person produced, such as a triage (LOG 248).</summary>
    static JsonObject Answer(string wait, string outcome, List<string> artifacts)
    {
        var body = new JsonObject { ["wait_id"] = wait, ["outcome"] = outcome };
        if (artifacts.Count > 0)
            body["artifacts"] = new JsonObject(artifacts.Select(a => a.Split('=', 2)).Select(p =>
                KeyValuePair.Create(p[0], (JsonNode?)JsonValue.Create(File.ReadAllText(p.Length == 2 ? p[1] : throw new ArgumentException($"--artifact takes <name>=<file>, not {p[0]}"))))));
        return body;
    }

    sealed class Client(HttpClient http, TextWriter output, TextWriter error)
    {
        /// <summary>A GET's reply as it came, for a document such as the findings ledger.</summary>
        public async Task<int> PrintAsync(string path)
        {
            var reply = await GetAsync(path);
            if (reply is null)
                return 1;
            output.WriteLine(reply.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        public async Task<int> StartAsync(string template, List<string> parameters, List<string> repositories, bool dryRun, string? handOver, string? parentBranch = null,
            string? trackerIssue = null)
        {
            var map = new JsonObject();
            foreach (var p in parameters)
            {
                var eq = p.IndexOf('=');
                if (eq <= 0)
                {
                    error.WriteLine($"--param takes <name>=<value>, not '{p}'");
                    return 2;
                }

                // A value is read as YAML, so true, 3 and text keep their types.
                var node = YamlText.Parse(p[(eq + 1)..], "--param", new DiagnosticBag());
                map[p[..eq]] = node is null ? p[(eq + 1)..] : JsonNode.Parse(YamlWriter.Write(node));
            }

            var body = new JsonObject
            {
                ["template"] = template,
                ["parameters"] = map,
                ["repositories"] = new JsonArray(repositories.Select(r => (JsonNode?)r).ToArray()),
                ["dry_run"] = dryRun,
            };
            if (parentBranch is not null)
                body["parent_branch"] = parentBranch;
            if (trackerIssue is not null)
                body["tracker_issue"] = trackerIssue;
            if (handOver is not null)
                body["continue_as_new_events"] = long.Parse(handOver, System.Globalization.CultureInfo.InvariantCulture);
            var reply = await PostAsync("runs", body);
            if (reply is null)
                return 1;
            output.WriteLine($"started {reply["run_id"]}");
            return 0;
        }

        public async Task<int> ShowAsync(string id, bool timeline)
        {
            var view = await GetAsync($"runs/{id}");
            if (view is null)
                return 1;
            var run = view["run"]!;
            output.WriteLine($"run {run["runId"]} {run["status"]} {run["template"]}{(run["dryRun"]!.GetValue<bool>() ? " (dry run)" : "")}");
            if (run["detail"] is { } detail)
                output.WriteLine($"  {detail}");
            foreach (var wait in view["waits"]!.AsArray())
                output.WriteLine($"wait {wait!["waitId"]} {wait["kind"]} at {wait["stepId"]} ({wait["decisionType"]}): {wait["reason"]}; answer {string.Join(" | ", wait["choices"]!.AsArray().Select(c => c!.ToString()))}");
            foreach (var child in view["children"]!.AsArray())
                output.WriteLine($"child {child!["runId"]} {child["status"]} {child["template"]}");
            if (timeline)
                foreach (var entry in view["timeline"]!.AsArray())
                    output.WriteLine($"  {entry!["sequence"],5} {entry["kind"]} {entry["stepId"]}{(entry["detail"] is { } d ? ": " + d : "")}");
            return 0;
        }

        /// <summary>Polls the record store through the API until the run ends, or until it has an open wait.</summary>
        public async Task<int> WaitAsync(string id, string until, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < timeout)
            {
                var view = await GetAsync($"runs/{id}", quiet: true);
                var status = view?["run"]?["status"]?.ToString();
                if (until == "wait" && view?["waits"]?.AsArray().Count > 0)
                {
                    var wait = view["waits"]![0]!;
                    output.WriteLine($"wait {wait["waitId"]} {wait["kind"]} at {wait["stepId"]}");
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

        public async Task<int> AnswerAsync(string id, JsonObject body) => await SendAsync($"runs/{id}/answers", body);

        /// <summary>A run-time override (docs/05 section 5): its key, reason, step and its arguments by name, each given as <c>name=value</c>.</summary>
        public async Task<int> OverrideAsync(string id, string key, string reason, string? step, List<string> changes)
        {
            var change = new JsonObject();
            foreach (var c in changes)
            {
                var eq = c.IndexOf('=');
                if (eq <= 0)
                {
                    error.WriteLine($"--change takes <name>=<value>, not '{c}'");
                    return 2;
                }

                change[c[..eq]] = c[(eq + 1)..];
            }

            var body = new JsonObject { ["key"] = key, ["reason"] = reason, ["change"] = change };
            if (step is not null)
                body["step_id"] = step;
            return await SendAsync($"runs/{id}/overrides", body);
        }

        public async Task<int> SendAsync(string path, JsonObject body)
        {
            var reply = await PostAsync(path, body);
            if (reply is null)
                return 1;
            output.WriteLine(reply["result"]?.ToString() ?? reply.ToJsonString());
            return 0;
        }

        async Task<JsonNode?> PostAsync(string path, JsonObject body)
        {
            using var response = await http.PostAsync(path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            return await ReadAsync(response, quiet: false);
        }

        async Task<JsonNode?> GetAsync(string path, bool quiet = false)
        {
            using var response = await http.GetAsync(path);
            return await ReadAsync(response, quiet);
        }

        async Task<JsonNode?> ReadAsync(HttpResponseMessage response, bool quiet)
        {
            var text = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                return JsonNode.Parse(text);
            if (!quiet)
            {
                string message;
                try
                {
                    message = JsonNode.Parse(text)?["error"]?.ToString() ?? text;
                }
                catch (JsonException)
                {
                    message = text;
                }

                error.WriteLine($"{(int)response.StatusCode}: {message}");
            }

            return null;
        }
    }
}
