// Dumps every method a .NET assembly declares, with its PARAMETER NAMES, one per line:
//
//     Namespace.Type::Method(param1, param2)
//
// Why: Harmony binds a patch's parameters to its target's BY NAME, so a rename inside the authored
// Main silently breaks a SkinForger.Core module at startup (2026-11-19: "resourceName" not found in
// TrySpawnForExtract(prefabName, use) took down HexieBlock and Skin Packages). Reading names out of the
// SOURCE with a regex misses what the compiler actually emitted; this reads the built assembly.
//
// Cecil reads metadata only, so none of the Il2Cpp references have to resolve — which is why plain
// reflection (Assembly.LoadFrom) cannot do this job.
//
//     dotnet run --project Analysis/MetaDump -- <path to a .dll>
using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace SkinForger.Analysis;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: MetaDump <assembly.dll>");
            return 2;
        }

        ReaderParameters parameters = new() { ReadingMode = ReadingMode.Deferred };
        using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(args[0], parameters);

        foreach (ModuleDefinition module in assembly.Modules)
            foreach (TypeDefinition type in All(module.Types))
                foreach (MethodDefinition method in type.Methods)
                    Console.WriteLine($"{type.FullName}::{method.Name}(" +
                        string.Join(", ", method.Parameters.Select(parameter => parameter.Name)) + ")");

        return 0;
    }

    private static IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types)
    {
        foreach (TypeDefinition type in types)
        {
            yield return type;
            foreach (TypeDefinition nested in All(type.NestedTypes)) yield return nested;
        }
    }
}
