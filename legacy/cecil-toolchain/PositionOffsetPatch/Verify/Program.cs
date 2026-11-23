using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json;

if (args.Length != 2) throw new ArgumentException("Usage: Verify <baseline.dll> <candidate.dll>");
using var baseline = AssemblyDefinition.ReadAssembly(args[0]);
using var candidate = AssemblyDefinition.ReadAssembly(args[1]);
var oldMod = baseline.MainModule.GetType("SkinForger.SkinForgerMod")!;
var newMod = candidate.MainModule.GetType("SkinForger.SkinForgerMod")!;

foreach (var typeName in new[] { "RegionAnchorMetadata", "RegionAnchor", "RegionPlacement" })
{
    var oldType = oldMod.NestedTypes.Single(t => t.Name == typeName);
    var newType = newMod.NestedTypes.Single(t => t.Name == typeName);
    Check(newType.Properties.Count == oldType.Properties.Count + 2, typeName + " adds only two properties");
    Check(newType.Properties.Any(p => p.Name == "OffsetX") && newType.Properties.Any(p => p.Name == "OffsetY"),
        typeName + " has OffsetX/OffsetY");
}

var metadata = newMod.NestedTypes.Single(t => t.Name == "RegionAnchorMetadata");
Check(JsonName(metadata, "OffsetX") == "offsetX" && JsonName(metadata, "OffsetY") == "offsetY",
    "JSON property names are offsetX/offsetY");
Check(metadata.Properties.Select(p => p.Name).Order().SequenceEqual(
    new[] { "AnchorX", "AnchorY", "OffsetX", "OffsetY", "Origin", "Rotation", "Version" }.Order()),
    "v2 metadata schema contains anchor, offset, origin, rotation only");

var reader = newMod.Methods.Single(m => m.Name == "GetRegionAnchor");
Check(Calls(reader, "RegionAnchorMetadata", "get_OffsetX") == 1 && Calls(reader, "RegionAnchorMetadata", "get_OffsetY") == 1,
    "sidecar offsets are read once");
Check(Calls(reader, "RegionAnchor", "set_OffsetX") == 1 && Calls(reader, "RegionAnchor", "set_OffsetY") == 1,
    "sidecar offsets are copied without modifying anchors");

var atlas = newMod.Methods.Single(m => m.Name == "ApplyRegionPngToAtlas");
Check(Calls(atlas, "RegionAnchor", "get_OffsetX") == 1 && Calls(atlas, "RegionAnchor", "get_OffsetY") == 1,
    "offsets enter runtime placement once");
Check(Calls(atlas, "RegionPlacement", "set_OffsetX") == 1 && Calls(atlas, "RegionPlacement", "set_OffsetY") == 1,
    "runtime placement stores offsets");

var attachment = newMod.Methods.Single(m => m.Name == "ApplyRegionPlacementToAttachment");
Check(Calls(attachment, "RegionPlacement", "get_OffsetX") == 1 && Calls(attachment, "RegionPlacement", "get_OffsetY") == 1,
    "final RegionAttachment consumes each offset once");
var offsetXCall = FindCall(attachment, "RegionPlacement", "get_OffsetX");
var offsetYCall = FindCall(attachment, "RegionPlacement", "get_OffsetY");
var offsetXCodes = NextCodes(offsetXCall, 3).ToArray();
var offsetYCodes = NextCodes(offsetYCall, 3).ToArray();
Console.WriteLine("Offset X IL: " + string.Join(", ", offsetXCodes));
Console.WriteLine("Offset Y IL: " + string.Join(", ", offsetYCodes));
Check(IsLoadLocal(offsetXCodes[0]) && offsetXCodes[1] == Code.Mul && offsetXCodes[2] == Code.Add,
    "+offsetX adds positive local X pixels");
Check(IsLoadLocal(offsetYCodes[0]) && offsetYCodes[1] == Code.Mul && offsetYCodes[2] == Code.Sub,
    "+offsetY subtracts local Y and therefore moves down");
Check(FindCall(attachment, "SkinForgerMod", "CalculateAttachmentCenterFromAnchor").Offset < offsetXCall.Offset,
    "offset is applied after anchor center calculation");

