"""Every bug SkinForger has already fixed, and whether the authored Main can bring it back.

Run from this folder:

    python -I Analysis/bug-log-regression.py

Why this exists: stage 6 of the cut asks for "BUG_LOG 里所有历史 bug 复查一遍". Most of those fixes
live in SkinForger.Core, which the cut never touched, so they cannot regress. The ones that matter are
the fixes that lived in the MAIN -- the replacement path, the Dump, the atlas, the window -- because
that is the half that was rewritten from scratch.

So this reads BUG_LOG.txt, sorts every entry by where its fix lives, and for the Main-side ones checks
that the thing which fixed it is still there. A GUARD is a short piece of evidence -- a name in the
sources, a checker that covers it, a test -- that would have to disappear for the bug to come back.

It is a census, not a proof: a guard that is present can still be wrong. What it does prevent is a
fixed bug quietly losing its fix in a rewrite, which is the whole shape of the 2026-11-20 week.
"""

import os
import re
import sys

# BUG_LOG.txt is written in Chinese, and a Windows console defaults to a codepage that cannot print it.
if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

HERE = os.path.dirname(os.path.abspath(__file__))
MAIN = os.path.dirname(HERE)
SOURCE = os.path.dirname(MAIN)


def _pick(*paths):
    """同一份工具在两种布局下都能跑：私有开发目录，以及公开仓库的 src/。"""
    for candidate in paths:
        if os.path.exists(candidate):
            return candidate
    return paths[0]


LOG = _pick(os.path.join(SOURCE, '..', 'SF日志', 'BUG_LOG.txt'),
            os.path.join(SOURCE, '..', 'docs', 'development-logs', 'BUG_LOG.md'),
            os.path.join(SOURCE, '..', 'docs', 'development-logs', 'BUG_LOG.txt'))

SKIP_DIRS = ('obj', 'bin', 'Build')

# A Main-side bug, and the evidence that its fix is still in place. The key is matched against the
# entry's title; the value is the text that must appear somewhere in the Main's own sources or tools.
# Entries named outright, where the title alone does not say where the fix lives. A guard of None means
# the cut cannot bring it back, with the reason; otherwise the text must still be in the Main's sources.
BY_ID = {
    'BUG-001': (None, 'the upstream SkinChanger it is about is exactly what the cut removed'),
    'BUG-007': (None, 'a launcher / environment problem, no SkinForger code involved'),
    'BUG-011': ('SkeletonHooks', 'the Main hooks Spine through one place, which is where the storm was'),
    'BUG-013': (None, 'a stray process holding the DLL; no code owns it'),
    'BUG-014': ('manifest', 'Mods\SkinForger\manifest.json still ships beside the two DLLs'),
    'BUG-018': (None, 'Bad Apple needs TouhouCompat, which is already a known gap in Core (SF next tasks)'),
    'BUG-024': ('PreviewManager', 'the authored Preview'),
    'BUG-025': (None, 'process lifetime, owned by Core'),
    'BUG-038': (None, 'the gameplay snapshot cache is Core (GameplayAtlasWarmReuse)'),
    'BUG-039': ('Padding', 'the composer leaves 2 px around every rectangle AND the atlas is premultiplied, '
                           'so a faded edge stays faded instead of growing a fringe -- which is why the '
                           'BattleAtlasEdgeIsolation module in Core stands down'),
    'BUG-040': ('PreviewManager', 'the authored Preview owns its clone'),
    'BUG-045': ('ResolveSource', 'every build starts from the texture the game shipped'),
    'BUG-049': ('PreviewManager', 'the authored Preview'),
    'BUG-052': (None, 'the Preview dropdowns are Core (PreviewEnhancements)'),
    'BUG-055': (None, 'the Preview mouse wheel is Core (PreviewEnhancements)'),
    'BUG-058': ('ForgetSkinIndexes', 'the reload path clears what it has to and nothing else'),
}

