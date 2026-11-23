using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 3 && args.Length != 4) throw new ArgumentException("Build <verified baseline> <runtime> <output> [previous loading release]");
if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))) != "BC69D658D1975970ABDBFB65824D90D3035668311E2376F3537BAD1C8BDBFCBE")
    throw new Exception("Baseline hash mismatch");
using var target = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { InMemory = true });
using var source = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { InMemory = true });
var module = target.MainModule;
var helpers = source.MainModule.Types.Where(t => t.Namespace == "SkinForger").ToArray();
if (helpers.Length != 4 || helpers.Any(t => t.NestedTypes.Count != 0)) throw new Exception("Unexpected helper types");
foreach (var type in helpers)
    foreach (var method in type.Methods)
    {
        _ = method.ReturnType;
        _ = method.Parameters.Count;
        if (!method.HasBody) continue;
        _ = method.Body.Instructions.Count;
        _ = method.Body.Variables.Count;
        _ = method.Body.ExceptionHandlers.Count;
    }
foreach (var type in helpers) { source.MainModule.Types.Remove(type); module.Types.Add(type); }
TypeDefinition? Own(TypeReference t) => helpers.SingleOrDefault(h => h.FullName == t.FullName);
foreach (var type in helpers)
{
    type.BaseType = module.ImportReference(type.BaseType);
    foreach (var field in type.Fields) field.FieldType = module.ImportReference(field.FieldType);
    foreach (var method in type.Methods)
    {
        method.ReturnType = module.ImportReference(method.ReturnType);
        foreach (var p in method.Parameters) p.ParameterType = module.ImportReference(p.ParameterType);
        if (!method.HasBody) continue;
        foreach (var v in method.Body.Variables) v.VariableType = module.ImportReference(v.VariableType);
        foreach (var eh in method.Body.ExceptionHandlers)
            if (eh.CatchType != null) eh.CatchType = module.ImportReference(eh.CatchType);
        foreach (var i in method.Body.Instructions)
            i.Operand = i.Operand switch
            {
                MethodReference m when Own(m.DeclaringType) is TypeDefinition owner => owner.Methods.Single(x => x.FullName == m.FullName),
                MethodReference m => module.ImportReference(m),
                FieldReference f when Own(f.DeclaringType) is TypeDefinition owner => owner.Fields.Single(x => x.Name == f.Name),
                FieldReference f => module.ImportReference(f),
                TypeReference t when Own(t) is TypeDefinition owner => owner,
                TypeReference t => module.ImportReference(t),
                _ => i.Operand
            };
    }
}
MethodDefinition H(string type, string name) => module.GetType("SkinForger." + type).Methods.Single(m => m.Name == name);
var mod = module.GetType("SkinForger.SkinForgerMod");
var paths = mod.Methods.Single(m => m.Name == "SetupPaths");
var literal = paths.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, "Skins"));
var combine = literal.Next;
if (combine.Operand is not MethodReference mr || mr.DeclaringType.FullName != "System.IO.Path" || mr.Name != "Combine") throw new Exception("Unexpected Skins assignment");
literal.OpCode = OpCodes.Nop;
literal.Operand = null;
combine.Operand = H("LoadingPaths", "Skins");
var start = mod.Methods.Single(m => m.Name == "OnApplicationStart");
foreach (var ret in start.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
    start.Body.GetILProcessor().InsertBefore(ret, Instruction.Create(OpCodes.Call, H("CustomLoadingPicManager", "Initialize")));

// Keep the existing state location; map persisted old absolute keys at the read boundary.
var backup = H("ReplacedBackup", "Initialize");
var parentCalls = backup.Body.Instructions.Where(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "System.IO.Path" && m.Name == "GetDirectoryName").ToArray();
if (parentCalls.Length != 1) throw new Exception("Unexpected backup state path");
parentCalls[0].Operand = H("LoadingPaths", "GameRoot");
var keys = backup.Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == "get_Key").ToArray();
if (keys.Length != 2) throw new Exception("Unexpected backup state key reads: " + keys.Length);
foreach (var key in keys) backup.Body.GetILProcessor().InsertAfter(key, Instruction.Create(OpCodes.Call, H("LoadingPaths", "NormalizeBackupPath")));
foreach (var method in new[] { paths, start, backup }.Concat(helpers.SelectMany(t => t.Methods)).Where(m => m.HasBody))
    foreach (var i in method.Body.Instructions)
        if (i.OpCode.OperandType == OperandType.ShortInlineBrTarget)
            i.OpCode = (OpCode)typeof(OpCodes).GetField(i.OpCode.Code.ToString()[..^2])!.GetValue(null)!;
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
target.Write(args[2]);

