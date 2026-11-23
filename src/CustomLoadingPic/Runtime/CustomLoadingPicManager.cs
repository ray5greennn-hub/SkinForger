using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SkinForger
{
    public static class CustomLoadingPicManager
    {
        private static bool _installed;
        private static string _folder;
        private static readonly System.Random Random = new System.Random();
        private static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> Stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static GameObject _overlay;
        private static Image _original;
        private static bool _originalEnabled;

        public static void Initialize()
        {
            if (_installed) return;
            try
            {
#if SKINFORGER_AUTHORED_MAIN
                // The folder moved into SkinCreator on 2026-11-15. In the old Main that move was made by
                // rewriting THIS string literal in the finished binary (MainHardening\Tool: "CustomLodingPic"
                // -> "SkinCreator\CustomLodingPic"), so the source still says the old place. The authored
                // Main is compiled, not post-processed, so it has to say the new one — and when it did not,
                // the feature looked in an empty folder, wrote a fresh template config there and reported
                // "File not found: 1.png ... Loaded 0 loading pictures" while the player's 14 pictures sat
                // in SkinCreator\CustomLodingPic (2026-11-19 16:31 trial).
                _folder = LoadingPaths.LoadingPictures(Environment.CurrentDirectory);
#else
                _folder = Path.Combine(Environment.CurrentDirectory, "SkinForger", "CustomLodingPic");
#endif
                Directory.CreateDirectory(_folder);
                string config = Path.Combine(_folder, "config.json");
                if (!File.Exists(config)) File.WriteAllText(config, LoadingConfig.Template);
                var method = AccessTools.Method(typeof(LoadingImg), "OnEnable", Type.EmptyTypes);
                if (method == null) throw new MissingMethodException("LoadingImg.OnEnable()");
                var patches = HarmonyLib.Harmony.GetPatchInfo(method);
                if (patches != null)
                    foreach (var patch in patches.Postfixes)
                        if (patch.PatchMethod.DeclaringType.Assembly.GetName().Name == "CustomLoadingScreens")
                        { Warning("CustomLoadingScreens owns LoadingImg.OnEnable; feature skipped to avoid competing writers."); return; }
                var harmony = new HarmonyLib.Harmony("skinforger.custom-loading-pic");
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(CustomLoadingPicManager), nameof(BeforeLoading)),
                    postfix: new HarmonyMethod(typeof(CustomLoadingPicManager), nameof(AfterLoading)));
                _installed = true;
                Log("Hook installed: LoadingImg.OnEnable prefix restore / postfix static picture; no frame polling.");
            }
            catch (Exception ex) { Warning("Initialization skipped: " + ex.Message); }
        }

        public static void BeforeLoading()
        {
            try { Restore(); }
            catch (Exception ex) { Warning("Restore failed: " + ex.Message); }
        }

        private static void Restore()
        {
            if (_original != null) _original.enabled = _originalEnabled;
            _original = null;
            if (_overlay != null) { _overlay.SetActive(false); Object.Destroy(_overlay); }
            _overlay = null;
        }

        public static void AfterLoading(LoadingImg __instance)
        {
            try
            {
                if (__instance == null) return;
                var paths = LoadingConfig.Read(_folder, Warning);
                var valid = new List<string>();
                foreach (string path in paths)
                {
                    try { if (Load(path) != null) valid.Add(path); }
                    catch (Exception ex) { Warning(Path.GetFileName(path) + ": " + ex.Message); }
                }
                Prune(valid);
                Log("Loaded " + valid.Count + " loading pictures.");
                if (valid.Count == 0) { Log("Fallback: original loading picture."); return; }
                Log("Selection mode: custom-only; originalProbability=0; validCustom=" + valid.Count);
                // Only inspect the game's explicitly declared illustration containers.
                var groups = new GameObject[] { __instance.simpleIllus, __instance.specialIllus,
                    __instance.specialIllusfor43, __instance.verySpecialIllus, __instance.squareIllus };
                Image target = null;
                foreach (var group in groups)
                {
                    if (group == null || !group.activeInHierarchy) continue;
                    var direct = group.GetComponent<Image>();
                    if (direct != null && direct.enabled) { target = direct; break; }
                    var children = group.GetComponentsInChildren<Image>(false);
                    Image only = null;
                    int count = 0;
                    foreach (var child in children)
                        if (child != null && child.enabled && child.sprite != null) { only = child; count++; }
                    if (count == 1) { target = only; break; }
                }
                if (target == null) { Warning("No unambiguous active illustration Image; keeping original."); return; }
                int selectedIndex = LoadingConfig.SelectCustomIndex(valid.Count, Random);
                string selected = valid[selectedIndex];
                var texture = Textures[selected];
                var targetRect = target.rectTransform;
                RectTransform displayArea = targetRect;
                if (targetRect.parent != null)
                {
                    var parentRect = targetRect.parent.gameObject.GetComponent<RectTransform>();
                    if (parentRect != null)
                    {
                        parentRect.ForceUpdateRectTransforms();
                        if (parentRect.rect.width > 0f && parentRect.rect.height > 0f)
                            displayArea = parentRect;
                    }
                }
                _overlay = new GameObject("SkinForger.CustomLodingPic");
                _overlay.SetActive(false);
                var viewport = _overlay.AddComponent<RectTransform>();
                viewport.SetParent(displayArea, false);
                viewport.localScale = Vector3.one;
                viewport.pivot = new Vector2(0.5f, 0.5f);
                viewport.anchorMin = Vector2.zero;
                viewport.anchorMax = Vector2.one;
                viewport.offsetMin = Vector2.zero;
                viewport.offsetMax = Vector2.zero;
                _overlay.AddComponent<RectMask2D>();
                if (displayArea != targetRect)
                    viewport.SetSiblingIndex(targetRect.GetSiblingIndex() + 1);
                var picture = new GameObject("Picture");
                picture.transform.SetParent(viewport, false);
                var image = picture.AddComponent<RawImage>();
                image.texture = texture;
                image.uvRect = new Rect(0f, 0f, 1f, 1f);
                image.color = Color.white;
                image.raycastTarget = false;
                var imageRect = image.rectTransform;
                imageRect.anchorMin = new Vector2(0.5f, 0.5f);
                imageRect.anchorMax = new Vector2(0.5f, 0.5f);
                imageRect.pivot = new Vector2(0.5f, 0.5f);
                imageRect.anchoredPosition = Vector2.zero;
                imageRect.localScale = Vector3.one;
                imageRect.localRotation = Quaternion.identity;
                // Unity's event-driven layout system reapplies Aspect Fill when the viewport changes.
                var aspect = picture.AddComponent<AspectRatioFitter>();
                aspect.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
                aspect.aspectRatio = (float)texture.width / texture.height;
                _original = target;
                _originalEnabled = target.enabled;
                target.enabled = false;
                _overlay.SetActive(true);
                Canvas.ForceUpdateCanvases();
                viewport.ForceUpdateRectTransforms();
                double scaledWidth, scaledHeight, marginX, marginY;
                var rect = viewport.rect;
                double scale = CoverGeometry.Calculate(texture.width, texture.height, rect.width, rect.height,
                    out scaledWidth, out scaledHeight, out marginX, out marginY);
                imageRect.ForceUpdateRectTransforms();
                Log("Selected: " + Path.GetFileName(selected) + "; target=" + target.name +
                    "; textureID=" + texture.GetInstanceID() + "; size=" + texture.width + "x" + texture.height +
                    "; mode=cover-center; displayArea=" + displayArea.name +
                    "; sourceImageRect=" + targetRect.rect.width + "x" + targetRect.rect.height +
                    "; targetRect=" + rect.width + "x" + rect.height +
                    "; uniformScale=" + scale + "; coverRect=" + scaledWidth + "x" + scaledHeight +
                    "; centerCrop=" + marginX + "," + marginY + "; actualImageRect=" +
                    imageRect.rect.width + "x" + imageRect.rect.height +
                    "; anchoredPosition=" + imageRect.anchoredPosition.x + "," + imageRect.anchoredPosition.y +
                    "; uv=" + image.uvRect.x + "," + image.uvRect.y + "," +
                    image.uvRect.width + "," + image.uvRect.height);
            }
            catch (Exception ex)
            {
                BeforeLoading();
                Warning("Fallback to original loading picture: " + ex.Message);
            }
        }

        private static Texture2D Load(string path)
        {
            var file = new FileInfo(path);
            string stamp = file.Length + ":" + file.LastWriteTimeUtc.Ticks;
            Texture2D cached;
            string previous;
            if (Textures.TryGetValue(path, out cached) && cached != null &&
                Stamps.TryGetValue(path, out previous) && previous == stamp) return cached;
            if (cached != null) Object.Destroy(cached);
            Textures.Remove(path);
            Stamps.Remove(path);
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 24 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71 ||
                bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 || bytes[7] != 10)
                throw new FormatException("Invalid PNG signature");
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                // Same IL2CPP decoder used by the existing SkinForger PNG pipeline.
                if (!Il2CppImageConversionManager.LoadImage(texture, (Il2CppStructArray<byte>)bytes))
                    throw new FormatException("PNG decode failed");
                texture.name = "SkinForger.Loading." + Path.GetFileName(path);
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
                Textures.Add(path, texture);
                Stamps.Add(path, stamp);
                return texture;
            }
            catch { Object.Destroy(texture); throw; }
        }

        private static void Prune(List<string> valid)
        {
            var keys = new List<string>(Textures.Keys);
            var keep = new HashSet<string>(valid, StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
                if (!keep.Contains(key))
                {
                    if (Textures[key] != null) Object.Destroy(Textures[key]);
                    Textures.Remove(key);
                    Stamps.Remove(key);
                }
        }

        private static void Warning(string text) { MelonLogger.Warning("[CustomLoadingPic] " + text); }
        private static void Log(string text) { MelonLogger.Msg("[CustomLoadingPic] " + text); }
    }
}
