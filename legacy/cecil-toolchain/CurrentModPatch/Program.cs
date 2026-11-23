using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

const string expectedInputHash = "2679D280B6DB20FBEB0E2DC4F976CB78C11AEA1EE09A81C64EDBAC7C113FEE10";

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: CurrentModPatch <current-mod.dll> <candidate.dll>");
    return 2;
}

var actualInputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0])));
if (!actualInputHash.Equals(expectedInputHash, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException($"Unexpected input DLL SHA256: {actualInputHash}");

using var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { InMemory = true });
var mod = assembly.MainModule.GetType("MuseDashSkinChangerMelon.SkinChangerMod")
    ?? throw new InvalidOperationException("SkinChangerMod not found");
var metadata = mod.NestedTypes.Single(t => t.Name == "RegionAnchorMetadata");
var writer = mod.Methods.Single(m => m.Name == "WriteAnchorMetadataIfMissing");
var apply = mod.Methods.Single(m => m.Name == "ApplyRegionPngToAtlas");
var placement = mod.Methods.Single(m => m.Name == "ApplyRegionPlacementToAttachment");

RequireCall(apply, "FindManifestEntry");
RequireCall(apply, "get_OriginalWidth");
RequireCall(apply, "get_OriginalHeight");
RequireCall(apply, "get_width");
RequireCall(apply, "get_height");
RequireCall(placement, "CalculateAttachmentCenterFromAnchor");
RequireCall(placement, "get_NormalWidth");
RequireCall(placement, "get_NormalHeight");
var placementType = mod.NestedTypes.Single(t => t.Name == "RegionPlacement");
var sidecarGetter = placementType.Methods.Single(m => m.Name == "get_AnchorFromSidecar");
var expandedChecks = placement.Body.Instructions.Where(i =>
    i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference mr &&
    mr.Name == "get_Expanded" && mr.DeclaringType.FullName == placementType.FullName).ToArray();
if (expandedChecks.Length != 4)
    throw new InvalidOperationException("Unexpected attachment expansion checks");
foreach (var check in expandedChecks.Skip(2))
{
    var branch = check.Next;
    if (branch == null || branch.OpCode != OpCodes.Brtrue_S || branch.Operand is not Instruction target)
        throw new InvalidOperationException("Unexpected attachment position branch");
    var il = placement.Body.GetILProcessor();
    var load = il.Create(OpCodes.Ldarg_2);
    var sidecar = il.Create(OpCodes.Callvirt, sidecarGetter);
    var useAnchor = il.Create(OpCodes.Brtrue, target);
    il.InsertAfter(branch, load);
    il.InsertAfter(load, sidecar);
    il.InsertAfter(sidecar, useAnchor);
}

foreach (var name in new[] { "SourceWidth", "SourceHeight" })
{
    var setter = metadata.Methods.Single(m => m.Name == $"set_{name}");
    var call = writer.Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt &&
        i.Operand is MethodReference mr && mr.Resolve() == setter);
    var value = call.Previous ?? throw new InvalidOperationException($"Missing {name} value");
    var duplicate = value.Previous ?? throw new InvalidOperationException($"Missing {name} duplicate");
    if (value.OpCode != OpCodes.Ldarg_S || duplicate.OpCode != OpCodes.Dup ||
        value.Operand is not ParameterDefinition parameter || parameter.Name != "source" + name[6..])
        throw new InvalidOperationException($"Unexpected {name} writer IL");

    var il = writer.Body.GetILProcessor();
    il.Remove(call);
    il.Remove(value);
    il.Remove(duplicate);
}

var setVersion = metadata.Methods.Single(m => m.Name == "set_Version");
var writerVersionCall = writer.Body.Instructions.Single(i => i.OpCode == OpCodes.Callvirt &&
    i.Operand is MethodReference mr && mr.Resolve() == setVersion);
if (writerVersionCall.Previous?.OpCode != OpCodes.Ldc_I4_1)
    throw new InvalidOperationException("Unexpected writer version");
writerVersionCall.Previous.OpCode = OpCodes.Ldc_I4_2;

