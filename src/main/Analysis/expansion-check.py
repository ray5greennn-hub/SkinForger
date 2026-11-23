"""Every expanded part in the player's own skins, and whether its placement can be described.

Run from this folder:

    python -I Analysis/expansion-check.py ["<SkinCreator\\Images>"] ["<MelonLoader log>"]

Why this exists: a part whose canvas the author ENLARGED is the only kind of part SkinForger has to
place, and getting that placement wrong moves the drawing on screen -- the elfin whose head, wings, fork
and leg sat in the wrong places for most of 2026-11-20. The placement is one subtraction,

    left = anchorX - defaultAnchorX        top = anchorY - defaultAnchorY

where anchor* is the author's sidecar and defaultAnchor* is the anchor the part has in the GAME's own
canvas: the canvas centre for a mesh, and for a quad the bone origin, which can only be computed from the
attachment the game hands over. So for a quad there IS no offline answer -- and pretending otherwise is
how this tool was wrong on 2026-11-20.

    A CORRECTION. This tool first read <atlas folder>\\replaced\\<part>.json as "the anchor the Dump
    wrote". It is not that. It is SkinForger's backup of an EARLIER version of the author's own sidecar --
    the elfin head's copy carries the author's hand-set offset (-18, 30), which a dumped file never
    would. For two of the player's six expanded elfin parts it happened to equal the game's anchor, and
    for four it did not, so four parts were reported describable when the game centres them.

What it reads instead: SkinForger logs the answer it actually used, once per part --

    head: the game's 115x138 canvas starts at (11.5, 2.5) of a 138x143 picture
          (anchor 92,157 against 61.65,130.869)

-- and that line is measured, not assumed. This tool pairs every expanded PNG with that line when the log
has one, and says plainly that it cannot tell when it does not.

Exit code 1 if a part the log covers is NOT placed by its own anchor (so it was centred, which is a
guess). A part with no log line yet is reported as unmeasured, not as a failure: run the game once with
that costume on screen and run this again.
"""

import json
import os
import re
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MAIN = os.path.dirname(HERE)
BACKSLASH = chr(92)
TOLERANCE = 0.51          # SkinForger.Core's own, in CanvasSpec

PLACEMENT = re.compile(
    r"(?P<part>[\w.\-]+): the game's (?P<cw>\d+)x(?P<ch>\d+) canvas starts at "
    r"\((?P<left>-?[\d.]+), (?P<top>-?[\d.]+)\) of a (?P<pw>\d+)x(?P<ph>\d+) picture"
    # ", by the sidecar's anchor" / ", by lining the drawings up (...)" -- optional, and it may itself
    # contain brackets, so the anchor clause is found by its own words rather than by position.
    r"(?P<how>.*?)\(anchor (?P<ax>-?[\d.]+),(?P<ay>-?[\d.]+) against (?P<dx>-?[\d.]+),(?P<dy>-?[\d.]+)\)")


def default_images():
    root = os.path.abspath(os.path.join(MAIN, '..', '..'))
    return os.path.join(root, 'SkinForger', 'SkinCreator', 'Images')


def default_logs():
    root = os.path.abspath(os.path.join(MAIN, '..', '..'))
    return os.path.join(root, 'MelonLoader')


def png_size(path):
    try:
        with open(path, 'rb') as handle:
            header = handle.read(24)
    except OSError:
        return None
    if len(header) < 24 or header[:4] != b'\x89PNG':
        return None
    return struct.unpack('>II', header[16:24])


def read_json(path):
    try:
        with open(path, encoding='utf-8-sig') as handle:
            return json.load(handle)
    except Exception:
        return None


def inside(left, top, canvas_w, canvas_h, image_w, image_h):
    return (left >= -TOLERANCE and top >= -TOLERANCE and
            image_w - left - canvas_w >= -TOLERANCE and
            image_h - top - canvas_h >= -TOLERANCE)


