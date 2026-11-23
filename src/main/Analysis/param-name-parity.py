"""Whether the PARAMETER NAMES of Core's Harmony patches exist on the authored Main's methods.

Static check, run from this folder:

    python -I Analysis/param-name-parity.py

Why this tool exists: Harmony binds a patch's parameters to the target's parameters BY NAME, and
throws when a name is not there — which kills the whole module, not just that patch. The
2026-11-19 trial failed exactly this way with 23 of 25 hooks fine:

    Parameter "resourceName" not found in ... TrySpawnForExtract(string prefabName, Action<GameObject> use)
    Parameter "obj" not found in ...          Scan(GameObject gameObject, bool elfin)

contract-parity.py checks names and arity and says so itself: it cannot see parameter names.
This one does only that. A rename inside the authored Main is therefore a CONTRACT BREAK, and
this is what catches it without launching the game.

Harmony's own injected parameters (__instance, __result, ___field and friends) and a
transpiler/finalizer's plumbing are not bound by name, so they are skipped.
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

# Form 1 -- Hook(host, "Target", nameof(PatchMethod)) / Patch(host, "Target", nameof(PatchMethod))
HOOK = re.compile(
    r'(?:Hook|Patch)\(\s*_?host\s*,\s*"([A-Za-z_]\w*)"\s*,\s*(?:nameof\(\s*(\w+)\s*\)|"(\w+)")')

# Form 2 -- the target is looked up into a local first:
#   MethodInfo? scan = AccessTools.DeclaredMethod(host, "Scan");
#   harmony.Patch(scan, prefix: new HarmonyMethod(typeof(X), nameof(ScanPrefix)), postfix: ...);
# This is how the 2026-11-19 trial's second failure (Scan / "obj") got in: form 1 alone misses it.
LOOKUP = re.compile(
    r'(\w+)\s*=\s*(?:AccessTools\.)?(?:Declared)?(?:Method|Field|Property)\(\s*_?host\s*,\s*"([A-Za-z_]\w*)"')

# Form 3 -- the lookup is a LINQ query over the host's methods instead of an AccessTools call:
#   MethodInfo? save = host.GetMethods(all).FirstOrDefault(m => m.Name == "SaveSelection" && ...);
#   harmony.Patch(save, prefix: ... nameof(SavePrefix) ...);
# This is how the 2026-11-19 16:13 failure (SaveSelection / "source") got past forms 1 and 2. Every
# way Core can name a target has to be here, or the tool reports a clean run that the game disproves.
LOOKUP_LINQ = re.compile(
    r'(\w+)\s*=\s*_?host\.GetMethods\([^)]*\)\s*\.\s*(?:FirstOrDefault|Single|First|SingleOrDefault)'
    r'\([^)]*?\.Name\s*==\s*"([A-Za-z_]\w*)"')
PATCH_CALL = re.compile(r'\w*[Hh]armony\w*\.Patch\(\s*(\w+)\s*,(.*?)\);', re.DOTALL)
HARMONY_METHOD = re.compile(r'nameof\(\s*(\w+)\s*\)')

# Harmony fills these itself; they are never matched against the target's parameter list.
# __0 / __1 bind BY POSITION, so they are safe against a rename too.
PLUMBING = re.compile(
    r'^(?:__instance|__result|__state|__args|__originalMethod|__exception|__\d+|___\w+)$')
PLUMBING_TYPES = ('IEnumerable<CodeInstruction>', 'ILGenerator', 'MethodBase', 'Exception',
                  'IEnumerable<MethodBase>', 'Harmony')


def declaration_of(text, name):
    """The parameter list of `name` as declared in this source text, or None."""
    pattern = re.compile(
        r'(?:public|internal|private|protected)[\w\s<>,\[\]?\.]*?\s' + re.escape(name) + r'\s*\(',
        re.MULTILINE)
    match = pattern.search(text)
    if match is None:
        return None
    start = match.end()
    depth = 1
    for offset, char in enumerate(text[start:start + 4000]):
        if char == '(':
            depth += 1
        elif char == ')':
            depth -= 1
            if depth == 0:
                return text[start:start + offset]
    return None


def parameters_of(signature):
    """[(type, name)] from a parameter list, splitting on top-level commas only."""
    parts, depth, current = [], 0, ''
    for char in signature:
        if char in '(<[':
            depth += 1
        elif char in ')>]':
            depth -= 1
        if char == ',' and depth == 0:
            parts.append(current)
            current = ''
            continue
        current += char
    parts.append(current)

    out = []
    for part in parts:
        words = part.strip().split('=')[0].strip().split()
        words = [word for word in words if word not in ('ref', 'out', 'in', 'this', 'params')]
        if len(words) < 2:
            continue
        out.append((' '.join(words[:-1]), words[-1]))
    return out


def core_patches():
    """[(module, target, patch method, [(type, name)], file)]"""
    found = []
    for root, _dirs, files in os.walk(CORE):
        for file in files:
            if not file.endswith('.cs'):
                continue
            path = os.path.join(root, file)
            module = os.path.relpath(path, CORE).split(os.sep)[0]
            if module == 'MainBridge':
                continue
            with open(path, encoding='utf-8-sig', errors='replace') as handle:
                text = handle.read()
            pairs = [(match.group(1), match.group(2) or match.group(3))
                     for match in HOOK.finditer(text)]

            # form 2: local -> target name, then every nameof() inside that local's Patch call
            locals_ = {match.group(1): match.group(2) for match in LOOKUP.finditer(text)}
            locals_.update({match.group(1): match.group(2) for match in LOOKUP_LINQ.finditer(text)})
            for match in PATCH_CALL.finditer(text):
                target = locals_.get(match.group(1))
                if target is None:
                    continue
                for patch in HARMONY_METHOD.findall(match.group(2)):
                    pairs.append((target, patch))

            for target, patch in pairs:
                signature = declaration_of(text, patch)
                if signature is None:
                    continue
                found.append((module, target, patch, parameters_of(signature),
                              os.path.relpath(path, CORE)))
    return found


METHOD = re.compile(
    r'(?:public|internal|private|protected)\s+(?:static\s+)?[\w<>,\[\]?\.]+\s+(\w+)\s*\(',
    re.MULTILINE)


def main_methods():
    """{name: [ [(type, param)] , ... ]} — every overload the authored Main declares."""
    methods = {}
    for root, _dirs, files in os.walk(MAIN):
        if any(part in root for part in ('obj', 'bin', 'Build', 'Tests', 'Analysis')):
            continue
        for file in files:
            if not file.endswith('.cs'):
                continue
            with open(os.path.join(root, file), encoding='utf-8-sig', errors='replace') as handle:
                text = handle.read()
            for match in METHOD.finditer(text):
                name = match.group(1)
                start = match.end()
                depth, end = 1, None
                for offset, char in enumerate(text[start:start + 4000]):
                    if char == '(':
                        depth += 1
                    elif char == ')':
                        depth -= 1
                        if depth == 0:
                            end = start + offset
                            break
                if end is None:
                    continue
                methods.setdefault(name, []).append(parameters_of(text[start:end]))
    return methods


# --------------------------------------------------------------- the built assembly, read with Cecil
# Source regexes cannot see what the compiler emitted, and the 2026-11-19 trial also lost Preview to a
# name that exists only AFTER the old Cecil pipeline rewrites it (BuildCurrentPreview ->
# BuildCurrentPreviewAttempt). So the authoritative check reads the DLL.

DUMP_LINE = re.compile(r'^(?P<type>[^:]+)::(?P<method>[^(]+)\((?P<params>.*)\)$')


def built(dll):
    """{type: {method: [[names]]}} from Analysis/MetaDump, or None when it cannot be run."""
    import subprocess
    try:
        result = subprocess.run(
            ['dotnet', 'run', '-v', 'q', '--project', os.path.join(HERE, 'MetaDump'), '--', dll],
            capture_output=True, text=True, cwd=MAIN, timeout=600)
    except Exception as problem:                      # noqa: BLE001 - the tool must still print the rest
        print(f'(could not read {dll}: {problem})')
        return None
    if result.returncode != 0:
        print(f'(could not read {dll}: {result.stdout.strip()[:400]})')
        return None

    types = {}
    for line in result.stdout.splitlines():
        match = DUMP_LINE.match(line.strip())
        if match is None:
            continue
        names = [part.strip() for part in match.group('params').split(',') if part.strip()]
        types.setdefault(match.group('type'), {}).setdefault(match.group('method'), []).append(names)
    return types


# Names Core looks up on the Preview runtime rather than on the host type. PreviewEnhancements throws
# (Single) when one is absent, which disables the module -- it is not a tolerated miss.
PREVIEW_TYPE = 'SkinForger.Preview.PreviewManager'
# The lookahead keeps GetMethod("...") on OTHER types out: those are not Preview lookups.
PREVIEW_ASKS = re.compile(r'(?<![A-Za-z_.])(?:PatchPrefix|PatchPostfix|Method)\(\s*"([A-Za-z_]\w*)"')


def preview_report(types):
    """Prints whether every Preview method name Core asks for exists in the built assembly."""
    asks = {}
    for root, _dirs, files in os.walk(CORE):
        for file in files:
            if not file.endswith('.cs'):
                continue
            path = os.path.join(root, file)
            with open(path, encoding='utf-8-sig', errors='replace') as handle:
                text = handle.read()
            if f'TypeByName("{PREVIEW_TYPE}")' not in text:
                continue   # another module's Method(...) helper asks the host, not the Preview runtime
            module = os.path.relpath(path, CORE).split(os.sep)[0]
            for name in PREVIEW_ASKS.findall(text):
                asks.setdefault(name, set()).add(module)

    have = types.get(PREVIEW_TYPE, {})
    absent = sorted(name for name in asks if name not in have)
    print(f'\nPreview runtime ({PREVIEW_TYPE}) ({len(asks)} names asked for)')
    print('-' * 60)
    if not have:
        print('  the built assembly declares no such type')
    for name in absent:
        print(f'  ABSENT  {name:34} {", ".join(sorted(asks[name]))}')
    if have and not absent:
        print('  every name Core asks for is declared')
    return len(absent)


def main():
    methods = main_methods()
    breaks, skipped, ok = [], [], []

    for module, target, patch, parameters, file in core_patches():
        wanted = [(kind, name) for kind, name in parameters
                  if not PLUMBING.match(name) and kind not in PLUMBING_TYPES]
        if target not in methods:
            skipped.append((module, target, patch, 'target absent from the authored Main', file))
            continue
        if not wanted:
            ok.append((module, target, patch, 'no by-name parameters', file))
            continue

        # Harmony binds against ONE target overload; a name that no overload has is a break.
        every = {name for overload in methods[target] for _kind, name in overload}
        unmatched = [name for _kind, name in wanted if name not in every]
        if unmatched:
            have = ' / '.join(
                '(' + ', '.join(name for _k, name in overload) + ')' for overload in methods[target]) or '()'
            breaks.append((module, target, patch,
                           f'wants {unmatched}, Main declares {have}', file))
        else:
            ok.append((module, target, patch, 'names ok', file))

    def show(title, rows):
        print(f'\n{title} ({len(rows)})')
        print('-' * len(f'{title} ({len(rows)})'))
        for module, target, patch, note, file in rows:
            print(f'  {module:34} {target:34} {patch:30} {note}')

    show('NAME BREAKS -- these kill the whole module at startup', breaks)
    show('target not declared (checked by contract-parity.py instead)', skipped)
    show('ok', ok)

    # The built assembly has the last word: it is what Harmony will actually bind against.
    dll = sys.argv[1] if len(sys.argv) > 1 else os.path.join('obj', 'Debug', 'SkinForger.Melon.dll')
    absent_preview = 0
    if os.path.exists(os.path.join(MAIN, dll)):
        types = built(dll)
        if types is not None:
            host = types.get('SkinForger.SkinForgerMod', {})
            emitted = []
            for module, target, patch, parameters, _file in core_patches():
                overloads = host.get(target)
                if overloads is None:
                    continue
                every = {name for overload in overloads for name in overload}
                for kind, name in parameters:
                    if PLUMBING.match(name) or kind in PLUMBING_TYPES or name in every:
                        continue
                    emitted.append((module, target, patch,
                                    f'{name!r} is not a parameter of the BUILT ' + target, dll))
            show('NAME BREAKS in the built assembly', emitted)
            absent_preview = preview_report(types)
            breaks += emitted
    else:
        print(f'\n(no built assembly at {dll}; source-level check only)')

    print(f'\n{len(ok)} ok, {len(breaks)} name breaks, {len(skipped)} targets absent, '
          f'{absent_preview} Preview names absent')
    return 1 if breaks or absent_preview else 0


if __name__ == '__main__':
    sys.exit(main())
