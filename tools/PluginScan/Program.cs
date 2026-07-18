using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

// Reproduces the checks that catch the three failure modes which compile
// perfectly and only show up in-game:
//
//   1. Discoverability - Lunaris scans every plugin DLL with Mono.Cecil BEFORE
//      loading it. If resolving any custom attribute's arguments throws, the
//      plugin is silently skipped: no error in-game, just an absent mod. An
//      enum-valued attribute argument from an unresolvable assembly did exactly
//      this once and cost hours.
//   2. Vector binding - ImGui.NET takes Vector2/Vector4 from
//      System.Numerics.Vectors. Binding to any other assembly's copy is a
//      different type identity and throws MissingMethodException on every
//      vector call at runtime.
//   3. Vault identity - [assembly: AssemblyMetadata("LunarisPluginId", <slug>)]
//      is what ties the DLL to its Erenshor Vault page.
//
// Usage: PluginScan <plugin.dll> <gameRoot> [expectedVaultSlug]
// Exit code 0 = all good, 1 = something would fail in-game.
class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: PluginScan <plugin.dll> <gameRoot> [expectedVaultSlug]");
            return 2;
        }

        string dll = args[0];
        string gameRoot = args[1];
        string expectedSlug = args.Length > 2 ? args[2] : null;

        if (!File.Exists(dll))
        {
            Console.WriteLine("FAIL: not found: " + dll);
            return 1;
        }

        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(gameRoot);
        resolver.AddSearchDirectory(Path.Combine(gameRoot, "Erenshor_Data", "Managed"));
        resolver.AddSearchDirectory(Path.Combine(gameRoot, "plugins"));
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(dll)));
        // The mod's own lib\ - 0Harmony, ImGui.NET, Newtonsoft, S.N.Vectors.
        var lib = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(dll))) ?? ".", "..", "lib");
        if (Directory.Exists(lib)) resolver.AddSearchDirectory(lib);

        bool ok = true;
        ok &= Discoverability(dll, resolver);
        ok &= VectorBinding(dll, resolver);
        ok &= VaultId(dll, resolver, expectedSlug);

        Console.WriteLine();
        Console.WriteLine(ok ? "SCAN RESULT: OK" : "SCAN RESULT: PROBLEMS FOUND");
        return ok ? 0 : 1;
    }

    /// <summary>Walks every custom attribute the way Lunaris's pre-load scan does.</summary>
    static bool Discoverability(string dll, IAssemblyResolver resolver)
    {
        int attrs = 0;
        string plugin = null;
        var commands = new List<string>();

        try
        {
            var asm = AssemblyDefinition.ReadAssembly(dll,
                new ReaderParameters { AssemblyResolver = resolver });

            foreach (var type in asm.MainModule.GetTypes())
                foreach (var ca in AllAttributes(type))
                {
                    attrs++;
                    // Touching ConstructorArguments is what actually resolves the
                    // argument types - the step that throws.
                    var vals = ca.ConstructorArguments.Select(a => a.Value?.ToString()).ToArray();
                    if (ca.AttributeType.Name == "LunarisPluginAttribute" && vals.Length > 0)
                        plugin = string.Join(" | ", vals.Take(3));
                    if (ca.AttributeType.Name == "LunarisCommandAttribute" && vals.Length > 0)
                        commands.Add(vals[0]);
                }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[discoverability] FAIL - Lunaris would silently skip this plugin");
            Console.WriteLine("    " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }

        Console.WriteLine("[discoverability] OK   " + attrs + " attributes resolved");
        Console.WriteLine("    plugin   : " + (plugin ?? "(NONE - would not load)"));
        Console.WriteLine("    commands : " + (commands.Count > 0 ? string.Join(", ", commands) : "(none)"));
        return plugin != null;
    }

    /// <summary>Every emitted Vector2/4 must come from System.Numerics.Vectors.</summary>
    static bool VectorBinding(string dll, IAssemblyResolver resolver)
    {
        var module = ModuleDefinition.ReadModule(dll,
            new ReaderParameters { AssemblyResolver = resolver });

        var bad = new List<string>();
        int good = 0;

        foreach (var mr in module.GetMemberReferences())
            foreach (var tr in TypesIn(mr))
            {
                if (tr.Name != "Vector2" && tr.Name != "Vector3" && tr.Name != "Vector4") continue;
                string scope = tr.Scope?.ToString() ?? "(none)";
                if (scope.StartsWith("System.Numerics.Vectors")) good++;
                else bad.Add(mr.DeclaringType?.Name + "." + mr.Name + " -> " + scope);
            }

        if (bad.Count > 0)
        {
            Console.WriteLine("[vector binding] FAIL - MissingMethodException at runtime");
            foreach (var b in bad.Distinct()) Console.WriteLine("    " + b);
            return false;
        }

        Console.WriteLine("[vector binding]  OK   " + good + " refs -> System.Numerics.Vectors");
        return true;
    }

    /// <summary>The attribute that ties this DLL to its Erenshor Vault page.</summary>
    static bool VaultId(string dll, IAssemblyResolver resolver, string expected)
    {
        var asm = AssemblyDefinition.ReadAssembly(dll,
            new ReaderParameters { AssemblyResolver = resolver });

        var meta = asm.CustomAttributes.FirstOrDefault(a =>
            a.AttributeType.FullName == "System.Reflection.AssemblyMetadataAttribute" &&
            a.ConstructorArguments.Count == 2 &&
            (a.ConstructorArguments[0].Value as string) == "LunarisPluginId");

        string slug = meta?.ConstructorArguments[1].Value as string;

        if (slug == null)
        {
            Console.WriteLine("[vault id]        WARN - no LunarisPluginId attribute");
            return true;   // not fatal: Lunaris injects one on a Vault install
        }

        if (expected != null && slug != expected)
        {
            Console.WriteLine("[vault id]        FAIL - is '" + slug + "', expected '" + expected + "'");
            return false;
        }

        Console.WriteLine("[vault id]        OK   " + slug);
        return true;
    }

    static IEnumerable<CustomAttribute> AllAttributes(TypeDefinition t)
    {
        foreach (var a in t.CustomAttributes) yield return a;
        foreach (var m in t.Methods)
        {
            foreach (var a in m.CustomAttributes) yield return a;
            foreach (var p in m.Parameters)
                foreach (var a in p.CustomAttributes) yield return a;
        }
        foreach (var f in t.Fields)
            foreach (var a in f.CustomAttributes) yield return a;
        foreach (var p in t.Properties)
            foreach (var a in p.CustomAttributes) yield return a;
    }

    static IEnumerable<TypeReference> TypesIn(MemberReference mr)
    {
        if (mr.DeclaringType != null) yield return mr.DeclaringType;
        if (mr is MethodReference m)
        {
            yield return m.ReturnType;
            foreach (var p in m.Parameters) yield return p.ParameterType;
        }
        if (mr is FieldReference f) yield return f.FieldType;
    }
}
