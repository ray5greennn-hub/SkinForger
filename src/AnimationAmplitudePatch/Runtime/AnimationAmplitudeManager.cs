using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSpine;
using MelonLoader;

namespace SkinForger.AnimationAmplitude
{
    public static class AnimationAmplitudeManager
    {
        private static readonly Dictionary<long, AnimationSkeletonState> SkeletonStates =
            new Dictionary<long, AnimationSkeletonState>();
        private static readonly Dictionary<long, AnimationDataState> DataStates =
            new Dictionary<long, AnimationDataState>();
        private static readonly HashSet<string> LoggedWarnings = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> LoggedApplications = new HashSet<string>(StringComparer.Ordinal);

        public static void BeginSkeleton(Skeleton skeleton)
        {
            try
            {
                if (skeleton == null || skeleton.Data == null) return;
                long skeletonKey = PointerOf(skeleton);
                long dataKey = PointerOf(skeleton.Data);
                RestoreData(dataKey);
                AnimationSkeletonState state;
                if (!SkeletonStates.TryGetValue(skeletonKey, out state))
                {
                    state = new AnimationSkeletonState(dataKey);
                    SkeletonStates[skeletonKey] = state;
                }
                state.DataKey = dataKey;
                state.SlotSettings.Clear();
                state.SlotSources.Clear();
            }
            catch (Exception ex)
            {
                WarnOnce("begin|" + ex.Message, "Animation amplitude setup skipped: " + ex.Message);
            }
        }

        public static void RegisterReplacement(Skeleton skeleton, int slotIndex, Attachment attachment, string regionPath)
        {
            try
            {
                if (skeleton == null || skeleton.Data == null || slotIndex < 0 || string.IsNullOrEmpty(regionPath))
                    return;
                long skeletonKey = PointerOf(skeleton);
                AnimationSkeletonState state;
                if (!SkeletonStates.TryGetValue(skeletonKey, out state))
                {
                    state = new AnimationSkeletonState(PointerOf(skeleton.Data));
                    SkeletonStates[skeletonKey] = state;
                }

                string jsonPath = Path.ChangeExtension(regionPath, ".json");
                AnimationAmplitudeSettings settings = LoadSettings(jsonPath);
                AnimationAmplitudeSettings existing;
                if (!state.SlotSettings.TryGetValue(slotIndex, out existing) ||
                    existing.IsDefault && !settings.IsDefault)
                {
                    state.SlotSettings[slotIndex] = settings;
                    state.SlotSources[slotIndex] = jsonPath;
                }
                else if (!AnimationAmplitudeSettings.Same(existing, settings) &&
                    !existing.IsDefault && !settings.IsDefault)
                {
                    WarnOnce("slot|" + skeletonKey + "|" + slotIndex,
                        "Conflicting movement settings for slot " + slotIndex + "; keeping " +
                        Path.GetFileName(state.SlotSources[slotIndex]) + ".");
                }
            }
            catch (Exception ex)
            {
                WarnOnce("register|" + regionPath + "|" + ex.Message,
                    "Could not read movement settings for " + Path.GetFileName(regionPath) + ": " + ex.Message);
            }
        }

        public static void ApplyToSkeleton(Skeleton skeleton, string skinPath)
        {
            try
            {
                if (skeleton == null || skeleton.Data == null || skeleton.Slots == null) return;
                long skeletonKey = PointerOf(skeleton);
                AnimationSkeletonState state;
                if (!SkeletonStates.TryGetValue(skeletonKey, out state)) return;

                Dictionary<int, AnimationAmplitudeSettings> byBone = new Dictionary<int, AnimationAmplitudeSettings>();
                Dictionary<int, string> boneSources = new Dictionary<int, string>();
                foreach (KeyValuePair<int, AnimationAmplitudeSettings> item in state.SlotSettings)
                {
                    if (item.Key < 0 || item.Key >= skeleton.Slots.Count) continue;
                    Slot slot = skeleton.Slots.Items[item.Key];
                    Bone bone = slot == null ? null : slot.bone;
                    BoneData data = bone == null ? null : bone.data;
                    if (data == null) continue;
                    int boneIndex = data.index;
                    AnimationAmplitudeSettings existing;
                    if (!byBone.TryGetValue(boneIndex, out existing) || existing.IsDefault && !item.Value.IsDefault)
                    {
                        byBone[boneIndex] = item.Value;
                        boneSources[boneIndex] = state.SlotSources[item.Key];
                    }
                    else if (!AnimationAmplitudeSettings.Same(existing, item.Value) &&
                        !existing.IsDefault && !item.Value.IsDefault)
                    {
                        WarnOnce("bone|" + state.DataKey + "|" + boneIndex,
                            "Multiple attachments on bone " + data.name + " request different movement settings; keeping " +
                            Path.GetFileName(boneSources[boneIndex]) + ".");
                    }
                }

                ApplyData(skeleton.Data, byBone);
                string signature = BuildSignature(byBone);
                string logKey = state.DataKey.ToString(CultureInfo.InvariantCulture) + "|" +
                    (skinPath ?? string.Empty) + "|" + signature;
                if (LoggedApplications.Add(logKey))
                {
                    MelonLogger.Msg("[SkinForger] Animation amplitude applied: skin=" + skinPath +
                        ", configuredSlots=" + state.SlotSettings.Count + ", configuredBones=" + byBone.Count +
                        ", factors=" + signature + ", mode=one-shot-timeline-data");
                }
            }
            catch (Exception ex)
            {
                WarnOnce("apply|" + ex.Message, "Animation amplitude apply skipped: " + ex.Message);
            }
        }

