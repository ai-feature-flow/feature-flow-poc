using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>A run's artifacts (<c>GET /runs/{id}/artifacts</c>) on its page, and the viewer (<c>#/runs/{id}/artifacts/{ref}</c>).</summary>
public sealed class ArtifactTests(BrowserFixture browser) : BrowserTest(browser)
{
    /// <summary>One more byte than the viewer shows.</summary>
    const int Shown = 1024 * 1024;

    static ILocator Kind(IPage page, string kind) => page.Locator($"[data-kind='{kind}']");

    [Fact]
    public async Task A_runs_artifacts_are_listed_by_kind_and_open_in_the_viewer()
    {
        // Started through the API, so its plan revision is indexed as the engine indexes it.
        var runId = await World.StartRunAsync();
        var summary = await Engine.ProduceAsync(runId, "go_ahead", "summary", "the summary", type: "std:artifact_types/requirements", execution: 2);
        await Engine.ProduceAsync(runId, "go_ahead", "checks", "all passed", evidence: true);
        await Engine.ProduceAsync(runId, "go_ahead", "prompt:1", "Do the work.");
        var page = await OpenAsync("#/runs/" + runId);

        var artifacts = Kind(page, "artifact");
        await Expect(artifacts.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Artifacts");
        await Expect(artifacts.Locator("tbody tr")).ToHaveTextAsync(["go_ahead2summarystd:artifact_types/requirements"]);
        await Expect(Kind(page, "evidence").Locator("tbody tr")).ToHaveTextAsync(["go_ahead1checks"]);
        await Expect(Kind(page, "prompt")).ToContainTextAsync("prompt:1");
        await Expect(Kind(page, "plan_revision")).ToContainTextAsync("plan_revision");

        // A transcript recorded while the page is open arrives over the change stream.
        await Engine.ProduceAsync(runId, "go_ahead", "transcript:1", "agent: done");
        await Expect(Kind(page, "transcript")).ToContainTextAsync("transcript:1", new() { Timeout = 5_000 });

        await artifacts.GetByRole(AriaRole.Link, new() { Name = "summary" }).ClickAsync();
        await Expect(page).ToHaveURLAsync(World.ConsoleUrl + "#/runs/" + runId + "/artifacts/" + Uri.EscapeDataString(summary));
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "summary" })).ToBeVisibleAsync();
        await Expect(page.Locator(".ff-viewer-about")).ToHaveTextAsync("artifact from go_ahead, execution 2, type std:artifact_types/requirements");
        await Expect(page.Locator("pre")).ToHaveTextAsync("the summary");

        await page.GetByRole(AriaRole.Link, new() { Name = "Back to the run" }).ClickAsync();
        await Expect(page).ToHaveURLAsync(World.ConsoleUrl + "#/runs/" + runId);
    }

    [Fact]
    public async Task A_transcript_is_shown_as_text()
    {
        // Markdown and markup in a transcript are its text: kept, line breaks and all, and never rendered.
        const string transcript = "# Plan\n\n**bold** and <b>bold</b>\n  - an indented item\n<script>window.ffx = 1</script>";
        await Engine.RunAsync("run-1");
        var reference = await Engine.ProduceAsync("run-1", "build", "transcript:1", transcript);
        var page = await OpenAsync("#/runs/run-1/artifacts/" + Uri.EscapeDataString(reference));

        var text = page.Locator("pre.ff-viewer-text");
        await Expect(text).ToBeVisibleAsync();
        Assert.Equal(transcript, await text.TextContentAsync());
        Assert.Equal("pre-wrap", await text.EvaluateAsync<string>("e => getComputedStyle(e).whiteSpace"));
        Assert.Equal(0, await text.Locator("*").CountAsync());
        await Expect(page.Locator(".ff-viewer-about")).ToHaveTextAsync("transcript from build, execution 1");
        Assert.True(await EvaluateAsync<bool>("window.ffx === undefined"));
    }

    [Fact]
    public async Task An_artifact_downloads_with_the_token()
    {
        UseWorld("local_token");
        await Engine.RunAsync("run-1");
        // Larger than the viewer shows, so the download is the only way to the end of it; the cut falls inside the two bytes of é.
        var content = new string('x', Shown - 1) + "é\nthe end";
        var size = System.Text.Encoding.UTF8.GetByteCount(content);
        var reference = await Engine.ProduceAsync("run-1", "build", "log", content);
        var page = await OpenAsync();
        await page.GetByLabel("API token").FillAsync(ConsoleWorld.Token);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Needs me" })).ToBeVisibleAsync();

        await page.EvaluateAsync("hash => { location.hash = hash; }", "#/runs/run-1/artifacts/" + Uri.EscapeDataString(reference));
        await Expect(page.GetByText($"This shows the first 1 MB of {size} bytes. Download the rest.")).ToBeVisibleAsync();
        // The character the cut splits is left out, not shown as a replacement character.
        Assert.Equal(new string('x', Shown - 1), await page.Locator("pre").TextContentAsync());
        // The file is named from the run's index, read beside the content.
        await Expect(page.Locator(".ff-viewer-name")).ToHaveTextAsync("log");

        var download = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Button, new() { Name = "Download" }).ClickAsync());
        Assert.Equal("log", download.SuggestedFilename);
        Assert.Null(await download.FailureAsync());
        Assert.Equal(content, await File.ReadAllTextAsync((await download.PathAsync())!, Ct));
    }
}
