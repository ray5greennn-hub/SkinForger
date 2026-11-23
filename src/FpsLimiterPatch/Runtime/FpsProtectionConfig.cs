using System;
using System.IO;
using Newtonsoft.Json;

namespace SkinForger.FpsProtection
{
    public sealed class FpsConfigFile
    {
        [JsonProperty("FPSLimit")]
        public int? FPSLimit { get; set; }

        [JsonProperty("enabled")]
        public bool? LegacyEnabled { get; set; }

        [JsonProperty("targetFps")]
        public int? LegacyTargetFps { get; set; }
    }

    public static class FpsProtectionConfig
    {
        public const int DefaultLimit = 60;
        public const string Template = "{\r\n  \"FPSLimit\": 60\r\n}\r\n";

        public static int Load(string path, Action<string> warning)
        {
            if (!File.Exists(path))
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, Template);
                return DefaultLimit;
            }

            try
            {
                var model = JsonConvert.DeserializeObject<FpsConfigFile>(File.ReadAllText(path));
                if (model == null) return DefaultLimit;
                int value;
                if (model.FPSLimit.HasValue)
                    value = model.FPSLimit.Value;
                else if (model.LegacyEnabled.HasValue && !model.LegacyEnabled.Value)
                    value = 0;
                else
                    value = model.LegacyTargetFps ?? DefaultLimit;
                if (value == 0) return 0;
                if (value < 15 || value > 360)
                {
                    warning("FPSLimit must be 0 or between 15 and 360; using 60.");
                    return DefaultLimit;
                }
                return value;
            }
            catch (Exception ex)
            {
                warning("Could not read FPS limiter config: " + ex.Message + "; using 60.");
                return DefaultLimit;
            }
        }

        public static bool ShouldApplyCap(int currentTarget, int limit)
        {
            return limit > 0 && (currentTarget <= 0 || currentTarget > limit);
        }
    }
}
