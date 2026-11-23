using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// Character voice / BGM replacement (2026-11-17). Sources, highest first:
///  1. SkinCreator\Sounds\&lt;Costume&gt;\&lt;Name&gt;.wav|ogg|mp3 and SkinCreator\Sounds\&lt;Costume&gt;\muted.txt (one name per line);
///  2. enabled Skin Library packages: audio/&lt;Costume&gt;/&lt;Name&gt;.ext and audio/&lt;Costume&gt;/muted.txt.
/// The game plays them through PeroTools2 AudioManager by asset name (PlayOneShot / PlayBGM / PlayLoop / Preload, string
/// or AudioClip overloads). Prefixes swap a known name / clip for the replacement (decoded with the game's own
/// RuntimeAudioClipLoader, kept with DontUnloadUnusedAsset) or for a short silent clip (muted: callbacks still run).
/// </summary>
internal static class SoundRuntime
{
    private const string Prefix = "[SkinForge][Sounds]";
    internal static string Root => Path.Combine(SkinForgerPaths.CreatorRoot, "Sounds");
    internal static readonly string[] Extensions = { ".wav", ".ogg", ".mp3" };

    private sealed record Source(string Name, string? File, byte[]? Data, string Extension, long Stamp, string Origin);

    private static Dictionary<string, Source> _replacements = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> _muted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (long Stamp, AudioClip Clip)> Clips = new(StringComparer.OrdinalIgnoreCase);
    private static AudioClip? _silence;
    private static string _signature = string.Empty;
    private static float _nextScan;
    [ThreadStatic] private static bool _bypass;
    private static readonly HashSet<string> LoggedPlays = new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsReplaced(string name) => _replacements.ContainsKey(name);
    internal static bool IsMuted(string name) => _muted.Contains(name);
    internal static string? ReplacementOrigin(string name) => _replacements.TryGetValue(name, out Source? s) ? s.Origin : null;
    internal static int ReplacedCount(IEnumerable<SoundEntry> entries) => entries.Count(e => _replacements.ContainsKey(e.Name) || _muted.Contains(e.Name));

    // ------------------------------------------------------------ index

    /// <summary>Every frame (cheap): rescans the creator folder every 2 s and when the mounted packages change.</summary>
    internal static void Poll(float time)
    {
        if (time < _nextScan) return;
        _nextScan = time + 2f;
        try { Rescan(force: false); } catch (Exception exception) { MelonLogger.Warning($"{Prefix} scan failed: {exception.GetBaseException().Message}"); }
    }

    internal static void Rescan(bool force)
    {
        var files = new List<(string Costume, string File)>();
        if (Directory.Exists(Root))
            foreach (string dir in Directory.GetDirectories(Root))
                foreach (string file in Directory.GetFiles(dir))
                    files.Add((Path.GetFileName(dir), file));
        string signature = PackageMounts.MountedSignature + "|" + PackageMounts.MountedLibraryVersion + "|" +
                           string.Join("|", files.Select(f => f.File + ":" + new FileInfo(f.File).Length + ":" + File.GetLastWriteTimeUtc(f.File).Ticks));
        if (!force && signature == _signature) return;
        _signature = signature;

        var replacements = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
        var muted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // packages first (lower priority), then the creator's own files on top
        foreach ((string path, byte[] data) in PackageMounts.PackageSounds)
        {
            string name = Path.GetFileName(path);
            if (name.Equals("muted.txt", StringComparison.OrdinalIgnoreCase)) { foreach (string line in ReadLines(data)) muted.Add(line); continue; }
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (!Extensions.Contains(ext)) continue;
            string key = Path.GetFileNameWithoutExtension(name);
            if (IsCensored(key)) continue;
            replacements[key] = new Source(key, null, data, ext, data.Length, "package");
        }
        foreach ((string costume, string file) in files)
        {
            string name = Path.GetFileName(file);
            if (name.Equals("muted.txt", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string line in ReadLines(File.ReadAllBytes(file))) { muted.Add(line); replacements.Remove(line); }
                continue;
            }
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (!Extensions.Contains(ext)) continue;
            string key = Path.GetFileNameWithoutExtension(name);
            if (IsCensored(key)) continue;
            replacements[key] = new Source(key, file, null, ext, File.GetLastWriteTimeUtc(file).Ticks ^ new FileInfo(file).Length, "SkinCreator");
            muted.Remove(key);
        }
        _replacements = replacements;
        _muted = muted;
        foreach (string gone in Clips.Keys.Where(k => !replacements.ContainsKey(k)).ToList()) Clips.Remove(gone);
        SyncBuffer();   // battle sounds already preloaded: replaced / given back right away
        MelonLogger.Msg($"{Prefix} {replacements.Count} replaced, {muted.Count} muted sound(s)");
    }

