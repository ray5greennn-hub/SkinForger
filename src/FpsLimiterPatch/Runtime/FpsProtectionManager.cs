using System;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace SkinForger.FpsProtection
{
    public static class FpsProtectionManager
    {
        private static bool _loaded;
        private static int _limit;
        private static string _configPath;

        public static void InitializeEarly()
        {
            if (_loaded) return;

            _configPath = Path.Combine(Environment.CurrentDirectory, "SkinForger", "Skins", "SkinForgeFpsLimiter.json");
            _limit = FpsProtectionConfig.Load(_configPath, Warning);
            _loaded = true;
            MelonLogger.Msg("[SkinForger] FPS protection loaded: FPSLimit=" + _limit +
                ", config=" + _configPath + ", vSync=preserved, polling=none");
            ApplyCap("melon-initialize");
        }

        public static void ApplyAtApplicationStart()
        {
            if (!_loaded) InitializeEarly();
            ApplyCap("application-start");
        }

        public static void ApplyAfterSceneInitialized(int buildIndex, string sceneName)
        {
            if (!_loaded) InitializeEarly();
            ApplyCap("scene-initialized:" + buildIndex + ":" + sceneName);
        }

        private static void ApplyCap(string stage)
        {
            if (_limit == 0) return;
            try
            {
                int before = Application.targetFrameRate;
                int vSyncBefore = QualitySettings.vSyncCount;
                if (FpsProtectionConfig.ShouldApplyCap(before, _limit))
                    Application.targetFrameRate = _limit;
                int after = Application.targetFrameRate;
                if (before != after || stage == "melon-initialize")
                    MelonLogger.Msg("[SkinForger] FPS protection " + stage + ": targetFrameRate " +
                        before + "->" + after + ", vSyncCount=" + vSyncBefore + " (unchanged)");
            }
            catch (Exception ex)
            {
                Warning("FPS protection skipped at " + stage + ": " + ex.Message);
            }
        }

        private static void Warning(string text)
        {
            MelonLogger.Warning("[SkinForger] " + text);
        }
    }
}
