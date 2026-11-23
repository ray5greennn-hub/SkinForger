# CustomLodingPic verification - 2026-09-27

Installed candidate SHA256:
`88F3E5A12B48E37D92E9B9C5C206D411DFD52CFDE851C298A518B5A405219235`

Preserved baseline SHA256:
`BC69D658D1975970ABDBFB65824D90D3035668311E2376F3537BAD1C8BDBFCBE`

Backup directory: `backups/20260927_164649`.
Full first-run log: `evidence/20260927-valid-and-empty.log`.

## Completed checks

| Check | Evidence / result |
| --- | --- |
| Runtime compilation | .NET 6 target, 0 warnings / 0 errors |
| Builder compilation | 0 warnings / 0 errors; final build in Build/bin/verified |
| Baseline preservation | 398 existing method bodies and exception tables unchanged |
| Private helper dependencies | Three source helper types embedded into main DLL; no Runtime.dll deployed |
| Skins migration | 396 files moved and SHA256 checked; backups and selection files preserved |
| Incremental backup continuity | Live `Baseline ready: new=0, tracked=213; no files copied` |
| Local initialization | 16:47:37 Hook installed; game process `<REDACTED>` |
| Migrated skin paths | 16:47:38 `Skin folder ready: ...\SkinForger\Skins` |
| Missing picture | Live warning for SF_TEST_MISSING.png; remaining files usable |
| Corrupt picture | Live invalid PNG signature warning; remaining files usable |
| Config allowlist | Only two valid configured images admitted; offline unlisted-file test passed |
| Green picture | Live 16:47:52, ImgLoadingVerySpecial, texture -1312, 1280x720; user confirmed seeing green |
| Magenta picture | Live 16:48:18, same target, texture -1314, 1280x720; separate visual confirmation pending |
| Random selection | Both configured valid images selected in live transitions; algorithm uses equal-weight Random.Next(count), not a claim based on statistical sampling |
| Empty array | Live 16:51:14: loaded 0, fallback original; user observed original picture on exit, matching deliberately empty config |
| Original skin regression | New-path 21 PNG decode and gameplay SkeletonRenderer logs present; user confirmed character textures normal |
| Exit / DLL lock | Process ended; main DLL opened with FileShare.None successfully |

## Offline-only cases

Missing directory, missing config/template creation, empty file, missing key,
malformed JSON, wrong JSON value type, duplicate entries, missing images,
path traversal, non-PNG names, unknown entries, selection/history preservation,
backup state root/key migration, and conflicting old/new directory preservation.

Malformed JSON and invalid field types throw to the manager's catch/restore path.
Actual Unity fallback for malformed JSON, all-invalid config, and deleting the
directory during a running game remains unverified in this report.

The first live log includes existing MelonLoader RegisterTypeInIl2Cpp cast errors
and an Info+ DNS failure. No CustomLoadingPic exception was observed. The baseline
already includes a System.Private.CoreLib 10 assembly reference; no new one is
introduced by this extension. Those unrelated items were not changed.

## Limits

PNG only. Memory scales with enabled images; first decode happens on a Loading
event and may take time for large sets. Size/mtime caching does not notice edits
that preserve both values. Only unambiguous Images in the five declared LoadingImg
illustration containers are handled. Boot animations are not targeted. The user
authorized temporarily disabling CustomLoadingScreens; its DLL is archived,
preferences untouched.

## Final test configuration

After the deliberate empty-array fallback check, config.json was restored to
only SF_TEST_GREEN.png and SF_TEST_MAGENTA.png. Missing/corrupt entries were
removed. There is no probability branch selecting original art when the valid
pool and supported Image target are available. Original art remains the safety
fallback for invalid inputs or an unsupported/ambiguous UI hierarchy.