def measured(log_folder):
    """part name -> the placement SkinForger logged, newest log wins."""
    found = {}
    if not os.path.isdir(log_folder):
        return found
    logs = [os.path.join(log_folder, name) for name in os.listdir(log_folder)
            if name.lower().endswith('.log')]
    for path in sorted(logs, key=lambda p: os.path.getmtime(p)):
        try:
            with open(path, encoding='utf-8', errors='replace') as handle:
                for line in handle:
                    match = PLACEMENT.search(line)
                    if match:
                        row = {k: float(v) if '.' in v or k in
                               ('left', 'top', 'ax', 'ay', 'dx', 'dy') else int(v)
                               for k, v in match.groupdict().items()
                               if k not in ('part', 'how')}
                        # ", by lining the drawings up (...)" -- SkinForger's own word for how it placed
                        # this part, which beats anything this tool could work out again.
                        row['how'] = (match.group('how') or '').strip().lstrip(',').strip()
                        if row['how'].startswith('by '):
                            row['how'] = row['how'][3:].strip()
                        found[match.group('part')] = row
        except OSError:
            continue
    return found


def expanded_parts(images):
    if not os.path.isdir(images):
        return
    for skin in sorted(os.listdir(images)):
        manifest = read_json(os.path.join(images, skin, 'manifest.json'))
        if not manifest:
            continue
        for region in manifest.get('regions', []):
            relative = region.get('file', '').replace(BACKSLASH, os.sep)
            if not relative:
                continue
            png = os.path.join(images, skin, relative)
            size = png_size(png)
            if size is None:
                continue
            png_w, png_h = size
            game_w = region.get('originalWidth', 0)
            game_h = region.get('originalHeight', 0)
            if game_w <= 0 or game_h <= 0 or (png_w == game_w and png_h == game_h):
                continue                                   # not expanded: nothing to place
            yield (skin, region.get('atlas', '?'), region.get('attachmentName', '?'),
                   game_w, game_h, png_w, png_h,
                   read_json(os.path.splitext(png)[0] + '.json') or {})


def main():
    images = sys.argv[1] if len(sys.argv) > 1 else default_images()
    logs = sys.argv[2] if len(sys.argv) > 2 else default_logs()
    seen = measured(logs)
    print(f'expanded parts under {images}')
    print(f'placements measured in game: {len(seen)} (from {logs})\n')
    print(f"{'skin':10} {'part':16} {'game':11} {'picture':11} {'left':>8} {'top':>8}  verdict")
    print('-' * 96)

    honoured = centred = unmeasured = 0
    advice = []
    for skin, _atlas, part, gw, gh, pw, ph, sidecar in expanded_parts(images):
        game, picture = f'{gw}x{gh}', f'{pw}x{ph}'
        row = seen.get(part)
        if row is None or int(row['cw']) != gw or int(row['ch']) != gh or int(row['pw']) != pw:
            unmeasured += 1
            print(f'{skin:10} {part:16} {game:11} {picture:11} {"":>8} {"":>8}  '
                  f'not measured yet: no log line for this part')
            continue

        left, top = row['left'], row['top']
        by_anchor = row['ax'] - row['dx'], row['ay'] - row['dy']
        ok = inside(by_anchor[0], by_anchor[1], gw, gh, pw, ph)
        # SkinForger says in the log how it placed the part; that is the truth, not this tool's guess.
        how = row.get('how') or ''
        if ok:
            honoured += 1
            verdict = 'ok: placed by its own anchor'
        elif how and 'centred' not in how:
            honoured += 1
            verdict = f'ok: {how}'
        else:
            centred += 1
            verdict = 'CENTRED: the anchor cannot place the canvas, and nothing better was found'
            advice.append((skin, part, row['dx'], row['dy'], sidecar.get('anchorX'), sidecar.get('anchorY')))
        print(f'{skin:10} {part:16} {game:11} {picture:11} {left:8.3f} {top:8.3f}  {verdict}')

    if advice:
        print('\nthe anchors those parts would need, if the author enlarged them to the right and bottom')
        print('-' * 96)
        for skin, part, dx, dy, ax, ay in advice:
            print(f'  {skin}/{part}: anchorX {ax} -> {dx}, anchorY {ay} -> {dy}')

    total = honoured + centred + unmeasured
    print(f'\n{total} expanded part(s): {honoured} placed by their own anchor, {centred} centred, '
          f'{unmeasured} not measured yet')
    return 1 if centred else 0


if __name__ == '__main__':
    sys.exit(main())
