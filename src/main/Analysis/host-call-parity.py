"""Everything the BUNDLED runtimes ask the Main for by name, checked against the authored Main.

contract-parity.py covers SkinForger.Core. It does NOT cover the runtimes compiled INTO Main -- PreviewMvp,
SelectionThumbnail, AnimationAmplitude and the rest -- which reach the host the same way, by reflection on
a name. Two of their asks were broken and nobody noticed:

  * GetCount("character"): the old Main answered "how many rows does that config table have"; the authored
    Main answered "how many skins does that costume have" -- same name, same signature, different meaning,
    so the Preview built an EMPTY character list and its window showed nothing at all.
  * GetDisplayName(index, elfin): simply absent, so SelectionThumbnail's fallback name was always empty.

This lists every name they ask for and whether the authored Main has it. A name that is present still has
to be read: the meaning is not something a tool can check, and the meaning is what bit us.

    python -I Analysis/host-call-parity.py [<SkinForger_Source>]
"""
import os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
MAIN = os.path.dirname(HERE)
SRC = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(MAIN)

SKIP = ("obj", "bin", "Build", "Tests", "Analysis", "Backups", "CurrentModDecompiled", "LegacyArtifacts")

# every member of the authored Main AND of each runtime compiled into it: a name the Preview asks for may
# live on a helper class (AnimationAmplitudeManager.BeginSkeleton) rather than on SkinForgerMod itself.
members = set()
roots = [MAIN]
with open(os.path.join(MAIN, "SkinForger.Main.csproj"), encoding="utf-8-sig") as handle:
    for match in re.finditer(r'<Compile Include="\.\.\\([^\\"]+)\\Runtime', handle.read()):
        folder = os.path.join(os.path.dirname(MAIN), match.group(1), "Runtime")
        if os.path.isdir(folder):
            roots.append(folder)

MEMBER = re.compile(r'\b(?:public|internal)\s+(?:static\s+)?(?:readonly\s+)?[\w<>,\[\]?.]+\s+(\w+)\s*[(={;]')
for root in roots:
    for folder, dirs, files in os.walk(root):
        if any(part in folder.split(os.sep) for part in SKIP):
            continue
        for name in files:
            if not name.endswith(".cs"):
                continue
            with open(os.path.join(folder, name), encoding="utf-8-sig", errors="replace") as handle:
                members.update(MEMBER.findall(handle.read()))

# which runtimes are compiled into Main
compiled = []
with open(os.path.join(MAIN, "SkinForger.Main.csproj"), encoding="utf-8-sig") as handle:
    for match in re.finditer(r'<Compile Include="\.\.\\([^\\"]+)\\Runtime', handle.read()):
        compiled.append(match.group(1))

ASK = [
    re.compile(r'GetMethod\(\s*"(\w+)"'),
    re.compile(r'GetField\(\s*"(\w+)"'),
    re.compile(r'HostString\(\s*"(\w+)"'),
    re.compile(r'HostInt\(\s*"(\w+)"'),
    re.compile(r'HostBool\(\s*"(\w+)"'),
    re.compile(r'AccessTools\.(?:Declared)?Method\(\s*\w+\s*,\s*"(\w+)"'),
    re.compile(r'AccessTools\.Field\(\s*\w+\s*,\s*"(\w+)"'),
]

rows = []
for module in sorted(set(compiled)):
    root = os.path.join(SRC, module, "Runtime")
    if not os.path.isdir(root):
        continue
    asked = set()
    for folder, dirs, files in os.walk(root):
        for name in files:
            if not name.endswith(".cs"):
                continue
            with open(os.path.join(folder, name), encoding="utf-8-sig", errors="replace") as handle:
                text = handle.read()
            for pattern in ASK:
                asked.update(pattern.findall(text))
    for name in sorted(asked):
        rows.append((module, name, "ok" if name in members else "ABSENT from the authored Main"))

missing = [row for row in rows if row[2] != "ok"]
width = max((len(m) for m, _, _ in rows), default=10)
for module, name, state in rows:
    if state != "ok":
        print("%-*s  %-34s %s" % (width, module, name, state))
print()
print("%d name(s) asked for by the bundled runtimes, %d absent" % (len(rows), len(missing)))
print("Names that are present are NOT proof: GetCount was present and meant something else.")
