using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// A step's artifacts (its Artifacts tab, from <c>GET /runs/{id}/artifacts</c>) and the viewer they open in; slice 1's
/// artifact link (<c>#/runs/{id}/artifacts/{ref}</c>) opens the viewer in the run's workspace.
/// </summary>
public sealed class ArtifactTests(BrowserFixture browser) : BrowserTest(browser)
{
    /// <summary>One more byte than the source view shows.</summary>
    const int Shown = 1024 * 1024;

    ILocator Viewer => Page.GetByRole(AriaRole.Complementary, new() { Name = "Viewer" });

    [Fact]
    public async Task A_steps_artifacts_are_listed_and_open_in_the_viewer()
    {
        // Started through the API, so its plan revision is indexed as the engine indexes it.
        var runId = await World.StartRunAsync();
        await Engine.ProduceAsync(runId, "go_ahead", "summary", "the summary", type: "std:artifact_types/requirements", execution: 2);
        await Engine.ProduceAsync(runId, "go_ahead", "checks", "all passed", evidence: true);
        await Engine.ProduceAsync(runId, "go_ahead", "prompt:1", "Do the work.");
        await OpenAsync($"#/runs/{runId}/steps/{runId}/go_ahead");
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Artifacts" }).ClickAsync();

        var rows = Page.GetByRole(AriaRole.Table, new() { Name = "Artifacts" }).Locator("tbody tr");
        await Expect(rows).ToHaveTextAsync(["2summaryartifactstd:artifact_types/requirements", "1checksevidence", "1prompt:1prompt"]);

        // A transcript recorded while the page is open arrives over the change stream.
        await Engine.ProduceAsync(runId, "go_ahead", "transcript:1", "agent: done");
        await Expect(rows.Filter(new() { HasText = "transcript:1" })).ToContainTextAsync("transcript", new() { Timeout = 5_000 });

        await rows.First.GetByRole(AriaRole.Button, new() { Name = "summary", Exact = true }).ClickAsync();
        await Expect(Viewer.Locator(".ff-viewer-name")).ToHaveTextAsync("summary");
        await Expect(Viewer.Locator(".ff-viewer-about")).ToHaveTextAsync("artifact from go_ahead, execution 2, type std:artifact_types/requirements");
        await Expect(Viewer.Locator(".ff-doc")).ToHaveTextAsync("the summary");
    }

    [Fact]
    public async Task Slice_ones_artifact_link_opens_it_in_the_viewer_and_a_transcript_is_shown_as_text()
    {
        // Markdown and markup in a transcript are its text: kept, line breaks and all, and never rendered.
        const string transcript = "# Plan\n\n**bold** and <b>bold</b>\n  - an indented item\n<script>window.ffx = 1</script>";
        await Engine.RunAsync("run-1");
        var reference = await Engine.ProduceAsync("run-1", "build", "transcript:1", transcript);
        await OpenAsync("#/runs/run-1/artifacts/" + Uri.EscapeDataString(reference));

        var text = Viewer.Locator("pre.ff-viewer-text");
        await Expect(text).ToBeVisibleAsync();
        Assert.Equal(transcript, await text.TextContentAsync());
        Assert.Equal("pre-wrap", await text.EvaluateAsync<string>("e => getComputedStyle(e).whiteSpace"));
        Assert.Equal(0, await text.Locator("*").CountAsync());
        await Expect(Viewer.Locator(".ff-viewer-about")).ToHaveTextAsync("transcript from build, execution 1");
        await Expect(Viewer.GetByRole(AriaRole.Tab)).ToContainTextAsync(reference);
        Assert.True(await EvaluateAsync<bool>("window.ffx === undefined"));

        // Its download is offered in the formatted view too.
        var download = await Page.RunAndWaitForDownloadAsync(() => Viewer.GetByRole(AriaRole.Button, new() { Name = "Download" }).ClickAsync());
        Assert.Equal(transcript, await File.ReadAllTextAsync((await download.PathAsync())!, Ct));
    }

    /// <summary>A real run's prompt (run-947be4435f5c's plan.plan, execution 2): the reporting preamble and the task, in Markdown.</summary>
    static string RealPrompt => File.ReadAllText(Path.Combine(Repository.Root, "tests", "FeatureFlow.Console.Tests", "data", "plan-prompt.md"));

    /// <summary>A real Copilot CLI transcript (run-947be4435f5c's plan.plan, execution 1), as the API's tests of its turns read it.</summary>
    internal static string RealTranscript => File.ReadAllText(Path.Combine(Repository.Root, "tests", "FeatureFlow.Api.Tests", "data", "copilot-transcript.jsonl"));

