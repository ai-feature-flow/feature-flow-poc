using FeatureFlow.Spike.CodeHost;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike3;

/// <summary>
/// The epic's texts (docs/17 3.1) and the agent prompts built from them. The
/// ticket texts are the pilot's (spikes/03-concurrent/pilot/pilot-v2.py) so the
/// in-run reviews and the R4 series check against the same words.
/// </summary>
public static class Texts
{
    public const string Epic =
        "Widget names are case- and whitespace-insensitive: the store keeps each name normalised " +
        "(trimmed, lower-case), and a client can always look a widget up by the name it created it " +
        "with, in any case or spacing. Widgets record their creation time.";

    public const string TicketA =
        "EPIC3-A: Names are stored normalised. In WidgetStore.Add, normalise the name with a new private " +
        "static Normalise(string) that trims and lower-cases it (invariant culture), used inside the new " +
        "Widget(...) expression. Add unit tests in WidgetNameNormalisationTests.cs: Add(\"  Alpha \") returns " +
        "a widget named alpha.";

    public const string TicketB =
        "EPIC3-B: Add WidgetStore.FindByName(string name) after Count(), returning the first widget whose " +
        "name equals name using an exact, ordinal (case-sensitive) comparison, or null. Add GET " +
        "/widgets/by-name/{name} returning 200 or 404. Add unit tests in WidgetFindByNameTests.cs: " +
        "Add(\"alpha\") then FindByName(\"alpha\") returns it; FindByName(\"ALPHA\") returns null.";

    public const string TicketC =
        "EPIC3-C: Widgets record their creation time. Add DateTimeOffset CreatedAt as the third member of " +
        "the Widget record, set to DateTimeOffset.UtcNow inside the new Widget(...) expression in " +
        "WidgetStore.Add. Add unit tests in WidgetCreatedAtTests.cs: a widget returned by Add has a " +
        "CreatedAt within a second of now.";

    public static string Ticket(string ticketId) => ticketId switch
    {
        "EPIC3-A" => TicketA,
        "EPIC3-B" => TicketB,
        "EPIC3-C" => TicketC,
        _ => throw new ArgumentException($"unknown ticket {ticketId}"),
    };

    /// <summary>docs/17 6.1: ticket text at ticket scope, epic description at epic scope.</summary>
    public static string ReviewSpecification(Step step, RunContext context) =>
        Inputs.Text(step, "scope") == "epic" ? $"Epic: {Epic}" : Ticket(context.TicketId);

    public const string AgentAllow =
        "Bash(git merge:*);Bash(git log:*);Bash(git show:*);Bash(dotnet test:*);Bash(dotnet build:*)";

    private const string UnitTier = "dotnet test tests/FeatureFlow.Example.Api.UnitTests --filter Category=unit";

    public static string? AgentPrompt(Step step, RunContext context, FileContent? manifest)
    {
        var ticket = context.TicketId;
        var planFile = $"docs/plans/{ticket}.md";
        string task;
        var mayTest = false;

        switch (step.Id)
        {
            case "plan_spec":
                task = $"Create the file {planFile} containing a three-line plan for the ticket below: the goal, " +
                       "the files to change, and the tests to add. Do not change any other file.\n\n" +
                       $"Ticket: {Ticket(ticket)}";
                break;
            case "design":
                task = $"Append a short '## Design' section to {planFile} describing the change for the ticket " +
                       "below: the members you will add or change and why. Do not change any other file.\n\n" +
                       $"Ticket: {Ticket(ticket)}";
                break;
            case "implement":
                mayTest = true;
                task = "Implement the ticket below exactly as written, in src/FeatureFlow.Example.Api, and add its " +
                       "unit tests in a NEW file in tests/FeatureFlow.Example.Api.UnitTests with the class marked " +
                       "[Trait(\"Category\", \"unit\")]. Do not edit WidgetStoreTests.cs. Do not change anything " +
                       "the ticket does not ask for.\n\n" +
                       $"Ticket: {Ticket(ticket)}";
                break;
            case "fix_findings":
                mayTest = true;
                var open = Findings.Open(context);
                var listed = open.Count == 0
                    ? "(none recorded)"
                    : string.Join("\n", open.Select(f =>
                        $"- {f.Id}: rule={f.Rule} path={f.Path} symbol={f.Symbol} blocking={f.Blocking} " +
                        $"snippet={f.Snippet} message={f.Message}"));
                task = "Fix the open review findings listed below, staying within the ticket. Every fix must come " +
                       "with a regression test: an xUnit [Fact] in the ticket's own test file, whose class is " +
                       "marked [Trait(\"Category\", \"unit\")].\n\n" +
                       $"Ticket: {Ticket(ticket)}\n\nOpen review findings:\n{listed}";
                break;
            case "resolve_conflict":
                mayTest = true;
                task = $"The integration branch {context.IntegrationBranch} has moved, and merging it into your " +
                       $"branch {context.Branch} conflicts. Resolve it:\n" +
                       $"  1. git merge {context.IntegrationBranch}\n" +
                       "  2. Resolve every conflict keeping BOTH intents: your ticket's change (below) and the " +
                       "change already on the integration branch. Remove all conflict markers.\n" +
                       $"  3. Run the unit tier: {UnitTier}. If it fails, fix only the merge.\n" +
                       "  4. git add the resolved files, then git commit --no-edit to conclude the merge, so the " +
                       "commit has two parents.\n" +
                       "Touch nothing else.\n\n" +
                       $"Your ticket: {Ticket(ticket)}";
                break;
            default:
                return null;
        }

        var testing = mayTest
            ? $"You may run the unit tier to check your work: {UnitTier}"
            : "Do NOT run tests.";
        var commit = step.Id == "resolve_conflict"
            ? "Commit as described above."
            : $"Then commit the change with git:\n  git add <the files you changed>\n  git commit -m \"{ticket}: {step.Id}\"";

        return $"""
            You are the {Inputs.Role(step) ?? "implementer"} for ticket {ticket} in the repository
            {RunContext.Owner}/{RunContext.Repository}, on branch {context.Branch}.

            Working directory: {context.WorkingDirectory}

            YOUR TASK — do exactly this, nothing else:
            {task}

            Use your tools to edit files. {commit}

            {testing}
            Do NOT push. Do NOT create a pull request. Do NOT ask questions; nobody can answer them.
            When you are done, reply with one short sentence saying what you changed.
            """;
    }
}
