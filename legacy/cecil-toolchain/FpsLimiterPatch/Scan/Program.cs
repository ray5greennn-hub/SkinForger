using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length == 0) throw new ArgumentException("Pass one or more assemblies.");
foreach (string path in args)
{
    using var assembly = AssemblyDefinition.ReadAssembly(path);
    foreach (var method in assembly.MainModule.Types.SelectMany(AllMethods).Where(m => m.HasBody))
    {
        var instructions = method.Body.Instructions;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].Operand is not MethodReference called) continue;
            bool fps = called.DeclaringType.FullName == "UnityEngine.Application" && called.Name == "set_targetFrameRate";
            bool vsync = called.DeclaringType.FullName == "UnityEngine.QualitySettings" && called.Name == "set_vSyncCount";
            if (!fps && !vsync) continue;
            Console.WriteLine($"{Path.GetFileName(path)} :: {method.FullName}");
            int first = Math.Max(0, i - 8), last = Math.Min(instructions.Count - 1, i + 3);
            for (int j = first; j <= last; j++) Console.WriteLine($"  {j:D4}: {instructions[j]}");
        }
    }
}

static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type) =>
    type.Methods.Concat(type.NestedTypes.SelectMany(AllMethods));
