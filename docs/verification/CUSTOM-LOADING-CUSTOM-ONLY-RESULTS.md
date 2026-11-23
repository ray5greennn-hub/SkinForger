# Extensionless config and custom-only isolation (2026-09-27)

Installed candidate SHA256:
`953355DA37236001DB666D1DD614576CC8608F5CFDF1BACEF8A333E19C1AE1B7`

Previous Cover release:
`97DC2310EAB292FE47C69A86BC5D830A91CFFE47B3961EC55C2F5EC8443E8073`

Backup: `backups/custom_only_20260927_171934`.
Evidence: `evidence/20260927-custom-only.log`.

## Root cause

The old LoadingConfig reader accepted only names with an explicit `.png`.
`custom_cover_A` and `custom_cover_B` were rejected before file lookup. Live
17:13 logs show valid count zero, followed by the intentional zero-valid original
fallback. The original picture was never part of a combined random pool.

## Implementation

An extensionless entry appends `.png`; explicit `.png` remains direct. Other
extensions, paths and traversal are rejected. Deduplication uses the resolved
full path, so `A` and `A.png` become one item.

SelectCustomIndex returns -1 only for zero valid custom files. With one or more,
Random.Next(validCount) yields indexes into decoded custom paths only. Runtime
logs state custom-only/originalProbability=0.

LoadingImg.OnEnable remains a prefix restore plus postfix replacement after the
game initializes original art. No Update, coroutine or animation was added.
Cover + Center, decoder/cache, skin paths and character replacement are unchanged.

## Tests

Offline tests passed extensionless and explicit names, alias deduplication,
one-valid-plus-one-missing, one valid at 100%, 10,000 two-image choices restricted
to indexes 0/1, zero-valid fallback, empty/malformed config and previous tests.
Compilation completed with zero warnings/errors. Static comparison found 398 core
methods unchanged and 412 previous-release methods unchanged outside reviewed
configuration/template/selection edits.

Live events using the user's extensionless config:

- 17:20:10 custom_cover_A.png, validCustom=2, originalProbability=0.
- 17:21:16 custom_cover_B.png, validCustom=2, originalProbability=0.
- 17:21:35 custom_cover_A.png, validCustom=2, originalProbability=0.
- Temporary one-image config: 17:22:28 custom_cover_A.png, validCustom=1,
  originalProbability=0.
- Restored two-image config: 17:25:14 custom_cover_B.png, validCustom=2,
  originalProbability=0.

No fallback/original event appears in this sequence. The two-name extensionless
config was restored afterward. Muse Dash exited normally and the main DLL passed
an exclusive-open check. User visual confirmation of original-picture absence
was requested but had not yet been received when this record was finalized.
