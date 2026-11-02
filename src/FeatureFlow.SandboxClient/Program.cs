// ff-sandbox: the client in the sandbox (LOG 67). `ff-sandbox mcp` is the stdio MCP server for the engine tools;
// `ff-sandbox hook <event>` is the hook command. Both read FF_ENGINE_URLS and FF_STEP_TOKEN.
using System.Globalization;
using FeatureFlow.SandboxClient;

var log = new ClientLog(Environment.GetEnvironmentVariable("FF_CLIENT_LOG"));
TimeSpan Seconds(string name, double fallback) =>
    TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable(name), CultureInfo.InvariantCulture, out var s) ? s : fallback);

switch (args.FirstOrDefault())
{
    case "mcp":
        var engine = EngineClient.FromEnvironment(log, Seconds("FF_RETRY_SECONDS", 600));
        await new McpServer(engine, McpSettings.FromEnvironment(), log).RunAsync(Console.In, Console.Out, CancellationToken.None);
        return 0;
    case "hook" when args.Length > 1:
        var hookEngine = EngineClient.FromEnvironment(log, Seconds("FF_HOOK_HOLD_SECONDS", 120));
        return await HookCommand.RunAsync(args[1], hookEngine, Console.In, Console.Out, Console.Error, log, CancellationToken.None);
    case "--version":
        Console.WriteLine("ff-sandbox 0.1.0");
        return 0;
    default:
        Console.Error.WriteLine("usage: ff-sandbox mcp | ff-sandbox hook <event>");
        return 64;
}
