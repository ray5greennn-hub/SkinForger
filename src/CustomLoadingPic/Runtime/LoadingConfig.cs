using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace SkinForger
{
    public static class LoadingConfig
    {
        public const string Template = "{\n  \"LoadingPictures\": [\n    \"1\",\n    \"2\",\n    \"3\"\n  ]\n}\n";

        public static List<string> Read(string folder, Action<string> warning)
        {
            var result = new List<string>();
            if (!Directory.Exists(folder)) return result;
            string config = Path.Combine(folder, "config.json");
            if (!File.Exists(config)) { File.WriteAllText(config, Template); return result; }
            string text = File.ReadAllText(config);
            if (string.IsNullOrWhiteSpace(text)) return result;
            var root = JObject.Parse(text);
            var entries = root["LoadingPictures"];
            if (entries == null || entries.Type == JTokenType.Null) return result;
            if (!(entries is JArray)) throw new FormatException("LoadingPictures must be an array of PNG filenames.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry.Type != JTokenType.String) { warning("Invalid filename entry"); continue; }
                string name = ((string)entry)?.Trim();
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    name.Contains('/') || name.Contains('\\') || name != Path.GetFileName(name))
                { warning("Only an image filename in CustomLodingPic is allowed: " + name); continue; }
                string extension = Path.GetExtension(name);
                if (string.IsNullOrEmpty(extension)) name += ".png";
                else if (!string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
                { warning("Unsupported image extension: " + name); continue; }
                string path = Path.GetFullPath(Path.Combine(folder, name));
                if (!seen.Add(path)) { warning("Duplicate ignored: " + name); continue; }
                if (!File.Exists(path)) { warning("File not found: " + name); continue; }
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                { warning("Linked file ignored: " + name); continue; }
                result.Add(path);
            }
            return result;
        }

        public static int SelectCustomIndex(int validCount, Random random)
        {
            if (validCount <= 0) return -1;
            if (random == null) throw new ArgumentNullException(nameof(random));
            return random.Next(validCount);
        }
    }
}