    private static bool IsCensored(string name) =>
        name.Contains("_hexie", StringComparison.OrdinalIgnoreCase) || global::SkinForger.HexieBlock.HexiePolicy.IsHexieName(name);

    private static IEnumerable<string> ReadLines(byte[] data) =>
        System.Text.Encoding.UTF8.GetString(data).Split('\n').Select(l => l.Trim().Trim('﻿')).Where(l => l.Length > 0 && !l.StartsWith("#"));

    // ------------------------------------------------------------ clips

    /// <summary>The clip to play instead of the game's one with this name; null = play the original.</summary>
    internal static AudioClip? Resolve(string? name)
    {
        if (_bypass || string.IsNullOrEmpty(name)) return null;
        string key = Path.GetFileNameWithoutExtension(name);
        if (_muted.Contains(key)) return Silence(MutedSeconds(key));
        return _replacements.TryGetValue(key, out Source? source) ? Load(source) : null;
    }

    internal static AudioClip? LoadReplacement(string name) => _replacements.TryGetValue(name, out Source? s) ? Load(s) : null;

    private static AudioClip? Load(Source source)
    {
        if (Clips.TryGetValue(source.Name, out var cached) && cached.Stamp == source.Stamp && cached.Clip != null && cached.Clip.Pointer != IntPtr.Zero)
            return cached.Clip;
        try
        {
            byte[] data = source.Data ?? File.ReadAllBytes(source.File!);
            var format = source.Extension switch
            {
                ".ogg" => Il2CppRuntimeAudioClipLoader.AudioFormat.ogg,
                ".mp3" => Il2CppRuntimeAudioClipLoader.AudioFormat.mp3,
                _ => Il2CppRuntimeAudioClipLoader.AudioFormat.wav,
            };
            var stream = new Il2CppSystem.IO.MemoryStream(data);
            AudioClip? clip = Il2CppRuntimeAudioClipLoader.Manager.Load(stream, format, source.Name, false, false, true);
            if (clip == null) { MelonLogger.Warning($"{Prefix} {source.Name}{source.Extension}: could not be decoded"); return null; }
            clip.name = source.Name;
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            if (cached.Clip != null && cached.Clip.Pointer != IntPtr.Zero) UnityEngine.Object.Destroy(cached.Clip);
            Clips[source.Name] = (source.Stamp, clip);
            MelonLogger.Msg($"{Prefix} loaded {source.Name}{source.Extension} ({source.Origin}, {clip.length:0.0}s)");
            return clip;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {source.Name}{source.Extension}: {exception.GetBaseException().Message}");
            return null;
        }
    }

    private static readonly Dictionary<int, AudioClip> Silences = new();
    private static readonly Dictionary<string, float> OriginalLengths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long a muted voice "plays" (its dialogue bubble stays while it plays, 2026-11-17 user rule): a voice with
    /// a dialogue line the creator edited lasts by the text length (about 3-8 s); otherwise as long as the game's
    /// own voice (no dialogue line or unchanged text). Voices without any length known: 0.05 s.
    /// </summary>
    internal static float MutedSeconds(string name)
    {
        try
        {
            TextEntry? text = TextHooks.TextOfVoice(name);
            if (text != null && text.Edited) return Math.Clamp(1.5f + text.Text.Length * 0.12f, 3f, 8f);
            if (!OriginalLengths.TryGetValue(name, out float seconds))
            {
                AudioClip? original = OriginalClip(name);
                seconds = original != null ? original.length : (text != null ? 3f : 0.05f);
                OriginalLengths[name] = seconds;
            }
            return seconds;
        }
        catch { return 0.05f; }
    }

