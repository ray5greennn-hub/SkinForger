using Mono.Cecil;

if (args.Length != 1) throw new ArgumentException("Pass Assembly-CSharp.dll.");

var apiNames = new HashSet<string>(StringComparer.Ordinal)
{
    "set_material", "set_sharedMaterial", "set_sharedMaterials", "set_mainTexture",
    "SetTexture", "SetColor", "SetFloat", "SetInt", "SetVector",
    "SetPropertyBlock", "GetPropertyBlock", "SetReplacementShader", "ResetReplacementShader",
    "SetGlobalTexture", "SetGlobalColor", "SetGlobalFloat", "SetGlobalInt", "SetGlobalVector",
    "EnableKeyword", "DisableKeyword", "EnableShaderKeyword", "DisableShaderKeyword",
    "Blit", "set_targetTexture", "SetRenderTarget"
};

var typeHints = new[]
{
    "Renderer", "Material", "Shader", "Graphics", "CommandBuffer", "RenderTexture",
    "SkeletonRenderer", "SkeletonGraphic", "CustomMaterial", "MaterialOverride", "SlotMaterial"
};

using var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { InMemory = true });
foreach (var method in assembly.MainModule.Types.SelectMany(AllMethods).Where(m => m.HasBody))
{
    var hits = method.Body.Instructions
        .Where(i => i.Operand is MethodReference called && IsRenderingCall(called))
        .Select(i => (Instruction: i, Called: (MethodReference)i.Operand))
        .ToArray();
    if (hits.Length == 0) continue;

    Console.WriteLine($"METHOD\t{method.DeclaringType.FullName}::{method.Name}");
    foreach (var hit in hits)
        Console.WriteLine($"CALL\t{hit.Called.DeclaringType.FullName}::{hit.Called.Name}\t{hit.Instruction.Offset:X4}");
}

static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type) =>
    type.Methods.Concat(type.NestedTypes.SelectMany(AllMethods));

bool IsRenderingCall(MethodReference called)
{
    if (!apiNames.Contains(called.Name)) return false;
    return typeHints.Any(h => called.DeclaringType.FullName.Contains(h, StringComparison.OrdinalIgnoreCase));
}
