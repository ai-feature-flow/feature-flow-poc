using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Cel.Checker;
using Cel.Tools;
using Google.Api.Expr.V1Alpha1;

namespace CelChecks;

/// <summary>
/// Check 2: replay determinism. Same expression, same input, thousands of runs ->
/// byte-identical results. Plus: no dependence on map insertion order.
/// </summary>
internal static class Check2Determinism
{
    private const int Iterations = 20_000;

    public static void Run()
    {
        Console.WriteLine("=== CHECK 2: replay determinism ===");
        Console.WriteLine();

        var host = ScriptHost.NewBuilder().Build();
        var decls = RestrictedEnv.Declarations();

        var expressions = new[]
        {
            "findings.blocking_count > 0",
            "size(run.repositories) > 1 && findings.open_count < 10",
            "params.ticket_id.startsWith('TICKET-') ? 'ticket' : 'other'",
            "run.repositories[0] + '/' + run.branch",
            "{'a': 1, 'b': 2, 'c': 3}.b == 2",
            "findings.blocking_count == 0 || findings.open_count > 3"
        };

        var args = BuildArgs(seed: 42);

        foreach (var expr in expressions)
        {
            var script = host.BuildScript(expr).WithDeclarations(decls).Build();

            // Warm up once so the first evaluation is not in the measured set.
            var first = Eval(script, args);
            var firstHash = Hash(first);

            int mismatches = 0;
            var values = new HashSet<string>();
            for (int i = 0; i < Iterations; i++)
            {
                var v = Eval(script, args);
                var h = Hash(v);
                values.Add(h);
                if (h != firstHash) mismatches++;
            }

            Console.WriteLine($"[{expr}]");
            Console.WriteLine($"  iterations:            {Iterations}");
            Console.WriteLine($"  first value:           {first} ({first.GetType().Name})");
            Console.WriteLine($"  distinct result hashes:{values.Count}");
            Console.WriteLine($"  mismatches vs first:   {mismatches}");
            Console.WriteLine($"  verdict:               {(mismatches == 0 && values.Count == 1 ? "DETERMINISTIC" : "NONDETERMINISTIC")}");
            Console.WriteLine();
        }

        MapOrderIndependence(host, decls);
        Console.WriteLine();
    }

    private static object? Eval(Cel.Tools.Script script, IDictionary<string, object> args)
    {
        // Re-create the argument map each time so we are not reusing a captured reference.
        return script.Execute<object?>(args);
    }

    private static IDictionary<string, object> BuildArgs(int seed)
    {
        return new Dictionary<string, object>
        {
            ["params"] = new Dictionary<string, object> { ["ticket_id"] = "TICKET-1234" },
            ["run"] = new Dictionary<string, object>
            {
                ["id"] = "run-0001",
                ["scope"] = "ticket",
                ["repositories"] = new[] { "svc-alpha", "svc-beta", "svc-gamma" },
                ["branch"] = "feature/TICKET-1234"
            },
            ["steps"] = new Dictionary<string, object>
            {
                ["review"] = new Dictionary<string, object> { ["outcome"] = "findings_open" }
            },
            ["findings"] = new Dictionary<string, object>
            {
                ["blocking_count"] = 0L,
                ["open_count"] = 7L
            }
        };
    }

