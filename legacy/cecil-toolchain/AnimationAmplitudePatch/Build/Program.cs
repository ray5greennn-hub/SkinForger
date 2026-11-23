using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 3)
    throw new ArgumentException("Build <verified SkinForger DLL> <runtime DLL> <output DLL>");

const string ExpectedInput = "2C8B60D016D0785B45D870C7ECF73A45E14F406D40ECB4C1BB6D187AB34D346D";
string inputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0])));
if (!inputHash.Equals(ExpectedInput, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Input DLL hash mismatch: " + inputHash);

using var target = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { InMemory = true });
using var source = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { InMemory = true });
var module = target.MainModule;
var helpers = source.MainModule.Types
    .Where(t => t.Namespace == "SkinForger.AnimationAmplitude")
    .ToArray();
if (helpers.Length != 7 || helpers.Any(t => t.NestedTypes.Count != 0))
    throw new InvalidOperationException("Unexpected animation helper type layout");

foreach (var type in helpers)
{
    foreach (var method in type.Methods)
    {
        _ = method.ReturnType;
        _ = method.Parameters.Count;
        if (!method.HasBody) continue;
        _ = method.Body.Instructions.Count;
        _ = method.Body.Variables.Count;
        _ = method.Body.ExceptionHandlers.Count;
    }
}
var helperMethods = helpers.ToDictionary(t => t.Name,
    t => t.Methods.ToDictionary(m => m.FullName, m => m));
var helperFields = helpers.ToDictionary(t => t.Name,
    t => t.Fields.ToDictionary(f => f.Name, f => f));

foreach (var type in helpers)
{
    source.MainModule.Types.Remove(type);
    module.Types.Add(type);
}

TypeDefinition? Own(TypeReference type) => helpers.SingleOrDefault(h => h.FullName == type.FullName);
foreach (var type in helpers)
{
    type.BaseType = module.ImportReference(type.BaseType);
    foreach (var field in type.Fields) field.FieldType = module.ImportReference(field.FieldType);
    foreach (var property in type.Properties) property.PropertyType = module.ImportReference(property.PropertyType);
    foreach (var method in type.Methods)
    {
        method.ReturnType = module.ImportReference(method.ReturnType);
        foreach (var parameter in method.Parameters)
            parameter.ParameterType = module.ImportReference(parameter.ParameterType);
        if (!method.HasBody) continue;
        foreach (var variable in method.Body.Variables)
            variable.VariableType = module.ImportReference(variable.VariableType);
        foreach (var handler in method.Body.ExceptionHandlers)
            if (handler.CatchType != null) handler.CatchType = module.ImportReference(handler.CatchType);
        foreach (var instruction in method.Body.Instructions)
        {
            instruction.Operand = instruction.Operand switch
            {
                MethodReference called when Own(called.DeclaringType) is TypeDefinition owner =>
                    helperMethods[owner.Name][called.FullName],
                MethodReference called => module.ImportReference(called),
                FieldReference field when Own(field.DeclaringType) is TypeDefinition owner =>
                    helperFields[owner.Name][field.Name],
                FieldReference field => module.ImportReference(field),
                TypeReference referenced when Own(referenced) is TypeDefinition owner => owner,
                TypeReference referenced => module.ImportReference(referenced),
                _ => instruction.Operand
            };
        }
    }
}

MethodDefinition Helper(string type, string method)
{
    var owner = helpers.SingleOrDefault(t => t.Name == type)
        ?? throw new InvalidOperationException("Helper type not found: " + type + "; available=" +
            string.Join(",", helpers.Select(t => t.Name)));
    return helperMethods[owner.Name].Values.SingleOrDefault(m => m.Name == method)
        ?? throw new InvalidOperationException("Helper method not found: " + type + "." + method +
            "; available=" + string.Join(",", helperMethods[owner.Name].Values.Select(m => m.Name)));
}

var mod = module.GetType("SkinForger.SkinForgerMod")
    ?? throw new InvalidOperationException("SkinForgerMod not found");
var applySkeleton = mod.Methods.Single(m => m.Name == "ApplySkeleton");
var synthesize = mod.Methods.Single(m => m.Name == "TryBuildSynthesizedAtlasForSkeleton");
var writer = mod.Methods.Single(m => m.Name == "WriteAnchorMetadataIfMissing");
var applyPlacement = mod.Methods.Single(m => m.Name == "ApplyRegionPlacementToAttachment");

