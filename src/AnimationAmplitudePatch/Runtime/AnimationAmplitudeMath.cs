namespace SkinForger.AnimationAmplitude
{
    public static class AnimationAmplitudeMath
    {
        public static float[] ScaleTranslateFrames(float[] original, AnimationAmplitudeSettings settings)
        {
            float[] scaled = (float[])original.Clone();
            float x = settings.FinalX;
            float y = settings.FinalY;
            for (int i = 0; i + 2 < scaled.Length; i += 3)
            {
                scaled[i + 1] = original[i + 1] * x;
                scaled[i + 2] = original[i + 2] * y;
            }
            return scaled;
        }

        public static float[] ScaleRotateFrames(float[] original, AnimationAmplitudeSettings settings)
        {
            float[] scaled = (float[])original.Clone();
            float rotate = settings.FinalRotate;
            for (int i = 0; i + 1 < scaled.Length; i += 2)
                scaled[i + 1] = original[i + 1] * rotate;
            return scaled;
        }
    }
}
