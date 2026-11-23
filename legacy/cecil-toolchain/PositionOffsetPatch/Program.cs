using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

const string expectedInputHash = "41F1BA46F2CDEC627AFE0FEA40E1C9E0132DB07AA73C4814509654C9FCF892C0";

if (args.Length != 2)
    throw new ArgumentException("Usage: PositionOffsetPatch <SkinForger.Melon.dll> <candidate.dll>");

var inputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0])));
if (!inputHash.Equals(expectedInputHash, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException($"Unexpected input SHA256: {inputHash}");

using var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { InMemory = true });
var module = assembly.MainModule;
var mod = module.GetType("SkinForger.SkinForgerMod")
    ?? throw new InvalidOperationException("SkinForgerMod not found");
var metadata = mod.NestedTypes.Single(t => t.Name == "RegionAnchorMetadata");
var anchor = mod.NestedTypes.Single(t => t.Name == "RegionAnchor");
var placement = mod.NestedTypes.Single(t => t.Name == "RegionPlacement");

var metadataOffsetX = AddFloatProperty(metadata, "OffsetX", "offsetX", json: true);
var metadataOffsetY = AddFloatProperty(metadata, "OffsetY", "offsetY", json: true);
var anchorOffsetX = AddFloatProperty(anchor, "OffsetX", null, json: false);
var anchorOffsetY = AddFloatProperty(anchor, "OffsetY", null, json: false);
var placementOffsetX = AddFloatProperty(placement, "OffsetX", null, json: false);
var placementOffsetY = AddFloatProperty(placement, "OffsetY", null, json: false);

PatchMetadataRead(mod.Methods.Single(m => m.Name == "GetRegionAnchor"),
    metadataOffsetX.GetMethod, metadataOffsetY.GetMethod, anchorOffsetX.SetMethod, anchorOffsetY.SetMethod);
PatchPlacementCopy(mod.Methods.Single(m => m.Name == "ApplyRegionPngToAtlas"),
    anchorOffsetX.GetMethod, anchorOffsetY.GetMethod, placementOffsetX.SetMethod, placementOffsetY.SetMethod);
PatchAttachmentPosition(mod.Methods.Single(m => m.Name == "ApplyRegionPlacementToAttachment"),
    placementOffsetX.GetMethod, placementOffsetY.GetMethod);

VerifyNoForbiddenRuntimeChanges(module);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
assembly.Write(args[1]);

var outputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1])));
Console.WriteLine($"Input SHA256: {inputHash}");
Console.WriteLine($"Output SHA256: {outputHash}");
Console.WriteLine("Position offset chain: JSON -> RegionAnchor -> RegionPlacement -> RegionAttachment x/y.");
Console.WriteLine("Direction: +offsetX = +local X (right), +offsetY = -local Y (down).");

static PropertyDefinition AddFloatProperty(TypeDefinition type, string name, string? jsonName, bool json)
{
    if (type.Properties.Any(p => p.Name == name))
        throw new InvalidOperationException($"{type.Name}.{name} already exists");

    var module = type.Module;
    var field = new FieldDefinition($"<{name}>k__BackingField", FieldAttributes.Private, module.TypeSystem.Single);
    type.Fields.Add(field);

    var get = new MethodDefinition($"get_{name}",
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
        module.TypeSystem.Single);
    var getIl = get.Body.GetILProcessor();
    getIl.Append(getIl.Create(OpCodes.Ldarg_0));
    getIl.Append(getIl.Create(OpCodes.Ldfld, field));
    getIl.Append(getIl.Create(OpCodes.Ret));
    type.Methods.Add(get);

    var set = new MethodDefinition($"set_{name}",
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
        module.TypeSystem.Void);
    set.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Single));
    var setIl = set.Body.GetILProcessor();
    setIl.Append(setIl.Create(OpCodes.Ldarg_0));
    setIl.Append(setIl.Create(OpCodes.Ldarg_1));
    setIl.Append(setIl.Create(OpCodes.Stfld, field));
    setIl.Append(setIl.Create(OpCodes.Ret));
    type.Methods.Add(set);

    var property = new PropertyDefinition(name, PropertyAttributes.None, module.TypeSystem.Single)
    {
        GetMethod = get,
        SetMethod = set
    };
    if (json)
    {
        var template = type.Properties.Single(p => p.Name == "AnchorX").CustomAttributes
            .Single(a => a.AttributeType.FullName == "Newtonsoft.Json.JsonPropertyAttribute");
        var attribute = new CustomAttribute(module.ImportReference(template.Constructor));
        attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, jsonName));
        property.CustomAttributes.Add(attribute);
    }
    type.Properties.Add(property);
    return property;
}