    [Fact]
    public async Task A_transcript_reads_as_turns_with_its_tool_calls_and_reasoning_folded_and_both_views_download_it()
    {
        await Engine.RunAsync("run-1");
        var reference = await Engine.ProduceAsync("run-1", "plan.plan", "transcript:1", RealTranscript, type: "transcript");
        await OpenAsync("#/runs/run-1/artifacts/" + Uri.EscapeDataString(reference));

        await Expect(Viewer.Locator(".ff-tx-head dd")).ToHaveTextAsync(["plan.plan", "Copilot CLI", "claude-sonnet-5.5", "26.8 s", "7", "7", "100,462 in · 3,130 out"]);
        var turns = Viewer.Locator(".ff-tx-turn");
        await Expect(turns).ToHaveCountAsync(13);
        await Expect(turns.Nth(0).Locator(".ff-tx-who")).ToHaveTextAsync("Prompt+0:02");
        // A message is Markdown, formatted.
        await Expect(turns.Nth(11).Locator(".ff-tx-who")).ToHaveTextAsync("Agent+0:27");
        await Expect(turns.Nth(11).Locator(".ff-md code").First).ToHaveTextAsync("requirements.yaml");

        // Reasoning is folded, and not built until opened.
        var reasoning = turns.Nth(7);
        await Expect(reasoning.Locator("summary")).ToContainTextAsync("I can't use bash to write file contents");
        Assert.Equal(0, await reasoning.Locator(".ff-tx-reasoning").CountAsync());

        // A tool call is one row, a failed one marked; it opens to its arguments and result.
        var denied = turns.Nth(5);
        await Expect(denied.Locator("summary .ff-tx-name")).ToHaveTextAsync("bash");
        await Expect(denied.Locator("summary .ff-tx-failed")).ToHaveTextAsync("failed");
        Assert.Equal(0, await denied.Locator("pre").CountAsync());
        await denied.Locator("summary").ClickAsync();
        // One block per argument, a command's lines as lines, then the result.
        await Expect(denied.Locator(".ff-tx-label")).ToHaveTextAsync(["command", "description", "Result"]);
        await Expect(denied.Locator("pre").First).ToContainTextAsync("cd /ff/out; cat > requirements.yaml <<'E'\nschema: 1\n");
        await Expect(denied.Locator("pre").Last).ToContainTextAsync("Permission denied because no interactive user response was available.");

        // A long result shows its first lines, and all of it on request.
        var first = turns.Nth(1);
        await Expect(first.Locator("summary")).ToContainTextAsync("bash Read inputs");
        await first.Locator("summary").ClickAsync();
        var output = first.Locator("pre").Last;
        await Expect(output).ToContainTextAsync("\"step\": \"plan.plan\"");
        Assert.DoesNotContain("/ff/work:", await output.TextContentAsync());
        await first.GetByRole(AriaRole.Button, new() { Name = "Show all (57 lines)" }).ClickAsync();
        await Expect(output).ToContainTextAsync("/ff/work:\nREADME.md");

        var formatted = await Page.RunAndWaitForDownloadAsync(() => Viewer.GetByRole(AriaRole.Button, new() { Name = "Download" }).ClickAsync());
        Assert.Equal(RealTranscript, await File.ReadAllTextAsync((await formatted.PathAsync())!, Ct));
        await Viewer.GetByRole(AriaRole.Button, new() { Name = "Source" }).ClickAsync();
        Assert.Equal(RealTranscript[..1000], (await Viewer.Locator("pre.ff-viewer-text").TextContentAsync())![..1000]);
        var source = await Page.RunAndWaitForDownloadAsync(() => Viewer.GetByRole(AriaRole.Button, new() { Name = "Download" }).ClickAsync());
        Assert.Equal(RealTranscript, await File.ReadAllTextAsync((await source.PathAsync())!, Ct));
    }

    [Fact]
    public async Task A_markdown_prompt_is_formatted_its_source_stays_raw_and_both_views_download_it()
    {
        await Engine.RunAsync("run-1");
        var reference = await Engine.ProduceAsync("run-1", "plan.plan", "prompt:1", RealPrompt, type: "prompt");
        await OpenAsync("#/runs/run-1/artifacts/" + Uri.EscapeDataString(reference));

        var md = Viewer.Locator(".ff-doc[data-format='markdown'] .ff-md");
        await Expect(md.Locator("h1").First).ToHaveTextAsync("How this step works");
        await Expect(md.Locator("h2").First).ToHaveTextAsync("Your inputs");
        await Expect(md.Locator("ul > li code").First).ToHaveTextAsync("outcome");
        await Expect(md.Locator("table").First.Locator("th")).ToHaveTextAsync(["Input", "Use"]);
        await Expect(md.Locator("ol > li").First).ToContainTextAsync("Read the verification skill. Read the ticket.");
        await Expect(md.Locator("strong").First).ToHaveTextAsync("200");

        var formatted = await Page.RunAndWaitForDownloadAsync(() => Viewer.GetByRole(AriaRole.Button, new() { Name = "Download" }).ClickAsync());
        Assert.Equal(RealPrompt, await File.ReadAllTextAsync((await formatted.PathAsync())!, Ct));

        await Viewer.GetByRole(AriaRole.Button, new() { Name = "Source" }).ClickAsync();
        Assert.Equal(RealPrompt, await Viewer.Locator("pre.ff-viewer-text").TextContentAsync());
        var source = await Page.RunAndWaitForDownloadAsync(() => Viewer.GetByRole(AriaRole.Button, new() { Name = "Download" }).ClickAsync());
        Assert.Equal(RealPrompt, await File.ReadAllTextAsync((await source.PathAsync())!, Ct));
    }

