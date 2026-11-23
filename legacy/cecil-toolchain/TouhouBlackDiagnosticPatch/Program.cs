using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: TouhouBlackDiagnosticPatch <input.dll> <output.dll>");
    Environment.ExitCode = 2;
    return;
}

var input = args[0];
var output = args[1];
using var assembly = AssemblyDefinition.ReadAssembly(input, new ReaderParameters { InMemory = true });
var module = assembly.MainModule;
var mod = module.Types.Single(t => t.FullName == "SkinForger.SkinForgerMod");
var apply = mod.Methods.Single(m => m.Name == "ApplySkeleton");

var materialGetName = FindCall(module, "UnityEngine.Object", "get_name");
var materialGetShader = FindCall(module, "UnityEngine.Material", "get_shader");
var materialGetMainTexture = FindCall(module, "UnityEngine.Material", "get_mainTexture");
var stringIndexOf = module.ImportReference(typeof(string).GetMethod(nameof(string.IndexOf), new[] { typeof(string), typeof(StringComparison) })!);
var stringFormat3 = module.ImportReference(typeof(string).GetMethod(nameof(string.Format), new[] { typeof(string), typeof(object), typeof(object), typeof(object) })!);
var melonMsg = module.ImportReference(assembly.MainModule.AssemblyReferences
    .SelectMany(_ => mod.Methods)
    .SelectMany(m => m.HasBody ? m.Body.Instructions : Enumerable.Empty<Instruction>())
    .Select(i => i.Operand as MethodReference)
    .First(m => m != null && m.DeclaringType.FullName == "MelonLoader.MelonLogger" && m.Name == "Msg" && m.Parameters.Count == 1)!);

var il = apply.Body.GetILProcessor();
var insertion = apply.Body.Instructions.Single(i => i.Offset == 0x008B);
var materialVar = apply.Body.Variables[4];

var continueInstruction = il.Create(OpCodes.Nop);
var logInstruction = il.Create(OpCodes.Ldstr, "[SkinForge][TouhouBlack] material={0} shader={1} mainTexture={2}");
var checkShader = il.Create(OpCodes.Ldloc_S, materialVar);
var shaderNotNull = il.Create(OpCodes.Dup);
var skipShaderCheck = il.Create(OpCodes.Pop);
var textureCheck = il.Create(OpCodes.Ldloc_S, materialVar);
var textureNotNull = il.Create(OpCodes.Pop);

var instructions = new List<Instruction>
{
    il.Create(OpCodes.Ldloc_S, materialVar),
    il.Create(OpCodes.Callvirt, materialGetName),
    il.Create(OpCodes.Ldstr, "touhou_black"),
    il.Create(OpCodes.Ldc_I4_5),
    il.Create(OpCodes.Callvirt, stringIndexOf),
    il.Create(OpCodes.Ldc_I4_0),
    il.Create(OpCodes.Bge_S, logInstruction),
    checkShader,
    il.Create(OpCodes.Callvirt, materialGetShader),
    shaderNotNull,
    il.Create(OpCodes.Brtrue_S, il.Create(OpCodes.Nop)),
};

// Patch branch targets after creating the target instructions.
var shaderNameCheck = instructions[^1].Operand as Instruction ?? throw new InvalidOperationException();
instructions.Add(skipShaderCheck);
instructions.Add(il.Create(OpCodes.Br_S, continueInstruction));
instructions.Add(shaderNameCheck);
instructions.Add(il.Create(OpCodes.Callvirt, materialGetName));
instructions.Add(il.Create(OpCodes.Ldstr, "touhou_black"));
instructions.Add(il.Create(OpCodes.Ldc_I4_5));
instructions.Add(il.Create(OpCodes.Callvirt, stringIndexOf));
instructions.Add(il.Create(OpCodes.Ldc_I4_0));
instructions.Add(il.Create(OpCodes.Blt_S, continueInstruction));
instructions.Add(textureCheck);
instructions.Add(il.Create(OpCodes.Callvirt, materialGetMainTexture));
instructions.Add(il.Create(OpCodes.Dup));
instructions.Add(il.Create(OpCodes.Brtrue_S, textureNotNull));
instructions.Add(il.Create(OpCodes.Pop));
instructions.Add(il.Create(OpCodes.Br_S, continueInstruction));
instructions.Add(textureNotNull);
instructions.Add(logInstruction);
instructions.Add(il.Create(OpCodes.Ldloc_S, materialVar));
instructions.Add(il.Create(OpCodes.Callvirt, materialGetName));
instructions.Add(il.Create(OpCodes.Ldloc_S, materialVar));
instructions.Add(il.Create(OpCodes.Callvirt, materialGetShader));
instructions.Add(il.Create(OpCodes.Callvirt, materialGetName));
instructions.Add(il.Create(OpCodes.Ldloc_S, materialVar));
instructions.Add(il.Create(OpCodes.Callvirt, materialGetMainTexture));
instructions.Add(il.Create(OpCodes.Callvirt, materialGetName));
instructions.Add(il.Create(OpCodes.Call, stringFormat3));
instructions.Add(il.Create(OpCodes.Call, melonMsg));
instructions.Add(continueInstruction);

foreach (var instruction in instructions)
    il.InsertBefore(insertion, instruction);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
assembly.Write(output);
Console.WriteLine("Input SHA256: " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))));
Console.WriteLine("Output SHA256: " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))));
Console.WriteLine("Injected Touhou black material/mainTexture diagnostics only; no gameplay logic changed.");

static MethodReference FindCall(ModuleDefinition module, string declaringType, string name)
{
    return module.Types.SelectMany(Methods)
        .Where(m => m.HasBody)
        .SelectMany(m => m.Body.Instructions)
        .Select(i => i.Operand as MethodReference)
        .First(m => m != null && m.DeclaringType.FullName == declaringType && m.Name == name)!;
}

static IEnumerable<MethodDefinition> Methods(TypeDefinition type)
{
    foreach (var method in type.Methods)
        yield return method;
    foreach (var nested in type.NestedTypes)
        foreach (var method in Methods(nested))
            yield return method;
}
