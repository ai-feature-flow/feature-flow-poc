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

        await rows.First.GetByRole(AriaRole.Button, new() { Name = "summary" }).ClickAsync();
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
