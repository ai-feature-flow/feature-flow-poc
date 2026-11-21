using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Cli;

namespace FeatureFlow.Cli.Tests;

/// <summary><c>ff run override --change name=@file</c> (LOG 406) and <c>ff run plan</c>, against a fake API that keeps what it is sent.</summary>
public sealed class RunCommandTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-cli-run-" + Guid.NewGuid().ToString("N"));
    readonly HttpListener listener = new();
    readonly List<(string Method, string Path, string Body)> received = [];
    readonly string address;

    public RunCommandTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "token"), "right");
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            address = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}/";
        }

        listener.Prefixes.Add(address);
        listener.Start();
        _ = ServeAsync();
    }

    public void Dispose()
    {
        listener.Close();
        Directory.Delete(folder, recursive: true);
    }

    async Task ServeAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var path = context.Request.Url!.AbsolutePath;
            lock (received)
                received.Add((context.Request.HttpMethod, path, reader.ReadToEnd()));
            if (path == "/runs/r0/plan")
            {
                // A run with no plan revision yet (LOG 493).
                context.Response.StatusCode = 204;
                context.Response.Close();
                continue;
            }

            object reply = path.EndsWith("/plan", StringComparison.Ordinal)
                ? new PlanView("r1", 2, "std:templates/ticket_delivery", "Template ticket_delivery (level ticket)\n", [])
                : new OverrideResponse("o1", "applied: plan revision 2");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, reply.GetType(), ApiJson.Options);
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    async Task<(int Exit, string Output, string Error)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await RunCommands.RunAsync([.. args, "--api", address, "--token-file", Path.Combine(folder, "token")], output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task A_change_given_as_at_file_sends_the_files_text_and_at_at_a_literal_at()
    {
        var patch = Path.Combine(folder, "patch.yaml");
        const string text = "api_version: feature-flow/v1\nkind: patch\nbase_revision: 1\nchanges: []\n";
        await File.WriteAllTextAsync(patch, text, TestContext.Current.CancellationToken);

        var (exit, output, error) = await RunAsync("override", "r1", "amend", "--reason", "add a step", "--change", $"patch=@{patch}", "--change", "note=@@team");

        Assert.True(exit == 0, error);
        Assert.Equal("applied: plan revision 2", output.Trim());
        var sent = JsonSerializer.Deserialize<OverrideRequest>(Assert.Single(received).Body, ApiJson.Options)!;
        Assert.Equal(new Dictionary<string, string> { ["patch"] = text, ["note"] = "@team" }, sent.Change);
    }

    [Fact]
    public async Task A_change_naming_a_missing_file_is_a_usage_error_and_nothing_is_sent()
    {
        var (exit, _, error) = await RunAsync("override", "r1", "amend", "--reason", "add a step", "--change", $"patch=@{Path.Combine(folder, "absent.yaml")}");

        Assert.Equal(2, exit);
        Assert.Contains("there is no file", error);
        Assert.Empty(received);
    }

    [Fact]
    public async Task Plan_prints_the_current_revision_and_its_readable_form()
    {
        var (exit, output, _) = await RunAsync("plan", "r1");

        Assert.Equal(0, exit);
        Assert.Equal("revision 2 of std:templates/ticket_delivery\nTemplate ticket_delivery (level ticket)\n", output.ReplaceLineEndings("\n"));
        Assert.Equal(("GET", "/runs/r1/plan"), (received.Single().Method, received.Single().Path));
    }

    [Fact]
    public async Task Plan_says_when_the_run_has_no_revision_yet()
    {
        var (exit, output, error) = await RunAsync("plan", "r0");

        Assert.Equal((0, "run r0 has recorded no plan revision yet\n", ""), (exit, output.ReplaceLineEndings("\n"), error));
    }
}