        private static AnimationAmplitudeSettings LoadSettings(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return AnimationAmplitudeSettings.Default();
            return AnimationAmplitudeSettings.Parse(File.ReadAllText(path), WarnJson);
        }

        private static void WarnJson(string warning)
        {
            WarnOnce("json|" + warning, "Movement JSON: " + warning);
        }

        private static void ApplyData(SkeletonData data, Dictionary<int, AnimationAmplitudeSettings> byBone)
        {
            long dataKey = PointerOf(data);
            AnimationDataState dataState;
            if (!DataStates.TryGetValue(dataKey, out dataState))
            {
                dataState = new AnimationDataState();
                DataStates[dataKey] = dataState;
            }
            RestoreData(dataKey);

            ExposedList<Animation> animations = data.Animations;
            if (animations == null) return;
            for (int animationIndex = 0; animationIndex < animations.Count; animationIndex++)
            {
                Animation animation = animations.Items[animationIndex];
                ExposedList<Timeline> timelines = animation == null ? null : animation.timelines;
                if (timelines == null) continue;
                for (int timelineIndex = 0; timelineIndex < timelines.Count; timelineIndex++)
                {
                    Timeline timeline = timelines.Items[timelineIndex];
                    if (timeline == null) continue;
                    Il2CppObjectBase timelineObject = (Il2CppObjectBase)timeline;

                    // ScaleTimeline and ShearTimeline inherit TranslateTimeline in this Spine runtime.
                    // Exclude them before TryCast so movement amplitude never changes scale or shear keys.
                    if (timelineObject.TryCast<ScaleTimeline>() != null ||
                        timelineObject.TryCast<ShearTimeline>() != null)
                        continue;

                    TranslateTimeline translate = timelineObject.TryCast<TranslateTimeline>();
                    if (translate != null)
                    {
                        AnimationAmplitudeSettings settings;
                        if (!byBone.TryGetValue(translate.boneIndex, out settings)) continue;
                        AnimationTimelineSnapshot snapshot = GetSnapshot(dataState, timeline, translate.frames, true);
                        WriteFrames(snapshot.Frames,
                            AnimationAmplitudeMath.ScaleTranslateFrames(snapshot.Original, settings));
                        continue;
                    }

                    RotateTimeline rotate = timelineObject.TryCast<RotateTimeline>();
                    if (rotate != null)
                    {
                        AnimationAmplitudeSettings settings;
                        if (!byBone.TryGetValue(rotate.boneIndex, out settings)) continue;
                        AnimationTimelineSnapshot snapshot = GetSnapshot(dataState, timeline, rotate.frames, false);
                        WriteFrames(snapshot.Frames,
                            AnimationAmplitudeMath.ScaleRotateFrames(snapshot.Original, settings));
                    }
                }
            }
            dataState.AppliedSignature = BuildSignature(byBone);
        }

        private static AnimationTimelineSnapshot GetSnapshot(AnimationDataState state, Timeline timeline,
            Il2CppStructArray<float> frames, bool translate)
        {
            long key = PointerOf(timeline);
            AnimationTimelineSnapshot snapshot;
            if (!state.Timelines.TryGetValue(key, out snapshot))
            {
                float[] original = new float[frames.Length];
                for (int i = 0; i < original.Length; i++) original[i] = frames[i];
                snapshot = new AnimationTimelineSnapshot(frames, original, translate);
                state.Timelines[key] = snapshot;
            }
            return snapshot;
        }

        private static void RestoreData(long dataKey)
        {
            AnimationDataState state;
            if (!DataStates.TryGetValue(dataKey, out state)) return;
            foreach (AnimationTimelineSnapshot snapshot in state.Timelines.Values)
                WriteFrames(snapshot.Frames, snapshot.Original);
            state.AppliedSignature = string.Empty;
        }

        private static void WriteFrames(Il2CppStructArray<float> target, float[] source)
        {
            int count = Math.Min(target.Length, source.Length);
            for (int i = 0; i < count; i++) target[i] = source[i];
        }

        private static string BuildSignature(Dictionary<int, AnimationAmplitudeSettings> byBone)
        {
            if (byBone.Count == 0) return "default";
            List<int> keys = new List<int>(byBone.Keys);
            keys.Sort();
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i != 0) result.Append(';');
                int key = keys[i];
                result.Append(key).Append('=').Append(byBone[key].Signature());
            }
            return result.ToString();
        }

        private static long PointerOf(Il2CppObjectBase value)
        {
            return value == null ? 0L : value.Pointer.ToInt64();
        }

        private static void WarnOnce(string key, string message)
        {
            if (LoggedWarnings.Add(key)) MelonLogger.Warning("[SkinForger] " + message);
        }
    }
}