    private static AudioClip Silence(float seconds)
    {
        int tenths = Math.Max(1, (int)Math.Round(seconds * 10f));
        if (Silences.TryGetValue(tenths, out AudioClip? cached) && cached != null && cached.Pointer != IntPtr.Zero) return cached;
        int samples = Math.Max(441, tenths * 4410);
        AudioClip clip = AudioClip.Create("SkinForger_Silence_" + tenths, samples, 1, 44100, false);
        clip.SetData(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(samples), 0);
        clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
        Silences[tenths] = clip;
        return clip;
    }

    // ------------------------------------------------------------ preview (Sound page)

    private static AudioSource? _preview;
    private static string _previewName = string.Empty;
    private static bool _previewReplacement, _previewPaused;

    /// <summary>The row / source the preview player holds (playing or paused).</summary>
    internal static bool IsCurrent(string name, bool replacement) =>
        _previewName == name && _previewReplacement == replacement && _preview != null && _preview.Pointer != IntPtr.Zero && _preview.clip != null;
    internal static bool IsPlaying(string name, bool replacement) => IsCurrent(name, replacement) && !_previewPaused && _preview!.isPlaying;
    internal static bool IsPaused(string name, bool replacement) => IsCurrent(name, replacement) && _previewPaused;

    /// <summary>"0:02 / 0:05" of the preview player, or empty.</summary>
    internal static string PreviewTime()
    {
        try
        {
            if (_preview == null || _preview.Pointer == IntPtr.Zero || _preview.clip == null || (!_preview.isPlaying && !_previewPaused)) return string.Empty;
            static string T(float s) => $"{(int)s / 60}:{(int)s % 60:00}";
            return $"{T(_preview.time)} / {T(_preview.clip.length)}";
        }
        catch { return string.Empty; }
    }

    /// <summary>Play button: same row playing = pause, paused = resume, anything else = play from the start.</summary>
    internal static string TogglePreview(string name, bool replacement)
    {
        try
        {
            if (IsPlaying(name, replacement)) { _preview!.Pause(); _previewPaused = true; return "Paused  " + name; }
            if (IsPaused(name, replacement)) { _preview!.UnPause(); _previewPaused = false; return (replacement ? "Playing your sound: " : "Playing the original: ") + name; }
        }
        catch { }
        return PlayPreview(name, replacement);
    }

    internal static string PlayPreview(string name, bool replacement)
    {
        try
        {
            AudioClip? clip;
            if (replacement) clip = _muted.Contains(name) ? null : LoadReplacement(name);
            else
            {
                _bypass = true;
                try { clip = OriginalClip(name); }
                finally { _bypass = false; }
            }
            if (clip == null) return replacement ? $"{name}: no replacement to play" : $"{name}: the game's sound could not be loaded";
            if (_preview == null || _preview.Pointer == IntPtr.Zero)
            {
                var go = new GameObject("SkinForger Sound Preview");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _preview = go.AddComponent<AudioSource>();
                _preview.playOnAwake = false;
            }
            _preview.Stop();
            _preview.clip = clip;
            _preview.volume = 1f;
            _preview.time = 0f;
            _preview.Play();
            _previewName = name;
            _previewReplacement = replacement;
            _previewPaused = false;
            return (replacement ? "Playing your sound: " : "Playing the original: ") + name + $"  ({clip.length:0.0}s)";
        }
        catch (Exception exception) { return name + ": " + exception.GetBaseException().Message; }
    }

    internal static void StopPreview()
    {
        try { if (_preview != null && _preview.Pointer != IntPtr.Zero) _preview.Stop(); } catch { }
        _previewName = string.Empty;
        _previewPaused = false;
    }

