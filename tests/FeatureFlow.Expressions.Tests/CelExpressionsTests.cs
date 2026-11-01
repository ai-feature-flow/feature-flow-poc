namespace FeatureFlow.Expressions.Tests;

/// <summary>
/// Spike 1's four CEL checks (spikes/01-interpreter/src/CelChecks; archive/docs/15 section 9.2), ported
/// against the restrictions of docs/03 section 5.2: restriction, determinism, cost estimate, cost limit.
/// </summary>
public class CelExpressionsTests
{
    // Check 1: restriction. Each must be accepted.
    [Theory]
    [InlineData("findings.open_blocking > 0")]
    [InlineData("findings.open_blocking == 0 && params.check_satisfaction")]
    [InlineData("size(run.repositories) > 1")]
    [InlineData("run.repositories.size() > 1")]
    [InlineData("params.ticket_id.startsWith('TICKET-')")]
    [InlineData("params.ticket_id.endsWith('-1')")]
    [InlineData("params.ticket_id.contains('TICKET')")]
    [InlineData("params.ticket_id.lower() == 'ticket-1'")]
    [InlineData("params.ticket_id.upper() == 'TICKET-1'")]
    [InlineData("'acme/api' in run.repositories")]
    [InlineData("steps.plan.plan.outputs.tier_tags")]
    [InlineData("[steps.design.artifacts.contract, params.x]")]
    [InlineData("1 + 2 * 3 - 4 / 2 % 3 < 10 ? 'a' : 'b'")]
    [InlineData("run.level + ': ' + run.id")]
    public void Allowed_expressions_check(string text)
    {
        var result = CelExpressions.Check(text);

        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }

    // Check 1: restriction. Each must be refused: functions outside the allow-list, comprehensions
    // other than the predicates, regular expressions, unknown variables.
    [Theory]
    [InlineData("httpGet('https://example.com')")]
    [InlineData("params.ticket_id.matches('^T-[0-9]+$')")]
    [InlineData("timestamp('2026-01-01T00:00:00Z') > timestamp(0)")]
    [InlineData("duration('1s')")]
    [InlineData("int('1') == 1")]
    [InlineData("dyn(1) == 1")]
    [InlineData("run.repositories.all(r, r.startsWith('acme/'))")]
    [InlineData("run.repositories.exists(r, r == 'acme/api')")]
    [InlineData("run.repositories.map(r, r + '!')")]
    [InlineData("run.repositories.filter(r, r == 'x')")]
    [InlineData("run.repositories.exists_one(r, r == 'x')")]
    [InlineData("has(params.x)")]
    [InlineData("manifest.repositories.size() > 0")]
    [InlineData("item == 'x'")]
    public void Restricted_expressions_are_refused(string text)
    {
        var result = CelExpressions.Check(text);

        Assert.False(result.Ok);
    }

    [Fact]
    public void Item_is_in_scope_only_when_asked()
    {
        Assert.False(CelExpressions.Check("item").Ok);
        Assert.True(CelExpressions.Check("item", withItem: true).Ok);
    }

    [Fact]
    public void Extra_variables_are_in_scope()
    {
        Assert.False(CelExpressions.Check("ticket_id").Ok);
        Assert.True(CelExpressions.Check("ticket_id", ["ticket_id"]).Ok);
    }

