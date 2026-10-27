using Cel;
using Cel.Checker;
using Cel.Common.Types;
using Cel.Tools;
using Google.Api.Expr.V1Alpha1;

namespace CelChecks;

/// <summary>
/// Builds the restricted environment the engine would use: only the namespaces
/// and functions the workflow spec allows (docs/09 section 8.3, 8.4).
/// Nothing else is registered.
/// </summary>
internal static class RestrictedEnv
{
    // docs/09 8.4: predicates and selection only. No comprehensions (all/any/exists
    // are macros, not functions, and are deliberately not enabled - see Check1).
    public static readonly string[] AllowedFunctions =
    {
        "size", "contains", "startsWith", "endsWith", "matches", "lower", "upper"
    };

    /// <summary>
    /// A host with the standard library but WITHOUT the comprehension macros.
    /// Used as the baseline for check 1.
    /// </summary>
    public static ScriptHost NewHost()
    {
        return ScriptHost.NewBuilder().Build();
    }

    /// <summary>
    /// The declarations the engine registers. Only these names are in scope.
    /// </summary>
    public static IList<Decl> Declarations()
    {
        // A map type stands in for the `steps` and `run` namespaces so that
        // `steps.x.outcome` style selections type-check against dyn values.
        var dynMap = Decls.NewMapType(Decls.String, Decls.Dyn);

        return new List<Decl>
        {
            Decls.NewVar("params", dynMap),
            Decls.NewVar("steps", dynMap),
            Decls.NewVar("run", dynMap),
            Decls.NewVar("findings", Decls.NewMapType(Decls.String, Decls.Int))
        };
    }

    /// <summary>
    /// The env the engine would actually use: stdlib on (it is where the operators
    /// and the 8.4 functions come from), comprehension macros off (8.4: no
    /// comprehensions), and only the four declared variable namespaces.
    /// </summary>
    public static Env NewEngineEnv()
    {
        return Env.NewCustomEnv(
            LibraryOptions.StdLib(),
            EnvOptions.ClearMacros(),
            EnvOptions.Declarations(Declarations()));
    }

    /// <summary>
    /// The docs/09 8.4 allow-list: predicates and selection only, no comprehensions.
    /// Used to filter the standard library's own declaration protos down to exactly
    /// what the spec permits.
    /// </summary>
    public static readonly HashSet<string> AllowedNames = new(StringComparer.Ordinal)
    {
        // 8.4 functions
        "size", "contains", "startsWith", "endsWith", "matches",
        // 8.4 "plus arithmetic, comparison and boolean operators"
        "_+_", "_-_", "_*_", "_/_", "_%_", "-_", "!_",
        "_<_", "_<=_", "_>_", "_>=_", "_==_", "_!=_", "_&&_", "_||_", "_?_:_", "_[_]",
        "in", "@in", "@not_strictly_false"
    };

    /// <summary>
    /// The strictest posture that still works: take Cel.NET's own standard-library
    /// declaration protos, keep only the names 8.4 allows, add the four variable
    /// namespaces, and switch the comprehension macros off.
    /// </summary>
    public static Env NewWhitelistEnv()
    {
        var std = Cel.Checker.Checker.StandardDeclarations
            .Where(d => AllowedNames.Contains(d.Name))
            .ToList();

        var decls = new List<Decl>(Declarations());
        decls.AddRange(std);

        return Env.NewCustomEnv(
            EnvOptions.ClearMacros(),
            EnvOptions.Declarations(decls));
    }
}
