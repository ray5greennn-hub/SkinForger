using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 3) throw new ArgumentException("Build <baseline> <runtime-helper> <candidate>");
const string expected = "ECCA713898AB9EDD9B2F82CF01F91EF4A748AEFBB8C7472D11F44E620214EAFE";
if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))) != expected)
    throw new InvalidOperationException("Input must be the installed offset candidate.");
using var target = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { InMemory = true });
using var source = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { InMemory = true, ReadingMode = ReadingMode.Immediate });
var module = target.MainModule;
var mod = module.GetType("SkinForger.SkinForgerMod");
var helper = source.MainModule.GetType("SkinForger.ReplacedBackup");
if (helper.NestedTypes.Count != 0) throw new Exception("Unexpected helper structure");
_ = helper.Fields.Count;
// Cecil lazily loads method bodies. Materialize them while they still belong to the donor module.
foreach (var method in helper.Methods)
{
    _ = method.ReturnType;
    _ = method.Parameters.Count;
    _ = method.Body.Instructions.Count;
    _ = method.Body.Variables.Count;
    _ = method.Body.ExceptionHandlers.Count;
}
source.MainModule.Types.Remove(helper);
module.Types.Add(helper);
helper.BaseType = module.ImportReference(helper.BaseType);
foreach (var field in helper.Fields) field.FieldType = module.ImportReference(field.FieldType);
foreach (var method in helper.Methods)
{
    method.ReturnType = module.ImportReference(method.ReturnType);
    foreach (var p in method.Parameters) p.ParameterType = module.ImportReference(p.ParameterType);
    foreach (var v in method.Body.Variables) v.VariableType = module.ImportReference(v.VariableType);
    foreach (var eh in method.Body.ExceptionHandlers)
        if (eh.CatchType != null) eh.CatchType = module.ImportReference(eh.CatchType);
    foreach (var instruction in method.Body.Instructions)
    {
        instruction.Operand = instruction.Operand switch
        {
            MethodReference m when m.DeclaringType.FullName == helper.FullName => helper.Methods.Single(x => x.Name == m.Name),
            MethodReference m => module.ImportReference(m),
            FieldReference f when f.DeclaringType.FullName == helper.FullName => helper.Fields.Single(x => x.Name == f.Name),
            FieldReference f => module.ImportReference(f),
            TypeReference t => module.ImportReference(t),
            _ => instruction.Operand
        };
    }
}
MethodDefinition H(string name) => helper.Methods.Single(m => m.Name == name);
MethodDefinition M(string name) => mod.Methods.Single(m => m.Name == name);
var logger = mod.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
    .Select(i => i.Operand).OfType<MethodReference>().First(m =>
        m.DeclaringType.FullName == "MelonLoader.MelonLogger" && m.Name == "Msg" &&
        m.Parameters.Count == 1 && m.Parameters[0].ParameterType.FullName == "System.String");
foreach (var i in H("Log").Body.Instructions)
    if (i.Operand is MethodReference mr && mr.DeclaringType.FullName == "System.Console") i.Operand = logger;

var changed = new HashSet<MethodDefinition>();
// Directory enumeration is centralized, including skin list construction at startup.
foreach (var method in mod.Methods.Where(m => m.HasBody))
    foreach (var instruction in method.Body.Instructions)
        if (instruction.Operand is MethodReference mr && mr.DeclaringType.FullName == "System.IO.Directory" && mr.Name == "GetDirectories")
        {
            if (mr.Parameters.Count != 1) throw new Exception("Unreviewed directory enumeration");
            instruction.Operand = H("ResourceDirectories");
            changed.Add(method);
        }

var gate = mod.Methods.Single(m => m.Name == "IsRuntimeSkinInputFile" && m.Parameters.Count == 2);
Prepend(gate, new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, H("IsExcludedPath")),
    Instruction.Create(OpCodes.Brfalse, gate.Body.Instructions[0]), Instruction.Create(OpCodes.Ldarg_1),
    Instruction.Create(OpCodes.Ldstr, "replaced/backup/debug path is not a runtime resource"),
    Instruction.Create(OpCodes.Stind_Ref), Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Ret) });
changed.Add(gate);

foreach (var name in new[] { "LoadManifest", "LoadAnchorMetadata" })
    ReplaceCall(M(name), "System.IO.File", "ReadAllText", H("ReadResourceText"));
ReplaceCall(M("ExtractAttachment"), "System.IO.File", "WriteAllBytes", H("WriteResourceBytes"));
ReplaceCall(M("WriteAnchorMetadataIfMissing"), "System.IO.File", "WriteAllText", H("WriteResourceText"));
ReplaceCall(M("SaveManifest"), "System.IO.File", "WriteAllText", H("WriteResourceText"));