    [Fact]
    public void Type_errors_are_reported()
    {
        var result = CelExpressions.Check("findings.open_blocking == 'none'");

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("type-check"));
    }

    [Fact]
    public void Result_type_distinguishes_guards()
    {
        Assert.True(CelExpressions.Check("findings.open > 0").ResultIsBoolOrDyn);
        Assert.True(CelExpressions.Check("params.flag").ResultIsBoolOrDyn);
        Assert.False(CelExpressions.Check("findings.open + 1").ResultIsBoolOrDyn);
    }

    [Fact]
    public void Typed_paths_take_their_declared_type()
    {
        var typed = new Dictionary<string, string>
        {
            ["steps.triage.outputs.to_fix"] = "number",
            ["steps.catch_up.outputs.changed"] = "boolean",
            ["steps.plan.plan.outputs.tier_tags"] = "array",
        };

        var number = CelExpressions.Check("steps.triage.outputs.to_fix", typedPaths: typed);
        Assert.True(number.Ok, string.Join("; ", number.Errors));
        Assert.Equal("int64", number.ResultType);
        Assert.False(number.ResultIsBoolOrDyn);

        Assert.True(CelExpressions.Check("steps.triage.outputs.to_fix > 0", typedPaths: typed).Ok);
        Assert.True(CelExpressions.Check("steps.catch_up.outputs.changed", typedPaths: typed).ResultIsBoolOrDyn);
        Assert.Equal("list", CelExpressions.Check("steps.plan.plan.outputs.tier_tags", typedPaths: typed).ResultType);
        Assert.False(CelExpressions.Check("steps.catch_up.outputs.changed == 'yes'", typedPaths: typed).Ok);
    }

    [Fact]
    public void Int_and_double_compare_at_run_time()
    {
        var empty = new Dictionary<string, object?>();

        Assert.Equal(true, CelExpressions.Evaluate("1.5 > 1", empty));
        Assert.Equal(false, CelExpressions.Evaluate("0.0 > 0", empty));
    }

    [Fact]
    public void Index_operands_are_collected()
    {
        var result = CelExpressions.Check("params['x'] == 1 && steps['triage'].outputs.bogus > 0 && run.branch['acme/api'] != ''");

        var operands = result.IndexedPaths.Select(p => string.Join(".", p)).ToList();
        Assert.Equal(["params", "steps", "run.branch"], operands);
    }

    [Fact]
    public void Paths_are_collected()
    {
        var result = CelExpressions.Check("steps.plan.plan.outputs.tier_tags.size() > params.n && findings.open == 0");

        var paths = result.Paths.Select(p => string.Join(".", p)).ToList();
        Assert.Contains("steps.plan.plan.outputs.tier_tags", paths);
        Assert.Contains("params.n", paths);
        Assert.Contains("findings.open", paths);
    }

    // Check 2: determinism. The same expression and input give one result, however often evaluated and
    // whatever the map insertion order.
    [Theory]
    [InlineData("findings.open_blocking > 0")]
    [InlineData("size(run.repositories) > 1 && findings.open < 10")]
    [InlineData("params.ticket_id.startsWith('TICKET-') ? 'ticket' : 'other'")]
    [InlineData("run.repositories[0] + '/' + run.level")]
    [InlineData("{'a': 1, 'b': 2, 'c': 3}.b == 2")]
    [InlineData("params.ticket_id.lower()")]
    public void Evaluation_is_deterministic(string text)
    {
        var forward = Variables(reverse: false);
        var backward = Variables(reverse: true);
        var first = CelExpressions.Evaluate(text, forward);

        var results = new HashSet<string>();
        for (var i = 0; i < 2_000; i++)
        {
            results.Add(Show(CelExpressions.Evaluate(text, forward)));
            results.Add(Show(CelExpressions.Evaluate(text, backward)));
        }

        Assert.Equal([Show(first)], results);
    }

    [Fact]
    public void Lower_and_upper_are_culture_invariant()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            var variables = new Dictionary<string, object?> { ["params"] = new Dictionary<string, object?> { ["id"] = "TITLE" } };

            Assert.Equal("title", CelExpressions.Evaluate("params.id.lower()", variables));
            Assert.Equal("TITLE", CelExpressions.Evaluate("'title'.upper()", variables));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    // Check 3: a static cost estimate exists for every accepted expression.
    [Fact]
    public void Cost_is_estimated_statically()
    {
        var result = CelExpressions.Check("findings.open_blocking == 0 && findings.open > 3 && size(run.repositories) > 1");

        Assert.True(result.Ok);
        Assert.NotNull(result.CostMax);
        Assert.InRange(result.CostMax!.Value, 1, CelExpressions.MaxCost);
    }

    // Check 4: the limits are enforced at load: length, syntax tree size and estimated cost.
    [Fact]
    public void Length_limit_is_enforced()
    {
        var text = string.Join(" + ", Enumerable.Range(0, 120).Select(i => $"params.k{i:D3}"));

        var result = CelExpressions.Check(text);

        Assert.True(text.Length > CelExpressions.MaxLength);
        Assert.Contains(result.Errors, e => e.Contains("characters"));
    }

    [Fact]
    public void Node_limit_is_enforced()
    {
        var text = string.Join(" || ", Enumerable.Range(0, 70).Select(i => $"x == {i}"));

        var result = CelExpressions.Check(text, ["x"]);

        Assert.True(text.Length <= CelExpressions.MaxLength);
        Assert.Contains(result.Errors, e => e.Contains("syntax tree nodes"));
    }

    [Fact]
    public void Cost_limit_is_enforced()
    {
        var text = string.Join(" + ", Enumerable.Range(0, 60).Select(i => $"params.k{i:D2}"));

        var result = CelExpressions.Check(text);

        Assert.True(text.Length <= CelExpressions.MaxLength);
        Assert.True(result.Nodes <= CelExpressions.MaxNodes, $"{result.Nodes} nodes");
        Assert.True(result.Errors.Any(e => e.Contains("estimated cost")), $"cost {result.CostMax}: {string.Join("; ", result.Errors)}");
    }

    [Fact]
    public void Template_text_expressions_are_found()
    {
        var found = TemplateText.Expressions("Implement **{{ ticket_id }}** for {{ run.level }}.\nNone here.\n{{ params.x }}");

        Assert.Equal(["ticket_id", "run.level", "params.x"], found.Select(f => f.Text));
        Assert.Equal(3, found[2].Line);
    }

    static Dictionary<string, object?> Variables(bool reverse)
    {
        var findings = new List<KeyValuePair<string, object?>> { new("open_blocking", 2L), new("open", 5L) };
        var parameters = new List<KeyValuePair<string, object?>> { new("ticket_id", "TICKET-1"), new("flag", true) };
        if (reverse)
        {
            findings.Reverse();
            parameters.Reverse();
        }

        return new Dictionary<string, object?>
        {
            ["params"] = parameters.ToDictionary(),
            ["findings"] = findings.ToDictionary(),
            ["run"] = new Dictionary<string, object?> { ["repositories"] = new List<object?> { "acme/api", "acme/web" }, ["level"] = "ticket" },
        };
    }

    static string Show(object? value) => value switch
    {
        null => "null",
        IEnumerable<object?> list when value is not string => "[" + string.Join(",", list.Select(Show)) + "]",
        _ => $"{value.GetType().Name}:{value}",
    };
}
