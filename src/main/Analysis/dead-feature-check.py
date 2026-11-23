"""Which authored runtimes are compiled into the Main but never started by it.

Static check, run from this folder:

    python -I Analysis/dead-feature-check.py

Why this exists: in the OLD Main nothing called these features from source either. Each one has its own
Cecil build step (CustomLoadingPic\\Build, SelectionThumbnail\\Build, ...) that INJECTED the call into the
start-up method of the finished binary. The authored Main is compiled and not post-processed, so those
calls have to be written out — and a missing one is invisible: the feature is simply absent, the build is
clean, the tests pass and the log says nothing at all.

That is how the 2026-11-19 16:17 trial lost the custom loading pictures (a feature that had never had a
bug) along with the selection thumbnails, the replaced-file backup and the Preview's own start-up.

It reports, per runtime folder the Main compiles in, the entry points nobody in the Main's own sources
calls. A finding is not automatically a bug — a helper the game's own hooks reach does not need a call —
but every one of them has to be explained rather than assumed.
"""

import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MAIN = os.path.dirname(HERE)
SOURCE = os.path.dirname(MAIN)

# The entry-point names a feature starts itself from. Narrow on purpose: these are the ones a build step
# used to inject, so an uncalled one means the feature never runs at all.
# `internal` counts, and so does Apply: CharacterMeshGeometry.Apply is an internal static method that sat
# uncalled while SkinForger.Core's mesh canvas hung its ENTIRE registration off it — the elfin had no canvas
# expansion for that reason alone, and this tool said "0 never started" the whole time (2026-11-20).
ENTRY = re.compile(
    r'\b(?:public|internal)\s+static\s+(?:void|bool|int)\s+'
    r'(Initialize\w*|Install\w*|Attach\w*|Hook|Apply)\s*\(')

# Entry points the authored Main deliberately does not call, each with the reason. Anything NOT listed
# here and not called is a feature that silently does not exist.
EXPLAINED = {
    ('PreviewSharedCore', 'Apply'):
        "not part of the authored Preview. The old Main's Preview had a Shared Core attempt with a "
        "fallback to a private replacement, built by its Cecil step; the authored PreviewManager has one "
        "replacement path and no adapter, so there is nothing to call. HexieBlock patches this name if it "
        "is present and simply finds nothing when it is not.",
    ('FpsProtectionManager', 'InitializeEarly'):
        'superseded: the authored Main reads SkinForgeFpsLimiter.json itself (FpsLimiterSettings) and '
        'enforces the cap in OnUpdate (UpdateFpsLimiterGuard). Verified in game 2026-11-19: "frame cap 60".',
}
TYPE = re.compile(r'\b(?:public|internal)\s+(?:static\s+|sealed\s+|partial\s+)*class\s+(\w+)')
SKIP_DIRS = ('obj', 'bin', 'Build', 'Tests', 'Analysis', 'Audit', 'JitAudit')


def runtimes():
    """The ..\\<Feature>\\Runtime folders the Main's project compiles in."""
    project = os.path.join(MAIN, 'SkinForger.Main.csproj')
    with open(project, encoding='utf-8-sig', errors='replace') as handle:
        text = handle.read()
    found = []
    for include in re.findall(r'Include="(\.\.\\[^"]*Runtime[^"]*)"', text):
        folder = os.path.normpath(os.path.join(MAIN, include.split('*')[0]))
        if os.path.isdir(folder):
            found.append(folder)
    return found


def main_sources():
    """Every line of the authored Main's own code (not the shared runtimes, not the checkers)."""
    text = []
    for root, dirs, files in os.walk(MAIN):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for file in files:
            if file.endswith('.cs'):
                with open(os.path.join(root, file), encoding='utf-8-sig', errors='replace') as handle:
                    text.append(handle.read())
    return '\n'.join(text)


def main():
    code = main_sources()
    dead, called, explained = [], [], []

    for folder in runtimes():
        feature = os.path.basename(os.path.dirname(folder))
        for root, dirs, files in os.walk(folder):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
            for file in files:
                if not file.endswith('.cs'):
                    continue
                path = os.path.join(root, file)
                with open(path, encoding='utf-8-sig', errors='replace') as handle:
                    source = handle.read()
                types = TYPE.findall(source)
                owner = types[0] if types else os.path.splitext(file)[0]
                for entry in ENTRY.findall(source):
                    # Called as Owner.Entry(, or through a using-shortened name in the Main's own code.
                    pattern = re.compile(r'(?:\b' + re.escape(owner) + r'\s*\.\s*)' + re.escape(entry) + r'\s*\(')
                    row = (feature, owner, entry)
                    if pattern.search(code):
                        called.append(row)
                    elif (owner, entry) in EXPLAINED:
                        explained.append(row)
                    else:
                        dead.append(row)

    def show(title, rows):
        print(f'\n{title} ({len(rows)})')
        print('-' * len(f'{title} ({len(rows)})'))
        for feature, owner, entry in sorted(set(rows)):
            print(f'  {feature:26} {owner}.{entry}()')

    show('NEVER STARTED by the authored Main -- each one has to be explained', dead)
    show('started', called)
    print(f'\nnot started on purpose ({len(set(explained))})')
    print('-' * 30)
    for feature, owner, entry in sorted(set(explained)):
        print(f'  {feature:26} {owner}.{entry}()')
        print(f'  {"":26} {EXPLAINED[(owner, entry)]}')
    print(f'\n{len(set(called))} started, {len(set(dead))} never started, '
          f'{len(set(explained))} not started on purpose')
    return 1 if dead else 0


if __name__ == '__main__':
    sys.exit(main())
