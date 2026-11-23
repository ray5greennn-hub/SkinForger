# SkinForger.Core 1.0.0: sidecars merged into one DLL (2026-11-14)

`Mods\SkinForger` previously held 23 files. It now holds 6.

| File | SHA256 |
|---|---|
| SkinForger.Core.dll (new) | E2920FC3D2FD957CA4BF74B0B48EA1D4B30A0C143171AAF11E8B02D577A26FD1 |
| SkinForger_MuseDashWatchdog.exe (single-file rebuild, same source) | 3D1100A582C166D597CAEB666A1944EED2A38807F0534413F614191187101100 |
| SkinForger.Melon.dll (Main, unchanged) | 11C58361D9E0A5A430D0F3FB6F64DF1147DAED0CC5AF6371DC957A9236B182EF |
| SkinForger.PreviewOriginalContractRestore.Runtime.dll (unchanged) | 8AC41FB3BD38736CDB1B9444AAE02A5D544943AB3FC0FAB2D18CD5B2D0B92250 |
| LICENSE, manifest.json | unchanged |

## What was merged

SkinForger.Core contains 15 former sidecars:

- Bootstrap
- AtlasBuildAccelerator
- BattleAtlasEdgeIsolation
- CharacterPolygonPackedRectRepair
- ElfinFreshPackedRect
- ElfinMeshCanvas
- FastReload
- GameplayAtlasWarmReuse
- OriginalStateRestore
- PreviewEnhancements
- PreviewLifecycleGuard
- R4_12MeshCanvas
- RuntimeTextureBilinearProbe
- UIFirstOpenLoadingPrewarm
- WindowInputShield

The 4 watchdog files (exe, dll, deps.json, runtimeconfig.json) are now a single framework-dependent exe.

## Verification

- Each sidecar's source was rebuilt and IL-compared to the deployed DLL first. All 15 match: 9 byte-identical, 6 IL-identical.
- IL-diffing every original DLL against SkinForger.Core shows only the removed `[assembly: Melon*]` attributes. Every method body is unchanged apart from the base type: `MelonMod` became `SfModule`.
- Host behavior (`Host\SkinForgerCoreMod.cs`):
  - It uses MelonPriority -10000, so the Bootstrap resolver is registered before Main.
  - The other 14 modules start from a finalizer on Main's `OnInitializeMelon`, in the original alphabetical melon order.
  - Each module has its own Harmony instance and logger. An exception in one module does not stop the others.
- Watchdog smoke test: `--mode selftest` returns exit code 2 with `invalid_mode`, as expected.
- The in-game test has not been run yet.

## Update: Watchdog 2.0.0 (2026-11-14)

