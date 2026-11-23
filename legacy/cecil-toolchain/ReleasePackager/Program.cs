using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

const string expectedInputHash = "0A87550AAB92CA5621C0D5A8041B76CA798193BE45BD0EBAF4227A75B6A227DC";

if (args.Length != 2)
    throw new ArgumentException("Usage: ReleasePackager <verified-input.dll> <release-output.dll>");

var inputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0])));
if (!inputHash.Equals(expectedInputHash, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException($"Unexpected input SHA256: {inputHash}");

using var assembly = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { InMemory = true });
var module = assembly.MainModule;
var melonMod = module.Types.Single(t => t.BaseType?.FullName == "MelonLoader.MelonMod");

assembly.Name.Name = "SkinForger.Melon";
module.Name = "SkinForger.Melon.dll";
melonMod.Namespace = "SkinForger";
melonMod.Name = "SkinForgerMod";

SetAssemblyString(assembly, "System.Reflection.AssemblyTitleAttribute", "SkinForger.Melon");
SetAssemblyString(assembly, "System.Reflection.AssemblyCompanyAttribute", "SkinForger");
SetAssemblyString(assembly, "System.Reflection.AssemblyProductAttribute", "SkinForger for Muse Dash");
SetAssemblyString(assembly, "System.Reflection.AssemblyDescriptionAttribute",
    "SkinForger, a GPL-3.0 fan-made Muse Dash skin mod based on the original MuseDashSkinChanger project.");

var melonInfo = assembly.CustomAttributes.Single(a => a.AttributeType.FullName == "MelonLoader.MelonInfoAttribute");
melonInfo.ConstructorArguments[1] = new CustomAttributeArgument(module.TypeSystem.String, "SkinForger");

foreach (var method in AllMethods(module).Where(m => m.HasBody))
{
    if (method.Name == "ApplySkinForgeTextureSettings")
        method.Name = "ApplySkinForgerTextureSettings";
    foreach (var instruction in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr))
    {
        if (instruction.Operand is not string value || value.Contains("SkinForgeFpsLimiter.json", StringComparison.Ordinal))
            continue;
        instruction.Operand = value.Replace("SkinForge", "SkinForger", StringComparison.Ordinal);
    }
}

if (AllMethods(module).Any(m => m.Name.Contains("SkeletonMecanim", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Forbidden SkeletonMecanim patch method found");
if (AllMethods(module).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
    .Any(i => i.OpCode == OpCodes.Ldstr && i.Operand is string s &&
        s.Contains("full atlas PNG fallback is enabled", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Full Atlas PNG fallback marker found");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
assembly.Write(args[1]);
Console.WriteLine($"Input SHA256: {inputHash}");
Console.WriteLine($"Output SHA256: {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1])))}");

static void SetAssemblyString(AssemblyDefinition assembly, string attributeName, string value)
{
    var attribute = assembly.CustomAttributes.Single(a => a.AttributeType.FullName == attributeName);
    attribute.ConstructorArguments[0] = new CustomAttributeArgument(assembly.MainModule.TypeSystem.String, value);
}

static IEnumerable<MethodDefinition> AllMethods(ModuleDefinition module) =>
    module.Types.SelectMany(Methods);

static IEnumerable<MethodDefinition> Methods(TypeDefinition type) =>
    type.Methods.Concat(type.NestedTypes.SelectMany(Methods));
