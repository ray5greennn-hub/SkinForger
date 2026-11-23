using System.Reflection;
using HarmonyLib;
using Il2CppSpine;
using Il2CppSpine.Unity;
using MelonLoader;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// Preview additions for other character skins (2026-11-16):
///  - main object choice: Preview's SelectPrimaryRenderer skips objects named "...cat..." (meant for small helper
///    objects), so a cat skin, whose main object is itself "..._cat", got no renderer and showed "Unavailable".
///    When nothing was chosen, the renderer with the most animations is taken (ghost / shadow helpers still skipped).
///  - "Frame &lt; &gt;" row under Animation: pauses and jumps to the previous / next moment an image changes in the
///    current animation (attachment keys: one drawn frame of a frame-by-frame cat animation); without such keys,
///    steps of 1/30 s. Shows the time and the frame number.
/// </summary>
internal static class PreviewFrames
{
    private const string Prefix = "[SkinForge][PreviewFrames]";
    private static Type? _preview;
    private static FieldInfo? _skeletonAnimation, _paused, _animations, _animationIndex, _speedIndex;
    /// <summary>The authored Preview shows the GAMEPLAY object: a SkeletonMecanim driven by this Animator.</summary>
    private static FieldInfo? _skeletonRenderer, _animator;
    private static MethodInfo? _speedValue;
    private static bool _pauseRequested;
    private static string _keysFor = string.Empty;
    private static List<float> _keys = new();

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        _preview = AccessTools.TypeByName("SkinForger.Preview.PreviewManager");
        if (_preview == null) return 0;
        _skeletonAnimation = AccessTools.Field(_preview, "_skeletonAnimation");
        _skeletonRenderer = AccessTools.Field(_preview, "_skeletonRenderer");
        _animator = AccessTools.Field(_preview, "_animator");
        _paused = AccessTools.Field(_preview, "_paused");
        _animations = AccessTools.Field(_preview, "Animations");
        _animationIndex = AccessTools.Field(_preview, "_animationIndex");
        _speedIndex = AccessTools.Field(_preview, "_speedIndex");
        _speedValue = AccessTools.DeclaredMethod(_preview, "SpeedValue");
        int patched = 0;
        MethodInfo? select = AccessTools.DeclaredMethod(_preview, "SelectPrimaryRenderer");
        if (select != null) { harmony.Patch(select, postfix: new HarmonyMethod(typeof(PreviewFrames), nameof(SelectPostfix))); patched++; }
        MethodInfo? draw = AccessTools.DeclaredMethod(_preview, "DrawAnimationSelector");
        // Either Preview will do: the old one keeps a SkeletonAnimation, the authored one a SkeletonMecanim
        // plus the Animator that drives it (2026-11-20 -- the row was simply absent on the authored Preview).
        if (draw != null && (_skeletonAnimation != null || (_skeletonRenderer != null && _animator != null)))
        { harmony.Patch(draw, postfix: new HarmonyMethod(typeof(PreviewFrames), nameof(FrameRow))); patched++; }
        MethodInfo? window = AccessTools.DeclaredMethod(_preview, "DrawPreviewWindow");
        if (window != null && _paused != null)
        {
            harmony.Patch(window, transpiler: new HarmonyMethod(typeof(PreviewFrames), nameof(PlayPauseTranspiler)));
            patched++;
        }
        return patched;
    }

    /// <summary>PreviewManager.SelectPrimaryRenderer(prefab): a cat skin's main object when Preview chose none.</summary>
    private static void SelectPostfix(GameObject __0, ref SkeletonRenderer __result)
    {
        if (__result != null || __0 == null) return;
        try
        {
            SkeletonRenderer? best = null;
            int bestCount = -1;
            foreach (SkeletonRenderer renderer in __0.GetComponentsInChildren<SkeletonRenderer>(true))
            {
                string name = renderer?.gameObject.name ?? string.Empty;
                if (name.Contains("ghost", StringComparison.OrdinalIgnoreCase) || name.Contains("shadow", StringComparison.OrdinalIgnoreCase)) continue;
                int count = renderer!.skeletonDataAsset?.GetSkeletonData(true)?.Animations?.Count ?? -1;
                if (count > bestCount) { best = renderer; bestCount = count; }
            }
            if (best == null) return;
            __result = best;
            MelonLogger.Msg($"{Prefix} main object of {__0.name}: {best.gameObject.name} ({bestCount} animations)");
        }
        catch { }
    }

    private static bool IsPaused() => _paused?.GetValue(null) is true;

    /// <summary>
    /// Play / Pause like Main's buttons: paused = stopped, playing = the selected speed. Whichever Preview
    /// is showing -- Spine's own timeScale, or the Unity Animator the authored Preview plays through.
    /// </summary>
    private static void TogglePlay()
    {
        bool paused = IsPaused();
        _paused?.SetValue(null, !paused);
        float speed = 1f;
        if (paused && _speedValue != null && _speedIndex?.GetValue(null) is int index && _speedValue.Invoke(null, new object[] { index }) is float value) speed = value;
        SkeletonAnimation? spine = Current();
        if (spine != null && spine.Pointer != IntPtr.Zero) { spine.timeScale = paused ? speed : 0f; return; }
        Animator? animator = CurrentAnimator();
        if (animator != null && animator.Pointer != IntPtr.Zero) animator.speed = paused ? speed : 0f;
    }

    /// <summary>
    /// Main's two buttons "Play" and "Pause" become one toggle: play symbol while paused, "||" while playing (user
    /// request 2026-11-16). The first button call draws the toggle and returns true when Play should run; the second
    /// draws nothing and returns true when Pause should run, so Main's own Play / Pause code is reused.
    /// </summary>
    private static IEnumerable<CodeInstruction> PlayPauseTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo play = AccessTools.DeclaredMethod(typeof(PreviewFrames), nameof(PlayToggleButton));
        MethodInfo pause = AccessTools.DeclaredMethod(typeof(PreviewFrames), nameof(PauseHiddenButton));
        string? pending = null;
        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is string text && (text == "Play" || text == "Pause")) pending = text;
            else if (pending != null && instruction.operand is MethodInfo method && method.Name == "Button" && method.DeclaringType == typeof(GUILayout))
            {
                instruction.operand = pending == "Play" ? play : pause;
                pending = null;
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 2) MelonLogger.Warning($"{Prefix} Play / Pause toggle: {replaced}/2 buttons found");
    }

    private static bool PlayToggleButton(string text, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GUILayoutOption> options)
    {
        bool paused = IsPaused();
        if (!GUILayout.Button(paused ? "\u25BA  Play" : "||  Pause", Array.Empty<GUILayoutOption>())) return false;
        if (paused) return true;
        _pauseRequested = true;
        return false;
    }

    private static bool PauseHiddenButton(string text, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GUILayoutOption> options)
    {
        bool run = _pauseRequested;
        _pauseRequested = false;
        return run;
    }

    private static SkeletonAnimation? Current() => _skeletonAnimation?.GetValue(null) as SkeletonAnimation;

    private static SkeletonRenderer? CurrentRenderer() =>
        (_skeletonAnimation?.GetValue(null) as SkeletonRenderer) ?? (_skeletonRenderer?.GetValue(null) as SkeletonRenderer);

    private static Animator? CurrentAnimator() => _animator?.GetValue(null) as Animator;

    /// <summary>
    /// The animation on show and where it is, for either Preview.
    ///
    /// Spine's AnimationState knows the clip and the time directly. A Mecanim preview does not: the clip is
    /// named by Preview's own Animations list and lives in the skeleton's data, and the time comes from the
    /// Animator's state. The frame STEP then writes the time back the way each one takes it.
    /// </summary>
    private static bool Showing(out Il2CppSpine.Animation clip, out float time, out float duration)
    {
        clip = null!;
        time = 0f;
        duration = 0f;
        SkeletonAnimation? spine = Current();
        TrackEntry? entry = spine?.AnimationState?.GetCurrent(0);
        if (entry?.Animation != null)
        {
            clip = entry.Animation;
            time = entry.AnimationTime;
            duration = Math.Max(0.0001f, clip.Duration);
            return true;
        }
        SkeletonRenderer? renderer = CurrentRenderer();
        Animator? animator = CurrentAnimator();
        SkeletonData? data = renderer?.Skeleton?.data;
        if (data == null || animator == null || animator.Pointer == IntPtr.Zero) return false;
        var names = _animations?.GetValue(null) as List<string>;
        int index = _animationIndex?.GetValue(null) is int i ? i : -1;
        if (names == null || index < 0 || index >= names.Count) return false;
        Il2CppSpine.Animation? found = data.FindAnimation(names[index]);
        if (found == null) return false;
        clip = found;
        duration = Math.Max(0.0001f, clip.Duration);
        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
        float normalised = state.normalizedTime;
        normalised -= (float)Math.Floor(normalised);            // the clip loops; only the position in it matters
        time = normalised * duration;
        return true;
    }

    /// <summary>Pauses and puts the animation at this moment, for either Preview.</summary>
    private static void GoTo(float target, Il2CppSpine.Animation clip, float duration)
    {
        _paused?.SetValue(null, true);
        SkeletonAnimation? spine = Current();
        TrackEntry? entry = spine?.AnimationState?.GetCurrent(0);
        if (spine != null && entry != null)
        {
            spine.timeScale = 0f;
            entry.TrackTime = target;
            spine.AnimationState.Apply(spine.Skeleton);
            spine.Skeleton.UpdateWorldTransform();
            return;
        }
        Animator? animator = CurrentAnimator();
        if (animator == null || animator.Pointer == IntPtr.Zero) return;
        animator.speed = 0f;
        animator.Play(clip.Name, 0, Math.Clamp(target / duration, 0f, 0.9999f));
        animator.Update(0f);   // apply it now, so the picture changes on this click and not on the next frame
    }

    /// <summary>Times (seconds) at which an attachment key changes an image in this animation, sorted.</summary>
    private static List<float> Keys(Il2CppSpine.Animation animation)
    {
        string id = animation.Name + "|" + animation.Duration;
        if (id == _keysFor) return _keys;
        var keys = new SortedSet<float>();
        var timelines = animation.timelines;
        for (int i = 0; timelines != null && i < timelines.Count; i++)
        {
            AttachmentTimeline? timeline = timelines.Items[i]?.TryCast<AttachmentTimeline>();
            var frames = timeline?.frames;
            if (frames == null) continue;
            foreach (float time in frames) keys.Add((float)Math.Round(time, 4));
        }
        keys.Add(0f);
        _keys = keys.Where(k => k <= animation.Duration + 0.0001f).ToList();
        _keysFor = id;
        return _keys;
    }

    /// <summary>"Frame  &lt;  0.40 s  (12 / 48)  &gt;" under the Animation row.</summary>
    private static void FrameRow()
    {
        try
        {
            bool showing = Showing(out Il2CppSpine.Animation shown, out float at, out float length);
            Il2CppSpine.Animation? clip = showing ? shown : null;
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.Label("Frame", GUILayout.Width(90f));
            GUI.enabled = clip != null;
            int step = 0;
            if (GUILayout.Button("<", GUILayout.Width(34f))) step = -1;
            bool toggle = GUILayout.Button(IsPaused() ? "\u25BA" : "||", GUILayout.Width(40f));
            if (GUILayout.Button(">", GUILayout.Width(34f))) step = 1;
            string text = "-";
            List<float>? keys = null;
            float time = at;
            if (clip != null)
            {
                keys = Keys(clip);
                int frame = keys.Count(k => k <= time + 0.0005f);
                text = keys.Count > 1 ? $"{time:0.00} s   ({frame} / {keys.Count})" : $"{time:0.00} s";
            }
            GUILayout.Label(text, GUILayout.Width(150f));
            GUI.enabled = true;
            GUILayout.Label("< > step one image (pauses)", GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            if (toggle) { TogglePlay(); return; }
            if (step == 0 || clip == null || keys == null) return;

            float duration = length;
            float target;
            if (keys.Count > 1)
            {
                target = step > 0
                    ? keys.FirstOrDefault(k => k > time + 0.0005f, keys[0])           // wraps to the first image
                    : keys.LastOrDefault(k => k < time - 0.0005f, keys[^1]);          // wraps to the last image
            }
            else
            {
                target = (float)(Math.Round(time * 30f) + step) / 30f;
                if (target < 0f) target = duration;
                if (target > duration) target = 0f;
            }
            GoTo(target, clip, duration);
        }
        catch (Exception exception)
        {
            GUI.enabled = true;
            MelonLogger.Warning($"{Prefix} frame step failed: {exception.GetBaseException().Message}");
        }
    }
}