var applyIl = applySkeleton.Body.GetILProcessor();
applyIl.InsertBefore(applySkeleton.Body.Instructions[0], applyIl.Create(OpCodes.Ldarg_0));
applyIl.InsertBefore(applySkeleton.Body.Instructions[1],
    applyIl.Create(OpCodes.Call, Helper("AnimationAmplitudeManager", "BeginSkeleton")));

var placementCall = synthesize.Body.Instructions.Single(i =>
    i.Operand is MethodReference called && called.Name == "ApplyRegionPlacementToAttachment");
var regionPathCall = synthesize.Body.Instructions
    .TakeWhile(i => i != placementCall)
    .Last(i => i.Operand is MethodReference called && called.Name == "get_RegionPath" &&
        called.DeclaringType.Name == "ReplacementRecord");
var replacementLocal = ResolveLoadedLocal(synthesize, regionPathCall.Previous)
    ?? throw new InvalidOperationException("ReplacementRecord local not found");
var recordType = mod.NestedTypes.Single(t => t.Name == "ReplacementRecord");
var getSlotIndex = recordType.Methods.Single(m => m.Name == "get_SlotIndex");
var getAttachment = recordType.Methods.Single(m => m.Name == "get_Attachment");
var getRegionPath = recordType.Methods.Single(m => m.Name == "get_RegionPath");
var synthIl = synthesize.Body.GetILProcessor();
var cursor = placementCall;
foreach (var instruction in new[]
{
    synthIl.Create(OpCodes.Ldarg_0),
    synthIl.Create(OpCodes.Ldloc, replacementLocal),
    synthIl.Create(OpCodes.Callvirt, getSlotIndex),
    synthIl.Create(OpCodes.Ldloc, replacementLocal),
    synthIl.Create(OpCodes.Callvirt, getAttachment),
    synthIl.Create(OpCodes.Ldloc, replacementLocal),
    synthIl.Create(OpCodes.Callvirt, getRegionPath),
    synthIl.Create(OpCodes.Call, Helper("AnimationAmplitudeManager", "RegisterReplacement"))
})
{
    synthIl.InsertAfter(cursor, instruction);
    cursor = instruction;
}

var finalReturn = synthesize.Body.Instructions.Last(i => i.OpCode == OpCodes.Ret);
var returnValueLoad = finalReturn.Previous
    ?? throw new InvalidOperationException("Final return value load not found");
synthIl.InsertBefore(returnValueLoad, synthIl.Create(OpCodes.Ldarg_0));
synthIl.InsertBefore(returnValueLoad, synthIl.Create(OpCodes.Ldarg_2));
synthIl.InsertBefore(returnValueLoad,
    synthIl.Create(OpCodes.Call, Helper("AnimationAmplitudeManager", "ApplyToSkeleton")));

var serializeCall = writer.Body.Instructions.Single(i =>
    i.Operand is MethodReference called && called.DeclaringType.FullName == "Newtonsoft.Json.JsonConvert" &&
    called.Name == "SerializeObject");
writer.Body.GetILProcessor().InsertAfter(serializeCall,
    Instruction.Create(OpCodes.Call, Helper("AnimationAmplitudeSettings", "AddDefaultsToGeneratedJson")));

var setScaleX = applyPlacement.Body.Instructions.Single(i =>
    i.Operand is MethodReference called && called.DeclaringType.Name == "RegionAttachment" &&
    called.Name == "set_scaleX");
var setScaleY = applyPlacement.Body.Instructions.Single(i =>
    i.Operand is MethodReference called && called.DeclaringType.Name == "RegionAttachment" &&
    called.Name == "set_scaleY");
var scaleLocal = new VariableDefinition(module.TypeSystem.Single);
applyPlacement.Body.Variables.Add(scaleLocal);
applyPlacement.Body.InitLocals = true;
var placementIl = applyPlacement.Body.GetILProcessor();
var scaleAssignmentStart = setScaleX.Previous?.Previous?.Previous
    ?? throw new InvalidOperationException("RegionAttachment scaleX assignment start not found");
placementIl.InsertBefore(scaleAssignmentStart, placementIl.Create(OpCodes.Ldarg, applyPlacement.Parameters[4]));
placementIl.InsertBefore(scaleAssignmentStart,
    placementIl.Create(OpCodes.Call, Helper("RegionScaleSettings", "GetScale")));
