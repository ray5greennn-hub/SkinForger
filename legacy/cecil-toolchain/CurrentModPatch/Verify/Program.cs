using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json;

if (args.Length != 3)
    throw new ArgumentException("Usage: Verify <baseline.dll> <candidate.dll> <skin-folder>");

using var baseline = AssemblyDefinition.ReadAssembly(args[0]);
using var candidate = AssemblyDefinition.ReadAssembly(args[1]);
var oldMod = baseline.MainModule.GetType("MuseDashSkinChangerMelon.SkinChangerMod")!;
var newMod = candidate.MainModule.GetType("MuseDashSkinChangerMelon.SkinChangerMod")!;
var metadata = newMod.NestedTypes.Single(t => t.Name == "RegionAnchorMetadata");
var expected = new[] { "Version", "Origin", "AnchorX", "AnchorY", "Rotation" };
Check(metadata.Properties.Select(p => p.Name).Order().SequenceEqual(expected.Order()), "v2 metadata properties");
Check(!metadata.Fields.Any(f => f.Name.Contains("SourceWidth") || f.Name.Contains("SourceHeight")), "no saved dimensions");

var writer = newMod.Methods.Single(m => m.Name == "WriteAnchorMetadataIfMissing");
var versionCall = writer.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "set_Version");
Check(versionCall.Previous?.OpCode == OpCodes.Ldc_I4_2, "new sidecars write version 2");
Check(!writer.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name.Contains("Source")), "writer has no source dimensions");

var anchorReader = newMod.Methods.Single(m => m.Name == "GetRegionAnchor");
Check(anchorReader.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "get_AnchorX") &&
      anchorReader.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "get_AnchorY"),
      "anchor values are read");
Check(!anchorReader.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name.Contains("Source")),
      "legacy dimensions are not read");

var apply = newMod.Methods.Single(m => m.Name == "ApplyRegionPngToAtlas");
foreach (var name in new[] { "FindManifestEntry", "get_OriginalWidth", "get_OriginalHeight", "get_width", "get_height", "AllocateExpandedPackedRect" })
    Check(apply.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == name), "expansion retains " + name);
var attachment = newMod.Methods.Single(m => m.Name == "ApplyRegionPlacementToAttachment");
foreach (var name in new[] { "get_NormalWidth", "get_NormalHeight", "get_AnchorX", "get_AnchorY", "CalculateAttachmentCenterFromAnchor" })
    Check(attachment.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == name), "attachment retains " + name);
Check(attachment.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == "get_AnchorFromSidecar") == 2,
    "sidecar anchor controls x and y independently of expansion");
var sidecarCalls = attachment.Body.Instructions.Where(i =>
    i.Operand is MethodReference m && m.Name == "get_AnchorFromSidecar").ToArray();
foreach (var call in sidecarCalls)
{
    var load = call.Previous;
    var expandedBranch = load?.Previous;
    var sidecarBranch = call.Next;
    Check(load?.OpCode == OpCodes.Ldarg_2 && expandedBranch?.OpCode == OpCodes.Brtrue_S &&
          sidecarBranch?.OpCode == OpCodes.Brtrue &&
          ReferenceEquals(expandedBranch.Operand, sidecarBranch.Operand),
          "sidecar and expansion select the same calculated center");
}

foreach (var name in new[] { "ApplyRegionPngToAtlas", "GetTexture", "ReloadSkins", "TryBuildSynthesizedAtlasForSkeleton" })
{
    var before = oldMod.Methods.Single(m => m.Name == name);
    var after = newMod.Methods.Single(m => m.Name == name);
    Check(before.Body.Instructions.Select(i => i.OpCode.Code).SequenceEqual(after.Body.Instructions.Select(i => i.OpCode.Code)),
        name + " opcode sequence unchanged");
}

var melonInfo = candidate.CustomAttributes.Single(a => a.AttributeType.FullName == "MelonLoader.MelonInfoAttribute");
Check((string)melonInfo.ConstructorArguments[1].Value == "SkinForger", "Melon display name");
Check(!newMod.Methods.Any(m => m.Name.Contains("Mecanim", StringComparison.OrdinalIgnoreCase)), "no Mecanim method added");

var v1 = """{"version":1,"origin":"top-left","sourceWidth":110,"sourceHeight":81,"anchorX":1.0265732,"anchorY":77.35274,"rotation":0.0}""";
var oldJson = JsonConvert.DeserializeObject<AnchorSidecar>(v1)!;
Check(oldJson.Version == 1 && oldJson.AnchorX == 1.0265732f && oldJson.AnchorY == 77.35274f,
    "v1 anchor preserved while legacy dimensions are ignored");
var newJson = JsonConvert.SerializeObject(new AnchorSidecar { AnchorX = 0, AnchorY = 81, Rotation = 0 }, Formatting.Indented);
Check(newJson.Contains("\"version\": 2") && !newJson.Contains("sourceWidth") && !newJson.Contains("sourceHeight"),
    "v2 JSON shape");

foreach (var size in new[] { (110, 81), (130, 136), (200, 200) })
{
    var (x, y) = AttachmentCenter(size.Item1, size.Item2, 0, 81, 1, 1);
    Check(x == size.Item1 / 2f && y == 81 - size.Item2 / 2f,
        $"top-left anchor uses current {size.Item1}x{size.Item2} canvas");
}

var pngs = Directory.GetFiles(args[2], "*.png");
Check(pngs.Length == 21, "active skin has 21 individual PNGs");
foreach (var file in pngs)
{
    var bytes = File.ReadAllBytes(file);
    Check(bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "valid PNG " + Path.GetFileName(file));
    var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
    var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
    Check(width > 0 && height > 0, "current dimensions " + Path.GetFileName(file));
    if (Path.GetFileName(file).Equals("wing_l.png", StringComparison.OrdinalIgnoreCase))
        Check(width == 130 && height == 136, "wing_l current PNG is 130x136");
    var sidecarPath = Path.ChangeExtension(file, ".json");
    if (File.Exists(sidecarPath))
        Check(JsonConvert.DeserializeObject<AnchorSidecar>(File.ReadAllText(sidecarPath)) is not null,
            "legacy sidecar readable " + Path.GetFileName(sidecarPath));
}

Console.WriteLine("Offline schema, compatibility, sizing, and IL regression checks passed. Gameplay not tested.");
return;

static (float X, float Y) AttachmentCenter(int width, int height, float anchorX, float anchorY,
    float unitsPerPixelX, float unitsPerPixelY) =>
    (-(anchorX - width / 2f) * unitsPerPixelX, -(height / 2f - anchorY) * unitsPerPixelY);

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

public sealed class AnchorSidecar
{
    [JsonProperty("version")] public int Version { get; set; } = 2;
    [JsonProperty("origin")] public string Origin { get; set; } = "top-left";
    [JsonProperty("anchorX")] public float AnchorX { get; set; }
    [JsonProperty("anchorY")] public float AnchorY { get; set; }
    [JsonProperty("rotation")] public float Rotation { get; set; }
}
