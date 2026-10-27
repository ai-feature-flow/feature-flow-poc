using System.Reflection;
using System.Text;

namespace CelChecks;

/// <summary>
/// Dumps every public type and public member exported by the Cel.NET assembly,
/// so the surface can be grepped for things like static cost estimation.
/// </summary>
internal static class ApiDump
{
    public static void Write(string outputPath)
    {
        var asm = typeof(Cel.Tools.ScriptHost).Assembly;

        var sb = new StringBuilder();
        sb.AppendLine($"# Cel.NET public API dump");
        sb.AppendLine($"# Assembly: {asm.FullName}");
        sb.AppendLine($"# Location: {asm.Location}");
        sb.AppendLine($"# Generated: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"# Public types: (see below)");
        sb.AppendLine();

        var types = asm.GetExportedTypes()
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        sb.AppendLine($"## Type count: {types.Count}");
        sb.AppendLine();

        foreach (var t in types)
        {
            sb.AppendLine($"TYPE {Describe(t)}");
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                                           BindingFlags.DeclaredOnly;
                foreach (var m in t.GetMembers(flags)
                             .Where(m => m is not MethodInfo mi || !mi.IsSpecialName || mi.Name.StartsWith("op_"))
                             .OrderBy(m => m.Name, StringComparer.Ordinal))
                {
                    switch (m)
                    {
                        case MethodInfo mi:
                            var ps = string.Join(", ", mi.GetParameters().Select(p =>
                                $"{ShortName(p.ParameterType)} {p.Name}{(p.HasDefaultValue ? " = ?" : "")}"));
                            sb.AppendLine($"  METHOD {ShortName(mi.ReturnType)} {mi.Name}({ps})" +
                                          (mi.IsStatic ? " [static]" : "") +
                                          (mi.IsGenericMethodDefinition
                                              ? $" [generic<{string.Join(",", mi.GetGenericArguments().Select(a => a.Name))}>]"
                                              : ""));
                            break;
                        case ConstructorInfo ci:
                            sb.AppendLine($"  CTOR .ctor({string.Join(", ", ci.GetParameters().Select(p => ShortName(p.ParameterType) + " " + p.Name))})");
                            break;
                        case PropertyInfo pi:
                            sb.AppendLine($"  PROP {ShortName(pi.PropertyType)} {pi.Name}" +
                                          (pi.GetMethod?.IsStatic == true ? " [static]" : ""));
                            break;
                        case FieldInfo fi:
                            sb.AppendLine($"  FIELD {ShortName(fi.FieldType)} {fi.Name}" +
                                          (fi.IsStatic ? " [static]" : "") +
                                          (fi.IsLiteral ? $" = {fi.GetRawConstantValue()}" : ""));
                            break;
                        case EventInfo ei:
                            sb.AppendLine($"  EVENT {ShortName(ei.EventHandlerType)} {ei.Name}");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  !! member enumeration failed: {ex.GetType().Name}: {ex.Message}");
            }

            sb.AppendLine();
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, sb.ToString());
        Console.WriteLine($"Wrote API dump: {outputPath} ({sb.Length} chars, {types.Count} public types)");
    }

    private static string Describe(Type t)
    {
        var kind = t.IsInterface ? "interface"
            : t.IsEnum ? "enum"
            : t.IsValueType ? "struct"
            : t.IsAbstract && t.IsSealed ? "static class"
            : t.IsAbstract ? "abstract class"
            : "class";
        var bases = new List<string>();
        if (t.BaseType is not null && t.BaseType != typeof(object) && !t.IsEnum && !t.IsValueType)
            bases.Add(ShortName(t.BaseType));
        bases.AddRange(t.GetInterfaces().Select(ShortName));
        return $"{kind} {t.FullName}{(bases.Count > 0 ? " : " + string.Join(", ", bases) : "")}";
    }

    private static string ShortName(Type t)
    {
        if (t.IsGenericType)
        {
            var name = t.Name;
            var tick = name.IndexOf('`');
            if (tick > 0) name = name[..tick];
            return $"{name}<{string.Join(",", t.GetGenericArguments().Select(ShortName))}>";
        }

        return t.Name;
    }
}