GUARDS = {
    # 2026-11-21. The selection screen's cover pictures are Unity Sprites SelectionThumbnail builds once
    # and caches; dropping the atlases does not touch them, so the Reload button has to tell it to rebuild.
    # The old build injected that call with Cecil and the authored Main had lost it: an edited cover only
    # appeared after a restart. The guard is the call itself, in Unity\MainWindow.cs's Reload().
    '封面图': ('SelectionThumbnailManager.Reload',
             'the Reload button rebuilds the selection thumbnails (checked by injection-parity.py too)'),
    '莉莉丝': ('QuadCentre', 'the quad placement recipe from ReplacementDispatcher'),
    '四边形': ('QuadCentre', 'the quad placement recipe'),
    '碎片化': ('ClampedTo', 'a region that hangs off its page is trimmed, not dropped'),
    '扩框': ('CanvasStart', 'the canvas placement rule'),
    '蓝线': ('PristineCanvas', 'every build starts from the pristine canvas, so it cannot grow twice'),
    '越长越大': ('PristineCanvas', 'the canvas is remembered, so a rebuild cannot feed on itself'),
    '锚点': ('GetRegionAnchor', 'anchors go through the seam Core patches'),
    '头发': ('RefreshUvs', "a mesh's own copy of the region numbers is refreshed"),
    'hair': ('RefreshUvs', "a mesh's own copy of the region numbers is refreshed"),
    '图集': ('AtlasComposer', 'the composer builds from the original atlas only'),
    'Dump': ('DumpPlan', 'the Dump plan'),
    '导出': ('DumpPlan', 'the Dump plan'),
    '启动警告': ('TrySpawnForExtract', 'the Main can still bring a prefab on screen for the Dump'),
    '窗口': ('MainWindow', "the Main's own window"),
    '快捷键': ('MenuKeySettings', 'the menu key settings'),
    '光标': ('MouseIcon', 'the cursor resource name'),
    '翻译': (None, 'Core only (SkinPackages/Loc): the cut did not touch it'),
    '字体': (None, 'Core only (SkinPackages/Text): the cut did not touch it'),
    '皮肤库': (None, 'Core only (SkinPackages/SkinLibrary): the cut did not touch it'),
    '中文': (None, 'Core only (SkinPackages/Loc): the cut did not touch it'),
    '加载速度': ('AtlasComposer', 'the composer is where the layout is decided'),
    '女仆': ('CanvasStart', 'the canvas placement rule'),
}


def read(path):
    with open(path, encoding='utf-8', errors='replace') as handle:
        return handle.read()


def main_sources():
    text = []
    for root, dirs, files in os.walk(MAIN):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for file in files:
            if file.endswith(('.cs', '.py')):
                text.append(read(os.path.join(root, file)))
    return '\n'.join(text)


def entries(log):
    """(title, body) for both shapes BUG_LOG uses: numbered BUG-nnn and dated [date]."""
    lines = log.split('\n')
    found, start, title = [], None, None
    for index, line in enumerate(lines):
        if re.match(r'^BUG-\d+', line) or re.match(r'^\[20\d\d-\d\d-\d\d\]', line):
            if title is not None:
                found.append((title, '\n'.join(lines[start:index])))
            title, start = line.strip(), index
    if title is not None:
        found.append((title, '\n'.join(lines[start:])))
    return found


def main():
    path = os.path.abspath(LOG)
    if not os.path.isfile(path):
        print(f'BUG_LOG.txt not found at {path}')
        return 0
    source = main_sources()
    rows = entries(read(path))

    core_only, guarded, unguarded = [], [], []
    for title, body in rows:
        needle = None
        reason = None
        named = re.match(r'^(BUG-\d+)', title)
        if named and named.group(1) in BY_ID:
            needle, reason = BY_ID[named.group(1)]
        else:
            for word, (guard, why) in GUARDS.items():
                if word in title:
                    needle, reason = guard, why
                    break
        if needle is None and reason is not None:
            core_only.append((title, reason))
        elif needle is None:
            # No rule matched. Decide from the body: a fix that names only Core files cannot regress.
            touches_main = ('SkinForger.Melon' in body or 'SkinForgerMod' in body or
                            'Main' in body and 'Modules/' not in body)
            (unguarded if touches_main else core_only).append(
                (title, 'no rule for this entry' if touches_main else 'the fix names Core files only'))
        elif needle in source:
            guarded.append((title, f'{needle} -- {reason}'))
        else:
            unguarded.append((title, f'{needle} IS GONE -- {reason}'))

    def show(heading, rows_):
        print(f'\n{heading} ({len(rows_)})')
        print('-' * len(f'{heading} ({len(rows_)})'))
        for title, note in rows_:
            print(f'  {title[:78]}')
            print(f'      {note}')

    show('NO GUARD -- the fix is Main-side and nothing here proves it survived', unguarded)
    show('guarded', guarded)
    show('Core only, the cut could not touch it', core_only)
    print(f'\n{len(rows)} entries: {len(guarded)} guarded, {len(core_only)} Core-only, '
          f'{len(unguarded)} without a guard')
    return 1 if unguarded else 0


if __name__ == '__main__':
    sys.exit(main())