    [Fact]
    public async Task Markdown_by_its_name_or_front_matter_is_formatted_and_a_hostile_document_renders_inert()
    {
        const string hostile = """
            # Notes

            <script>window.ffx = 1</script>
            <img src=x onerror="window.ffx=1"> <b class="ffx">bold</b>

            [click](javascript:window.ffx=1) [data](data:text/html,x) <javascript:window.ffx=1> ![img](javascript:window.ffx=1)
            [docs](https://example.com/docs) and [mail](mailto:a@example.com)
            """;
        await Engine.RunAsync("run-1");
        var notes = await Engine.ProduceAsync("run-1", "build", "notes.md", hostile);
        var plan = await Engine.ProduceAsync("run-1", "build", "plan", "---\nowner: ann\n---\n## Steps\n\n1. first\n2. second\n");
        await OpenAsync("#/runs/run-1/artifacts/" + Uri.EscapeDataString(notes));

        var md = Viewer.Locator(".ff-md");
        await Expect(md.Locator("h1")).ToHaveTextAsync("Notes");
        await Expect(md.Locator("p").First).ToHaveTextAsync("<script>window.ffx = 1</script>\n<img src=x onerror=\"window.ffx=1\"> <b class=\"ffx\">bold</b>");
        await Expect(md.Locator("p").Nth(1)).ToContainTextAsync("click data <javascript:window.ffx=1> img");
        // Only the http(s) and mailto links are links, opened without an opener or a referrer.
        var anchors = md.Locator("a");
        await Expect(anchors).ToHaveTextAsync(["docs", "mail"]);
        await Expect(anchors.Nth(0)).ToHaveAttributeAsync("href", "https://example.com/docs");
        await Expect(anchors.Nth(1)).ToHaveAttributeAsync("href", "mailto:a@example.com");
        for (var i = 0; i < 2; i++)
        {
            await Expect(anchors.Nth(i)).ToHaveAttributeAsync("rel", "noopener noreferrer");
            await Expect(anchors.Nth(i)).ToHaveAttributeAsync("target", "_blank");
        }
        Assert.Equal(0, await EvaluateAsync<int>("document.querySelectorAll('.ff-md script, .ff-md img, .ffx, .ff-md b').length"));
        Assert.True(await EvaluateAsync<bool>("window.ffx === undefined"));

        // Front matter makes it Markdown too: the front matter as a table, the body formatted.
        await GoAsync("#/runs/run-1/artifacts/" + Uri.EscapeDataString(plan));
        await Expect(Viewer.Locator(".ff-doc-kv th")).ToHaveTextAsync(["owner"]);
        await Expect(Viewer.Locator(".ff-md h2")).ToHaveTextAsync("Steps");
        await Expect(Viewer.Locator(".ff-md ol > li")).ToHaveTextAsync(["first", "second"]);
    }

    [Fact]
    public async Task An_artifacts_source_downloads_with_the_token()
    {
        UseWorld("local_token");
        await Engine.RunAsync("run-1");
        // Larger than the source view shows, so the download is the only way to the end of it; the cut falls inside the two bytes of é.
        var content = new string('x', Shown - 1) + "é\nthe end";
        var size = System.Text.Encoding.UTF8.GetByteCount(content);
        var reference = await Engine.ProduceAsync("run-1", "build", "log", content);
        var page = await OpenAsync();
        await page.GetByLabel("API token").FillAsync(ConsoleWorld.Token);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await LiveAsync();

        await GoAsync("#/runs/run-1/artifacts/" + Uri.EscapeDataString(reference));
        await Expect(Viewer.Locator(".ff-viewer-name")).ToHaveTextAsync("log");
        await Viewer.GetByRole(AriaRole.Button, new() { Name = "Source" }).ClickAsync();
        await Expect(page.GetByText($"This shows the first 1 MB of {size} bytes. Download the rest.")).ToBeVisibleAsync();
        // The character the cut splits is left out, not shown as a replacement character.
        Assert.Equal(new string('x', Shown - 1), await Viewer.Locator("pre").TextContentAsync());

        var download = await page.RunAndWaitForDownloadAsync(() => Viewer.GetByRole(AriaRole.Button, new() { Name = "Download" }).ClickAsync());
        Assert.Equal("log", download.SuggestedFilename);
        Assert.Null(await download.FailureAsync());
        Assert.Equal(content, await File.ReadAllTextAsync((await download.PathAsync())!, Ct));
    }
}
