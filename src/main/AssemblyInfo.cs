using MelonLoader;

// SkinForger.Main (authored) — the replacement for SkinForger.Melon.dll.
// Deployed since 2026-11-19. The old Cecil-built Main is not distributed any more: deploying both at
// once would register the type SkinForger.SkinForgerMod twice.
[assembly: MelonInfo(typeof(SkinForger.SkinForgerMod), "SkinForger", "0.1.0", "雷茂教主Ray5Green")]
[assembly: MelonGame(null, "MuseDash")]