placementIl.InsertBefore(scaleAssignmentStart, placementIl.Create(OpCodes.Stloc, scaleLocal));
placementIl.InsertBefore(setScaleX, placementIl.Create(OpCodes.Ldloc, scaleLocal));
placementIl.InsertBefore(setScaleX, placementIl.Create(OpCodes.Mul));
placementIl.InsertBefore(setScaleY, placementIl.Create(OpCodes.Ldloc, scaleLocal));
placementIl.InsertBefore(setScaleY, placementIl.Create(OpCodes.Mul));

foreach (var method in new[] { applySkeleton, synthesize, writer, applyPlacement }
    .Concat(helpers.SelectMany(t => t.Methods)).Where(m => m.HasBody))
{
    foreach (var instruction in method.Body.Instructions)
    {
        if (instruction.OpCode.OperandType != OperandType.ShortInlineBrTarget) continue;
        string longName = instruction.OpCode.Code.ToString()[..^2];
        instruction.OpCode = (OpCode)typeof(OpCodes).GetField(longName)!.GetValue(null)!;
    }
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
target.Write(args[2]);

using var written = AssemblyDefinition.ReadAssembly(args[2]);
using var baseline = AssemblyDefinition.ReadAssembly(args[0]);
var changed = new HashSet<string>
{
    "System.Void SkinForger.SkinForgerMod::ApplySkeleton(Il2CppSpine.Skeleton,System.Int32,System.Int32,System.Boolean)",
    "UnityEngine.Texture2D SkinForger.SkinForgerMod::TryBuildSynthesizedAtlasForSkeleton(Il2CppSpine.Skeleton,UnityEngine.Texture2D,System.String,System.String,System.Boolean)",
    "System.Void SkinForger.SkinForgerMod::WriteAnchorMetadataIfMissing(System.String,Il2CppSpine.Attachment,Il2CppSpine.AtlasRegion,System.String,System.Int32,System.Int32)",
    "System.Void SkinForger.SkinForgerMod::ApplyRegionPlacementToAttachment(Il2CppSpine.Attachment,Il2CppSpine.AtlasRegion,SkinForger.SkinForgerMod/RegionPlacement,System.String,System.String,System.String)"
};
int preserved = 0;
foreach (var type in baseline.MainModule.Types)
{
    foreach (var before in Methods(type).Where(m => m.HasBody))
    {
        if (changed.Contains(before.FullName)) continue;
        var after = written.MainModule.Types.SelectMany(Methods).Single(m => m.FullName == before.FullName);
        if (Fingerprint(before) != Fingerprint(after))
            throw new InvalidOperationException("Unexpected method change: " + before.FullName);
        preserved++;
    }
}

var writtenMod = written.MainModule.GetType("SkinForger.SkinForgerMod");
var writtenUpdate = writtenMod.Methods.Single(m => m.Name == "OnUpdate");
if (writtenUpdate.Body.Instructions.Any(i => i.Operand is MethodReference called &&
    called.DeclaringType.Namespace == "SkinForger.AnimationAmplitude"))
    throw new InvalidOperationException("Animation amplitude was added to OnUpdate");
var added = written.MainModule.Types.Where(t => t.Namespace == "SkinForger.AnimationAmplitude").ToArray();
if (added.SelectMany(t => t.Methods).Any(m => m.Name is "Update" or "LateUpdate" or "FixedUpdate"))
    throw new InvalidOperationException("Forbidden per-frame method found");
var amplitudeManager = added.Single(t => t.Name == "AnimationAmplitudeManager");
var applyData = amplitudeManager.Methods.Single(m => m.Name == "ApplyData");
bool HasTryCast(string typeName) => applyData.Body.Instructions.Any(i =>
    i.Operand is GenericInstanceMethod generic && generic.Name == "TryCast" &&
    generic.GenericArguments.Any(argument => argument.Name == typeName));
int TryCastIndex(string typeName)
{
    for (int i = 0; i < applyData.Body.Instructions.Count; i++)
    {
        if (applyData.Body.Instructions[i].Operand is GenericInstanceMethod generic &&
            generic.Name == "TryCast" &&
            generic.GenericArguments.Any(argument => argument.Name == typeName))
            return i;
    }
    return -1;
}
if (!HasTryCast("ScaleTimeline") || !HasTryCast("ShearTimeline") ||
    !HasTryCast("TranslateTimeline") || !HasTryCast("RotateTimeline"))
    throw new InvalidOperationException("Timeline type guard is incomplete");
if (TryCastIndex("ScaleTimeline") > TryCastIndex("TranslateTimeline") ||
    TryCastIndex("ShearTimeline") > TryCastIndex("TranslateTimeline"))
    throw new InvalidOperationException("ScaleTimeline and ShearTimeline must be excluded before TranslateTimeline");
if (added.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
    .Any(i => i.Operand is MethodReference called &&
        (called.DeclaringType.Name == "ScaleTimeline" || called.DeclaringType.Name == "ShearTimeline") &&
        (called.Name == "get_frames" || called.Name == "set_frames" || called.Name == "Apply")))
    throw new InvalidOperationException("ScaleTimeline and ShearTimeline frame data must not be accessed");
if (written.MainModule.Types.SelectMany(Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
    .Any(i => i.OpCode == OpCodes.Ldstr && i.Operand is string text &&
        text.Contains("SkeletonMecanim.Initialize", StringComparison.Ordinal)))
    throw new InvalidOperationException("Forbidden SkeletonMecanim.Initialize marker found");

var writtenPlacement = writtenMod.Methods.Single(m => m.Name == "ApplyRegionPlacementToAttachment");
if (writtenPlacement.Body.Instructions.Count(i =>
    i.Operand is MethodReference called && called.DeclaringType.Name == "RegionScaleSettings" &&
    called.Name == "GetScale") != 1)
    throw new InvalidOperationException("Region image Scale must be read exactly once per attachment apply");
foreach (var pair in new[] { (Setter: "set_scaleX", Getter: "get_ScaleX"), (Setter: "set_scaleY", Getter: "get_ScaleY") })
{
    var call = writtenPlacement.Body.Instructions.Single(i =>
        i.Operand is MethodReference called && called.DeclaringType.Name == "RegionAttachment" &&
        called.Name == pair.Setter);
    if (call.Previous?.OpCode != OpCodes.Mul || call.Previous.Previous?.OpCode.Code is not Code.Ldloc and not Code.Ldloc_S)
        throw new InvalidOperationException(pair.Setter + " is not multiplied by the validated JSON Scale");
    if (call.Previous.Previous.Previous?.Operand is not MethodReference getter ||
        getter.DeclaringType.Name != "RegionAttachmentGeometry" || getter.Name != pair.Getter)
        throw new InvalidOperationException(pair.Setter + " is not based on cached original attachment geometry");
}

var referencesBefore = baseline.MainModule.AssemblyReferences.Select(r => r.FullName).OrderBy(x => x).ToArray();
var referencesAfter = written.MainModule.AssemblyReferences.Select(r => r.FullName).OrderBy(x => x).ToArray();
if (!referencesBefore.SequenceEqual(referencesAfter))
    throw new InvalidOperationException("Assembly references changed");

Console.WriteLine("PASS: " + preserved + " existing methods preserved; only four low-frequency methods changed.");
Console.WriteLine("PASS: exact timeline guard excludes ScaleTimeline and ShearTimeline before TranslateTimeline.");
Console.WriteLine("PASS: JSON Scale is read once and multiplies cached original RegionAttachment scaleX/scaleY.");
Console.WriteLine("PASS: no OnUpdate hook, no scale/shear frame access, no SkeletonMecanim.Initialize patch.");
Console.WriteLine("PASS: assembly references unchanged.");
Console.WriteLine("SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[2]))));

static VariableDefinition? ResolveLoadedLocal(MethodDefinition method, Instruction? instruction)
{
    if (instruction?.Operand is VariableDefinition variable) return variable;
    if (instruction == null) return null;
    if (instruction.OpCode == OpCodes.Ldloc_0) return method.Body.Variables[0];
    if (instruction.OpCode == OpCodes.Ldloc_1) return method.Body.Variables[1];
    if (instruction.OpCode == OpCodes.Ldloc_2) return method.Body.Variables[2];
    if (instruction.OpCode == OpCodes.Ldloc_3) return method.Body.Variables[3];
    return null;
}

static string Fingerprint(MethodDefinition method)
{
    return string.Join("|", method.Body.Instructions.Select(i =>
        i.OpCode.Code + ":" + (i.Operand switch
        {
            MethodReference called => called.FullName,
            FieldReference field => field.FullName,
            TypeReference type => type.FullName,
            Instruction target => "target:" + method.Body.Instructions.IndexOf(target),
            Instruction[] targets => "targets:" + string.Join(",", targets.Select(method.Body.Instructions.IndexOf)),
            VariableDefinition variable => "var:" + variable.Index,
            ParameterDefinition parameter => "arg:" + parameter.Index,
            _ => i.Operand?.ToString() ?? string.Empty
        })));
}

static IEnumerable<MethodDefinition> Methods(TypeDefinition type) =>
    type.Methods.Concat(type.NestedTypes.SelectMany(Methods));
