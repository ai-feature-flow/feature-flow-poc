using Cel.Checker;
using Cel.Tools;

namespace CelChecks;

internal static class Program
{
    private static int Main(string[] args)
    {
        var resultsDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "results"));
        Directory.CreateDirectory(resultsDir);

        var mode = args.Length > 0 ? args[0] : "all";
        switch (mode)
        {
            case "api":
                ApiDump.Write(Path.Combine(resultsDir, "celnet-api-dump.txt"));
                break;
            case "c1":
                Check1Restricted.Run();
                break;
            case "c3":
                Check3CostEstimate.Run();
                break;
            case "probe":
                Probe.Run();
                break;
            case "c2":
                Check2Determinism.Run();
                break;
            case "c4":
                Check4CostLimit.Run();
                break;
            case "timing":
                Timing.Run();
                break;
            case "pkg":
                PackageFacts.Run();
                break;
            case "all":
                PackageFacts.Run();
                Check1Restricted.Run();
                Check2Determinism.Run();
                Check3CostEstimate.Run();
                Check4CostLimit.Run();
                Timing.Run();
                break;
            default:
                Console.WriteLine("usage: CelChecks [api]");
                break;
        }

        return 0;
    }
}
