using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;

namespace SkinForger.AtlasBuildAccelerator;

/// <summary>
/// Bit-identical fast paths for SkinForger Main's per-pixel helpers (Main's IL read with Cecil on 2026-11-13; Main
/// itself is not modified). Each runs after every other prefix (Priority.Last):
///  - CalculateTextureChecksum: its value is only formatted into DebugLog strings (GetTexture x2,
///    ApplyRegionPngToAtlas x1) and DebugLog prints nothing unless ReplacementDebugEnabled. The full-atlas checksum
///    (GetPixels of the whole atlas + 4 per-pixel calls) ran after EVERY region write. Returns 0 when debug is off.
///  - CreateTransparentTexture / EnsureAtlasCapacity: a new Il2CppStructArray is already zero; the per-pixel
///    "write Color(0,0,0,0)" loop is skipped, everything else is identical.
///  - PremultiplyAlphaInPlace / RotateTexture: same float math / same index mapping on Span views.
///  - CalculateAlphaBounds: Texture.get_width/get_height native calls inside the per-pixel loops are replaced by a
///    value read once per call (transpiler); the loop body is unchanged.
/// </summary>
public sealed class AtlasBuildAcceleratorMod : global::SkinForger.Core.SfModule
{
    private const string Prefix = "[SkinForge][AtlasBuildAccelerator]";
    private static FieldInfo? _replacementDebug, _verboseTexture;
    private static MethodInfo? _applySettings, _createTransparent;
    private static readonly Dictionary<string, (int Calls, double Ms)> Stats = new();

    /// <summary>How often the atlas had to be grown this session (diagnostic).</summary>
    private static int _growths;

    /// <summary>Reused destination for atlas growth; see EnsureCapacityPrefix. Never shared across threads
    /// because every atlas build runs on Unity's main thread.</summary>
    private static Color32[]? _growBuffer;