Source: `src\Watchdog\Program.cs`. The v1 source is kept as `Program.v1.cs.bak`. Build output: `dist-2.0.0\`.

Previous single-file exe: `Backup_SkinForger_MuseDashWatchdog.1.x-singlefile.exe`.

**Safety fixes**

- **Residual cleanup is limited to the game's own processes.** It only touches processes whose executable is under the Muse Dash folder, plus the game's own WerFault.exe. v1 treated every process started under the game as its own, and on 2026-11-05 it killed SogouCloud.exe and SGTool.exe, which belong to the input method. The watchdog's own console window is also excluded now.
- **"File released" now means the file can actually be replaced.** The check opens the file read/write. v1 opened it read-only, which still succeeds while a DLL is mapped by a process, so it could report "released" for a file that could not be replaced.

**New behaviour**

- The game's exit code is recorded and classified: normal exit, stack overflow, access violation, and so on.
- After a crash, it waits up to 60 seconds for WerFault (the Windows crash reporter) to finish before cleaning.
- If SkinForger files are still locked, it asks Windows Restart Manager which processes hold them. It ends a holder only if all of these are true:
  - its executable is the game's;
  - it has no window;
  - it is a 0-thread crash snapshot, or it has had no window for more than 2 minutes.
- It writes a Chinese summary to `UserData\SkinForger\watchdog-status.txt`.
- New mode `--mode predeploy --dir Mods\SkinForger` checks the folder before a deploy. Exit codes:
  - 0: all files released;
  - 5: still locked;
  - 6: the game is running.

**Tests**

- Self-test passes.
- predeploy:
  - With the game closed, it reports the folder as released.
  - An unrelated program holding a file is reported and left running.
  - A windowless fake "MuseDash.exe" is left alone while it is under 2 minutes old.
  - After 2 minutes it is cleaned, while the unrelated holder keeps running.
- Deployed SHA256: 88CD5E316F95A80384E24603A56E9164A3519DA844E7CBBCD93E459F01D193C1. The predeploy check before replacing reported all files released.
- Simulated crash (exit code 0xC00000FD):
  - It is classified as a stack overflow.
  - A child process in the game folder is cleaned.
  - Unrelated cmd, ping and conhost processes are left alone.
  - The status file is written.
- Renaming a mapped DLL and copying a new file in its place works. This is the deploy fallback when a file stays locked.

## Update: SkinForger.Core 1.1 — HexieBlock (2026-11-14)

SkinForger supports the Steam release only. Censored (hexie / 和谐) resources are never dumped, read or replaced, even when matching files are on disk.

- **New module:** `Modules\HexieBlock\HexieBlockMod.cs`. It applies 12 Priority.First prefixes on Main:
  - **Reading:** GetExistingRegionPath, GetTexture, ApplyRegionPngToAtlas, TryBuildSynthesizedAtlasForSkeleton, TryFindSelectedSkinByAtlas.
  - **Dump:** TrySpawnForExtract, ExtractSkeleton, ExtractAttachment.
  - **Thumbnails:** HasHexieOriginal (removes the "Dump Hexie" button), Dump, CaptureAndApply and ReplacementPath when the hexie variant is involved.
- **Hexie detection:**
  - Any name containing "hexie": atlas, texture, attachment, region, page, skeleton data, prefab, or path.
  - Prefabs that only censored RoleSkins (`skins[].hexie == true`) or `*Hexie` config fields use. These come from the game's character and elfin config, minus any name that a normal entry also uses.
- **Module read gates:** the CharacterPolygonPackedRectRepair and FastReload PNG header reads, and R4-12 SpecResolver, skip hexie files.
- **Verification:**
  - A Cecil hook check confirms that every prefix parameter and `__result` matches the Main signature.
  - An IL diff against Core 1.0 shows changes only in the new module, the module list and the 3 gated methods.
- **Deployed Core SHA256:** 9A911AA69AD2A367C899775C5EB0F918E54D3390E5853AE00C0361BD5E65264F. Previous version: `Backup_SkinForger.Core.1.0.0.dll`.
- **Existing hexie files:** the 30 files under SkinForger\Skins (Bunny Girl `*_hexie` attachments, plus their replaced copies) were moved to `私有归档`. Manifest entries are left in place and are ignored.

## Update: HexieBlock 1.1.0 — censored Spine skins (2026-11-14)

- **Preview:** the Form/skin row no longer lists Spine skins whose name contains "hexie". Before, Zombie Girl battle showed "1: origin / 2: hexie". A preview that starts on a censored skin switches to the first normal skin.
- **New attachment rule:** an attachment counts as censored when it appears only in censored Spine skins and never in a normal skin or the default skin. The attachment's own name does not need to contain "hexie".
  - A postfix on Main `EnumerateAttachmentContexts` removes these attachments from every replacement and Preview build.
  - `ExtractSkeleton` records them, so Dump skips them even when it exports them in a later frame.
- **In-game test of 1.1.0 (log 2026-11-14_16-57-44):** blocked 10 Bunny `*_hexie` reads and the `Christmas2021_hexie` atlas lookup. Normal skins were unaffected.
- **Deployed Core SHA256:** 6ECF0C096BA4616F9F6690CA6A0F6FECECB1CB59ABE83DF330B382AAE14558DA. Previous version: `Backup_SkinForger.Core.1.1.0.dll`.

## Update: HexieBlock 1.2.0 (2026-11-14)

- **Problem in 1.1.0 (log 17-48-09):** Bunny Battle Preview failed with `transaction mismatch expected=57 committed=55`.
  - The context filter removed `skirt_L` and `skirt_R`, which are attachments used only by the censored skin.
  - PreviewSharedCore still counted them, because it builds its expected set through Main `GetExistingRegionPath`.
- **Fix:**
  - Censored-skin attachments are now learned earlier, before any lookup runs. This happens in prefixes on Main ApplySkeleton, PreviewManager.ApplyCurrentReplacement and PreviewSharedCore.Apply, as well as in TryBuild and in the enumeration.
  - `IsHexie(attachment)` now also checks that set, so `GetExistingRegionPath` returns null for those attachments.
- **Patches:** 16.
- **Deployed SHA256:** 0D13626BB2B2AD752820610BE209692998D3746E0ABEAB2DC11B6E5F9B387B6C. Previous version: `Backup_SkinForger.Core.hexie-1.1.0.dll`.

## Rollback

With Muse Dash closed:

1. Delete `Mods\SkinForger\*`.
2. Copy back `Backup_Mods_SkinForger_before_merge\*` (23 files).
