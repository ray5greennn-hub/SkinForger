"""Which members SkinForger.Core asks of Main, and whether the authored Main has them.

Static check, run from this folder:

    python -I Analysis/contract-parity.py

It reads SkinForger.Core's module sources for the members it looks up on
SkinForger.SkinForgerMod (and on Main's nested runtime types), then reads the authored Main's
own sources for what it declares. The point is not to be clever: the 2026-11-17 trial failed
because a dozen modules asked for members nobody had listed, and a list is cheaper than a
game launch.

Arity is checked where Core states it (Find(host, "X", 3), GetParameters().Length == 3,
Method(host, "X", 8)). Where Core only names a member, only the name is checked.
"""

import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MAIN = os.path.dirname(HERE)


def _pick(*paths):
    """同一份工具在两种布局下都能跑：私有开发目录，以及公开仓库的 src/。"""
    for candidate in paths:
        if os.path.exists(candidate):
            return candidate
    return paths[0]


CORE = _pick(os.path.join(os.path.dirname(MAIN), 'SkinForgerCore-20261114', 'Modules'),
             os.path.join(os.path.dirname(MAIN), 'core', 'Modules'))

# How Core asks for a member of the host type. Each pattern yields (name, arity or None).
ASKS = [
    # Find(_host, "Name", 3) / Method(_host, "Name", 8) / Find(host, "Name", -1)
    (re.compile(r'(?:Find|Method)\(\s*_?host\s*,\s*"([A-Za-z_][\w]*)"\s*,\s*(-?\d+)\s*\)'), 'pair'),
    # AccessTools.Method(host, "Name") / DeclaredMethod(host, "Name") / Hook(host, "Name", ...)
    (re.compile(r'(?:AccessTools\.)?(?:Declared)?Method\(\s*_?host\s*,\s*"([A-Za-z_][\w]*)"'), 'name'),
    (re.compile(r'Hook\(\s*host\s*,\s*"([A-Za-z_][\w]*)"'), 'name'),
    (re.compile(r'Patch\(\s*host\s*,\s*"([A-Za-z_][\w]*)"'), 'name'),
    # host.GetField("Name", ...) / host.GetProperty("Name", ...)
    (re.compile(r'_?host\.Get(?:Field|Property)\(\s*"([A-Za-z_][\w]*)"'), 'name'),
    # AccessTools.Field(host, "Name") / AccessTools.Inner(host, "Name")
    (re.compile(r'AccessTools\.(?:Field|Property|Inner)\(\s*_?host\s*,\s*"([A-Za-z_][\w]*)"'), 'name'),
    # method.Name == "Name" && ... GetParameters().Length == 3 -- the gap may not cross another string
    # literal or a statement end, or the arity of a NEARBY lookup gets attributed to this name.
    (re.compile(r'\.Name\s*==\s*"([A-Za-z_][\w]*)"[^";]{0,200}?GetParameters\(\)\.Length\s*==\s*(\d+)'), 'pair'),
    # m.Name == "Name" with no arity stated
    (re.compile(r'(?:method|m)\.Name\s*==\s*"([A-Za-z_][\w]*)"'), 'name'),
]

# Arities Core states that the authored Main deliberately does not match, with the reason. Every module
# that asks for the 8-argument shape of ApplyRegionPngToAtlas stands down when the authored Main is in
# place (it transpiles the old per-region pixel writing, which the composer does not contain). The two
# modules that DO bind to it -- HexieBlock and SkinPackages' OffsetCanvas -- look it up by name only and
# work with the authored 7-argument shape, which is what the seam exists for.
ARITY_EXPLAINED = {
    'ApplyRegionPngToAtlas': 'only the stood-down modules ask for 8; HexieBlock and OffsetCanvas bind by '
                             'name and use the authored 7-argument seam',
}

# Names this tool attributes to the host type but that Core actually looks for on a UNITY or GAME type.
# Verified one by one on 2026-11-19 (file and line in the comment); listing them keeps the report honest
# instead of carrying permanent noise that hides a real miss.
NOT_ON_THE_HOST = {
    'Button':        'GUILayout.Button -- CreatorWindowHooks.cs:63, Loc.cs:247',
    'EndHorizontal': 'GUILayout.EndHorizontal -- PreviewEnhancementsMod.cs:421',
    'GetRect':       'GUILayoutUtility.GetRect -- PreviewEnhancementsMod.cs:747',
    'UpdateMesh':    'SkeletonGraphic.UpdateMesh -- R412MeshCanvasMod.cs:71',
}