    // ------------------------------------------------------------ mute list (SkinCreator\Sounds\<Costume>\muted.txt)

    internal static void SetMuted(string costume, IEnumerable<string> names, bool mute)
    {
        string dir = Path.Combine(Root, costume);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "muted.txt");
        var list = File.Exists(file) ? ReadLines(File.ReadAllBytes(file)).ToList() : new List<string>();
        foreach (string name in names)
        {
            list.RemoveAll(l => l.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (mute) list.Add(name);
        }
        if (list.Count == 0) { if (File.Exists(file)) File.Delete(file); }
        else File.WriteAllText(file, "# SkinForger: these sounds are muted (one name per line)\n" + string.Join("\n", list) + "\n");
        Rescan(force: true);
    }


    // ------------------------------------------------------------ hooks
    //
    // 2026-11-17 (round 2): Muse Dash has TWO AudioManagers. Voices, BGM and battle sounds go through
    // Assets.Scripts.PeroTools.Managers.AudioManager (Assembly-CSharp: PlayVoice, PlayOneShot, PlayBGM, Preload into
    // m_SfxBuffer); PeroTools2's is hooked as well. Name overloads are redirected to the clip overloads; clip overloads
    // swap the clip by its name; m_SfxBuffer entries (preloaded battle sounds, played straight from the buffer) are
    // swapped after every Preload and on every rescan, and given back when a replacement is removed.

    private static readonly HashSet<string> RecentSet = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> RecentList = new();

    /// <summary>Sound names the game played this session, newest first (for the Sound page).</summary>
    internal static IReadOnlyList<string> Recent { get { lock (RecentList) return RecentList.ToList(); } }

