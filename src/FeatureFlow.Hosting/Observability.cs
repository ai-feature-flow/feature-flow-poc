using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Castle.DynamicProxy;
using FeatureFlow.Providers.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Temporalio.Extensions.OpenTelemetry;

namespace FeatureFlow.Hosting;

/// <summary>
/// The engine's provider decorator (docs/02 section 8, docs/06 section 1): every provider instance composition creates is
/// wrapped, keeping every interface it implements, so each call that returns a <see cref="ProviderResult{T}"/> gets a span
/// (<c>&lt;type&gt;.&lt;method&gt;</c>, tagged with the instance and, on failure, the error's reason and class), its type's
/// default timeout (docs/06 section 1.2) and error classification: a call past its timeout is <c>timeout</c>
/// (infrastructure), and a <see cref="ProviderException"/> becomes its own error. Any other exception is recorded on the span
/// and re-thrown, so the caller decides as before: an action stops for a person on a provider's bug (LOG 303). A
/// cancellation the caller asked for still throws. Providers add attributes only.
/// </summary>
public static class ProviderCalls
{
    public const string SourceName = "FeatureFlow.Providers";

    public static readonly ActivitySource Source = new(SourceName, "0.1.0");

    /// <summary>
    /// docs/06 section 1.2, by provider type: metadata reads and writes 30 s; the VCS (fetches and pushes), CI (result files)
    /// and artifact store (large payloads) 5 minutes. A sandbox, an agent runtime, a session surface and a validator have
    /// none here: their calls are bounded by the step or by heartbeats.
    /// </summary>
    public static TimeSpan? TimeoutFor(string type) => type switch
    {
        "tracker" or "code_host" or "notifications" or "findings_source" or "secrets" or "credential_broker" or "record_store" => TimeSpan.FromSeconds(30),
        "vcs" or "ci" or "artifact_store" => TimeSpan.FromMinutes(5),
        _ => null,
    };

    static readonly ProxyGenerator Generator = new();

    /// <summary>Wraps <paramref name="provider"/>; <paramref name="timeout"/> overrides its type's default (tests).</summary>
    public static IProvider Decorate(string type, string instance, IProvider provider, TimeSpan? timeout = null)
    {
        var interfaces = provider.GetType().GetInterfaces().Where(i => i.IsVisible && i != typeof(IProvider)).ToArray();
        return (IProvider)Generator.CreateInterfaceProxyWithTargetInterface(typeof(IProvider), interfaces, provider,
            new Interceptor(type, instance, provider.Describe().Name, timeout ?? TimeoutFor(type)));
    }