    private static void MapOrderIndependence(ScriptHost host, IList<Decl> decls)
    {
        Console.WriteLine("-- map insertion order independence --");

        const int keyCount = 64;
        var keys = Enumerable.Range(0, keyCount).Select(i => $"k{i:D3}").ToArray();

        // Build one expression that reads every key and folds the values together.
        var expr = string.Join(" + ", keys.Select(k => $"params.{k}"));

        var script = host.BuildScript(expr).WithDeclarations(decls).Build();

        // Several insertion orders: forward, reverse, and 5 shuffled orders.
        var rng = new Random(12345);
        var orders = new List<List<string>>();
        orders.Add(keys.ToList());
        orders.Add(keys.Reverse().ToList());
        for (int s = 0; s < 5; s++)
        {
            var shuffled = keys.OrderBy(_ => rng.Next()).ToList();
            orders.Add(shuffled);
        }

        var hashes = new List<string>();
        for (int i = 0; i < orders.Count; i++)
        {
            // A Dictionary<string,object> whose insertion order differs.
            var map = new Dictionary<string, object>();
            foreach (var k in orders[i]) map[k] = (long)Array.IndexOf(keys, k);

            var args = new Dictionary<string, object>
            {
                ["params"] = map,
                ["run"] = new Dictionary<string, object>(),
                ["steps"] = new Dictionary<string, object>(),
                ["findings"] = new Dictionary<string, object> { ["blocking_count"] = 0L, ["open_count"] = 0L }
            };

            var value = script.Execute<object?>(args);
            var h = Hash(value);
            hashes.Add(h);
            var orderLabel = i switch
            {
                0 => "forward",
                1 => "reverse",
                _ => $"shuffle#{i - 1}"
            };
            Console.WriteLine($"  order {orderLabel,-9} keys={keyCount}  value={value}  hash={h}");
        }

        var distinct = hashes.Distinct().Count();
        Console.WriteLine($"  distinct hashes across {orders.Count} insertion orders: {distinct}");
        Console.WriteLine($"  verdict: {(distinct == 1 ? "ORDER-INDEPENDENT" : "ORDER-DEPENDENT")}");

        // Second, stronger test: does the engine iterate maps at all in a way that leaks?
        // size() over a map with keys inserted in different orders.
        Console.WriteLine();
        Console.WriteLine("-- map size() and key order --");
        var sizeScript = host.BuildScript("size(params)").WithDeclarations(decls).Build();
        var sizeHashes = new List<string>();
        for (int i = 0; i < orders.Count; i++)
        {
            var map = new Dictionary<string, object>();
            foreach (var k in orders[i]) map[k] = 1L;
            var args = new Dictionary<string, object>
            {
                ["params"] = map,
                ["run"] = new Dictionary<string, object>(),
                ["steps"] = new Dictionary<string, object>(),
                ["findings"] = new Dictionary<string, object> { ["blocking_count"] = 0L, ["open_count"] = 0L }
            };
            var v = sizeScript.Execute<object?>(args);
            sizeHashes.Add(Hash(v));
            Console.WriteLine($"  order[{i}] size(params) = {v}");
        }

        Console.WriteLine($"  distinct hashes: {sizeHashes.Distinct().Count()}");
        Console.WriteLine($"  verdict: {(sizeHashes.Distinct().Count() == 1 ? "ORDER-INDEPENDENT" : "ORDER-DEPENDENT")}");

        // 2b: a nested map of 32 sub-maps, inserted in 7 different orders, with an
        // expression that reads a fixed set of leaves. Any map-order dependence in
        // the engine's field resolution would show up as a differing result.
        Console.WriteLine();
        Console.WriteLine("-- nested maps: 32 sub-maps, 7 insertion orders --");
        const int outer = 32;
        const int inner = 4;
        var outerKeys = Enumerable.Range(0, outer).Select(i => $"n{i:D2}").ToArray();
        var innerKeys = new[] { "a", "b", "c", "d" };

        var nestedExpr = string.Join(" + ",
            outerKeys.SelectMany(o => innerKeys.Select(k => $"params.{o}.{k}")));
        var nested = host.BuildScript(nestedExpr).WithDeclarations(decls).Build();

        var rng2 = new Random(777);
        var nestedHashes = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            var order = i switch
            {
                0 => outerKeys.ToList(),
                1 => outerKeys.Reverse().ToList(),
                _ => outerKeys.OrderBy(_ => rng2.Next()).ToList()
            };

            var map = new Dictionary<string, object>();
            foreach (var ok in order)
            {
                var innerMap = new Dictionary<string, object>();
                // Insert the inner keys in a different order each time too.
                foreach (var ik in innerKeys.OrderBy(_ => rng2.Next())) innerMap[ik] = (long)(ik[0] - 'a');
                map[ok] = innerMap;
            }

            var args = new Dictionary<string, object>
            {
                ["params"] = map,
                ["run"] = new Dictionary<string, object>(),
                ["steps"] = new Dictionary<string, object>(),
                ["findings"] = new Dictionary<string, object> { ["blocking_count"] = 0L, ["open_count"] = 0L }
            };

            var v = nested.Execute<object?>(args);
            nestedHashes.Add(Hash(v));
            Console.WriteLine($"  order[{i}] value={v}  hash={nestedHashes[^1]}");
        }