# Members that are deliberately NOT provided (CONTRACT.md 2.2): the two accelerators' fast paths.
DELIBERATELY_ABSENT = {
    'CalculateTextureChecksum', 'CalculateAlphaBounds', 'CreateTransparentTexture',
    'ReplacementDebugEnabled', 'VerboseTextureDiagnosticsEnabled', 'CreateEditableRegionTexture',
}

# Core modules that STAND DOWN on the authored Main and say so in the log. They patched the old Main's
# synthesized-atlas machinery; the authored Main composes every atlas from the game's own art each time,
# so there is nothing of theirs to patch. A member only THEY ask for cannot be a gap -- and 2026-11-21
# this tool answered "10 missing" because of them, every session, which is how a real tenth gap would
# have gone unread. Evidence, MelonLoader Latest.log of 2026-11-21 15:30:22:
#   "[SkinForge][AtlasBuildAccelerator] not needed: the authored Main composes the atlas itself"
#   ... the same line for BattleAtlasEdgeIsolation, CharacterPolygonPackedRectRepair, ElfinFreshPackedRect,
#   GameplayAtlasWarmReuse; "not installed: the authored Main does not need it" for FastReload and
#   OriginalStateRestore; ElfinMeshCanvas says its work belongs in R4-12.
STOOD_DOWN = {
    'AtlasBuildAccelerator', 'BattleAtlasEdgeIsolation', 'CharacterPolygonPackedRectRepair',
    'ElfinFreshPackedRect', 'ElfinMeshCanvas', 'FastReload', 'GameplayAtlasWarmReuse',
    'OriginalStateRestore',
}

# Asked for by a module that IS running, and absent on purpose. Each one has to be a sentence, not a set.
ABSENT_EXPLAINED = {
    'GameplayScope':
        'absent by design, and explained in full by field-name-parity.py. It gated the old Main\'s '
        'synthesized atlas state; the authored Main keeps its own decoded images (TextureBridge, keyed by '
        'each file\'s length and write time), so ReloadFilter says "not needed" instead of warning.',
}


def core_asks():
    """{member: {'arity': set(), 'modules': set()}}"""
    asks = {}
    for root, _dirs, files in os.walk(CORE):
        for file in files:
            if not file.endswith('.cs'):
                continue
            path = os.path.join(root, file)
            module = os.path.relpath(path, CORE).split(os.sep)[0]
            if module == 'MainBridge':
                continue   # the bridge is allowed to probe for both generations
            with open(path, encoding='utf-8-sig', errors='replace') as handle:
                text = handle.read()
            if 'SkinForger.SkinForgerMod' not in text and '_host' not in text:
                continue
            for pattern, kind in ASKS:
                for match in pattern.finditer(text):
                    name = match.group(1)
                    entry = asks.setdefault(name, {'arity': set(), 'modules': set()})
                    entry['modules'].add(module)
                    if kind == 'pair':
                        arity = int(match.group(2))
                        if arity >= 0:
                            entry['arity'].add(arity)
    return asks


DECLARATION = re.compile(
    r'^\s*(?:public|internal|private)\s+(?:static\s+)?(?:readonly\s+)?[\w<>,\[\]?\. ]+?\s(\w+)\s*(\(|=>|\{|;|=)',
    re.MULTILINE)


def main_declares():
    """{member: set(arity)} — arity counted from the declaration's parameter list, properties/fields None."""
    declared = {}
    for root, _dirs, files in os.walk(MAIN):
        if any(part in root for part in ('obj', 'bin', 'Build', 'Tests', 'Analysis')):
            continue
        for file in files:
            if not file.endswith('.cs'):
                continue
            with open(os.path.join(root, file), encoding='utf-8-sig', errors='replace') as handle:
                text = handle.read()
            for match in DECLARATION.finditer(text):
                name, follows = match.group(1), match.group(2)
                arities = declared.setdefault(name, set())
                if follows != '(':
                    arities.add(None)
                    continue
                # count top-level commas of the parameter list
                start = match.end(2)
                depth, params, seen = 1, 1, False
                for char in text[start:start + 2000]:
                    if char in '(<[':
                        depth += 1
                    elif char in ')>]':
                        depth -= 1
                        if depth == 0:
                            break
                    elif char == ',' and depth == 1:
                        params += 1
                    if not char.isspace():
                        seen = True
                arities.add(params if seen else 0)
    return declared


