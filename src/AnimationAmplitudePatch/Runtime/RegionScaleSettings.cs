using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using Newtonsoft.Json.Linq;

namespace SkinForger.AnimationAmplitude
{
    public static class RegionScaleSettings
    {
        public const float DefaultScale = 1f;
        public const float MaximumScale = 100f;

        private static readonly HashSet<string> LoggedWarnings =
            new HashSet<string>(StringComparer.Ordinal);

        public static float GetScale(string regionPath)
        {
            if (string.IsNullOrEmpty(regionPath)) return DefaultScale;
            string jsonPath = Path.ChangeExtension(regionPath, ".json");
            if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath)) return DefaultScale;

            try
            {
                List<string> warnings = new List<string>();
                float scale = Parse(File.ReadAllText(jsonPath), warnings.Add);
                for (int i = 0; i < warnings.Count; i++)
                {
                    WarnOnce(jsonPath + "|" + warnings[i],
                        "Image Scale for " + Path.GetFileName(jsonPath) + ": " + warnings[i]);
                }
                return scale;
            }
            catch (Exception ex)
            {
                WarnOnce(jsonPath + "|read|" + ex.Message,
                    "Could not read image Scale from " + Path.GetFileName(jsonPath) +
                    "; using 1.0: " + ex.Message);
                return DefaultScale;
            }
        }

        public static float Parse(string json, Action<string> warning)
        {
            if (string.IsNullOrWhiteSpace(json)) return DefaultScale;
            try
            {
                JObject root = JObject.Parse(json);
                JToken token = root.GetValue("Scale", StringComparison.OrdinalIgnoreCase);
                if (token == null) return DefaultScale;
                if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
                {
                    if (warning != null) warning("Scale must be a positive number; using 1.0.");
                    return DefaultScale;
                }

                double value;
                try
                {
                    value = token.Value<double>();
                }
                catch
                {
                    if (warning != null) warning("Scale could not be parsed; using 1.0.");
                    return DefaultScale;
                }

                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > MaximumScale)
                {
                    if (warning != null) warning("Scale is outside the safe range 0..100; using 1.0.");
                    return DefaultScale;
                }
                return (float)value;
            }
            catch (Exception ex)
            {
                if (warning != null) warning("Invalid JSON; using Scale 1.0: " + ex.Message);
                return DefaultScale;
            }
        }

        private static void WarnOnce(string key, string message)
        {
            if (LoggedWarnings.Add(key)) MelonLogger.Warning("[SkinForger] " + message);
        }
    }
}