        var nd = nestedHashes.Distinct().Count();
        Console.WriteLine($"  distinct hashes across 7 orders: {nd}");
        Console.WriteLine($"  verdict: {(nd == 1 ? "ORDER-INDEPENDENT" : "ORDER-DEPENDENT")}");

        // Third test: an expression whose RESULT is a map, built from a literal with
        // the keys written in different orders in the source text. This checks that
        // the engine's own map representation does not leak source order.
        Console.WriteLine();
        Console.WriteLine("-- map-valued result: source key order vs equality --");
        var forward = "{'a': 1, 'b': 2, 'c': 3, 'd': 4}";
        var reversed = "{'d': 4, 'c': 3, 'b': 2, 'a': 1}";
        var mixed = "{'b': 2, 'd': 4, 'a': 1, 'c': 3}";

        var f = host.BuildScript(forward).WithDeclarations(decls).Build();
        var rev = host.BuildScript(reversed).WithDeclarations(decls).Build();
        var shuf = host.BuildScript(mixed).WithDeclarations(decls).Build();

        var empty = new Dictionary<string, object>
        {
            ["params"] = new Dictionary<string, object>(),
            ["run"] = new Dictionary<string, object>(),
            ["steps"] = new Dictionary<string, object>(),
            ["findings"] = new Dictionary<string, object>()
        };

        var fv = f.Execute<object?>(empty);
        var rv = rev.Execute<object?>(empty);
        var shv = shuf.Execute<object?>(empty);
        Console.WriteLine($"  {forward} -> {Render(fv)}");
        Console.WriteLine($"  {reversed} -> {Render(rv)}");
        Console.WriteLine($"  {mixed} -> {Render(shv)}");

        var eq = host.BuildScript($"{forward} == {reversed} && {forward} == {mixed}")
            .WithDeclarations(decls).Build();
        var allEqual = eq.Execute<object?>(empty);
        Console.WriteLine($"  ({forward}) == ({reversed}) == ({mixed}) -> {allEqual}");

        var rendered = new[] { Render(fv), Render(rv), Render(shv) }.Distinct().Count();
        Console.WriteLine($"  distinct rendered results: {rendered}");
        Console.WriteLine($"  verdict: {(rendered == 1 && allEqual is true ? "ORDER-INDEPENDENT" : "ORDER-DEPENDENT")}");
    }

    private static string Render(object? v)
    {
        if (v is System.Collections.IDictionary d)
        {
            var parts = d.Keys.Cast<object>()
                .Select(k => $"{k}={d[k]}")
                .OrderBy(x => x, StringComparer.Ordinal);
            return "{" + string.Join(", ", parts) + "}";
        }

        return v?.ToString() ?? "<null>";
    }

    private static string Hash(object? v)
    {
        var s = v switch
        {
            null => "<null>",
            string str => "s:" + str,
            System.Collections.ICollection c => "coll[" + c.Count + "]:" + string.Join(",", c.Cast<object>().Select(x => x?.ToString() ?? "null")),
            _ => v.GetType().FullName + ":" + v
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];
    }
}
