using System.Text.Json;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>
/// The record store's contract generated from the declarations (docs/06 section 3.12, F17): every declared kind
/// (<see cref="RecordKinds"/>) keeps the write rule <see cref="RecordSamples"/> expects of it, and every declared query
/// (<see cref="RecordQueries"/>) answers each of its forms as its cases expect, text ordered ordinally. A kind or query
/// declared without a sample or a case fails. A record store's test project subclasses it; each test uses its own ids, so
/// it can run against a shared database.
/// </summary>
public abstract class RecordKindContract : ContractBase
{
    protected abstract IRecordStoreProvider Store { get; }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly string run = "ff-kind-" + Guid.NewGuid().ToString("N")[..12];

    public static TheoryData<string> Kinds => [.. RecordKinds.All.Select(k => k.Name)];

    public static TheoryData<string, int> Cases
    {
        get
        {
            var cases = new TheoryData<string, int>();
            foreach (var query in RecordQueries.All.Keys)
                for (var i = 0; i < RecordSamples.QueryCases.GetValueOrDefault(query, []).Count; i++)
                    cases.Add(query, i);
            return cases;
        }
    }

    [Fact]
    public void Every_declared_kind_has_a_sample_and_every_form_of_every_declared_query_a_case()
    {
        Assert.Empty(RecordKinds.All.Where(k => !RecordSamples.Kinds.ContainsKey(k.Record)).Select(k => k.Name));
        Assert.Empty(RecordQueries.All.Values
            .SelectMany(q => q.Variants.Select((v, i) => (Query: q, Variant: v, Form: i + 1)))
            .Where(x => !RecordSamples.QueryCases.GetValueOrDefault(x.Query.Name, []).Any(c => ReferenceEquals(x.Query.VariantFor(c.For(run).Parameters), x.Variant)))
            .Select(x => $"{x.Query.Name}, form {x.Form}"));
    }

    [Fact]
    public void Every_named_query_is_declared_or_store_native()
    {
        var named = typeof(Queries).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!);

        Assert.Empty(named.Where(q => !RecordQueries.All.ContainsKey(q) && !RecordQueries.StoreNative.Contains(q)));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task A_kind_keeps_what_its_write_rule_says(string name)
    {
        var kind = RecordKinds.All.Single(k => k.Name == name);
        var sample = RecordSamples.Kinds[kind.Record];
        object Version(int version) => sample.Make(run, 1, version);
        async Task Write(params int[] versions)
        {
            foreach (var version in versions)
                Ok(await Store.AppendAsync(Batch([Version(version)]), Ct));
        }

        async Task Kept(params string[] expected) =>
            Assert.Equal(expected, await Read(sample.Query, sample.Parameters(run), mine: true));

        switch (sample.Rule)
        {
            case WriteRule.Replace:
                await Write(1, 2);
                await Kept(Text(Version(2)));
                break;
            case WriteRule.ReplaceIfNotOlder:
                await Write(2, 1);
                await Kept(Text(Version(2)));
                break;
            case WriteRule.KeepFirst:
                await Write(1, 2);
                await Kept(Text(Version(1)));
                break;
            case WriteRule.Append:
                await Write(1, 2);
                await Kept(Text(Version(1)), Text(Version(2)));
                break;
            case WriteRule.CloseKeepsOpening:
                await Write(1, 2);
                var closed = JsonSerializer.SerializeToNode(Version(1), kind.Record, Json)!;
                closed["open"] = false;
                closed["updatedAt"] = JsonSerializer.SerializeToNode(Version(2), kind.Record, Json)!["updatedAt"]!.DeepClone();
                await Kept(Text(closed.Deserialize(kind.Record, Json)!));
                await Write(3);
                await Kept(Text(Version(3)));
                break;
            case WriteRule.KeepFirstWhileOpen:
                await Write(1, 3);
                await Kept(Text(Version(1)));
                await Write(2, 3);
                await Kept(Text(Version(2)));
                break;
            default:
                Assert.Fail($"no case for the rule {sample.Rule}");
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_query_answers_each_of_its_forms(string query, int index)
    {
        var @case = RecordSamples.QueryCases[query][index];
        var (write, parameters, expect) = @case.For(run);

        Ok(await Store.AppendAsync(Batch(write), Ct));

        Assert.Equal(expect.Select(Text), await Read(query, parameters, mine: @case.Shared));
    }

    static string Text(object record) => JsonSerializer.Serialize(record, record.GetType(), Json);

    /// <summary>A batch of records of any kinds, each in its kind's list in the order given.</summary>
    static RecordBatch Batch(IEnumerable<object> records)
    {
        var constructor = typeof(RecordBatch).GetConstructors().Single();
        return (RecordBatch)constructor.Invoke([.. constructor.GetParameters().Select(p =>
        {
            var type = p.ParameterType.GetGenericArguments()[0];
            var of = records.Where(r => r.GetType() == type).ToArray();
            var list = Array.CreateInstance(type, of.Length);
            Array.Copy(of, list, of.Length);
            return of.Length == 0 ? null : (object)list;
        })]);
    }

    /// <summary>A query's records as JSON, in order; with <paramref name="mine"/>, only this test's.</summary>
    async Task<List<string>> Read(string query, Dictionary<string, string> parameters, bool mine)
    {
        var type = RecordQueries.All[query].Kind.Record;
        var read = (Task<List<string>>)typeof(RecordKindContract).GetMethod(nameof(ReadAs), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .MakeGenericMethod(type).Invoke(this, [query, parameters])!;
        return [.. (await read).Where(r => !mine || r.Contains(run, StringComparison.Ordinal))];
    }

    async Task<List<string>> ReadAs<T>(string query, Dictionary<string, string> parameters) =>
        [.. Ok(await Store.QueryAsync<T>(query, parameters, Ct)).Select(r => Text(r!))];
}