SEAM_NOT_CALLED_BY_MAIN = {
    # Seams something OTHER than Main's own code drives, with what drives them. Anything NOT listed here
    # and never called from Main's sources is a seam Core patches that nothing ever reaches -- the shape
    # of the 2026-11-20 elfin bug, where GetRegionAnchor existed only to be patched and Main read the
    # sidecar itself, so Core's AutoAnchor and OffsetCanvas were dead and four parts were misplaced.
    'CharacterApplyOnEnable': "the game's own hook",
    'CharacterShows': "the game's character page",
    'Scan': "the game's object hooks",
    'GetRootSkinDataDirectories': 'Core (RoleSkins) asks Main for the roots',
    'GetSelected': 'the window and Core',
    'DeselectAll': 'the window',
    'ApplyRegionPlacementToRegion': 'absent from the authored Main (see MISSING)',
}


def main_calls():
    """Every name Main's own sources call, as Name( or .Name(."""
    text = []
    for root, dirs, files in os.walk(MAIN):
        dirs[:] = [d for d in dirs if d not in ('obj', 'bin', 'Build', 'Tests', 'Analysis')]
        for file in files:
            if file.endswith('.cs'):
                with open(os.path.join(root, file), encoding='utf-8-sig', errors='replace') as handle:
                    text.append(handle.read())
    body = chr(10).join(text)
    return set(re.findall(r'(?:[.]|\b)(\w+)\s*\(', body))


def main():
    asks = core_asks()
    declared = main_declares()
    called = main_calls()

    missing, arity_mismatch, present = [], [], []
    for name in sorted(asks):
        entry = asks[name]
        modules = ', '.join(sorted(entry['modules']))
        if name not in declared:
            if name in NOT_ON_THE_HOST:
                present.append((name, modules, 'not on the host: ' + NOT_ON_THE_HOST[name]))
            elif name in DELIBERATELY_ABSENT:
                present.append((name, modules, 'absent'))
            else:
                missing.append((name, modules, 'absent'))
            continue
        wanted = entry['arity']
        have = declared[name]
        if wanted and not (wanted & have) and None not in have:
            shape = f'Core wants {sorted(wanted)}, Main has {sorted(a for a in have if a is not None)}'
            if name in ARITY_EXPLAINED:
                present.append((name, modules, 'ok (on purpose: ' + ARITY_EXPLAINED[name] + ')'))
            else:
                arity_mismatch.append((name, modules, shape))
        else:
            present.append((name, modules, 'ok'))

    def show(title, rows):
        print(f'\n{title} ({len(rows)})')
        print('-' * len(f'{title} ({len(rows)})'))
        for name, modules, note in rows:
            print(f'  {name:38} {note:44} {modules}')

    # A seam can be present, the right arity, and still DEAD: declared only so Core can patch it, and
    # never reached. Core's prefix / postfix then never runs and the feature silently does not exist.
    uncalled = []
    for name, modules, note in present:
        if note.startswith('absent') or note.startswith('not on the host'):
            continue
        if not asks[name]['arity']:
            continue          # a field or a type Core reads, not a method it patches
        if name in called or name in SEAM_NOT_CALLED_BY_MAIN:
            continue
        uncalled.append((name, modules, 'declared for Core to patch, nothing in Main calls it'))

    # Split the missing ones: a member only the stood-down modules ask for is not a gap in the Main.
    real, stood_down, explained = [], [], []
    for name, modules, note in missing:
        askers = {m.strip() for m in modules.split(',')}
        if name in ABSENT_EXPLAINED:
            explained.append((name, modules, note))
        elif askers and askers <= STOOD_DOWN:
            stood_down.append((name, modules, note))
        else:
            real.append((name, modules, note))

    show('MISSING from the authored Main -- a module that IS running asks for it', real)
    show('ARITY MISMATCH', arity_mismatch)
    show('NEVER REACHED -- Core patches it, Main never calls it', uncalled)

    if explained:
        print(f'\nabsent on purpose ({len(explained)})')
        print('-' * 30)
        for name, modules, _ in explained:
            print(f'  {name:38} {modules}')
            print(f'    {ABSENT_EXPLAINED[name]}')

    show('absent, and only the stood-down modules ask for it', stood_down)
    show('satisfied', present)

    print(f'\n{len(present)} satisfied, {len(real)} missing, {len(arity_mismatch)} arity mismatches, '
          f'{len(uncalled)} never reached; {len(stood_down)} asked for only by stood-down modules, '
          f'{len(explained)} absent on purpose')
    return 1 if real or arity_mismatch or uncalled else 0


if __name__ == '__main__':
    sys.exit(main())
