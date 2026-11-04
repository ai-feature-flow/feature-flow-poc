using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Model.Tests;

/// <summary>
/// The checks an artifact's type defines (LOG 246): format, the schema subset, Markdown structure. The malformed documents
/// are the shapes M6's live planners wrote (m6-results.md, "Integration", seams 6 and 12).
/// </summary>
public class ArtifactContentTests
{
    static string Root
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "FeatureFlow.slnx")))
                    return dir.FullName;
            throw new InvalidOperationException("FeatureFlow.slnx not found");
        }
    }

    static (ArtifactTypeItem? Type, DiagnosticBag Diagnostics) Read(string yaml, string name = "t")
    {
        var d = new DiagnosticBag();
        var node = YamlText.Parse(yaml, name + ".yaml", d)!;
        return (LibraryItemReader.ReadArtifactType(node, name, d), d);
    }

    static ArtifactTypeItem Std(string name) =>
        Read(File.ReadAllText(Path.Combine(Root, "library", "std", "artifact_types", name + ".yaml")), name).Type!;

    public static TheoryData<string> StdTypes() =>
        new(Directory.GetFiles(Path.Combine(Root, "library", "std", "artifact_types"), "*.yaml").Select(Path.GetFileNameWithoutExtension).Order()!);

    [Theory]
    [MemberData(nameof(StdTypes))]
    public void Every_std_type_reads_and_uses_only_the_supported_schema_keywords(string name)
    {
        var (type, d) = Read(File.ReadAllText(Path.Combine(Root, "library", "std", "artifact_types", name + ".yaml")), name);
        Assert.Empty(d);
        Assert.NotNull(type);
    }

    [Fact]
    public void A_schema_keyword_outside_the_subset_is_refused_when_the_type_loads()
    {
        var (_, d) = Read("""
            api_version: feature-flow/v1
            kind: artifact_type
            metadata: { name: t }
            format: yaml
            schema:
              type: object
              properties:
                a: { anyOf: [{ type: string }, { type: integer }] }
                b: { $ref: "#/$defs/missing" }
                c: { type: text }
            """);
        var messages = d.Select(x => x.Message).ToList();
        Assert.Contains(messages, m => m.StartsWith("schema keyword 'anyOf' is not supported", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("$ref must name a schema under #/$defs/", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("type must be one of", StringComparison.Ordinal));
    }

    const string ValidRequirements = """
        schema: 1
        scope: { kind: project, id: invoicing }
        requirements:
          - id: R1
            title: Totals
            behaviour: Invoices show a total.
            supports_reason: The project's own goal
            owner: { kind: epic, id: billing }
        """;

    [Fact]
    public void A_valid_plan_passes()
    {
        Assert.Empty(ArtifactContent.Check(Std("requirements"), ValidRequirements, "requirements.yaml"));
    }

    [Fact]
    public void An_owner_written_as_text_is_named_by_field_and_rule()
    {
        // M6 seam 6: planners guessed owner's shape and wrote it as a string.
        var errors = ArtifactContent.Check(Std("requirements"), ValidRequirements.Replace("owner: { kind: epic, id: billing }", "owner: billing"), "requirements.yaml");

        Assert.Equal(["requirements[0].owner (line 8): must be a map, not text 'billing' [type]"], errors);
    }

    [Fact]
    public void A_work_list_keyed_by_id_names_the_unknown_field_and_the_missing_one()
    {
        // M6: the project planner keyed its work list by id.
        var errors = ArtifactContent.Check(Std("work_list"), """
            schema: 1
            level: epic
            items:
              - { id: billing, title: Billing, repositories: [acme/api], boundary: The API, owns: [R1] }
            """, "work_list.yaml");

        Assert.Contains("items[0] (line 4): field 'name' is required [required]", errors);
        Assert.Contains("items[0].id (line 4): field 'id' is not allowed (allowed: name, title, repositories, boundary, owns, depends_on, tracker_issue, parameters) [additionalProperties]", errors);
    }

    [Theory]
    [InlineData("schema: 2", "schema (line 1): must be 1, not 2 [const]")]
    [InlineData("kind: program", "scope.kind (line 2): must be one of \"project\", \"epic\", \"ticket\", not \"program\" [enum]")]
    [InlineData("id: R1", "requirements[0].id (line 4): 'R 1' must match ^[A-Za-z0-9][A-Za-z0-9._-]*$ [pattern]")]
    [InlineData("title: Totals", "requirements[0].title (line 5): must not be empty [minLength]")]
    [InlineData("supports_reason: The project's own goal", "requirements[0] (line 4): gives both supports and supports_reason; give one or the other [not]")]
    public void Each_rule_is_named_where_it_is_broken(string replace, string error)
    {
        var broken = replace switch
        {
            "schema: 2" => ValidRequirements.Replace("schema: 1", "schema: 2"),
            "kind: program" => ValidRequirements.Replace("kind: project", "kind: program"),
            "id: R1" => ValidRequirements.Replace("id: R1", "id: R 1"),
            "title: Totals" => ValidRequirements.Replace("title: Totals", "title: \"\""),
            _ => ValidRequirements.Replace("supports_reason: The project's own goal",
                "supports_reason: The project's own goal\n    supports: [{ artifact: goals, requirement: G1 }]"),
        };

        Assert.Equal([error], ArtifactContent.Check(Std("requirements"), broken, "requirements.yaml"));
    }

    [Fact]
    public void Text_that_does_not_parse_is_named_with_its_line()
    {
        var errors = ArtifactContent.Check(Std("requirements"), "schema: 1\nscope: { kind: project\nrequirements: []\n", "requirements.yaml");

        Assert.Single(errors);
        Assert.StartsWith("does not parse as YAML: line ", errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_json_type_must_be_json_and_an_empty_document_is_refused()
    {
        var (type, _) = Read("api_version: feature-flow/v1\nkind: artifact_type\nmetadata: { name: t }\nformat: json\nschema: { type: object }\n");

        Assert.StartsWith("does not parse as JSON", Assert.Single(ArtifactContent.Check(type!, "a: 1", "t.json")), StringComparison.Ordinal);
        Assert.Empty(ArtifactContent.Check(type!, "{\"a\": 1}", "t.json"));
        Assert.Equal(["the document is empty"], ArtifactContent.Check(type!, "  \n", "t.json"));
    }

    [Fact]
    public void A_markdown_contract_needs_its_front_matter_and_headings()
    {
        var contract = Std("contract");
        const string Good = """
            ---
            scope_kind: project
            scope_id: invoicing
            ---
            # Scope
            ## Components and responsibilities
            ## Contracts
            ## Data and control flow
            ## Failure handling
            ## Decisions
            """;

        Assert.Empty(ArtifactContent.Check(contract, Good, "architecture.md"));
        Assert.Equal(["front matter: 'scope_id' is required", "heading 'Failure handling' is required"],
            ArtifactContent.Check(contract, Good.Replace("scope_id: invoicing\n", "").Replace("## Failure handling\n", ""), "architecture.md"));
        Assert.Equal(["must begin with front matter between '---' lines, giving scope_kind, scope_id"],
            ArtifactContent.Check(contract, Good[Good.IndexOf("# Scope", StringComparison.Ordinal)..], "architecture.md"));
    }

    [Fact]
    public void Identifier_patterns_must_each_match()
    {
        var (type, _) = Read("""
            api_version: feature-flow/v1
            kind: artifact_type
            metadata: { name: t }
            format: markdown
            markdown: { identifier_patterns: ["^AC-[0-9]+"] }
            """);

        Assert.Empty(ArtifactContent.Check(type!, "# Criteria\nAC-1 totals\n", "t.md"));
        Assert.Equal(["must hold an identifier matching ^AC-[0-9]+"], ArtifactContent.Check(type!, "# Criteria\nnone\n", "t.md"));
    }

    [Fact]
    public void Lists_and_numbers_are_bounded_and_types_are_exact()
    {
        var (type, d) = Read("""
            api_version: feature-flow/v1
            kind: artifact_type
            metadata: { name: t }
            format: yaml
            schema:
              type: object
              properties:
                tags: { type: array, minItems: 1, maxItems: 2, items: { type: string } }
                count: { type: integer, minimum: 0, maximum: 5 }
                flag: { type: boolean }
            """);
        Assert.Empty(d);

        Assert.Empty(ArtifactContent.Check(type!, "tags: [a]\ncount: 3\nflag: true\n", "t.yaml"));
        Assert.Equal(
        [
            "tags (line 1): must have at most 2 items [maxItems]",
            "tags[1] (line 1): must be text, not a whole number [type]",
            "count (line 2): must be at most 5 [maximum]",
            "flag (line 3): must be true or false, not text 'yes please' [type]",
        ], ArtifactContent.Check(type!, "tags: [a, 1, c]\ncount: 9\nflag: yes please\n", "t.yaml"));
        Assert.Equal(["count (line 1): must be a whole number, not text '3' [type]"], ArtifactContent.Check(type!, "count: \"3\"\n", "t.yaml"));
    }

    [Fact]
    public void A_reviewer_s_rule_must_come_from_std_s_vocabulary()
    {
        // M6 follow-up 6 (LOG 250): a fixed vocabulary, so the same problem raised again matches by fingerprint.
        const string Report = """
            schema: 1
            findings:
              - { rule: null_dereference, title: T, description: D, severity: high, blocking: false, basis: reasoned, location: { repository: acme/api, file: a.cs } }
            """;

        Assert.Empty(ArtifactContent.Check(Std("findings_report"), Report, "findings.yaml"));
        var error = Assert.Single(ArtifactContent.Check(Std("findings_report"), Report.Replace("null_dereference", "null-check"), "findings.yaml"));
        Assert.StartsWith("findings[0].rule (line 3): must be one of \"wrong_behaviour\", \"null_dereference\"", error, StringComparison.Ordinal);
    }
}