foreach (var name in new[] { "GetTexture", "ApplyRegionPlacementToRegion", "SetRegionUvs", "AllocateExpandedPackedRect", "TryBuildSynthesizedAtlasForSkeleton" })
{
    var before = oldMod.Methods.Single(m => m.Name == name);
    var after = newMod.Methods.Single(m => m.Name == name);
    Check(before.Body.Instructions.Select(i => i.OpCode.Code).SequenceEqual(after.Body.Instructions.Select(i => i.OpCode.Code)),
        name + " unchanged");
}

Check(!AllMethods(newMod).Any(m => m.Name.Contains("SkeletonMecanim", StringComparison.OrdinalIgnoreCase)),
    "no SkeletonMecanim patch");
Check(!AllMethods(newMod).Any(m => m.Name is "Update" or "LateUpdate" or "FixedUpdate"),
    "no per-frame mod method");
Check(!AllMethods(newMod).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
    .Any(i => i.OpCode == OpCodes.Ldstr && i.Operand is string s &&
        s.Contains("full atlas PNG fallback is enabled", StringComparison.OrdinalIgnoreCase)),
    "full Atlas PNG fallback remains disabled");

var oldJson = """{"version":2,"origin":"top-left","anchorX":0.0,"anchorY":136.0,"rotation":0.0}""";
var oldSidecar = JsonConvert.DeserializeObject<Sidecar>(oldJson)!;
Check(oldSidecar.OffsetX == 0f && oldSidecar.OffsetY == 0f, "old JSON defaults offsets to zero");
var newJson = JsonConvert.SerializeObject(new Sidecar { AnchorX = 0, AnchorY = 136, OffsetX = 0, OffsetY = 0 }, Formatting.Indented);
Check(newJson.Contains("\"offsetX\": 0.0") && newJson.Contains("\"offsetY\": 0.0"), "new JSON writes both offsets");

var basePosition = Position(130, 136, 0, 136, 0, 0, 1, 1);
Check(basePosition == (65f, 68f), "Test 1: zero offset preserves existing anchor result");
Check(Position(130, 136, 0, 136, 0, 100, 1, 1) == (65f, -32f),
    "Test 2: +100 offsetY moves down 100 pixels");
Check(Position(130, 136, 0, 136, 100, 0, 1, 1) == (165f, 68f),
    "Test 3: +100 offsetX moves right 100 pixels");
Check(Position(130, 136, 0, 136, -100, -100, 1, 1) == (-35f, 168f),
    "Test 4: negative offsets move left and up");
Check(oldSidecar.AnchorX == 0f && oldSidecar.AnchorY == 136f, "offset compatibility does not change anchor values");

Console.WriteLine("Position Offset static and compatibility verification passed. Gameplay direction still requires visual validation.");

static string JsonName(TypeDefinition type, string property) =>
    (string)type.Properties.Single(p => p.Name == property).CustomAttributes
        .Single(a => a.AttributeType.FullName == "Newtonsoft.Json.JsonPropertyAttribute").ConstructorArguments[0].Value;
static int Calls(MethodDefinition method, string type, string name) => method.Body.Instructions.Count(i =>
    i.Operand is MethodReference mr && mr.DeclaringType.Name == type && mr.Name == name);
static Instruction FindCall(MethodDefinition method, string type, string name) => method.Body.Instructions.Single(i =>
    i.Operand is MethodReference mr && mr.DeclaringType.Name == type && mr.Name == name);
static IEnumerable<Code> NextCodes(Instruction instruction, int count)
{
    for (var i = instruction.Next; i != null && count-- > 0; i = i.Next) yield return i.OpCode.Code;
}
static bool IsLoadLocal(Code code) => code is Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3;
static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type) =>
    type.Methods.Concat(type.NestedTypes.SelectMany(AllMethods));
static (float X, float Y) Position(int width, int height, float anchorX, float anchorY,
    float offsetX, float offsetY, float unitsX, float unitsY)
{
    var x = -(anchorX - width / 2f) * unitsX;
    var y = -(height / 2f - anchorY) * unitsY;
    return (x + offsetX * unitsX, y - offsetY * unitsY);
}
static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

public sealed class Sidecar
{
    [JsonProperty("version")] public int Version { get; set; } = 2;
    [JsonProperty("origin")] public string Origin { get; set; } = "top-left";
    [JsonProperty("anchorX")] public float AnchorX { get; set; }
    [JsonProperty("anchorY")] public float AnchorY { get; set; }
    [JsonProperty("offsetX")] public float OffsetX { get; set; }
    [JsonProperty("offsetY")] public float OffsetY { get; set; }
    [JsonProperty("rotation")] public float Rotation { get; set; }
}
