using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace SkinForger.AnimationAmplitude
{
    public sealed class AnimationTimelineSnapshot
    {
        public AnimationTimelineSnapshot(Il2CppStructArray<float> frames, float[] original, bool translate)
        {
            Frames = frames;
            Original = original;
            Translate = translate;
        }

        public Il2CppStructArray<float> Frames { get; private set; }
        public float[] Original { get; private set; }
        public bool Translate { get; private set; }
    }

    public sealed class AnimationDataState
    {
        public AnimationDataState()
        {
            Timelines = new Dictionary<long, AnimationTimelineSnapshot>();
        }

        public Dictionary<long, AnimationTimelineSnapshot> Timelines { get; private set; }
        public string AppliedSignature { get; set; }
    }

    public sealed class AnimationSkeletonState
    {
        public AnimationSkeletonState(long dataKey)
        {
            DataKey = dataKey;
            SlotSettings = new Dictionary<int, AnimationAmplitudeSettings>();
            SlotSources = new Dictionary<int, string>();
        }

        public long DataKey { get; set; }
        public Dictionary<int, AnimationAmplitudeSettings> SlotSettings { get; private set; }
        public Dictionary<int, string> SlotSources { get; private set; }
    }
}
