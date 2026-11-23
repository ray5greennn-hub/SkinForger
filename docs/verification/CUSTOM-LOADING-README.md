# CustomLodingPic test feature

This is a narrow binary-derived extension of the verified incremental-backup
baseline `BC69D658D1975970ABDBFB65824D90D3035668311E2376F3537BAD1C8BDBFCBE`.
Attribution to the upstream project is unchanged (see NOTICE.md). This directory is the
authoritative build source for this addition; CurrentModDecompiled is historical.

## Runtime

- `LoadingPaths.cs`: moves legacy `Skins` to `SkinForger/Skins` only when the
  destination is absent. Existing destinations are never merged or overwritten.
  Backup state remains in game `UserData/SkinForger`; old absolute keys are
  translated at the read boundary without changing the stored hashes.
- `LoadingConfig.cs`: explicit PNG allowlist, case-insensitive deduplication,
  filename validation, no recursive discovery. Entries may use `name` or
  `name.png`; extensionless names resolve to PNG, and aliases deduplicate after
  resolution. Missing, empty or invalid inputs
  fall back to the original illustration through the manager's exception guard.
- `CustomLoadingPicManager.cs`: the local Assembly-CSharp interop confirms
  `Il2Cpp.LoadingImg.OnEnable()` and five illustration fields. A prefix restores
  the previous original Image; a postfix inspects only those declared containers
  and overlays a RawImage on an unambiguous active Image. The original native
  OnEnable always runs. No original loading asset is dumped or overwritten.
  This UI RawImage does not alter SkinForger's Spine replacement architecture.
- The installed IL2CPP image decoder is reused. No Spine PMA processing is
  applied to these ordinary UI pictures. Textures are cached by path/size/mtime,
  released when removed/changed, and protected against unused-asset unloading.
  Selection is Random.Next(valid decoded count), one equal-weight entry per file.
- All Unity access is in application start or LoadingImg.OnEnable on the game
  thread. No new frame callback, coroutine, task, timer, global renderer scan,
  Skeleton patch, atlas fallback, or third-party assembly dependency is added.
- Existing CustomLoadingScreens conflicts with this UI owner; the manager skips
  initialization if that Mod already owns a postfix. For this test the user
  authorized temporarily archiving that Mod, preserving its DLL and preferences.
- A non-empty decoded pool always calls Random.Next against that custom list.
  Original art is not a pool entry and has no probability. Zero valid customs is
  the sole normal fallback; live logs state custom-only/originalProbability=0.
- Cover adaptation: an owned RectMask2D viewport follows the original Image's
  visible parent container rather than the game's oversized Cover Image node.
  Its centered child RawImage uses AspectRatioFitter.EnvelopeParent,
  not FitInParent. Unity's UI layout keeps the source aspect and reacts to parent
  dimensions; the viewport clips excess equally around the center. No fixed
  resolution, disk resizing, custom frame callback, or loading animation is used.
  CoverGeometry independently calculates the expected scale/crop for tests and
  one-shot logs; actualImageRect is read back from the Unity RectTransform.

## Build and verification

Runtime targets the installed .NET 6 assemblies; Build/Tests use the installed
.NET 10 SDK offline. Build accepts only the pinned baseline and embeds the helper
types into the main Mod; do not deploy Runtime.dll.

1. `dotnet build Runtime/Runtime.csproj -c Release`
2. `dotnet build Build/Build.csproj -c Release`
3. Run Build.dll with baseline DLL, compiled Runtime.dll, candidate output path.
4. `dotnet run --project Tests/Tests.csproj -c Release` from the game root.

For Cover, the helper count is four. Pass the previous loading release as an
optional fourth builder argument to assert that all 413 previous methods except
CustomLoadingPicManager.AfterLoading remain unchanged. The original input remains
the BC69D658 baseline. Cover output goes to dist-cover; the historical
Install-Test.ps1 remains pinned to the initial loading test release.

Cover fixtures include 4000x4000, 4000x2000, 2000x4000, 1920x1080 and 100x80 PNGs
with a centered circle and square grid. Geometry tests use four target aspect
ratios for each input. These synthetic files are separate from user artwork.

The builder asserts 398 existing method bodies/exception tables remain unchanged,
excluding SetupPaths, OnApplicationStart and backup initialization key/path reads.
The stable baseline already has a System.Private.CoreLib 10 reference; this
extension adds no new .NET 10 runtime references.

Install-Test.ps1 requires the game to be fully exited, probes an exclusive DLL
handle, backs up the baseline and backup state, moves Skins within the game root,
hash-verifies every moved file, archives CustomLoadingScreens, and installs the
pinned candidate. It does not launch the game or operate UI.

## Validation status

Compilation and configuration/filesystem tests passed. Live initialization,
both texture decodes/selections, visible green card, empty-array original-image
fallback, normal character textures and clean exit were verified. See
TEST_RESULTS.md for exact evidence and remaining offline-only cases.

Final Cover extension: five input sizes passed 20 geometry cases. Live evidence
showed the original Image was 2340x1440 while the real visible parent was
1920x1080. The 2847x2847 square produced a centered 1920x1920 RawImage with
420 units cropped above and below; the user confirmed the final visual result.
The installed DLL hash is recorded in the Chinese development log.

Memory use grows with the enabled image set. First use decodes that set on the
main thread; very large libraries can increase transition latency. PNG only.
Caching does not detect edits preserving both size and modification timestamp.
Dynamic opening animation is outside this feature. Ambiguous illustration
hierarchies are deliberately left unchanged and logged.