using var written = AssemblyDefinition.ReadAssembly(args[2]);
using var baseline = AssemblyDefinition.ReadAssembly(args[0]);
int untouched = 0;
foreach (var t in baseline.MainModule.Types)
    foreach (var a in Methods(t))
    {
        if (!a.HasBody || (a.DeclaringType.FullName == mod.FullName && a.Name is "SetupPaths" or "OnApplicationStart") ||
            (a.DeclaringType.FullName == "SkinForger.ReplacedBackup" && a.Name == "Initialize")) continue;
        var b = written.MainModule.Types.SelectMany(Methods).Single(x => x.FullName == a.FullName);
        if (Fingerprint(a) != Fingerprint(b)) throw new Exception("Unexpected change: " + a.FullName);
        untouched++;
    }
if (written.MainModule.AssemblyReferences.Any(r => r.Name == "Runtime" ||
    (r.Version.Major >= 10 && !baseline.MainModule.AssemblyReferences.Any(b => b.FullName == r.FullName)))) throw new Exception("Invalid embedded reference");
Console.WriteLine("PASS: " + untouched + " baseline methods unchanged; only paths, backup-key migration and application-start hook changed.");
if (args.Length == 4)
{
    if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[3]))) != "953355DA37236001DB666D1DD614576CC8608F5CFDF1BACEF8A333E19C1AE1B7") throw new Exception("Previous loading release mismatch");
    using var previous = AssemblyDefinition.ReadAssembly(args[3]);
    int preserved = 0;
    foreach (var a in previous.MainModule.Types.SelectMany(Methods).Where(m => m.HasBody))
    {
        if (a.DeclaringType.FullName == "SkinForger.CoverGeometry") continue;
        if ((a.DeclaringType.FullName == "SkinForger.CustomLoadingPicManager" && (a.Name == "AfterLoading" || a.Name == "Initialize")) ||
            (a.DeclaringType.FullName == "SkinForger.LoadingConfig" && a.Name == "Read")) continue;
        var b = written.MainModule.Types.SelectMany(Methods).Single(x => x.FullName == a.FullName);
        if (Fingerprint(a) != Fingerprint(b)) throw new Exception("Cover changed unrelated method: " + a.FullName);
        preserved++;
    }
    Console.WriteLine("PASS: preserves " + preserved + " previous release methods; only loading layout and geometry helper differ.");
}
Console.WriteLine("SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[2]))));

static IEnumerable<MethodDefinition> Methods(TypeDefinition t) => t.Methods.Concat(t.NestedTypes.SelectMany(Methods));
static string Fingerprint(MethodDefinition m)
{
    string Operand(object? o) => o switch {
        Instruction i => "@" + m.Body.Instructions.IndexOf(i),
        Instruction[] a => string.Join(",", a.Select(i => m.Body.Instructions.IndexOf(i))),
        _ => o?.ToString() ?? ""
    };
    return string.Join("\n", m.Body.Instructions.Select(i => i.OpCode.Code + " " + Operand(i.Operand))) +
        string.Join("\n", m.Body.ExceptionHandlers.Select(e => e.HandlerType + ":" + e.CatchType + ":" + Operand(e.TryStart) + ":" + Operand(e.TryEnd) + ":" + Operand(e.HandlerStart) + ":" + Operand(e.HandlerEnd)));
}
