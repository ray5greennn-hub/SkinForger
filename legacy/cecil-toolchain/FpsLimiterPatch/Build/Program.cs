using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 3) throw new ArgumentException("Build <current verified DLL> <runtime DLL> <output DLL>");
const string ExpectedInput = "2699EB0EF2EC573AE9391B640BF25D05B0AC661FCCD8404EF6A243CB457B117A";
if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))) != ExpectedInput)
    throw new Exception("Input DLL hash mismatch");

using var target = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { InMemory = true });
using var source = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { InMemory = true });
var module = target.MainModule;
var helpers = source.MainModule.Types.Where(t => t.Namespace == "SkinForger.FpsProtection").ToArray();
if (helpers.Length != 3 || helpers.Any(t => t.NestedTypes.Count != 0)) throw new Exception("Unexpected helper types");
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
    foreach (var property in type.Properties) property.PropertyType = module.ImportReference(property.PropertyType);
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

MethodDefinition Helper(string name) => module.GetType("SkinForger.FpsProtection.FpsProtectionManager").Methods.Single(m => m.Name == name);
var mod = module.GetType("SkinForger.SkinForgerMod");
var initialize = mod.Methods.Single(m => m.Name == "OnInitializeMelon");
var appStart = mod.Methods.Single(m => m.Name == "OnApplicationStart");
var update = mod.Methods.Single(m => m.Name == "OnUpdate");
var ensure = mod.Methods.Single(m => m.Name == "EnsureInitialized");
initialize.Body.GetILProcessor().InsertBefore(initialize.Body.Instructions[0], Instruction.Create(OpCodes.Call, Helper("InitializeEarly")));
appStart.Body.GetILProcessor().InsertBefore(appStart.Body.Instructions[0], Instruction.Create(OpCodes.Call, Helper("ApplyAtApplicationStart")));

var guardCalls = update.Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == "UpdateFpsLimiterGuard").ToArray();
if (guardCalls.Length != 1) throw new Exception("Unexpected FPS guard call count: " + guardCalls.Length);
guardCalls[0].OpCode = OpCodes.Nop;
guardCalls[0].Operand = null;
foreach (string name in new[] { "LoadFpsLimiterConfig", "ApplyFpsLimiterConfig" })
{
    var calls = ensure.Body.Instructions.Where(i => i.Operand is MethodReference m && m.Name == name).ToArray();
    if (calls.Length != 1) throw new Exception("Unexpected " + name + " call count: " + calls.Length);
    calls[0].OpCode = OpCodes.Nop;
    calls[0].Operand = null;
}

if (mod.Methods.Any(m => m.Name == "OnSceneWasInitialized")) throw new Exception("Scene override already exists");
var scene = new MethodDefinition("OnSceneWasInitialized", appStart.Attributes, module.TypeSystem.Void);
scene.Parameters.Add(new ParameterDefinition("buildIndex", ParameterAttributes.None, module.TypeSystem.Int32));
scene.Parameters.Add(new ParameterDefinition("sceneName", ParameterAttributes.None, module.TypeSystem.String));
scene.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
scene.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_2));
scene.Body.Instructions.Add(Instruction.Create(OpCodes.Call, Helper("ApplyAfterSceneInitialized")));
scene.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
mod.Methods.Add(scene);

foreach (var method in new[] { initialize, appStart, update, ensure, scene }.Concat(helpers.SelectMany(t => t.Methods)).Where(m => m.HasBody))
    foreach (var i in method.Body.Instructions)
        if (i.OpCode.OperandType == OperandType.ShortInlineBrTarget)
            i.OpCode = (OpCode)typeof(OpCodes).GetField(i.OpCode.Code.ToString()[..^2])!.GetValue(null)!;

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
target.Write(args[2]);

using var written = AssemblyDefinition.ReadAssembly(args[2]);
using var baseline = AssemblyDefinition.ReadAssembly(args[0]);
var allowed = new HashSet<string> { "OnInitializeMelon", "OnApplicationStart", "OnUpdate", "EnsureInitialized" };
int preserved = 0;
foreach (var type in baseline.MainModule.Types)
    foreach (var before in Methods(type).Where(m => m.HasBody))
    {
        if (before.DeclaringType.FullName == mod.FullName && allowed.Contains(before.Name)) continue;
        var after = written.MainModule.Types.SelectMany(Methods).Single(m => m.FullName == before.FullName);
        if (Fingerprint(before) != Fingerprint(after)) throw new Exception("Unexpected method change: " + before.FullName);
        preserved++;
    }
var writtenMod = written.MainModule.GetType("SkinForger.SkinForgerMod");
var writtenUpdate = writtenMod.Methods.Single(m => m.Name == "OnUpdate");
if (writtenUpdate.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "UpdateFpsLimiterGuard"))
    throw new Exception("Per-frame FPS guard still reachable");
var writtenEnsure = writtenMod.Methods.Single(m => m.Name == "EnsureInitialized");
if (writtenEnsure.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name is "LoadFpsLimiterConfig" or "ApplyFpsLimiterConfig"))
    throw new Exception("Legacy FPS config path still reachable");
var added = written.MainModule.Types.Where(t => t.Namespace == "SkinForger.FpsProtection").ToArray();
if (added.SelectMany(t => t.Methods).Any(m => m.Name is "Update" or "LateUpdate" or "FixedUpdate" ||
    m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference called &&
        called.DeclaringType.FullName == "UnityEngine.QualitySettings" && called.Name == "set_vSyncCount")))
    throw new Exception("Forbidden polling or VSync mutation found");
Console.WriteLine("PASS: " + preserved + " existing methods preserved; four lifecycle/config call sites changed.");
Console.WriteLine("PASS: no FPS Update guard, background worker, coroutine, or VSync setter.");
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