static void PatchMetadataRead(MethodDefinition method, MethodReference metadataGetX, MethodReference metadataGetY,
    MethodReference anchorSetX, MethodReference anchorSetY)
{
    var anchorYCall = method.Body.Instructions.Single(i =>
        i.Operand is MethodReference mr && mr.Name == "set_AnchorY" && mr.DeclaringType.Name == "RegionAnchor");
    var anchorLocal = ResolveLoadedLocal(method, anchorYCall.Previous?.Previous?.Previous)
        ?? throw new InvalidOperationException("Anchor local not found");
    var metadataLocal = ResolveLoadedLocal(method, anchorYCall.Previous?.Previous)
        ?? throw new InvalidOperationException("Metadata local not found");
    var il = method.Body.GetILProcessor();
    var cursor = anchorYCall;
    foreach (var instruction in new[]
    {
        il.Create(OpCodes.Ldloc, anchorLocal), il.Create(OpCodes.Ldloc, metadataLocal),
        il.Create(OpCodes.Callvirt, metadataGetX), il.Create(OpCodes.Callvirt, anchorSetX),
        il.Create(OpCodes.Ldloc, anchorLocal), il.Create(OpCodes.Ldloc, metadataLocal),
        il.Create(OpCodes.Callvirt, metadataGetY), il.Create(OpCodes.Callvirt, anchorSetY)
    })
    {
        il.InsertAfter(cursor, instruction);
        cursor = instruction;
    }
}

static void PatchPlacementCopy(MethodDefinition method, MethodReference anchorGetX, MethodReference anchorGetY,
    MethodReference placementSetX, MethodReference placementSetY)
{
    var anchorYCall = method.Body.Instructions.Single(i =>
        i.Operand is MethodReference mr && mr.Name == "set_AnchorY" && mr.DeclaringType.Name == "RegionPlacement");
    var anchorLocal = anchorYCall.Previous?.Previous?.Operand as VariableDefinition
        ?? throw new InvalidOperationException("Placement anchor local not found");
    var il = method.Body.GetILProcessor();
    var cursor = anchorYCall;
    foreach (var instruction in new[]
    {
        il.Create(OpCodes.Dup), il.Create(OpCodes.Ldloc, anchorLocal),
        il.Create(OpCodes.Callvirt, anchorGetX), il.Create(OpCodes.Callvirt, placementSetX),
        il.Create(OpCodes.Dup), il.Create(OpCodes.Ldloc, anchorLocal),
        il.Create(OpCodes.Callvirt, anchorGetY), il.Create(OpCodes.Callvirt, placementSetY)
    })
    {
        il.InsertAfter(cursor, instruction);
        cursor = instruction;
    }
}

static void PatchAttachmentPosition(MethodDefinition method, MethodReference placementGetX, MethodReference placementGetY)
{
    var calculate = method.Body.Instructions.Single(i =>
        i.Operand is MethodReference mr && mr.Name == "CalculateAttachmentCenterFromAnchor");
    var xLocal = calculate.Previous?.Previous?.Operand as VariableDefinition
        ?? throw new InvalidOperationException("Calculated X local not found");
    var yLocal = calculate.Previous?.Operand as VariableDefinition
        ?? throw new InvalidOperationException("Calculated Y local not found");
    var unitX = calculate.Previous?.Previous?.Previous?.Previous?.Operand as VariableDefinition
        ?? throw new InvalidOperationException("Unit X local not found");
    var unitY = calculate.Previous?.Previous?.Previous?.Operand as VariableDefinition
        ?? throw new InvalidOperationException("Unit Y local not found");
    var il = method.Body.GetILProcessor();
    var cursor = calculate;
    foreach (var instruction in new[]
    {
        il.Create(OpCodes.Ldloc, xLocal), il.Create(OpCodes.Ldarg_2), il.Create(OpCodes.Callvirt, placementGetX),
        il.Create(OpCodes.Ldloc, unitX), il.Create(OpCodes.Mul), il.Create(OpCodes.Add), il.Create(OpCodes.Stloc, xLocal),
        il.Create(OpCodes.Ldloc, yLocal), il.Create(OpCodes.Ldarg_2), il.Create(OpCodes.Callvirt, placementGetY),
        il.Create(OpCodes.Ldloc, unitY), il.Create(OpCodes.Mul), il.Create(OpCodes.Sub), il.Create(OpCodes.Stloc, yLocal)
    })
    {
        il.InsertAfter(cursor, instruction);
        cursor = instruction;
    }
}

static void VerifyNoForbiddenRuntimeChanges(ModuleDefinition module)
{
    var methods = AllMethods(module).ToArray();
    if (methods.Any(m => m.Name.Contains("SkeletonMecanim", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("Forbidden SkeletonMecanim patch method found");
    if (methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
        .Any(i => i.OpCode == OpCodes.Ldstr && i.Operand is string s &&
            s.Contains("full atlas PNG fallback is enabled", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("Full Atlas PNG fallback marker found");
}

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

static IEnumerable<MethodDefinition> AllMethods(ModuleDefinition module) => module.Types.SelectMany(Methods);
static IEnumerable<MethodDefinition> Methods(TypeDefinition type) =>
    type.Methods.Concat(type.NestedTypes.SelectMany(Methods));
