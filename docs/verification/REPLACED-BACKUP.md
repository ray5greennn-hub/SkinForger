# Replaced Backup: per-file incremental history (2026-09-27)

Status: incremental correction passed offline .NET 6 filesystem tests and static
IL verification; in-game Reload/Dump verification pending. The previous full
snapshot design was rejected in user testing and has been removed.

`Runtime/ReplacedBackup.cs` is compiled against the installed .NET 6.0.36 runtime,
then embedded into the existing `SkinForger.Melon.dll` by `Build/Program.cs`.
The input is pinned to Offset candidate SHA256
`ECCA713898AB9EDD9B2F82CF01F91EF4A748AEFBB8C7472D11F44E620214EAFE`.
Only the main DLL is deployed. Runtime.dll and test/build outputs stay here.
The readable `CurrentModDecompiled` snapshot mirrors the call-site edits but
is still not a directly buildable original-source project.

## Behavior

- Explicit Reload enumerates PNG/JSON beneath character directories but copies
  only files whose SHA-256 content differs from the persisted last-known hash.
  Root-level menu/selection configuration is excluded.
- First initialization enrolls unknown files without copying them. Existing
  tracked hashes are never reset at startup, so offline edits across restart are
  detected. Unknown/new files establish a baseline and do not trigger snapshots.
- State lives in `UserData/SkinForger/replaced-state-v1.json`, outside `Skins`.
  It is persisted through an atomic temp-file rename after successful backups.
  Failed backups do not advance the known hash. Corrupt state blocks protected
  writes rather than silently discarding history.
- Each source file is saved beside its containing directory under `replaced`.
- First backup keeps the exact filename. Subsequent copies use `_1`, `_2`, etc.
  `FileMode.CreateNew` prevents overwriting an earlier backup even on collision.
- Unchanged files and repeated Reloads create no backups. Timestamp-only changes
  create no backups; changed content with identical size/timestamp is detected.
- Dump and Dump All do not trigger any tree backup. Dump still skips PNG/anchor
  JSON that already exist. An actual write protects only its changed target.
- Actual Dump PNG/JSON/manifest writes use protected wrappers. Failed backup
  aborts the write. An unknown file about to be overwritten is protected
  conservatively. Successful SF writes update the known hash, preventing SF's own
  output from being misclassified as a user edit on the next Reload.
- `replaced` is case-insensitively excluded at every path component. Directory
  enumeration, PNG validation, manifest/anchor reads and Dump destinations share
  the exclusion policy. `_Atlas`, `_Debug`, existing backup/cache exclusions and
  filesystem reparse points are excluded too.
- No per-frame work, new lifecycle patch, PNG decode or atlas algorithm change.
- The actual image renderer remains Spine. Offset behavior is preserved from
  the input candidate; nonzero Offset visual tests are still pending.

## Verification

`Tests/Program.cs` tests 50 PNGs plus JSON in two isolated skins: one-file edits,
PNG plus JSON edits, JSON-only edits, repeated unchanged Reload, timestamp-only
changes, same-length/same-timestamp content changes, restart persistence, version
retention, protected overwrites, excluded backups, failures and corrupt state.

`Verify/Program.cs` compares all untouched methods (370), checks every backup
entry point, compares the embedded helper IL with the tested helper, and confirms
no extra private runtime DLL or .NET 10 runtime dependency is introduced.

Known boundaries: the initial baseline represents current known bytes, not a claim
that the user's files are original game assets. Edits before the first enrollment
cannot be inferred without earlier hashes. Losing/deleting state loses that
comparison baseline; existing backup files are never deleted. Hashing occurs at
initial enrollment and explicit Reload, with no per-frame IO; large libraries
may make Reload slower. Close external saves before clicking Reload.
