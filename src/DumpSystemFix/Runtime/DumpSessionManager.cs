using System;
using System.Collections.Generic;
using System.Diagnostics;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SkinForger.DumpSystem
{
    internal static class DumpWriteBridge
    {
        // Rebound by the Cecil build to SkinForger.ReplacedBackup.WriteResourceBytes.
        public static void WriteResourceBytes(string path, byte[] bytes)
        {
            throw new InvalidOperationException("Dump write bridge was not rebound");
        }

        // Rebound by the Cecil build to SkinForgerMod.LoadManifest/SaveManifest.
        public static void FlushManifest(string skin)
        {
            throw new InvalidOperationException("Manifest flush bridge was not rebound");
        }
    }

    public static class DumpSessionManager
    {
        private static readonly Dictionary<int, Texture2D> ReadableAtlases = new Dictionary<int, Texture2D>();
        private static readonly HashSet<string> DirtyManifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Stopwatch Total = new Stopwatch();
        private static string _mode = "Dump";
        private static bool _active;
        private static bool _spawnInProgress;
        private static bool _indexOnlyRefreshRequested;
        private static int _attachmentAttempts;
        private static int _written;
        private static int _skipped;
        private static int _failed;
        private static int _atlasHits;
        private static int _atlasMisses;
        private static int _manifestWrites;
        private static long _encodedBytes;
        private static double _readbackMs;
        private static double _encodeMs;
        private static double _writeMs;
        private static double _manifestMs;
        private static string _lastExtracted = string.Empty;

        public static void Begin(string mode)
        {
            CleanupReadableAtlases();
            DirtyManifests.Clear();
            _mode = string.IsNullOrEmpty(mode) ? "Dump" : mode;
            _active = true;
            _spawnInProgress = false;
            _attachmentAttempts = 0;
            _written = 0;
            _skipped = 0;
            _failed = 0;
            _atlasHits = 0;
            _atlasMisses = 0;
            _manifestWrites = 0;
            _encodedBytes = 0;
            _readbackMs = 0;
            _encodeMs = 0;
            _writeMs = 0;
            _manifestMs = 0;
            _lastExtracted = string.Empty;
            Total.Restart();
        }

        public static void RecordAttachmentAttempt()
        {
            if (_active) _attachmentAttempts++;
        }

        public static void RecordSkipped()
        {
            if (_active) _skipped++;
        }

        public static void RecordFailed()
        {
            if (_active) _failed++;
        }

        public static void RecordExtractedName(string name)
        {
            if (_active) _lastExtracted = name ?? string.Empty;
        }

        public static Texture2D GetReadableAtlas(Texture2D source)
        {
            if ((Object)source == null) return null!;
            int id = source.GetInstanceID();
            if (ReadableAtlases.TryGetValue(id, out Texture2D? cached) && (Object)cached != null)
            {
                _atlasHits++;
                return cached;
            }

            Stopwatch timer = Stopwatch.StartNew();
            Texture2D readable = MakeReadable(source);
            timer.Stop();
            _readbackMs += timer.Elapsed.TotalMilliseconds;
            _atlasMisses++;
            ReadableAtlases[id] = readable;
            return readable;
        }

        public static void ReleaseReadable(Texture2D texture)
        {
            // Batch cache owns this texture until End().
        }

        public static void ReleaseReadableObject(Object texture)
        {
            // Batch cache owns this texture until CompleteCostume()/End().
        }

        public static void DestroyTemporary(Texture2D texture)
        {
            if ((Object)texture != null) Object.DestroyImmediate(texture);
        }

        public static void DestroyTemporaryObject(Object value)
        {
            if (value != null) Object.DestroyImmediate(value);
        }

        public static void EncodeAndWritePng(string path, Texture2D texture)
        {
            Stopwatch timer = Stopwatch.StartNew();
            Il2CppStructArray<byte> encoded = Il2CppImageConversionManager.EncodeToPNG(texture);
            byte[] bytes = encoded;
            timer.Stop();
            _encodeMs += timer.Elapsed.TotalMilliseconds;

            timer.Restart();
            DumpWriteBridge.WriteResourceBytes(path, bytes);
            timer.Stop();
            _writeMs += timer.Elapsed.TotalMilliseconds;
            _encodedBytes += bytes.LongLength;
            _written++;
        }

        public static void MarkManifestDirty(string skin)
        {
            if (!string.IsNullOrEmpty(skin)) DirtyManifests.Add(skin);
        }

        public static bool ConsumeManifestDirty(string skin)
        {
            return !string.IsNullOrEmpty(skin) && DirtyManifests.Remove(skin);
        }

        public static void CompleteCostume(string skin)
        {
            if (ConsumeManifestDirty(skin))
            {
                long started = BeginManifestWrite();
                DumpWriteBridge.FlushManifest(skin);
                EndManifestWrite(started);
            }
            CleanupReadableAtlases();
        }

        public static long BeginManifestWrite()
        {
            return Stopwatch.GetTimestamp();
        }

        public static void EndManifestWrite(long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            _manifestMs += elapsed * 1000.0 / Stopwatch.Frequency;
            _manifestWrites++;
        }

        public static void BeginSpawn()
        {
            _spawnInProgress = true;
        }

        public static void EndSpawn()
        {
            _spawnInProgress = false;
        }

        public static bool ShouldSkipLifecycleApply()
        {
            return _spawnInProgress;
        }

        public static void RequestIndexOnlyRefresh()
        {
            _indexOnlyRefreshRequested = true;
        }

        public static bool ConsumeIndexOnlyRefresh()
        {
            bool value = _indexOnlyRefreshRequested;
            _indexOnlyRefreshRequested = false;
            return value;
        }

        public static bool IsIndexOnlyRefreshRequested()
        {
            return _indexOnlyRefreshRequested;
        }

        public static void End()
        {
            if (!_active) return;
            foreach (string skin in new List<string>(DirtyManifests))
                CompleteCostume(skin);
            Total.Stop();
            _spawnInProgress = false;
            CleanupReadableAtlases();
            double measured = _readbackMs + _encodeMs + _writeMs + _manifestMs;
            double other = Math.Max(0.0, Total.Elapsed.TotalMilliseconds - measured);
            MelonLogger.Msg(string.Format(
                "[SkinForge][DumpProfile] mode={0}; totalMs={1:F1}; attempts={2}; written={3}; skipped={4}; failed={5}; atlasReadbacks={6}; atlasCacheHits={7}; atlasReadbackMs={8:F1}; pngEncodeMs={9:F1}; diskWriteMs={10:F1}; manifestWrites={11}; manifestWriteMs={12:F1}; otherMs={13:F1}; encodedMiB={14:F2}; last={15}",
                _mode, Total.Elapsed.TotalMilliseconds, _attachmentAttempts, _written, _skipped, _failed,
                _atlasMisses, _atlasHits, _readbackMs, _encodeMs, _writeMs, _manifestWrites,
                _manifestMs, other, _encodedBytes / 1048576.0, _lastExtracted));
            _active = false;
            DirtyManifests.Clear();
        }

        private static Texture2D MakeReadable(Texture2D source)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height);
            try
            {
                temporary.filterMode = FilterMode.Point;
                RenderTexture.active = temporary;
                Graphics.Blit(source, temporary);
                Texture2D readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
                readable.Apply();
                readable.name = source.name;
                readable.filterMode = FilterMode.Point;
                readable.wrapMode = TextureWrapMode.Clamp;
                return readable;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
            }
        }

        private static void CleanupReadableAtlases()
        {
            foreach (Texture2D texture in ReadableAtlases.Values)
                if ((Object)texture != null) Object.DestroyImmediate(texture);
            ReadableAtlases.Clear();
        }
    }
}
