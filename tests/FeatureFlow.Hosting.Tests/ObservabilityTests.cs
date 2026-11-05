using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace FeatureFlow.Hosting.Tests;

/// <summary>
/// The engine's provider decorator (docs/02 section 8, docs/06 section 1): a span per provider call, its type's timeout,
/// and error classification; and the OTLP exporter the installation configures.
/// </summary>
public sealed class ObservabilityTests : IDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Each test's instance name is its own, so tests running at once never read each other's spans.
    readonly string instance = "probe-" + Guid.NewGuid().ToString("N")[..8];
    readonly ConcurrentQueue<Activity> spans = new();
    readonly ActivityListener listener;

    public ObservabilityTests()
    {
        listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == ProviderCalls.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.GetTagItem("feature_flow.provider.instance") as string == instance)
                    spans.Enqueue(a);
            },
        };
        ActivitySource.AddActivityListener(listener);
    }

    public void Dispose() => listener.Dispose();

    /// <summary>A tracker that also receives inbound events, answering <see cref="GetIssueAsync"/> as the test says.</summary>
    sealed class Probe(Func<CancellationToken, Task<ProviderResult<Issue>>> get) : ITrackerProvider, IInboundEventSource
    {
        public ProviderDescription Describe() => new("tracker", "probe", "0", new TrackerCapabilities(true, false, false, false, false, false, false));

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

        public Task<ProviderResult<Issue>> GetIssueAsync(IssueRef issue, CancellationToken cancellationToken) => get(cancellationToken);

        public Task<ProviderResult<IReadOnlyList<Issue>>> SearchAsync(IssueQuery query, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<ProviderResult<IssueRef>> CreateAsync(IssueSpec spec, string key, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<ProviderResult<IssueRef>> UpdateAsync(IssueRef issue, IssueSpec spec, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<ProviderResult<Unit>> TransitionAsync(IssueRef issue, string status, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<ProviderResult<Unit>> LinkAsync(IssueRef from, IssueRef to, string linkType, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<ProviderResult<CommentRef>> CommentAsync(IssueRef issue, string body, string key, CancellationToken cancellationToken) => throw new NotImplementedException();

        public string Source => "probe";

        public Task<ProviderResult<InboundEvent>> ReadEventAsync(InboundRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<InboundEvent>.Success(new InboundEvent("probe", "d1", "push", null)));
    }

    static readonly Issue Found = new(new IssueRef("A-1"), "T", "", "open", [], null, new Dictionary<string, string>());

    ITrackerProvider Decorated(Func<CancellationToken, Task<ProviderResult<Issue>>> get, TimeSpan? timeout = null) =>
        (ITrackerProvider)ProviderCalls.Decorate("tracker", instance, new Probe(get), timeout);

    [Fact]
    public async Task A_call_is_a_span_named_by_type_and_method_and_a_failure_tags_its_reason_and_class()
    {
        var tracker = Decorated(_ => Task.FromResult(ProviderResult<Issue>.Failure("unreachable", "down")));

        var result = await tracker.GetIssueAsync(new IssueRef("A-1"), Ct);

        Assert.Equal("unreachable", result.Error!.Reason);
        var span = Assert.Single(spans);
        Assert.Equal(("tracker.GetIssue", ActivityKind.Client, ActivityStatusCode.Error), (span.DisplayName, span.Kind, span.Status));
        Assert.Equal(("tracker", "probe", "unreachable", "infrastructure"),
            (span.GetTagItem("feature_flow.provider.type"), span.GetTagItem("feature_flow.provider.kind"), span.GetTagItem("feature_flow.error.reason"),
                span.GetTagItem("feature_flow.error.class")));
    }

    [Fact]
    public async Task A_call_past_its_timeout_is_a_timeout_even_when_the_provider_ignores_its_token()
    {
        var never = new TaskCompletionSource<ProviderResult<Issue>>();
        var tracker = Decorated(_ => never.Task, TimeSpan.FromMilliseconds(100));
        var clock = Stopwatch.StartNew();

        var result = await tracker.GetIssueAsync(new IssueRef("A-1"), Ct);

        Assert.Equal((ProviderErrorClass.Infrastructure, "timeout"), (result.Error!.Class, result.Error.Reason));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task The_provider_is_given_a_token_that_its_timeout_cancels()
    {
        CancellationToken given = default;
        var tracker = Decorated(async ct =>
        {
            given = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return ProviderResult<Issue>.Success(Found);
        }, TimeSpan.FromMilliseconds(100));

        Assert.Equal("timeout", (await tracker.GetIssueAsync(new IssueRef("A-1"), Ct)).Error!.Reason);
        Assert.True(given.IsCancellationRequested);
    }

    [Fact]
    public async Task An_exception_is_internal_error_and_a_provider_exception_keeps_its_error()
    {
        var thrown = Decorated(_ => throw new InvalidOperationException("a bug"));
        var asyncThrown = Decorated(async _ =>
        {
            await Task.Yield();
            throw new KeyNotFoundException("another bug");
        });
        var unwrapped = Decorated(_ => throw new ProviderException(new ProviderError("not_found", "no such issue")));

        var first = (await thrown.GetIssueAsync(new IssueRef("A-1"), Ct)).Error!;
        var second = (await asyncThrown.GetIssueAsync(new IssueRef("A-1"), Ct)).Error!;
        var third = (await unwrapped.GetIssueAsync(new IssueRef("A-1"), Ct)).Error!;

        Assert.Equal(("internal_error", ProviderErrorClass.Infrastructure), (first.Reason, first.Class));
        Assert.Contains("tracker.GetIssue threw InvalidOperationException: a bug", first.Message);
        Assert.Equal("internal_error", second.Reason);
        Assert.Equal("not_found", third.Reason);
    }

    [Fact]
    public async Task A_cancellation_the_caller_asked_for_still_throws()
    {
        using var cancel = new CancellationTokenSource();
        var tracker = Decorated(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ProviderResult<Issue>.Success(Found);
        });

        var call = tracker.GetIssueAsync(new IssueRef("A-1"), cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task Every_interface_the_provider_implements_is_kept_and_the_callers_current_span_is_unchanged()
    {
        var provider = (IProvider)Decorated(_ => Task.FromResult(ProviderResult<Issue>.Success(Found)));
        using var outer = new Activity("outer").Start();

        var inbound = Assert.IsAssignableFrom<IInboundEventSource>(provider);
        Assert.Equal("d1", (await inbound.ReadEventAsync(new InboundRequest(new Dictionary<string, string>(), "", DateTimeOffset.UtcNow), Ct)).Value!.DeliveryId);
        Assert.Equal(Found, (await ((ITrackerProvider)provider).GetIssueAsync(new IssueRef("A-1"), Ct)).Value);
        Assert.Same(outer, Activity.Current);
        Assert.Equal("probe", provider.Describe().Name);
    }

    [Fact]
    public void The_default_timeouts_follow_docs_06_by_type()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ProviderCalls.TimeoutFor("tracker"));
        Assert.Equal(TimeSpan.FromSeconds(30), ProviderCalls.TimeoutFor("record_store"));
        Assert.Equal(TimeSpan.FromMinutes(5), ProviderCalls.TimeoutFor("vcs"));
        Assert.Equal(TimeSpan.FromMinutes(5), ProviderCalls.TimeoutFor("artifact_store"));
        Assert.Null(ProviderCalls.TimeoutFor("sandbox"));
        Assert.Null(ProviderCalls.TimeoutFor("agent_runtime"));
    }

    [Fact]
    public async Task Composition_decorates_every_instance()
    {
        var d = new DiagnosticBag();
        var installation = ConfigurationReader.ReadInstallation(
            $"api_version: feature-flow/v1\nkind: installation\nproviders:\n  tracker: {{ instances: {{ {instance}: {{ provider: probe }} }} }}\n", "/tmp/i.yaml", d)!;
        Assert.False(d.HasErrors, string.Join("\n", d));
        var set = ProviderComposer.Compose(installation, [new("tracker", "probe", [], _ => new Probe(_ => Task.FromResult(ProviderResult<Issue>.Success(Found))))]);

        await ((ITrackerProvider)set.ForRun("tracker", false)!).GetIssueAsync(new IssueRef("A-1"), Ct);

        Assert.Equal("tracker.GetIssue", Assert.Single(spans).DisplayName);
    }

    [Fact]
    public async Task The_installations_otlp_endpoint_receives_the_spans()
    {
        var received = new ConcurrentQueue<byte[]>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        await using var collector = builder.Build();
        // The OTLP trace service over gRPC: each export is one length-prefixed protobuf message; an empty reply is success.
        collector.MapPost("/opentelemetry.proto.collector.trace.v1.TraceService/Export", async (HttpContext context) =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            received.Enqueue(body.ToArray());
            context.Response.ContentType = "application/grpc";
            context.Response.AppendTrailer("grpc-status", "0");
            await context.Response.Body.WriteAsync(new byte[5]);
        });
        await collector.StartAsync(Ct);
        var endpoint = collector.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        using (var tracing = Tracing.Start(endpoint))
        {
            await Decorated(_ => Task.FromResult(ProviderResult<Issue>.Success(Found))).GetIssueAsync(new IssueRef("A-1"), Ct);
            Assert.True(tracing!.ForceFlush(10_000));
        }

        var exported = string.Concat(received.Select(b => Encoding.UTF8.GetString(b)));
        Assert.Contains("tracker.GetIssue", exported);
        Assert.Contains(instance, exported);
        Assert.Contains(Tracing.ServiceName, exported);
        Assert.Null(Tracing.Start(null));
    }
}
