using Mono.Cecil;
using Mono.Cecil.Cil;
if (args.Length != 3) throw new ArgumentException("Verify <baseline> <candidate> <tested runtime>");
using var baseline = AssemblyDefinition.ReadAssembly(args[0]);
using var candidate = AssemblyDefinition.ReadAssembly(args[1]);
using var runtime = AssemblyDefinition.ReadAssembly(args[2]);
var before = baseline.MainModule.GetType("SkinForger.SkinForgerMod");
var after = candidate.MainModule.GetType("SkinForger.SkinForgerMod");
var helper = candidate.MainModule.GetType("SkinForger.ReplacedBackup");
var allowed = new HashSet<string> { "DrawWindow", "ExtractAttachment", "IsRuntimeSkinInputFile", "LoadAnchorMetadata", "LoadManifest", "ReloadSkins", "SaveManifest", "WriteAnchorMetadataIfMissing" };
int unchanged = 0;
foreach (var a in Methods(before))
{
    var b = Methods(after).Single(m => m.FullName == a.FullName);
    if (!a.HasBody || allowed.Contains(a.Name)) continue;
    Check(Fingerprint(a) == Fingerprint(b), "unchanged " + a.Name, false);
    unchanged++;
}
Console.WriteLine("PASS: " + unchanged + " existing methods unchanged, including Offset, PNG, atlas and UV algorithms");
foreach (var a in runtime.MainModule.GetType("SkinForger.ReplacedBackup").Methods)
{
    if (a.Name == "Log") continue;
    Check(Fingerprint(a) == Fingerprint(helper.Methods.Single(m => m.Name == a.Name)), "embedded helper matches tested code: " + a.Name);
}
var newRefs = candidate.MainModule.AssemblyReferences.Where(r => !baseline.MainModule.AssemblyReferences.Any(b => b.FullName == r.FullName)).ToArray();
foreach (var r in newRefs) Console.WriteLine("New BCL reference: " + r.FullName);
Check(!newRefs.Any(r => r.Name is "Runtime" or "ReplacedBackup" || r.Version.Major >= 10), "no new runtime helper / .NET 10 dependency");
var draw = after.Methods.Single(m => m.Name == "DrawWindow");
var snapshot = draw.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "BackupChangedAll");
Check(snapshot.Next.Operand is MethodReference reload && reload.Name == "ReloadSkins", "incremental comparison executes before reload");
Check(!after.Methods.Single(m => m.Name == "ReloadSkins").Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name.StartsWith("Snapshot")), "startup directory scan never triggers backup");
foreach (var name in new[] { "DumpCostume", "ExtractAll" })
    Check(!after.Methods.Single(m => m.Name == name).Body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == helper.FullName), name + " has no bulk backup");
Check(after.Methods.Single(m => m.Name == "ReloadSkins").Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "Initialize" && m.DeclaringType.FullName == helper.FullName), "initialization enrolls persistent baseline");
foreach (var name in new[] { "ExtractAttachment", "WriteAnchorMetadataIfMissing", "SaveManifest" })
{
    var method = after.Methods.Single(m => m.Name == name);
    Check(method.Body.Instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == helper.FullName && m.Name.StartsWith("WriteResource")), name + " protects disk writes");
    Check(method.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "IsExcludedPath"), name + " rejects backup destinations");
}
Check(!after.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "System.IO.Directory" && m.Name == "GetDirectories"), "all original directory scans use exclusion wrapper");
foreach (var name in new[] { "LoadAnchorMetadata", "LoadManifest" })
    Check(after.Methods.Single(m => m.Name == name).Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "ReadResourceText"), name + " rejects backup JSON");
Check(after.Methods.Single(m => m.Name == "IsRuntimeSkinInputFile" && m.Parameters.Count == 2).Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "IsExcludedPath"), "PNG gate rejects replaced");
Check(!Methods(after).Any(m => m.Name.Contains("MecanimInitialize")), "no new Mecanim patch");
foreach (var method in Methods(after).Concat(helper.Methods).Where(m => m.HasBody))
    foreach (var i in method.Body.Instructions)
        if (i.Operand is Instruction target) Check(method.Body.Instructions.Contains(target), "branch target in same method", false);
Console.WriteLine("PASS: IL regression, entry points, path exclusion and embedded helper verified");

static IEnumerable<MethodDefinition> Methods(TypeDefinition t) => t.Methods.Concat(t.NestedTypes.SelectMany(Methods));
static string Fingerprint(MethodDefinition method)
{
    string Operand(object? value) => value switch
    {
        Instruction i => "@" + method.Body.Instructions.IndexOf(i),
        Instruction[] a => string.Join(",", a.Select(i => method.Body.Instructions.IndexOf(i))),
        VariableDefinition v => "local" + v.Index,
        ParameterDefinition p => "arg" + p.Index,
        _ => value?.ToString() ?? ""
    };
    return string.Join("\n", method.Body.Instructions.Select(i =>
        (i.OpCode.OperandType == OperandType.ShortInlineBrTarget ? i.OpCode.Code.ToString()[..^2] : i.OpCode.Code.ToString()) + " " + Operand(i.Operand))) +
        string.Join("\n", method.Body.ExceptionHandlers.Select(e => e.HandlerType + ":" + e.CatchType + ":" + Operand(e.TryStart) + ":" + Operand(e.TryEnd) + ":" + Operand(e.HandlerStart) + ":" + Operand(e.HandlerEnd)));
}
static void Check(bool condition, string label, bool print = true)
{
    if (!condition) throw new Exception("FAIL: " + label);
    if (print) Console.WriteLine("PASS: " + label);
}
