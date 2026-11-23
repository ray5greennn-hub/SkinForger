"""Field names SkinForger.Core looks up on the Main, and whether the Main declares them.

Run from this folder:

    python -I Analysis/field-name-parity.py

Why this exists: param-name-parity.py covers the names Harmony binds patch PARAMETERS by, and
contract-parity.py covers the METHODS Core patches. Neither sees a third contract that is just as silent:
Core reads private fields off Main's types by name with AccessTools.Field, and a name that is not there
comes back null. Most call sites then simply do nothing.

That is how the Preview lost features with no error of its own -- the 2026-11-20 14:01 log says

    AccessTools.Field: Could not find field for type SkinForger.Preview.PreviewManager
                       and name _skeletonAnimation

and the only sign of it was that warning, in a log full of other warnings.

A finding is not automatically a bug: the authored Main is a different program in places, and a feature
built on a field it has no equivalent of cannot work whatever the field is called. But every one of them
has to be EXPLAINED here rather than discovered in a screenshot.
"""

import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MAIN = os.path.dirname(HERE)
SOURCE = os.path.dirname(MAIN)


def _pick(*paths):
    """同一份工具在两种布局下都能跑：私有开发目录，以及公开仓库的 src/。"""
    for candidate in paths:
        if os.path.exists(candidate):
            return candidate
    return paths[0]


CORE = _pick(os.path.join(SOURCE, 'SkinForgerCore-20261114'),
             os.path.join(SOURCE, 'core'))

SKIP_DIRS = ('obj', 'bin', 'Build', 'Tests', 'Analysis', 'Audit', 'JitAudit')

# AccessTools.Field(<anything>, "name") / DeclaredField(<anything>, "name")
LOOKUP = re.compile(r'AccessTools\.(?:Declared)?Field\s*\(\s*[^,()]+(?:\([^()]*\))?\s*,\s*"([^"]+)"')

# A field or property the Main declares, however it is spelled.
DECLARED = re.compile(
    r'(?:^|\s)(?:public|internal|private|protected)\s+(?:static\s+|readonly\s+|volatile\s+|const\s+)*'
    r'[\w<>,\[\]?.]+\s+(\w+)\s*(?:[=;{]|=>)', re.MULTILINE)

# Names Core asks for that the authored Main deliberately does not have, each with the reason.
EXPLAINED = {
    '_skeletonAnimation':
        "the authored Preview is built on a different renderer. PreviewMvp's PreviewManager clones the "
        "GAMEPLAY prefab and keeps it in _skeletonRenderer, which is a SkeletonMecanim driven by a Unity "
        "Animator in _animator; the object simply is not a SkeletonAnimation, so no rename would help. "
        "2026-11-20: Core no longer needs it. PreviewEnhancements reads the renderer through its own "
        "Renderer() / Showing() helpers (SkeletonRenderer serves the skeleton, its data and the component "
        "for both kinds), and SkinPackages' frame row drives the Animator when there is no AnimationState. "
        "The one thing still specific to SkeletonAnimation is FitResultAnimation, which samples a whole "
        "animation to fit the camera and says so where it returns.",
    'GameplayScope':
        "absent by design, and already reported by contract-parity.py. It gated the old Main's synthesized "
        "atlas state; the authored Main composes every atlas from the game's own art each time, so there "
        "is no scope to enter. 2026-11-20: the one feature that asked for it -- ReloadFilter's "
        "decoded-image reuse -- is now unnecessary, because the authored Main keeps its own decoded images "
        "(TextureBridge, keyed by each file's length and write time), and ReloadFilter says that instead "
        "of warning.",
}


def read(path):
    with open(path, encoding='utf-8-sig', errors='replace') as handle:
        return handle.read()


def sources(root):
    for folder, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in names:
            if name.endswith('.cs'):
                yield os.path.join(folder, name)


def main():
    if not os.path.isdir(CORE):
        print(f'SkinForger.Core sources not found at {CORE}')
        return 0

    asked = {}
    for path in sources(CORE):
        text = read(path)
        for name in LOOKUP.findall(text):
            asked.setdefault(name, set()).add(os.path.basename(os.path.dirname(path)))

    # Everything the Main and its compiled-in runtimes declare.
    declared = set()
    for root in (MAIN, SOURCE):
        for path in sources(root):
            if root is SOURCE and not os.path.sep + 'Runtime' + os.path.sep in path:
                continue
            declared.update(DECLARED.findall(read(path)))

    missing, present, explained = [], [], []
    for name, modules in sorted(asked.items()):
        row = (name, ', '.join(sorted(modules)))
        if name in declared:
            present.append(row)
        elif name in EXPLAINED:
            explained.append(row)
        else:
            missing.append(row)

    def show(title, rows):
        print(f'\n{title} ({len(rows)})')
        print('-' * len(f'{title} ({len(rows)})'))
        for name, modules in rows:
            print(f'  {name:34} {modules}')

    show('ASKED FOR BY CORE, NOT DECLARED BY THE MAIN -- each one has to be explained', missing)
    show('declared', present)
    print(f'\nnot declared on purpose ({len(explained)})')
    print('-' * 30)
    for name, modules in explained:
        print(f'  {name:34} {modules}')
        print(f'  {"":34} {EXPLAINED[name]}')
    print(f'\n{len(present)} declared, {len(missing)} missing, {len(explained)} absent on purpose')
    return 1 if missing else 0


if __name__ == '__main__':
    sys.exit(main())