    public override void OnInitializeMelon()
    {
        if (StandDownForAuthoredMain()) return;
        try
        {
            Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForgerMod");
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            _replacementDebug = host.GetField("ReplacementDebugEnabled", flags) ?? throw new MissingFieldException("ReplacementDebugEnabled");
            _verboseTexture = host.GetField("VerboseTextureDiagnosticsEnabled", flags) ?? throw new MissingFieldException("VerboseTextureDiagnosticsEnabled");
            // 2026-11-15 speed: Main ships with ReplacementDebugEnabled = true. That prints hundreds of log lines per
            // load (per-slot "FINAL REGION MAPPING", "FORK FINAL TRACE", per-renderer candidates) AND keeps the
            // full-atlas checksum after every region write (ChecksumPrefix below only skips it when debug is off).
            // Everything gated by the flag is diagnostics (checked in Main's IL). Opt back in with the file
            // UserData\SkinForger\verbose-diagnostics.txt.
            string verboseFile = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SkinForger", "verbose-diagnostics.txt");
            bool verbose = File.Exists(verboseFile);
            if (!verbose) _replacementDebug.SetValue(null, false);
            MelonLogger.Msg($"{Prefix} Main diagnostics {(verbose ? "ON (verbose-diagnostics.txt)" : "off (faster loading; create UserData/SkinForger/verbose-diagnostics.txt to turn on)")}");
            _applySettings = Find(host, "ApplySkinForgerTextureSettings", typeof(Texture2D));
            _createTransparent = Find(host, "CreateTransparentTexture", typeof(int), typeof(int), typeof(string));

            Patch(Find(host, "CalculateTextureChecksum", typeof(Texture2D)), nameof(ChecksumPrefix));
            Patch(_createTransparent, nameof(CreateTransparentPrefix));
            Patch(Find(host, "EnsureAtlasCapacity", typeof(Texture2D), typeof(int), typeof(int)), nameof(EnsureCapacityPrefix));
            Patch(Find(host, "PremultiplyAlphaInPlace", typeof(Texture2D)), nameof(PremultiplyPrefix));
            Patch(Find(host, "RotateTexture", typeof(Texture2D), typeof(int)), nameof(RotatePrefix));
            MethodInfo bounds = Find(host, "CalculateAlphaBounds", typeof(Texture2D));
            HarmonyInstance.Patch(bounds,
                prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AtlasBuildAcceleratorMod), nameof(BoundsPrefix))),
                transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AtlasBuildAcceleratorMod), nameof(CacheSizeTranspiler))));
            MelonLogger.Msg($"{Prefix} ready version=0.3.0 checksum=skipped-when-debug-off zeroFill=skipped premultiply,rotate=span alphaBounds=cached-size");
            try { BuildProfiler.Install(HarmonyInstance); }
            catch (Exception exception) { MelonLogger.Warning($"{Prefix} BuildProfiler not installed: {exception.GetBaseException().Message}"); }
        }
        catch (Exception exception)
        {
            MelonLogger.Error($"{Prefix} DISABLED {exception.GetBaseException().Message}");
        }
        InstallDumpProgress();
    }

    // ---------------------------------------------------------------- Dump progress (log only)

    private const string DumpPrefix = "[SkinForge][DumpProgress]";
    private static MethodInfo? _getCostumeName;
    private static int _dumpWritten;
    private static long _dumpStart;
    private static bool _dumpActive;

    private void InstallDumpProgress()
    {
        try
        {
            Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForgerMod");
            Type session = AccessTools.TypeByName("SkinForger.DumpSystem.DumpSessionManager") ?? throw new TypeLoadException("DumpSessionManager");
            _getCostumeName = Find(host, "GetCostumeName", typeof(int), typeof(bool));
            HarmonyInstance.Patch(Find(session, "Begin", typeof(string)), postfix: Hook(nameof(DumpBeginPostfix)));
            HarmonyInstance.Patch(Find(host, "DumpCostume", typeof(int), typeof(bool)), prefix: Hook(nameof(DumpCostumePrefix)));
            HarmonyInstance.Patch(Find(session, "EncodeAndWritePng", typeof(string), typeof(Texture2D)), postfix: Hook(nameof(DumpWritePostfix)));
            HarmonyInstance.Patch(Find(session, "End", Type.EmptyTypes), prefix: Hook(nameof(DumpEndPrefix)));
            // PMA export (BUG-060): Main dumps CreateEditableRegionTexture(readable atlas copy) as-is. For a premultiplied
            // atlas those pixels are premultiplied, and Main premultiplies again on import -> fringe around every part.
            // Same decision as import (ShouldUsePremultipliedAlpha(GetMaterial(attachment))), same math as the verified
            // LiveAttachmentDump 1.7.x export (disabled on the current Main by its hash gate).
            _shouldUsePma = Find(host, "ShouldUsePremultipliedAlpha", typeof(Material));
            _getMaterial = host.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(m => m.Name == "GetMaterial" && m.GetParameters().Length == 1);
            MethodInfo extract = host.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(m => m.Name == "ExtractAttachment" && m.GetParameters().Length == 3);
            HarmonyInstance.Patch(extract, prefix: Hook(nameof(ExtractPrefix)), finalizer: Hook(nameof(ExtractFinalizer)));
            HarmonyInstance.Patch(Find(session, "EncodeAndWritePng", typeof(string), typeof(Texture2D)),
                prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AtlasBuildAcceleratorMod), nameof(DumpUnpremultiplyPrefix))) { priority = HarmonyLib.Priority.First });
            MelonLogger.Msg($"{DumpPrefix} ready (one line per written PNG; PMA atlases exported as straight alpha)");
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{DumpPrefix} disabled {exception.GetBaseException().Message}");
        }
    }

    private static HarmonyMethod Hook(string name) => new(AccessTools.DeclaredMethod(typeof(AtlasBuildAcceleratorMod), name));

    private static double DumpSeconds() =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - _dumpStart) / (double)System.Diagnostics.Stopwatch.Frequency;

    private static void DumpBeginPostfix(string mode)
    {
        _dumpWritten = 0;
        _dumpStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _dumpActive = true;
        MelonLogger.Msg($"{DumpPrefix} START mode={mode}");
    }

    private static void DumpCostumePrefix(int index, bool elfin)
    {
        string name;
        try { name = _getCostumeName?.Invoke(null, new object[] { index, elfin }) as string ?? index.ToString(); }
        catch { name = index.ToString(); }
        MelonLogger.Msg($"{DumpPrefix} costume={name} kind={(elfin ? "elfin" : "character")}");
    }

    private static void DumpWritePostfix(string path)
    {
        int n = System.Threading.Interlocked.Increment(ref _dumpWritten);
        string shortPath = path ?? string.Empty;
        try
        {
            string? dir = Path.GetFileName(Path.GetDirectoryName(shortPath));
            shortPath = string.IsNullOrEmpty(dir) ? Path.GetFileName(shortPath) : dir + "/" + Path.GetFileName(shortPath);
        }
        catch { }
        MelonLogger.Msg($"{DumpPrefix} #{n} t={DumpSeconds():0.0}s {shortPath}");
    }

    private static void DumpEndPrefix()
    {
        if (!_dumpActive) return;
        _dumpActive = false;
        MelonLogger.Msg($"{DumpPrefix} DONE written={_dumpWritten} elapsed={DumpSeconds():0.0}s straightAlphaExports={_unpremultiplied} " +
            "(dump finished; safe to continue)");
        _unpremultiplied = 0;
    }

    private static MethodInfo? _shouldUsePma, _getMaterial;
    private static bool _exportPma;
    private static int _unpremultiplied;

    private static void ExtractPrefix(object __0)
    {
        _exportPma = false;
        try
        {
            if (_getMaterial?.Invoke(null, new[] { __0 }) is Material material && material != null)
                _exportPma = _shouldUsePma?.Invoke(null, new object[] { material }) is true;
        }
        catch { _exportPma = false; }
    }

    private static Exception? ExtractFinalizer(Exception? __exception)
    {
        _exportPma = false;
        return __exception;
    }

    /// <summary>The texture is Main's temporary CreateEditableRegionTexture copy (destroyed right after the write).</summary>
    private static void DumpUnpremultiplyPrefix(Texture2D __1)
    {
        if (!_exportPma || __1 == null) return;
        var pixels = __1.GetPixels32();
        Span<Color32> span = pixels.AsSpan();
        for (int i = 0; i < span.Length; i++)
        {
            ref Color32 p = ref span[i];
            byte a = p.a;
            if (a == 0) { p.r = p.g = p.b = 0; }
            else if (a != 255)
            {
                p.r = (byte)Math.Min(255, (p.r * 255 + a / 2) / a);
                p.g = (byte)Math.Min(255, (p.g * 255 + a / 2) / a);
                p.b = (byte)Math.Min(255, (p.b * 255 + a / 2) / a);
            }
        }
        __1.SetPixels32(pixels);
        __1.Apply(false, false);
        _unpremultiplied++;
    }

    public override void OnDeinitializeMelon()
    {
        lock (Stats)
            MelonLogger.Msg($"{Prefix} SUMMARY " + string.Join(" ", Stats.Select(p => $"{p.Key}:calls={p.Value.Calls},ms={p.Value.Ms:0.#}")));
    }

    private void Patch(MethodInfo target, string prefix) =>
        HarmonyInstance.Patch(target, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(AtlasBuildAcceleratorMod), prefix)) { priority = HarmonyLib.Priority.Last });

    private static MethodInfo Find(Type host, string name, params Type[] parameters) =>
        host.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, parameters, null)
        ?? throw new MissingMethodException(host.FullName, name);

    private static void Record(string name, long start)
    {
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        lock (Stats) { Stats.TryGetValue(name, out var s); Stats[name] = (s.Calls + 1, s.Ms + ms); }
    }

    private static bool Flag(FieldInfo? field) => field?.GetValue(null) is true;

    // ---------------------------------------------------------------- CalculateTextureChecksum

    private static bool ChecksumPrefix(ref ulong __result)
    {
        if (Flag(_replacementDebug)) return true;   // debug output on: keep Main's exact checksum
        __result = 0;
        lock (Stats) { Stats.TryGetValue("checksumSkipped", out var s); Stats["checksumSkipped"] = (s.Calls + 1, s.Ms); }
        return false;
    }

    // ---------------------------------------------------------------- CreateTransparentTexture

    private static bool CreateTransparentPrefix(int width, int height, string textureName, ref Texture2D __result)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
        // Color32 is 4 bytes per pixel where Color is 16, and a fresh native array is already zero, so this
        // is the same transparent texture for a quarter of the memory traffic (2026-11-17: 290 calls of this
        // cost 5.8 s during a cat-skin build). Apply without mipmaps, keeping the texture readable.
        texture.SetPixels32(new Il2CppStructArray<Color32>((long)width * height));
        texture.Apply(false, false);
        texture.name = textureName;
        _applySettings!.Invoke(null, new object[] { texture });
        __result = texture;
        Record("createTransparent", start);
        return false;
    }

    // ---------------------------------------------------------------- EnsureAtlasCapacity

    private static bool EnsureCapacityPrefix(Texture2D atlas, int requiredWidth, int requiredHeight)
    {
        if (Flag(_verboseTexture)) return true;   // Main logs a verbose line in that mode
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        if (atlas == null) return false;
        int oldWidth = atlas.width, oldHeight = atlas.height;
        if (oldWidth >= requiredWidth && oldHeight >= requiredHeight) return false;
        // 2026-11-17: one growth of the cat atlas (2048x3603) cost about 440 ms, and a build did 135 of
        // them — 59.3 s of a 69.8 s freeze. Two things are changed here, both of them local:
        //   1. Color is 16 bytes per pixel, Color32 is 4: the same atlas moves 29 MB instead of 118 MB.
        //   2. The old code allocated a SECOND full array of zeros and wrote the whole atlas twice (a
        //      zero-fill, then the old block). One array, written once, does the same: a fresh array is
        //      already zero, so the rows added stay transparent without being written.
        //
        // EVERY array here is a MANAGED Color32[]. Il2CppStructArray is not a System.Array; it only
        // converts to one, and that conversion copies the whole thing. Putting such a conversion inside
        // the row loop is what hung the game on the first attempt at this fix (2026-11-17 22:17): each of
        // the 3603 rows marshalled both arrays in full. Convert once on the way in, once on the way out.
        int width = Math.Max(oldWidth, requiredWidth);
        int height = Math.Max(oldHeight, requiredHeight);

        Color32[] previous = atlas.GetPixels32();                 // one conversion
        atlas.Resize(width, height, TextureFormat.RGBA32, false);
        _applySettings!.Invoke(null, new object[] { atlas });

        // Rows are bottom-up in Unity, so copying row y to row y keeps the old content where it was
        // (bottom-left) and adds the new space above it — exactly what SetPixels(0, 0, oldW, oldH) did.
        // The destination buffer is kept and reused (2026-11-19). A character atlas of 2048x5950 is 12.2
        // million pixels, i.e. a 48 MB array, and this ran 23 times in one session: over a gigabyte handed
        // to the large-object heap for buffers that are thrown away immediately. Keeping one and clearing
        // only what the copy does not cover costs nothing and removes both the allocation and the collection.
        int total = checked(width * height);
        if (_growBuffer == null || _growBuffer.Length < total) _growBuffer = new Color32[total];
        Color32[] next = _growBuffer;

        int copyWidth = Math.Min(oldWidth, width), copyHeight = Math.Min(oldHeight, height);
        // Everything above the copied block, and the strip to the right of it, must be transparent: the
        // texture's contents after Resize are undefined, and a placement's 2px padding would otherwise
        // sample whatever happened to be in memory.
        Array.Clear(next, copyHeight * width, total - copyHeight * width);
        if (copyWidth < width)
            for (int y = 0; y < copyHeight; y++)
                Array.Clear(next, y * width + copyWidth, width - copyWidth);
        for (int y = 0; y < copyHeight; y++)
            Array.Copy(previous, (long)y * oldWidth, next, (long)y * width, copyWidth);

        atlas.SetPixels32(new Il2CppStructArray<Color32>(next));   // one conversion
        atlas.Apply(false, false);
        Record("ensureCapacity", start);

        // Why does a build grow the atlas over a hundred times when CharacterPolygonPackedRectRepair reports
        // "preallocation=applied"? The first few growths say it: if each one adds just one image's worth of
        // rows, the pre-sized atlas and the real placement disagree about the order or the sizes.
        // Three lines, once per session, then only the count.
        _growths++;
        if (_growths <= 3)
            MelonLogger.Msg($"{Prefix} grow #{_growths}: {oldWidth}x{oldHeight} -> {width}x{height} " +
                            $"(asked for {requiredWidth}x{requiredHeight}, +{height - oldHeight} rows)");
        return false;
    }

    // ---------------------------------------------------------------- PremultiplyAlphaInPlace

    private static bool PremultiplyPrefix(Texture2D texture)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        if (texture == null) return false;
        var pixels = texture.GetPixels();
        Span<Color> span = pixels.AsSpan();
        for (int i = 0; i < span.Length; i++)
        {
            ref Color c = ref span[i];
            float a = c.a;
            c.r *= a; c.g *= a; c.b *= a;
        }
        texture.SetPixels(pixels);
        texture.Apply();
        Record("premultiply", start);
        return false;
    }

    // ---------------------------------------------------------------- RotateTexture

    private static int NormalizeDegrees(int degrees)
    {
        degrees %= 360;
        if (degrees < 0) degrees += 360;
        return degrees is 90 or 180 or 270 ? degrees : 0;
    }

    private static bool RotatePrefix(Texture2D source, int degreesClockwise, ref Texture2D __result)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        int d = NormalizeDegrees(degreesClockwise);
        int sw = source.width, sh = source.height;
        int width = d is 90 or 270 ? sh : sw;
        int height = d is 90 or 270 ? sw : sh;
        // The destination used to come from CreateTransparentTexture, which fills it with zeros and uploads
        // it — and then every one of those pixels is overwritten below, because a rotation covers the whole
        // target exactly once. That made each rotation write the texture twice and upload it twice (366
        // rotations in a load). The texture is created here with the same format and the same settings
        // Main applies, and filled once.
        var result = new Texture2D(width, height, TextureFormat.RGBA32, false, true) { name = source.name };
        _applySettings!.Invoke(null, new object[] { result });
        // Color32 (4 bytes) instead of Color (16), and the loop runs over MANAGED arrays: one conversion in,
        // one out. 2026-11-17: 276 rotations cost 6.3 s during a cat-skin build, and every one of them wrote
        // the texture twice (the transparent texture it starts from is filled, then overwritten here).
        Color32[] src = source.GetPixels32();
        var dst = new Color32[(long)width * height];
        for (int y = 0; y < sh; y++)
        for (int x = 0; x < sw; x++)
        {
            int tx, ty;
            switch (d)
            {
                case 90: tx = sh - 1 - y; ty = x; break;
                case 180: tx = sw - 1 - x; ty = sh - 1 - y; break;
                case 270: tx = y; ty = sw - 1 - x; break;
                default: tx = x; ty = y; break;
            }
            dst[ty * width + tx] = src[y * sw + x];
        }
        result.SetPixels32(new Il2CppStructArray<Color32>(dst));
        result.Apply(false, false);
        __result = result;
        Record("rotate", start);
        return false;
    }

    // ---------------------------------------------------------------- CalculateAlphaBounds

    [ThreadStatic] private static IntPtr _sizePointer;
    [ThreadStatic] private static int _width, _height;

    private static void BoundsPrefix() => _sizePointer = IntPtr.Zero;

    public static int CachedWidth(Texture texture) { Load(texture); return _width; }
    public static int CachedHeight(Texture texture) { Load(texture); return _height; }

    private static void Load(Texture texture)
    {
        IntPtr pointer = texture.Pointer;
        if (pointer == _sizePointer) return;
        _width = texture.width;
        _height = texture.height;
        _sizePointer = pointer;
    }

    private static IEnumerable<CodeInstruction> CacheSizeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo getWidth = AccessTools.PropertyGetter(typeof(Texture), nameof(Texture.width));
        MethodInfo getHeight = AccessTools.PropertyGetter(typeof(Texture), nameof(Texture.height));
        MethodInfo cachedWidth = AccessTools.Method(typeof(AtlasBuildAcceleratorMod), nameof(CachedWidth));
        MethodInfo cachedHeight = AccessTools.Method(typeof(AtlasBuildAcceleratorMod), nameof(CachedHeight));
        foreach (CodeInstruction instruction in instructions)
        {
            if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && instruction.operand is MethodInfo method)
            {
                if (method == getWidth) { instruction.opcode = OpCodes.Call; instruction.operand = cachedWidth; }
                else if (method == getHeight) { instruction.opcode = OpCodes.Call; instruction.operand = cachedHeight; }
            }
            yield return instruction;
        }
    }

    /// <summary>
    /// This module patches the OLD Main, which writes its atlas one region at a time. The authored Main
    /// composes the whole atlas in one pass instead, where the primitives this accelerates (per-region grow, rotate, transparent page) are not how the composer builds.
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
