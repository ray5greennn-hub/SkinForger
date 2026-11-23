using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Il2CppSpine;
using MelonLoader;

namespace SkinForger.ElfinFreshPackedRect;

/// <summary>
/// Makes the existing 97E allocator reserve a fresh rect for every Elfin
/// MeshAttachment replacement. No texture, region, attachment, or renderer is
/// implemented here; the original 97E code performs all allocation and writes.
/// </summary>
public sealed class ElfinFreshPackedRectMod : global::SkinForger.Core.SfModule
{
    private const string Prefix = "[SkinForge][ElfinFreshPackedRect]";

    [ThreadStatic]
    private static Stack<bool>? _elfinBuilds;

    public override void OnInitializeMelon()
    {
        if (StandDownForAuthoredMain()) return;
        Type? host = AccessTools.TypeByName("SkinForger.SkinForgerMod");
        MethodInfo? build = Find(host, "TryBuildSynthesizedAtlasForSkeleton", 5);
        MethodInfo? apply = Find(host, "ApplyRegionPngToAtlas", 8);
        if (build == null || apply == null)
        {
            MelonLogger.Warning($"{Prefix} disabled: compatible 97E allocation methods were not found.");
            return;
        }

        HarmonyInstance.Patch(build,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(ElfinFreshPackedRectMod), nameof(BuildPrefix))),
            finalizer: new HarmonyMethod(AccessTools.Method(typeof(ElfinFreshPackedRectMod), nameof(BuildFinalizer))));
        HarmonyInstance.Patch(apply,
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(ElfinFreshPackedRectMod), nameof(ApplyTranspiler))));

        MelonLogger.Msg($"{Prefix} ready: Elfin full-canvas MeshAttachments use the existing fresh-rect allocator.");
    }

    private static MethodInfo? Find(Type? type, string name, int parameterCount) =>
        type?.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == name && method.GetParameters().Length == parameterCount);

    private static void BuildPrefix(bool __4) => (_elfinBuilds ??= new Stack<bool>()).Push(__4);

    private static Exception? BuildFinalizer(Exception? __exception)
    {
        if (_elfinBuilds is { Count: > 0 }) _elfinBuilds.Pop();
        return __exception;
    }

    private static bool ShouldForceFreshRect(Attachment attachment) =>
        _elfinBuilds is { Count: > 0 } && _elfinBuilds.Peek() && attachment?.TryCast<MeshAttachment>() != null;

    private static IEnumerable<CodeInstruction> ApplyTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int allocatorCall = code.FindIndex(instruction =>
            instruction.Calls(AccessTools.Method(AccessTools.TypeByName("SkinForger.SkinForgerMod"), "AllocateExpandedPackedRect")));
        if (allocatorCall < 0) throw new InvalidOperationException("AllocateExpandedPackedRect call was not found.");

        int branch = -1;
        for (int index = allocatorCall - 1; index >= Math.Max(0, allocatorCall - 16); index--)
        {
            if (code[index].opcode == OpCodes.Brtrue || code[index].opcode == OpCodes.Brtrue_S)
            {
                branch = index;
                break;
            }
        }
        if (branch < 1 || !IsLoadLocal(code[branch - 1]))
            throw new InvalidOperationException("Existing expanded-condition branch was not found.");

        object? expandedLocal = code[branch - 1].operand;
        int store = -1;
        for (int index = branch - 2; index >= Math.Max(0, branch - 40); index--)
        {
            if (IsMatchingStore(code[index], code[branch - 1], expandedLocal))
            {
                store = index;
                break;
            }
        }
        if (store < 0) throw new InvalidOperationException("Existing expanded-condition local assignment was not found.");

        MethodInfo helper = AccessTools.Method(typeof(ElfinFreshPackedRectMod), nameof(ShouldForceFreshRect))
            ?? throw new MissingMethodException(nameof(ShouldForceFreshRect));
        code.InsertRange(store + 1, new[]
        {
            CloneLocalLoad(code[branch - 1]),
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Call, helper),
            new CodeInstruction(OpCodes.Or),
            CloneLocalStore(code[store])
        });
        return code;
    }

    private static bool IsLoadLocal(CodeInstruction instruction) =>
        instruction.opcode == OpCodes.Ldloc || instruction.opcode == OpCodes.Ldloc_S ||
        instruction.opcode == OpCodes.Ldloc_0 || instruction.opcode == OpCodes.Ldloc_1 ||
        instruction.opcode == OpCodes.Ldloc_2 || instruction.opcode == OpCodes.Ldloc_3;

    private static bool IsMatchingStore(CodeInstruction store, CodeInstruction load, object? local)
    {
        if (load.opcode == OpCodes.Ldloc || load.opcode == OpCodes.Ldloc_S)
            return (store.opcode == OpCodes.Stloc || store.opcode == OpCodes.Stloc_S) && Equals(store.operand, local);
        return (load.opcode == OpCodes.Ldloc_0 && store.opcode == OpCodes.Stloc_0) ||
               (load.opcode == OpCodes.Ldloc_1 && store.opcode == OpCodes.Stloc_1) ||
               (load.opcode == OpCodes.Ldloc_2 && store.opcode == OpCodes.Stloc_2) ||
               (load.opcode == OpCodes.Ldloc_3 && store.opcode == OpCodes.Stloc_3);
    }

    private static CodeInstruction CloneLocalLoad(CodeInstruction source) => new(source.opcode, source.operand);
    private static CodeInstruction CloneLocalStore(CodeInstruction source) => new(source.opcode, source.operand);

    /// <summary>
    /// This module patches the OLD Main, which writes its atlas one region at a time. The authored Main
    /// composes the whole atlas in one pass instead, where elfins are composed the same way as characters: a fresh rectangle, never the packed one.
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

}
