using System.Collections.Concurrent;
using System.Diagnostics;
using FeatureFlow.Hosting;
using Temporalio.Extensions.OpenTelemetry;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// Tracing (docs/02 section 8): one trace per run, from the API's start through the workflow and its activities, with a
/// span per provider call inside the activity that made it.
/// </summary>
[Trait("Category", "Integration")]
public class ObservabilityRunTests
{
    [Fact]
    public async Task A_runs_activities_are_spans_of_one_trace_and_each_provider_call_is_a_span_inside_its_activity()
    {
        var spans = new ConcurrentQueue<Activity>();
        var sources = Tracing.Sources.ToHashSet();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => sources.Contains(s.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        await using var h = await Harness.StartAsync("otel", autoApprove: true);

        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=FF-11", "--dry-run");
        var (exit, end, _) = h.Cli("wait", id, "--timeout", "120");

        Assert.True((exit, end.Trim()) == (0, $"run {id} completed"), (await h.ShowAsync(id)).ToJsonString());
        var activities = spans.Where(s => s.Source.Name == TracingInterceptor.ActivitiesSource.Name && s.GetTagItem("temporalWorkflowID") as string == id).ToList();
        Assert.Contains(activities, a => a.DisplayName == "RunActivity:Record");
        var trace = Assert.Single(activities.Select(a => a.TraceId).Distinct());
        Assert.Contains(spans, s => s.Source.Name == TracingInterceptor.ClientSource.Name && s.TraceId == trace && s.DisplayName.StartsWith("StartWorkflow:", StringComparison.Ordinal));
        var record = activities.Where(a => a.DisplayName == "RunActivity:Record").Select(a => a.SpanId).ToHashSet();
        Assert.Contains(spans, s => s.Source.Name == ProviderCalls.SourceName && s.DisplayName == "record_store.Append" && s.TraceId == trace && record.Contains(s.ParentSpanId));
    }
}
