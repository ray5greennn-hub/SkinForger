# Cover + Center test record (2026-09-27)

Candidate installed: `97DC2310EAB292FE47C69A86BC5D830A91CFFE47B3961EC55C2F5EC8443E8073`.
Previous release: `88F3E5A12B48E37D92E9B9C5C206D411DFD52CFDE851C298A518B5A405219235`.
Backup: `backups/cover_20260927_170120` (main DLL and original config).

## Modified files

- Runtime/CustomLoadingPicManager.cs: only AfterLoading changes; the existing
  native OnEnable hook, restore, decoder, cache, random selection and error paths
  remain unchanged.
- Build/Program.cs: fourth embedded type and previous-release regression check.
- Tests/Tests.csproj and Tests/Program.cs: geometry tests plus synthetic PNGs.
- README.md and game SkinForger/CustomLodingPic/README.txt: Cover documentation.
- Game config.json: temporarily selects the five test PNGs.

## New files

- Runtime/CoverGeometry.cs: independent expected-layout math and validation.
- Five synthetic SF_COVER_*.png fixtures (4000x4000, 1920x1080, 3000x500,
  500x3000, 100x80) with a centered circle and square grid.
- This report; dist-cover/SkinForger.Melon.dll is the single deployment candidate.

## Image scaling

The original Image's RectTransform is the target viewport. A child RectTransform
fills it and owns RectMask2D. A centered RawImage inside uses the source aspect
and AspectRatioFitter.EnvelopeParent. Only the clipping viewport stretches with
its parent; image dimensions remain proportional. Unity's existing UI layout
handles parent-size changes, with no new Mod Update/LateUpdate or animation.

Expected scale is max(targetWidth/sourceWidth, targetHeight/sourceHeight).
The excess width/height is clipped equally on opposite sides. RawImage keeps
the full texture UV range; clipping is performed by RectMask2D, not by resizing
or writing the source PNG. No fixed display resolution is used by runtime code.
Logs read back the actual image RectTransform as well as expected dimensions.

## Offline results

Runtime and builder compiled with zero errors and warnings. All 413 existing
methods besides AfterLoading match the previous installed release. The original
398 core methods also match the pinned pre-loading baseline.

Each of five source dimensions passed four viewport aspect ratios (1920x1080,
1024x768, 2560x1080, 800x1200). All 20 cases assert equal XY scale, coverage,
center symmetry, and minimal cover. Four invalid viewport cases safely throw
into the manager's fallback guard. Existing configuration and migration tests pass.

Illustrative results for a 1920x1080 viewport (not a runtime constant):

| Source | Uniform scale | Scaled image | Crop per side X / Y |
| --- | --- | --- | --- |
| 4000x4000 | 0.48 | 1920x1920 | 0 / 420 |
| 1920x1080 | 1 | 1920x1080 | 0 / 0 |
| 3000x500 | 2.16 | 6480x1080 | 2280 / 0 |
| 500x3000 | 3.84 | 1920x11520 | 0 / 5220 |
| 100x80 | 19.2 | 1920x1536 | 0 / 228 |

## Live results

Game process `<REDACTED>` started at 17:03:36 after the initial Steam request had produced
only an empty log. Hook initialized at 17:03:43. The backup baseline remained
new=0/tracked=213 and the skin path remained SkinForger/Skins.

At 17:03:54 all five configured PNGs decoded successfully. The selected 4000x4000
image used Texture2D -1328 at its full 4000x4000 size. The actual target was
2340x1440; expected uniform scale 0.585, image 2340x2340, crop 0/450 per side.
Unity's actualImageRect was exactly 2340x2340. The second transition at 17:04:33
reused the same texture ID and layout. All five PNG hashes matched the original
synthetic fixtures after loading; no on-disk resampling occurred.

At 17:09:06 the 500x3000 portrait used Texture2D -1334. Expected uniform scale
was 4.68, image 2340x14040, crop 0/6300 per side; actualImageRect was exactly
2340x14040. Thus square and extreme portrait dimensions were independently
checked against actual Unity objects. The user confirmed all visual checks normal,
including the circle/grid, fill and original character textures. The game exited;
no MuseDash process remains. Evidence: evidence/20260927-cover.log.

The config was restored to SF_TEST_GREEN.png / SF_TEST_MAGENTA.png after testing.
Synthetic dimension fixtures remain in the developer test directories, outside
the active loading-picture folder. Window resizing while a loading picture is
visible was not exercised. The wide, 16:9 and tiny inputs decoded successfully
and passed geometry tests but were not individually selected in this live log.

## Limitations

GPU maximum texture size and available memory still apply. Small inputs may blur
when enlarged; transparent or black borders already inside a source PNG remain
source pixels, not letterboxing introduced by the fitter. Unrecognized loading
hierarchies retain the existing original-image fallback. Window-resize behavior
uses Unity's layout system but requires live validation on the current game.
