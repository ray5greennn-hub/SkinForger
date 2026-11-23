using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace SkinForger.BattleAtlasEdgeIsolation;

/// <summary>
/// Adds one texel of duplicated edge color around fresh Character Gameplay
/// placements. The host remains responsible for rotation, PMA, placement,
/// Region metadata, UVs, geometry and the final deferred texture upload.
/// </summary>
public sealed class BattleAtlasEdgeIsolationMod : global::SkinForger.Core.SfModule
{
    private const string Prefix = "[SkinForge][BattleAtlasEdgeIsolation]";
    private const int RequiredPadding = 2;
    private const int MaxSummaryLines = 12;
    private const int MaxLogChars = 16 * 1024;

    [ThreadStatic]
    private static Stack<BuildScope>? _buildScopes;

    private static PropertyInfo? _paddingProperty;
    private static PropertyInfo? _baseHeightProperty;
    private static bool _warningWritten;
    private static int _summaryLines;
    private static int _logChars;

    /// <summary>
    /// This module patches the OLD Main, which writes its atlas one region at a time. The authored Main
    /// composes the whole atlas in one pass instead, where the composer leaves a 2px gap around every placed image, so no neighbour can bleed in.
    ///
    /// Patching it anyway would be worse than useless: the transpilers here look for IL the authored Main
    /// does not contain, and teaching it to write region by region would bring back the very design the cut
    /// removes — with it the 2026-10-01 endlessly growing atlas and the 2026-10-04 overlapping rectangles.
    /// So when the authored Main is the one loaded, this module stands down and says so.
    /// </summary>
    private static bool StandDownForAuthoredMain()
    {
        if (global::SkinForger.MainBridge.MainHost.Generation != global::SkinForger.MainBridge.MainGeneration.Authored)
            return false;
        MelonLoader.MelonLogger.Msg($"{Prefix} not needed: the authored Main composes the atlas itself");
        return true;
    }

    public override void OnInitializeMelon()
    {
        if (StandDownForAuthoredMain()) return;
        Type? host = AccessTools.TypeByName("SkinForger.SkinForgerMod");
        MethodInfo? build = Find(host, "TryBuildSynthesizedAtlasForSkeleton", 5);
        MethodInfo? getState = Find(host, "GetSynthesizedAtlasState", 1);
        MethodInfo? applyPng = Find(host, "ApplyRegionPngToAtlas", 8);
        if (build == null || getState == null || applyPng == null)
        {
            MelonLogger.Warning($"{Prefix} disabled: compatible Character atlas methods were not found.");
            return;
        }

        Type stateType = getState.ReturnType;
        _paddingProperty = stateType.GetProperty("Padding", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        _baseHeightProperty = stateType.GetProperty("BaseHeight", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (_paddingProperty?.CanWrite != true || _baseHeightProperty?.CanRead != true)
        {
            MelonLogger.Warning($"{Prefix} disabled: synthesized atlas state contract is incompatible.");
            return;
        }

        HarmonyInstance.Patch(
            build,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(BattleAtlasEdgeIsolationMod), nameof(BuildPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(BattleAtlasEdgeIsolationMod), nameof(BuildPostfix))),
            finalizer: new HarmonyMethod(AccessTools.Method(typeof(BattleAtlasEdgeIsolationMod), nameof(BuildFinalizer))));
        HarmonyInstance.Patch(
            getState,
            postfix: new HarmonyMethod(AccessTools.Method(typeof(BattleAtlasEdgeIsolationMod), nameof(GetStatePostfix))));
        HarmonyInstance.Patch(
            applyPng,
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(BattleAtlasEdgeIsolationMod), nameof(ApplyPngTranspiler))));