// Skip excluded Dump destinations before directory creation or texture extraction.
var extract = M("ExtractAttachment");
var combine = extract.Body.Instructions.Single(i => i.Operand is MethodReference mr &&
    mr.DeclaringType.FullName == "System.IO.Path" && mr.Name == "Combine");
var store = combine.Next;
var pathLocal = StoreLocal(extract, store);
InsertBefore(extract, store.Next, new[] { Instruction.Create(OpCodes.Ldloc, pathLocal),
    Instruction.Create(OpCodes.Call, H("IsExcludedPath")), Instruction.Create(OpCodes.Brfalse, store.Next), Instruction.Create(OpCodes.Ret) });
changed.Add(extract);
foreach (var name in new[] { "WriteAnchorMetadataIfMissing", "SaveManifest" })
{
    var method = M(name);
    Prepend(method, new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, H("IsExcludedPath")),
        Instruction.Create(OpCodes.Brfalse, method.Body.Instructions[0]), Instruction.Create(OpCodes.Ret) });
    changed.Add(method);
}

var root = mod.Fields.Single(f => f.Name == "_skinsDirectory");
var draw = M("DrawWindow");
var reloadCalls = draw.Body.Instructions.Where(i => i.Operand is MethodReference mr && mr.Name == "ReloadSkins").ToArray();
if (reloadCalls.Length != 2) throw new Exception("Reload menu layout changed");
// Startup registers unknown files once; subsequent calls never rebaseline known files.
Prepend(M("ReloadSkins"), new[] { Instruction.Create(OpCodes.Ldsfld, root), Instruction.Create(OpCodes.Call, H("Initialize")) });
// Only the explicit Reload button performs a content comparison and incremental backup.
InsertBefore(draw, reloadCalls[0], new[] { Instruction.Create(OpCodes.Ldsfld, root), Instruction.Create(OpCodes.Call, H("BackupChangedAll")) });
changed.Add(draw);
// Dump snapshots are intentionally absent. Only an actual write can back up its target.
foreach (var method in changed.Concat(helper.Methods)) WidenBranches(method);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
target.Write(args[2]);
Console.WriteLine("Changed existing methods: " + string.Join(", ", changed.Select(m => m.Name).Order()));
Console.WriteLine("SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[2]))));
Console.WriteLine("Helper embedded; no extra runtime DLL.");

void ReplaceCall(MethodDefinition method, string type, string name, MethodReference replacement)
{
    var calls = method.Body.Instructions.Where(i => i.Operand is MethodReference mr && mr.DeclaringType.FullName == type && mr.Name == name).ToArray();
    if (calls.Length != 1) throw new Exception("Unexpected call count in " + method.Name + ": " + name);
    calls[0].Operand = replacement;
    changed.Add(method);
}
static VariableDefinition StoreLocal(MethodDefinition method, Instruction i) => i.OpCode.Code switch
{
    Code.Stloc_0 => method.Body.Variables[0], Code.Stloc_1 => method.Body.Variables[1],
    Code.Stloc_2 => method.Body.Variables[2], Code.Stloc_3 => method.Body.Variables[3],
    Code.Stloc or Code.Stloc_S => (VariableDefinition)i.Operand,
    _ => throw new Exception("Expected path local assignment")
};
static void Prepend(MethodDefinition method, Instruction[] instructions)
{
    var first = method.Body.Instructions[0];
    var il = method.Body.GetILProcessor();
    foreach (var instruction in instructions) il.InsertBefore(first, instruction);
}
static void InsertBefore(MethodDefinition method, Instruction at, Instruction[] added)
{
    // Redirect incoming branches to the newly inserted guard/snapshot as well.
    foreach (var instruction in method.Body.Instructions)
    {
        if (ReferenceEquals(instruction.Operand, at)) instruction.Operand = added[0];
        if (instruction.Operand is Instruction[] targets)
            for (int n = 0; n < targets.Length; n++) if (ReferenceEquals(targets[n], at)) targets[n] = added[0];
    }
    foreach (var eh in method.Body.ExceptionHandlers)
    {
        if (eh.TryStart == at) eh.TryStart = added[0];
        if (eh.HandlerStart == at) eh.HandlerStart = added[0];
    }
    var il = method.Body.GetILProcessor();
    foreach (var instruction in added) il.InsertBefore(at, instruction);
}
static void WidenBranches(MethodDefinition method)
{
    foreach (var i in method.Body.Instructions)
        if (i.OpCode.OperandType == OperandType.ShortInlineBrTarget)
            i.OpCode = (OpCode)typeof(OpCodes).GetField(i.OpCode.Code.ToString()[..^2])!.GetValue(null)!;
}
