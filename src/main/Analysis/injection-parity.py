"""Every call the old build's Cecil steps INJECTED into Main, checked against the authored Main.

The authored Main is compiled, not post-processed, so anything a Build\\Program.cs used to insert has to be
written out by hand. Three features were found missing this way (the Preview button, the Dump trigger,
ReplacedBackup's log sink); this lists the rest.

    python -I Analysis/injection-parity.py [<SkinForger_Source>]

2026-11-21, during the full QA pass. The first version of this tool answered "28 of 40 not called", and
that number was useless twice over:

  * it looked for the injected NAME in the whole Main as one blob, so any method of that name counted.
    DumpSystemFix's Begin was reported "called" because the Main calls ReplacementExecutionContext.Begin --
    a different type, a different thing. A false green is worse than a red.
  * it listed the injected calls of every tool folder in the tree, including the four runtimes the authored
    Main does not compile at all (CharacterSpriteCanvasFix, IndependentPng, StandaloneBox3Renderer and the
    source_snapshot copy). A call injected into code that is not in the build cannot be missing from it;
    those rows were noise, and noise is where a real regression hides.

So each name is now resolved to the type that DECLARES it in the runtime's own sources, and the Main has to
call it as Type.Name(...). Runtimes the csproj does not compile are listed apart, and the few that ARE
compiled and deliberately uncalled are explained here by name -- the same shape as dead-feature-check.py.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
src = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.dirname(HERE))


def _pick(*paths):
    """同一份工具在两种布局下都能跑：私有开发目录，以及公开仓库的 src/。"""
    for candidate in paths:
        if os.path.exists(candidate):
            return candidate
    return paths[0]


main = _pick(os.path.join(src, "SkinForgerMain-Authored"), os.path.join(src, "main"))

# A compiled-in runtime whose injected call is deliberately never written out. Keyed by (module, name);
# "" as the name covers every name of that runtime.
EXPLAINED = {
    ("DumpSystemFix", ""):
        "the authored Dump is Unity\\DumpSession.cs, which does this work itself: its own clock, its own\n"
        "    written / kept / problem counts, the manifest written once per atlas, and the GAME's atlas found\n"
        "    through AtlasOwnershipManager rather than a spawn flag. Nothing in the Main, and nothing in any\n"
        "    runtime it compiles, names DumpSessionManager (checked 2026-11-21). The file stays in the build\n"
        "    because three Core modules can still reach the type by reflection (AtlasBuildAccelerator,\n"
        "    FastReload, OriginalStateRestore) -- and all three stand down on the authored Main, so this is\n"
        "    dead weight, not a missing feature. Drop it from SkinForger.Main.csproj at the next Main\n"
        "    deployment that has a reason of its own; it does not deserve a deployment to itself.",
    ("ReplacedBackup", "IsExcludedPath"):
        "a filter the backup asks ITSELF before copying a file, not an entry point. The old build injected\n"
        "    the call at Main's write site; the authored Main hands the backup its folder (ReloadSkins ->\n"
        "    Initialize) and lets it decide, so the question is asked inside ReplacedBackup.",
    ("SelectionThumbnail", "IsSelectionThumbnailPath"):
        "the same shape: SelectionThumbnail's own code asks it while deciding whether a path is one of its\n"
        "    pictures. The authored Main has no reason to ask on its behalf.",
}

# the runtimes the csproj actually compiles into the Main
compiled = set()
with open(os.path.join(main, "SkinForger.Main.csproj"), encoding="utf-8-sig") as handle:
    for match in re.finditer(r'Include="\.\.\\([^\\"]+)\\', handle.read()):
        compiled.add(match.group(1))

# what the authored Main's own source says
pieces = []
for folder, dirs, files in os.walk(main):
    if any(part in folder for part in ("obj", "bin", "Build", "Tests", "Analysis")):
        continue
    for name in files:
        if name.endswith(".cs"):
            with open(os.path.join(folder, name), encoding="utf-8-sig", errors="replace") as handle:
                pieces.append(handle.read())
authored_text = "\n".join(pieces)

patterns = [
    re.compile(r'Instruction\.Create\(OpCodes\.Call,\s*H\("([A-Za-z_][\w]*)"\)'),
    re.compile(r'Instruction\.Create\(OpCodes\.Call,\s*\w*[Ii]mport\w*\([^)]*"([A-Za-z_][\w]*)"'),
]
# A TYPE DECLARATION, not the word "record" in a comment. 2026-11-21: the loose version read
# "...the record logged by..." in a comment as a type called "logged", so ReplacedBackup.BackupChangedAll --
# which the Main does call -- was reported missing. A checker's own false alarm costs the same as a bug.
DECLARES = re.compile(r'^[ \t]*(?:\[[^\]]*\][ \t]*)*'
                      r'(?:(?:public|internal|private|protected|static|sealed|abstract|partial|unsafe|new)[ \t]+)*'
                      r'(?:class|struct|record)[ \t]+([A-Za-z_]\w*)', re.MULTILINE)


def declaring_types(module, wanted):
    """member name -> the type(s) that declare it, plus every type the runtime folder declares."""
    found = {}
    every = set()
    for folder, dirs, files in os.walk(os.path.join(src, module)):
        if any(part in folder for part in ("obj", "bin", "Build", "Tests")):
            continue
        for name in files:
            if not name.endswith(".cs"):
                continue
            with open(os.path.join(folder, name), encoding="utf-8-sig", errors="replace") as handle:
                text = handle.read()
            every.update(DECLARES.findall(text))
            for member in wanted:
                site = re.search(r'\b(?:static|void|bool|int|long|string|public|internal)[^\n]*\b'
                                 + re.escape(member) + r'\s*\(', text)
                if site is None:
                    continue
                types = DECLARES.findall(text[:site.start()])   # the last type opened before the member
                if types:
                    found.setdefault(member, set()).add(types[-1])
    return found, every


rows = []
for folder, dirs, files in os.walk(src):
    if "Backups" in folder or "obj" in folder or "bin" in folder:
        continue
    if os.path.basename(folder) != "Build":
        continue
    module = os.path.basename(os.path.dirname(folder))
    names = set()
    for name in files:
        if not name.endswith(".cs"):
            continue
        with open(os.path.join(folder, name), encoding="utf-8-sig", errors="replace") as handle:
            text = handle.read()
        for pattern in patterns:
            names.update(pattern.findall(text))
    if not names:
        continue
    owners, every_type = declaring_types(module, names)
    for called in sorted(names):
        types = sorted(owners.get(called) or set())
        # Type.Name( -- the qualified call. A bare name is NOT accepted: that is what made
        # DumpSystemFix.Begin look called (the Main calls ReplacementExecutionContext.Begin).
        # Any type THIS runtime declares will do: what matters is that the call goes into this runtime,
        # not which of its classes the member was found in.
        qualified = any(re.search(r'\b' + re.escape(owner) + r'\s*\.\s*' + re.escape(called) + r'\s*\(',
                                  authored_text) for owner in (types or sorted(every_type)))
        rows.append((module, called, qualified, types))


def is_explained(module, called):
    return (module, called) in EXPLAINED or (module, "") in EXPLAINED


called_rows = [r for r in rows if r[2]]
uncalled = [r for r in rows if not r[2]]
absent = [r for r in uncalled if r[0] in compiled and not is_explained(r[0], r[1])]
explained = [r for r in uncalled if r[0] in compiled and is_explained(r[0], r[1])]
not_built = [r for r in uncalled if r[0] not in compiled]

width = max((len(m) for m, _, _, _ in rows), default=10)


def show(title, group):
    print()
    print("%s (%d)" % (title, len(group)))
    print("-" * min(len(title) + 6, 110))
    for module, called, _, types in group:
        print("  %-*s  %-34s %s" % (width, module, called, "/".join(types) or "(declaring type not found)"))


show("NOT CALLED by the authored Main -- each one is a feature that silently does nothing", absent)

print()
print("not called on purpose (%d)" % len(explained))
print("------------------------------")
shown = set()
for module, called, _, _ in explained:
    whole = (module, "") in EXPLAINED
    key = (module, "") if whole else (module, called)
    if key in shown:
        continue
    shown.add(key)
    names = sorted(c for m, c, _, _ in explained if m == module) if whole else [called]
    print("  %-*s  %s" % (width, module, ", ".join(names)))
    print("    " + EXPLAINED[key])

print()
print("the runtime is not compiled into the authored Main (%d)" % len(not_built))
print("--------------------------------------------------------")
for module in sorted({m for m, _, _, _ in not_built}):
    names = sorted(c for m, c, _, _ in not_built if m == module)
    print("  %-*s  %s" % (width, module, ", ".join(names)))

show("called", called_rows)

print()
print("%d injected call(s) in all: %d called, %d explained, %d in runtimes the Main does not compile, "
      "%d UNEXPLAINED" % (len(rows), len(called_rows), len(explained), len(not_built), len(absent)))