        MelonLogger.Msg($"{Prefix} ready version=0.1.1: Character Gameplay fresh placements use {RequiredPadding}px spacing and 1px post-rotation/PMA edge extrusion; explicit Preview and Elfin builds excluded; summaries={MaxSummaryLines}/{MaxLogChars}chars.");
    }

    private static MethodInfo? Find(Type? type, string name, int parameterCount) =>
        type?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(method => method.Name == name && method.GetParameters().Length == parameterCount);

    [HarmonyPriority(HarmonyLib.Priority.First)]
    private static void BuildPrefix(bool __4)
    {
        bool contextKnown = TryReadPreviewExecution(out bool preview);
        bool active = EdgeIsolationPolicy.ShouldActivate(__4, contextKnown, preview);
        (_buildScopes ??= new Stack<BuildScope>()).Push(new BuildScope(active, __4, contextKnown, preview));
    }

    [HarmonyPriority(HarmonyLib.Priority.Last)]
    private static void BuildPostfix(Texture2D? __result)
    {
        BuildScope? scope = CurrentScope;
        if (scope == null)
            return;

        scope.ResultAtlasId = __result == null ? 0 : __result.GetInstanceID();
        scope.ResultWidth = __result == null ? 0 : __result.width;
        scope.ResultHeight = __result == null ? 0 : __result.height;
        scope.ResultTracked = scope.ResultAtlasId != 0 && scope.AtlasBaseHeights.ContainsKey(scope.ResultAtlasId);
        if (scope.Active)
            WriteSummary(scope, "completed", null);
    }

    [HarmonyPriority(HarmonyLib.Priority.Last)]
    private static Exception? BuildFinalizer(Exception? __exception)
    {
        if (_buildScopes is { Count: > 0 })
        {
            BuildScope scope = _buildScopes.Peek();
            if (__exception != null && scope.Active)
                WriteSummary(scope, "failed", __exception.GetBaseException().Message);
            _buildScopes.Pop();
        }
        return __exception;
    }

    [HarmonyPriority(HarmonyLib.Priority.Last)]
    private static void GetStatePostfix(Texture2D __0, object? __result)
    {
        BuildScope? scope = CurrentScope;
        if (scope is not { Active: true } || __0 == null || __result == null)
            return;

        try
        {
            int previousPadding = Convert.ToInt32(_paddingProperty!.GetValue(__result));
            _paddingProperty!.SetValue(__result, RequiredPadding);
            int baseHeight = Convert.ToInt32(_baseHeightProperty!.GetValue(__result));
            scope.AtlasBaseHeights[__0.GetInstanceID()] = baseHeight;
            scope.StatePreparations++;
            scope.PaddingBefore ??= previousPadding;
            scope.PaddingAfter = RequiredPadding;
        }
        catch (Exception ex)
        {
            WarnOnce("atlas state preparation failed: " + ex.GetBaseException().Message);
        }
    }

    [HarmonyPriority(HarmonyLib.Priority.First)]
    private static IEnumerable<CodeInstruction> ApplyPngTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        MethodInfo setPixels = AccessTools.Method(
            typeof(Texture2D),
            nameof(Texture2D.SetPixels),
            new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(Il2CppStructArray<Color>) })
            ?? throw new MissingMethodException(typeof(Texture2D).FullName, nameof(Texture2D.SetPixels));
        MethodInfo getPixels = AccessTools.Method(typeof(Texture2D), nameof(Texture2D.GetPixels), Type.EmptyTypes)
            ?? throw new MissingMethodException(typeof(Texture2D).FullName, nameof(Texture2D.GetPixels));
        MethodInfo helper = AccessTools.Method(typeof(BattleAtlasEdgeIsolationMod), nameof(ExtrudeFreshPlacement))
            ?? throw new MissingMethodException(nameof(ExtrudeFreshPlacement));

        int write = code.FindIndex(instruction => instruction.Calls(setPixels));
        if (write < 0 || code.Count(instruction => instruction.Calls(setPixels)) != 1)
            throw new InvalidOperationException("Expected one packed-region Texture2D.SetPixels call.");

        CodeInstruction xLoad = FindRectLoad(code, write, "get_X");
        CodeInstruction yLoad = FindRectLoad(code, write, "get_Y");
        CodeInstruction widthLoad = FindRectLoad(code, write, "get_Width");
        CodeInstruction heightLoad = FindRectLoad(code, write, "get_Height");
        int sourcePixels = FindPreviousCall(code, write, getPixels, 16);
        if (sourcePixels <= 0 || !IsLoadLocal(code[sourcePixels - 1]))
            throw new InvalidOperationException("Packed replacement Texture2D local was not found.");
        CodeInstruction sourceLoad = Clone(code[sourcePixels - 1]);

        MethodInfo xGetter = (MethodInfo)code[FindPreviousNamedCall(code, write, "get_X", 20)].operand;
        MethodInfo yGetter = (MethodInfo)code[FindPreviousNamedCall(code, write, "get_Y", 20)].operand;
        MethodInfo widthGetter = (MethodInfo)code[FindPreviousNamedCall(code, write, "get_Width", 20)].operand;
        MethodInfo heightGetter = (MethodInfo)code[FindPreviousNamedCall(code, write, "get_Height", 20)].operand;

        code.InsertRange(write + 1, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            xLoad, new CodeInstruction(OpCodes.Call, xGetter),
            yLoad, new CodeInstruction(OpCodes.Call, yGetter),
            widthLoad, new CodeInstruction(OpCodes.Call, widthGetter),
            heightLoad, new CodeInstruction(OpCodes.Call, heightGetter),
            sourceLoad,
            new CodeInstruction(OpCodes.Call, helper)
        });
        return code;
    }

    private static CodeInstruction FindRectLoad(IReadOnlyList<CodeInstruction> code, int before, string getterName)
    {
        int getter = FindPreviousNamedCall(code, before, getterName, 20);
        if (getter <= 0 || !IsLoadLocalAddress(code[getter - 1]))
            throw new InvalidOperationException($"PixelRect {getterName} receiver was not found.");
        return Clone(code[getter - 1]);
    }

    private static int FindPreviousNamedCall(IReadOnlyList<CodeInstruction> code, int before, string name, int distance)
    {
        for (int index = before - 1; index >= Math.Max(0, before - distance); index--)
            if ((code[index].opcode == OpCodes.Call || code[index].opcode == OpCodes.Callvirt) &&
                code[index].operand is MethodInfo method && method.Name == name)
                return index;
        throw new InvalidOperationException($"Expected {name} call was not found near packed-region write.");
    }

    private static int FindPreviousCall(IReadOnlyList<CodeInstruction> code, int before, MethodInfo method, int distance)
    {
        for (int index = before - 1; index >= Math.Max(0, before - distance); index--)
            if (code[index].Calls(method))
                return index;
        return -1;
    }

    private static bool IsLoadLocal(CodeInstruction instruction) =>
        instruction.opcode == OpCodes.Ldloc || instruction.opcode == OpCodes.Ldloc_S ||
        instruction.opcode == OpCodes.Ldloc_0 || instruction.opcode == OpCodes.Ldloc_1 ||
        instruction.opcode == OpCodes.Ldloc_2 || instruction.opcode == OpCodes.Ldloc_3;

    private static bool IsLoadLocalAddress(CodeInstruction instruction) =>
        instruction.opcode == OpCodes.Ldloca || instruction.opcode == OpCodes.Ldloca_S;

    private static CodeInstruction Clone(CodeInstruction source) => new(source.opcode, source.operand);

    private static void ExtrudeFreshPlacement(Texture2D atlas, int x, int y, int width, int height, Texture2D packedSource)
    {
        BuildScope? scope = CurrentScope;
        if (scope is not { Active: true } || atlas == null || packedSource == null || width <= 0 || height <= 0)
            return;
        scope.ExtrusionCalls++;
        if (!scope.AtlasBaseHeights.TryGetValue(atlas.GetInstanceID(), out int baseHeight))
        {
            scope.MissingStateSkips++;
            return;
        }

        // Fresh Character placements begin above the immutable source atlas.
        // Fail closed if the placement does not have the reserved one-texel ring.
        if (x < 1 || y <= baseHeight || x + width >= atlas.width || y + height >= atlas.height)
        {
            scope.NonFreshOrUnreservedSkips++;
            return;
        }

        Il2CppStructArray<Color> pixels = packedSource.GetPixels();
        if (pixels.Length != (long)width * height)
        {
            scope.PixelMismatchSkips++;
            WarnOnce($"packed pixel length mismatch: {pixels.Length} != {width}x{height}.");
            return;
        }

        var left = new Il2CppStructArray<Color>(height);
        var right = new Il2CppStructArray<Color>(height);
        for (int row = 0; row < height; row++)
        {
            left[row] = pixels[EdgeIsolationMath.LeftIndex(row, width)];
            right[row] = pixels[EdgeIsolationMath.RightIndex(row, width)];
        }

        var bottom = new Il2CppStructArray<Color>(width);
        var top = new Il2CppStructArray<Color>(width);
        for (int column = 0; column < width; column++)
        {
            bottom[column] = pixels[EdgeIsolationMath.BottomIndex(column)];
            top[column] = pixels[EdgeIsolationMath.TopIndex(column, width, height)];
        }

        atlas.SetPixels(x - 1, y, 1, height, left);
        atlas.SetPixels(x + width, y, 1, height, right);
        atlas.SetPixels(x, y - 1, width, 1, bottom);
        atlas.SetPixels(x, y + height, width, 1, top);

        var corner = new Il2CppStructArray<Color>(1L);
        WriteCorner(atlas, corner, x - 1, y - 1, pixels[EdgeIsolationMath.BottomLeftIndex()]);
        WriteCorner(atlas, corner, x + width, y - 1, pixels[EdgeIsolationMath.BottomRightIndex(width)]);
        WriteCorner(atlas, corner, x - 1, y + height, pixels[EdgeIsolationMath.TopLeftIndex(width, height)]);
        WriteCorner(atlas, corner, x + width, y + height, pixels[EdgeIsolationMath.TopRightIndex(width, height)]);
        scope.ExtrusionsWritten++;
        scope.LastAtlasId = atlas.GetInstanceID();
        scope.LastRect = $"{x},{y},{width}x{height}";
    }

    private static void WriteCorner(Texture2D atlas, Il2CppStructArray<Color> buffer, int x, int y, Color color)
    {
        buffer[0] = color;
        atlas.SetPixels(x, y, 1, 1, buffer);
    }

    private static bool TryReadPreviewExecution(out bool preview)
    {
        preview = false;
        try
        {
            Type? contextType = AccessTools.TypeByName("SkinForger.CharacterReplacement.ReplacementExecutionContext");
            object? current = contextType?
                .GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?
                .GetValue(null);
            PropertyInfo? previewProperty = current?.GetType()
                .GetProperty("Preview", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (current == null || previewProperty == null)
                return false;
            preview = Convert.ToBoolean(previewProperty.GetValue(current));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static BuildScope? CurrentScope =>
        _buildScopes is { Count: > 0 } ? _buildScopes.Peek() : null;

    private static void WarnOnce(string message)
    {
        if (_warningWritten)
            return;
        _warningWritten = true;
        MelonLogger.Warning($"{Prefix} {message}");
    }

    private static void WriteSummary(BuildScope scope, string status, string? error)
    {
        if (scope.SummaryWritten || _summaryLines >= MaxSummaryLines)
            return;

        string context = scope.ContextKnown ? (scope.Preview ? "preview" : "gameplay") : "missing-gameplay-entry";
        string message =
            $"{Prefix} event=BUILD status={status} active={scope.Active} elfin={scope.Elfin} context={context} " +
            $"states={scope.StatePreparations} padding={scope.PaddingBefore?.ToString() ?? "UNKNOWN"}->{scope.PaddingAfter?.ToString() ?? "UNCHANGED"} " +
            $"extrusionCalls={scope.ExtrusionCalls} extrusionWrites={scope.ExtrusionsWritten} " +
            $"skips=missing-state:{scope.MissingStateSkips},unreserved:{scope.NonFreshOrUnreservedSkips},pixel-mismatch:{scope.PixelMismatchSkips} " +
            $"resultAtlas={scope.ResultAtlasId}[{scope.ResultWidth}x{scope.ResultHeight}] resultTracked={scope.ResultTracked} " +
            $"lastWriteAtlas={scope.LastAtlasId} lastRect={scope.LastRect ?? "<none>"}" +
            (string.IsNullOrEmpty(error) ? string.Empty : $" error={error}");
        if (_logChars + message.Length > MaxLogChars)
            return;

        scope.SummaryWritten = true;
        _summaryLines++;
        _logChars += message.Length;
        MelonLogger.Msg(message);
    }

    private sealed class BuildScope
    {
        public BuildScope(bool active, bool elfin, bool contextKnown, bool preview)
        {
            Active = active;
            Elfin = elfin;
            ContextKnown = contextKnown;
            Preview = preview;
        }

        public bool Active { get; }
        public bool Elfin { get; }
        public bool ContextKnown { get; }
        public bool Preview { get; }
        public Dictionary<int, int> AtlasBaseHeights { get; } = new();
        public int StatePreparations { get; set; }
        public int? PaddingBefore { get; set; }
        public int? PaddingAfter { get; set; }
        public int ExtrusionCalls { get; set; }
        public int ExtrusionsWritten { get; set; }
        public int MissingStateSkips { get; set; }
        public int NonFreshOrUnreservedSkips { get; set; }
        public int PixelMismatchSkips { get; set; }
        public int ResultAtlasId { get; set; }
        public int ResultWidth { get; set; }
        public int ResultHeight { get; set; }
        public bool ResultTracked { get; set; }
        public int LastAtlasId { get; set; }
        public string? LastRect { get; set; }
        public bool SummaryWritten { get; set; }
    }
}

internal static class EdgeIsolationMath
{
    public static int LeftIndex(int row, int width) => row * width;
    public static int RightIndex(int row, int width) => row * width + width - 1;
    public static int BottomIndex(int column) => column;
    public static int TopIndex(int column, int width, int height) => (height - 1) * width + column;
    public static int BottomLeftIndex() => 0;
    public static int BottomRightIndex(int width) => width - 1;
    public static int TopLeftIndex(int width, int height) => (height - 1) * width;
    public static int TopRightIndex(int width, int height) => width * height - 1;

}