    sealed class Interceptor(string type, string instance, string kind, TimeSpan? timeout) : IInterceptor
    {
        static readonly ConcurrentDictionary<Type, MethodInfo> Wrappers = new();
        static readonly MethodInfo WrapMethod = typeof(Interceptor).GetMethod(nameof(WrapAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

        public void Intercept(IInvocation invocation)
        {
            var returns = invocation.Method.ReturnType;
            if (!returns.IsGenericType || returns.GetGenericTypeDefinition() != typeof(Task<>)
                                       || returns.GetGenericArguments()[0] is not { IsGenericType: true } result
                                       || result.GetGenericTypeDefinition() != typeof(ProviderResult<>))
            {
                invocation.Proceed();
                return;
            }

            var method = invocation.Method.Name.EndsWith("Async", StringComparison.Ordinal) ? invocation.Method.Name[..^5] : invocation.Method.Name;
            var previous = Activity.Current;
            var activity = Source.StartActivity($"{type}.{method}", ActivityKind.Client);
            activity?.SetTag("feature_flow.provider.type", type);
            activity?.SetTag("feature_flow.provider.instance", instance);
            activity?.SetTag("feature_flow.provider.kind", kind);
            var position = Array.FindIndex(invocation.Method.GetParameters(), p => p.ParameterType == typeof(CancellationToken));
            var caller = position >= 0 && invocation.Arguments[position] is CancellationToken given ? given : CancellationToken.None;
            var limit = CancellationTokenSource.CreateLinkedTokenSource(caller);
            if (timeout is { } t)
                limit.CancelAfter(t);
            if (position >= 0)
                invocation.SetArgumentValue(position, limit.Token);
            Task? call;
            Exception? thrown = null;
            try
            {
                invocation.Proceed();
                call = (Task)invocation.ReturnValue!;
            }
            catch (Exception e)
            {
                call = null;
                thrown = e;
            }

            // The provider's own flow keeps the span as its parent; the caller's goes on as it was.
            Activity.Current = previous;
            var wrapper = Wrappers.GetOrAdd(result.GetGenericArguments()[0], v => WrapMethod.MakeGenericMethod(v));
            invocation.ReturnValue = wrapper.Invoke(this, [call, thrown, activity, limit, caller, $"{type}.{method}"]);
        }

        async Task<ProviderResult<T>> WrapAsync<T>(Task<ProviderResult<T>>? call, Exception? thrown, Activity? activity, CancellationTokenSource limit,
            CancellationToken caller, string what)
        {
            ProviderResult<T> result;
            try
            {
                if (thrown is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(thrown);
                // WaitAsync as well as the token: a provider that ignores its token cannot hold the caller past the timeout.
                result = await call!.WaitAsync(limit.Token);
            }
            catch (OperationCanceledException) when (caller.IsCancellationRequested)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
                activity?.Dispose();
                limit.Dispose();
                throw;
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
                _ = call?.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                result = ProviderResult<T>.Failure("timeout", $"{what} did not answer within {timeout?.TotalSeconds:0} s");
            }
            catch (ProviderException e)
            {
                result = new ProviderResult<T>(default, e.Error);
            }
            catch (Exception e)
            {
                // Not reclassified (LOG 303): the caller decides, as it did before the decorator. An action stops for a
                // person on a provider's bug and retries only network faults (docs/06 section 1).
                activity?.SetTag("error.type", e.GetType().FullName);
                activity?.AddException(e);
                activity?.SetStatus(ActivityStatusCode.Error, $"{what} threw {e.GetType().Name}: {e.Message}");
                activity?.Dispose();
                limit.Dispose();
                throw;
            }

            if (result.Error is { } error)
            {
                activity?.SetTag("feature_flow.error.reason", error.Reason);
                activity?.SetTag("feature_flow.error.class", error.Class.ToString().ToLowerInvariant());
                activity?.SetStatus(ActivityStatusCode.Error, $"{error.Reason}: {error.Message}");
            }

            activity?.Dispose();
            limit.Dispose();
            return result;
        }
    }
}

/// <summary>
/// Tracing (docs/02 section 8): one trace per run, from the API's start through the workflow, its activities and child
/// runs (Temporal's tracing interceptor, on every client), with a span per provider call (<see cref="ProviderCalls"/>).
/// Exported with OTLP to the installation's <c>observability.otlp_endpoint</c>; without one nothing is exported.
/// </summary>
public static class Tracing
{
    public const string ServiceName = "feature-flow";

    /// <summary>The span sources the exporter takes: Temporal's client, workflows and activities, and the provider calls.</summary>
    public static readonly IReadOnlyList<string> Sources =
    [
        TracingInterceptor.ClientSource.Name,
        TracingInterceptor.WorkflowsSource.Name,
        TracingInterceptor.ActivitiesSource.Name,
        ProviderCalls.SourceName,
    ];

    /// <summary>
    /// Temporal's tracing interceptor for a client and the workers on it, with W3C trace context set explicitly: the API's
    /// default propagator does nothing until an exporter starts the SDK, and a run's trace must not depend on that.
    /// </summary>
    public static TracingInterceptor Interceptor() =>
        new(new TracingInterceptorOptions { Propagator = new CompositeTextMapPropagator([new TraceContextPropagator(), new BaggagePropagator()]) });

    /// <summary>The exporter for <paramref name="otlpEndpoint"/>, flushed and stopped when disposed; null when there is none.</summary>
    public static TracerProvider? Start(string? otlpEndpoint) =>
        otlpEndpoint is null
            ? null
            : Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName))
                .AddSource([.. Sources])
                .AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint))
                .Build();
}