var ctor = metadata.Methods.Single(m => m.IsConstructor && !m.IsStatic);
var versionField = metadata.Fields.Single(f => f.Name == "<Version>k__BackingField");
var defaultVersion = ctor.Body.Instructions.Single(i => i.OpCode == OpCodes.Stfld &&
    i.Operand is FieldReference fr && fr.Resolve() == versionField).Previous;
if (defaultVersion?.OpCode != OpCodes.Ldc_I4_1)
    throw new InvalidOperationException("Unexpected default version");
defaultVersion.OpCode = OpCodes.Ldc_I4_2;

foreach (var name in new[] { "SourceWidth", "SourceHeight" })
{
    var property = metadata.Properties.Single(p => p.Name == name);
    var accessors = new[] { property.GetMethod, property.SetMethod };
    var field = metadata.Fields.Single(f => f.Name == $"<{name}>k__BackingField");
    foreach (var method in AllMethods(assembly.MainModule))
    {
        if (!method.HasBody || accessors.Contains(method))
            continue;
        if (method.Body.Instructions.Any(i =>
            i.Operand is MethodReference mr && mr.DeclaringType.FullName == metadata.FullName &&
                accessors.Any(a => a.Name == mr.Name) ||
            i.Operand is FieldReference fr && fr.DeclaringType.FullName == metadata.FullName &&
                fr.Name == field.Name))
            throw new InvalidOperationException($"{name} is still referenced by {method.FullName}");
    }
    metadata.Properties.Remove(property);
    foreach (var accessor in accessors)
        metadata.Methods.Remove(accessor);
    metadata.Fields.Remove(field);
}

foreach (var methodName in new[] { "GetRegionAnchor", "CalculateDefaultRegionAnchor", "WriteAnchorMetadataIfMissing" })
{
    var method = mod.Methods.Single(m => m.Name == methodName);
    foreach (var parameter in method.Parameters)
    {
        if (parameter.Name == "sourceWidth") parameter.Name = "originalRegionWidth";
        if (parameter.Name == "sourceHeight") parameter.Name = "originalRegionHeight";
    }
}

var melonInfo = assembly.CustomAttributes.Single(a => a.AttributeType.FullName == "MelonLoader.MelonInfoAttribute");
if (melonInfo.ConstructorArguments.Count < 2 ||
    melonInfo.ConstructorArguments[1].Value is not string oldDisplayName ||
    !oldDisplayName.Contains("SkinForge", StringComparison.Ordinal))
    throw new InvalidOperationException("Unexpected MelonInfo display name");
melonInfo.ConstructorArguments[1] = new CustomAttributeArgument(assembly.MainModule.TypeSystem.String, "SkinForger");

if (assembly.MainModule.Types.SelectMany(t => t.Methods)
    .Where(m => m.HasBody)
    .SelectMany(m => m.Body.Instructions)
    .Any(i => i.OpCode == OpCodes.Ldstr && i.Operand is string s &&
        s.Contains("SkeletonMecanim.Initialize", StringComparison.Ordinal)))
    throw new InvalidOperationException("Forbidden SkeletonMecanim.Initialize patch marker found");

var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
Directory.CreateDirectory(outputDirectory);
assembly.Write(args[1]);
Console.WriteLine($"Input SHA256: {actualInputHash}");
Console.WriteLine($"Output SHA256: {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1])))}");
Console.WriteLine("Anchor JSON v2; v1 extra dimensions ignored; sidecar anchor applies at any PNG size; Melon display name SkinForger.");
return 0;

static void RequireCall(MethodDefinition method, string name)
{
    if (!method.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == name))
        throw new InvalidOperationException($"{method.Name} no longer calls {name}");
}

static IEnumerable<MethodDefinition> AllMethods(ModuleDefinition module)
{
    foreach (var type in module.Types)
        foreach (var method in Methods(type))
            yield return method;
}

static IEnumerable<MethodDefinition> Methods(TypeDefinition type)
{
    foreach (var method in type.Methods)
        yield return method;
    foreach (var nested in type.NestedTypes)
        foreach (var method in Methods(nested))
            yield return method;
}
