using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length != 2)
    throw new ArgumentException("Usage: ReleaseVerify <verified-input.dll> <release.dll>");

using var baseline = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { InMemory = true });
using var release = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { InMemory = true });
var oldMod = baseline.MainModule.Types.Single(t => t.BaseType?.FullName == "MelonLoader.MelonMod");
var newMod = release.MainModule.Types.Single(t => t.BaseType?.FullName == "MelonLoader.MelonMod");

Check(release.Name.Name == "SkinForger.Melon", "assembly name");
Check(release.MainModule.Name == "SkinForger.Melon.dll", "module name");
Check(newMod.FullName == "SkinForger.SkinForgerMod", "main namespace and type");
var melonInfo = release.CustomAttributes.Single(a => a.AttributeType.FullName == "MelonLoader.MelonInfoAttribute");
Check(((TypeReference)melonInfo.ConstructorArguments[0].Value).FullName == newMod.FullName, "MelonInfo main type");
Check((string)melonInfo.ConstructorArguments[1].Value == "SkinForger", "MelonInfo display name");
var melonGame = release.CustomAttributes.Single(a => a.AttributeType.FullName == "MelonLoader.MelonGameAttribute");
Check((string)melonGame.ConstructorArguments[0].Value == "PeroPeroGames" &&
      (string)melonGame.ConstructorArguments[1].Value == "MuseDash", "MelonGame identity");

Check(baseline.MainModule.AssemblyReferences.Select(r => r.FullName)
    .SequenceEqual(release.MainModule.AssemblyReferences.Select(r => r.FullName)), "dependencies unchanged");
Check(oldMod.NestedTypes.Count == newMod.NestedTypes.Count, "nested type count unchanged");

var oldMethods = Methods(oldMod).ToArray();
var newMethods = Methods(newMod).ToArray();
Check(oldMethods.Length == newMethods.Length, "method count unchanged");
for (var i = 0; i < oldMethods.Length; i++)
{
    var before = oldMethods[i];
    var after = newMethods[i];
    var expectedName = before.Name == "ApplySkinForgeTextureSettings" ? "ApplySkinForgerTextureSettings" : before.Name;
    Check(after.Name == expectedName, $"method identity {expectedName}");
    Check(before.HasBody == after.HasBody, $"body presence {expectedName}");
    if (!before.HasBody) continue;
    Check(before.Body.Instructions.Select(x => x.OpCode.Code)
        .SequenceEqual(after.Body.Instructions.Select(x => x.OpCode.Code)), $"opcode sequence {expectedName}");
    Check(before.Body.Instructions.Count == after.Body.Instructions.Count, $"instruction count {expectedName}");
}

var strings = Methods(newMod).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
    .Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).ToArray();
Check(strings.Any(s => s.Contains("|rt=", StringComparison.Ordinal)), "runtime texture cache key preserved");
Check(strings.Any(s => s.Contains("full atlas PNG fallback is disabled", StringComparison.OrdinalIgnoreCase)), "Atlas PNG fallback remains disabled");
Check(!strings.Any(s => s.Contains("SkeletonMecanim.Initialize", StringComparison.Ordinal)), "no SkeletonMecanim.Initialize patch");
Check(strings.Any(s => s.Contains("High-frequency Harmony patches: none", StringComparison.Ordinal)), "high-frequency patch audit preserved");
Check(strings.Any(s => s.Contains("SkinForgeFpsLimiter.json", StringComparison.Ordinal)), "legacy FPS config path preserved");

var anchorMetadata = newMod.NestedTypes.Single(t => t.Name == "RegionAnchorMetadata");
var expectedProperties = new[] { "AnchorX", "AnchorY", "Origin", "Rotation", "Version" };
Check(anchorMetadata.Properties.Select(p => p.Name).Order().SequenceEqual(expectedProperties.Order()), "JSON v2 anchor schema");
Check(!anchorMetadata.Properties.Any(p => p.Name is "SourceWidth" or "SourceHeight"), "no JSON source dimensions");

var oldNames = new List<string>();
if (release.Name.Name.Contains("MuseDashSkinChanger", StringComparison.OrdinalIgnoreCase)) oldNames.Add("assembly name");
oldNames.AddRange(release.MainModule.Types.Where(t => t.Namespace.Contains("MuseDashSkinChanger", StringComparison.OrdinalIgnoreCase)).Select(t => "namespace:" + t.FullName));
oldNames.AddRange(release.CustomAttributes.SelectMany(a => a.ConstructorArguments)
    .Where(a => a.Value is string s && s.Contains("MuseDashSkinChanger", StringComparison.OrdinalIgnoreCase))
    .Select(a => "attribution:" + a.Value));
Console.WriteLine("OLD_NAME_OCCURRENCES:");
foreach (var item in oldNames.Distinct()) Console.WriteLine(item);
Check(oldNames.All(x => x.StartsWith("attribution:", StringComparison.Ordinal)), "old project name only remains in attribution");

Console.WriteLine("Release identity and no-core-change checks passed.");

static IEnumerable<MethodDefinition> Methods(TypeDefinition type) =>
    type.Methods.Concat(type.NestedTypes.SelectMany(Methods));

static void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + label);
    Console.WriteLine("PASS: " + label);
}