    private static void Remember(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.StartsWith("SkinForger_", StringComparison.Ordinal) || IsCensored(name)) return;
        string key = Path.GetFileNameWithoutExtension(name);
        lock (RecentList)
        {
            RecentList.Remove(key);
            RecentList.Insert(0, key);
            if (RecentList.Count > 60) RecentList.RemoveAt(RecentList.Count - 1);
        }
        if (RecentSet.Add(key))
            MelonLogger.Msg($"{Prefix} game sound: {key}{(_muted.Contains(key) ? " (muted)" : _replacements.ContainsKey(key) ? " (replaced)" : string.Empty)}");
    }

    private static Il2CppAssets.Scripts.PeroTools.Managers.AudioManager? GameManager()
    {
        try { return Il2CppAssets.Scripts.PeroTools.Commons.Singleton<Il2CppAssets.Scripts.PeroTools.Managers.AudioManager>.instance; }
        catch { return null; }
    }

    /// <summary>Game clips taken out of m_SfxBuffer (given back when the replacement goes away).</summary>
    private static readonly Dictionary<string, AudioClip> Originals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The game's own clip of a name (preview): the game's Preload into its buffer, else PeroTools2's Preload.</summary>
    private static AudioClip? OriginalClip(string name)
    {
        bool bypass = _bypass;
        _bypass = true;
        try
        {
            if (Originals.TryGetValue(name, out AudioClip? kept) && kept != null && kept.Pointer != IntPtr.Zero) return kept;
            var manager = GameManager();
            if (manager != null)
            {
                manager.Preload(name, false);
                var buffer = manager.m_SfxBuffer;
                if (buffer != null && buffer.ContainsKey(name))
                {
                    AudioClip clip = buffer[name];
                    bool mine = Clips.TryGetValue(name, out var loaded) && loaded.Clip != null && clip != null && loaded.Clip.Pointer == clip.Pointer;
                    if (clip != null && !mine && !clip.name.StartsWith("SkinForger_", StringComparison.Ordinal)) return clip;
                }
            }
            return Il2CppPeroTools2.Managers.AudioManager.instance?.Preload(name, false);
        }
        catch { return null; }
        finally { _bypass = bypass; }
    }

    /// <summary>Puts replacements into the game's sound buffer (and the game's clips back where they no longer apply).</summary>
    internal static void SyncBuffer()
    {
        try
        {
            var buffer = GameManager()?.m_SfxBuffer;
            if (buffer == null) return;
            var keys = new List<string>();
            foreach (var pair in buffer) keys.Add(pair.Key);
            foreach (string key in keys)
            {
                AudioClip? current = buffer[key];
                AudioClip? wanted = Resolve(key);
                if (wanted != null)
                {
                    if (current != null && current.Pointer == wanted.Pointer) continue;
                    if (current != null && !Originals.ContainsKey(key) && !current.name.StartsWith("SkinForger_", StringComparison.Ordinal)) Originals[key] = current;
                    buffer[key] = wanted;
                }
                else if (Originals.TryGetValue(key, out AudioClip? original))
                {
                    buffer[key] = original;
                    Originals.Remove(key);
                }
            }
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} sound buffer not updated: {exception.GetBaseException().Message}"); }
    }

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        int patched = 0;
        void Hook(Type type, string name, Type[] parameters, string patch, bool postfix = false)
        {
            MethodInfo? method = AccessTools.DeclaredMethod(type, name, parameters);
            if (method == null) { MelonLogger.Warning($"{Prefix} {type.Name}.{name} not found"); return; }
            try
            {
                var hm = new HarmonyMethod(typeof(SoundRuntime), patch) { priority = Priority.First };
                if (postfix) harmony.Patch(method, postfix: hm); else harmony.Patch(method, prefix: hm);
                patched++;
            }
            catch (Exception exception) { MelonLogger.Warning($"{Prefix} {type.Name}.{name} not patched: {exception.GetBaseException().Message}"); }
        }
        Type game = typeof(Il2CppAssets.Scripts.PeroTools.Managers.AudioManager);
        Type s = typeof(string), f = typeof(float), c = typeof(AudioClip), src = typeof(AudioSource), act = typeof(Il2CppSystem.Action), b = typeof(bool);
        Type ease = typeof(Il2CppDG.Tweening.Ease), list = typeof(Il2CppSystem.Collections.Generic.IList<string>);
        Hook(game, "PlayVoice", new[] { s, f }, nameof(GamePlayVoiceName));
        Hook(game, "PlayVoice", new[] { c, f }, nameof(ClipArgument));
        Hook(game, "PlayOneShot", new[] { s, f, act }, nameof(GamePlayOneShotName));
        Hook(game, "PlayOneShot", new[] { c, f, act }, nameof(ClipArgument));
        Hook(game, "PlayOneShot", new[] { src, s, f }, nameof(GamePlayOneShotSourceName));
        Hook(game, "PlayOneShot", new[] { src, c, f }, nameof(ClipArgument1));
        Hook(game, "PlayOneShotOnBgm", new[] { c, f }, nameof(ClipArgument));
        Hook(game, "PlayLoop", new[] { s, f }, nameof(GamePlayLoopName));
        Hook(game, "PlayLoop", new[] { c, f }, nameof(ClipArgument));
        Hook(game, "PlayBGM", new[] { s, f, f, ease, b, b, act }, nameof(GamePlayBgmName));
        Hook(game, "PlayBGM", new[] { c, f, f, ease, b }, nameof(ClipArgument));
        Hook(game, "Preload", new[] { s, b }, nameof(AfterPreload), postfix: true);
        Hook(game, "Preload", new[] { s, s, b }, nameof(AfterPreload), postfix: true);
        Hook(game, "Preload", new[] { list, b }, nameof(AfterPreload), postfix: true);
        Hook(game, "PreloadBattleSfx", new[] { list, list, b }, nameof(AfterPreload), postfix: true);
        Hook(typeof(Il2Cpp.AttacksController), "PlayOneShot", new[] { s, typeof(Il2Cpp.AttacksController.KeyAudioType) }, nameof(AttackSound));

        // PeroTools2's manager (some UI)
        Type tools = typeof(Il2CppPeroTools2.Managers.AudioManager);
        Hook(tools, "PlayOneShot", new[] { s, f, act }, nameof(ToolsPlayOneShotName));
        Hook(tools, "PlayOneShot", new[] { c, f, act }, nameof(ClipArgument));
        Hook(tools, "PlayBGM", new[] { s, f, f, ease, b }, nameof(ToolsPlayBgmName));
        Hook(tools, "PlayBGM", new[] { c, f, f, ease, b }, nameof(ClipArgument));
        MelonLogger.Msg($"{Prefix} ready: {patched} audio hooks; files in SkinCreator\\Sounds\\<Character>\\");
        return patched;
    }

    // ---- the game's AudioManager
    private static bool GamePlayVoiceName(Il2CppAssets.Scripts.PeroTools.Managers.AudioManager __instance, string __0, float __1, ref AudioSource __result)
    {
        Remember(__0);
        AudioClip? clip = Resolve(__0);
        if (clip == null) return true;
        __result = __instance.PlayVoice(clip, __1);
        return false;
    }

    private static bool GamePlayOneShotName(Il2CppAssets.Scripts.PeroTools.Managers.AudioManager __instance, string __0, float __1, Il2CppSystem.Action __2, ref AudioSource __result)
    {
        Remember(__0);
        AudioClip? clip = Resolve(__0);
        if (clip == null) return true;
        __result = __instance.PlayOneShot(clip, __1, __2);
        return false;
    }

    private static bool GamePlayOneShotSourceName(Il2CppAssets.Scripts.PeroTools.Managers.AudioManager __instance, AudioSource __0, string __1, float __2)
    {
        Remember(__1);
        AudioClip? clip = Resolve(__1);
        if (clip == null) return true;
        __instance.PlayOneShot(__0, clip, __2);
        return false;
    }

    private static bool GamePlayLoopName(Il2CppAssets.Scripts.PeroTools.Managers.AudioManager __instance, string __0, float __1, ref AudioSource __result)
    {
        Remember(__0);
        AudioClip? clip = Resolve(__0);
        if (clip == null) return true;
        __result = __instance.PlayLoop(clip, __1);
        return false;
    }

    private static bool GamePlayBgmName(Il2CppAssets.Scripts.PeroTools.Managers.AudioManager __instance, string __0, float __1, float __2, Il2CppDG.Tweening.Ease __3, bool __4)
    {
        Remember(__0);
        AudioClip? clip = Resolve(__0);
        if (clip == null) return true;
        __instance.PlayBGM(clip, __1, __2, __3, __4);
        return false;
    }

    private static void AfterPreload()
    {
        if (!_bypass) SyncBuffer();
    }

    private static void AttackSound(string __0) => Remember(__0);

    // ---- PeroTools2's AudioManager
    private static bool ToolsPlayOneShotName(Il2CppPeroTools2.Managers.AudioManager __instance, string __0, float __1, Il2CppSystem.Action __2, ref AudioSource __result)
    {
        Remember(__0);
        AudioClip? clip = Resolve(__0);
        if (clip == null) return true;
        __result = __instance.PlayOneShot(clip, __1, __2);
        return false;
    }

    private static bool ToolsPlayBgmName(Il2CppPeroTools2.Managers.AudioManager __instance, string __0, float __1, float __2, Il2CppDG.Tweening.Ease __3, bool __4)
    {
        Remember(__0);
        AudioClip? clip = Resolve(__0);
        if (clip == null) return true;
        __instance.PlayBGM(clip, __1, __2, __3, __4);
        return false;
    }

    // ---- clip overloads: swap by clip name
    private static void ClipArgument(ref AudioClip __0) => Swap(ref __0);
    private static void ClipArgument1(ref AudioClip __1) => Swap(ref __1);

    private static void Swap(ref AudioClip clip)
    {
        if (clip == null) return;
        string name = clip.name;
        Remember(name);
        AudioClip? replacement = Resolve(name);
        if (replacement != null && replacement.Pointer != clip.Pointer) clip = replacement;
    }
}
