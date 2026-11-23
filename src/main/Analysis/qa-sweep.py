"""Every SkinForger replacement feature, checked against the player's own files.

Run from this folder:

    python -I Analysis/qa-sweep.py "<game root>"

(the game root is the folder that contains Mods\ and SkinForger\; it defaults to four levels up)

Why this exists: the authored Main was trialled five times in one afternoon and each trial found a
different feature silently doing nothing. Every one of those was visible in the player's own files or in
the built assembly, and none of them needed the game. This sweep is the thing to run instead of asking
for another trial.

What it CANNOT tell you is deliberately marked NEEDS GAME: how it looks on screen, how fast it is, and
whether a texture ends up bound to the right renderer. Everything else is data, and data can be checked.
"""

import json
import os
import re
import sys

# 有些 Windows 控制台默认不是 UTF-8，输出里的中文和部件名会让 print 直接抛异常。
try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:                                             # noqa: BLE001
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
MAIN = os.path.dirname(HERE)
SOURCE = os.path.dirname(MAIN)          # the folder that holds Main, Core and every feature folder

def _pick(*paths):
    """同一份工具在两种布局下都能跑：私有开发目录，以及公开仓库的 src/。"""
    for candidate in paths:
        if os.path.exists(candidate):
            return candidate
    return paths[0]


# 许可与 README 在私有开发目录里和 SOURCE 同级，在公开仓库里是 src/ 的上一层。
DOCROOT = SOURCE if os.path.isfile(os.path.join(SOURCE, 'LICENSE')) else os.path.dirname(SOURCE)

_results = []


def report(feature, state, detail=''):
    """state: 'ok' | 'FAIL' | 'needs game' | 'empty'"""
    _results.append((feature, state, detail))


def read_json(path):
    try:
        with open(path, encoding='utf-8-sig') as handle:
            return json.load(handle)
    except Exception as problem:                               # noqa: BLE001
        return {'__error__': str(problem)}


# ---------------------------------------------------------------- the player's own layout

class Layout:
    def __init__(self, game_root):
        self.root = game_root
        self.sf = os.path.join(game_root, 'SkinForger')
        self.creator = os.path.join(self.sf, 'SkinCreator')
        self.images = os.path.join(self.creator, 'Images')
        self.text = os.path.join(self.creator, 'Text')
        self.sounds = os.path.join(self.creator, 'Sounds')
        self.loading = os.path.join(self.creator, 'CustomLodingPic')
        self.packages = os.path.join(self.sf, 'Skins')
        self.characters_file = os.path.join(self.creator, 'Saved.json')
        self.elfins_file = os.path.join(self.creator, 'SavedElfin.json')
        self.mods = os.path.join(game_root, 'Mods', 'SkinForger')


# ---------------------------------------------------------------- selections

def selections(layout):
    """{'character': {index: position}, 'elfin': {...}} -- only the ones NOT set to Original."""
    chosen = {}
    for kind, path in (('character', layout.characters_file), ('elfin', layout.elfins_file)):
        data = read_json(path)
        if '__error__' in data:
            report(f'selections ({kind})', 'FAIL', f'{path}: {data["__error__"]}')
            chosen[kind] = {}
            continue
        picked = {int(k): int(v) for k, v in data.items() if int(v) >= 0}
        chosen[kind] = picked
        report(f'selections ({kind})', 'ok' if picked else 'empty',
               f'{len(picked)} of {len(data)} set to a skin: indexes {sorted(picked)}'
               if picked else 'every costume is on Original, so nothing should change on screen')
    return chosen


# ---------------------------------------------------------------- the images a skin replaces

def costume_images(layout, chosen):
    """Each costume folder: does its manifest point at files that exist, and do the identities line up."""
    if not os.path.isdir(layout.images):
        report('replacement images', 'FAIL', f'{layout.images} does not exist')
        return

    folders = sorted(name for name in os.listdir(layout.images)
                     if os.path.isdir(os.path.join(layout.images, name)))
    total_set = len(chosen['character']) + len(chosen['elfin'])
    with_content = 0

    for costume in folders:
        folder = os.path.join(layout.images, costume)
        if costume in NOT_A_COSTUME:
            files = [f for f in os.listdir(folder) if f.lower().endswith('.png')]
            report(f'images: {costume}', 'ok' if files else 'empty',
                   NOT_A_COSTUME[costume] + f' -- {len(files)} png file(s)')
            continue

        manifest_path = os.path.join(folder, 'manifest.json')
        if not os.path.exists(manifest_path):
            # A folder with PNGs but no manifest cannot be applied: the manifest IS the part list.
            loose = sum(1 for _r, _d, files in os.walk(folder) for f in files if f.endswith('.png'))
            report(f'images: {costume}', 'empty' if loose == 0 else 'FAIL',
                   'no manifest.json' + (f' but {loose} png file(s) -- they can never be applied' if loose else ''))
            continue

        manifest = read_json(manifest_path)
        if '__error__' in manifest:
            report(f'images: {costume}', 'FAIL', manifest['__error__'])
            continue

        regions = manifest.get('regions') or []
        if not regions:
            report(f'images: {costume}', 'empty', 'manifest has no parts')
            continue
        with_content += 1

        # Censored parts are never dumped and never replaced (HexieBlock, Steam release), so a manifest
        # entry for one with no file on disk is correct, not a miss.
        regions = [r for r in regions if not censored(r.get('file', '') + '|' + r.get('atlas', ''))]
        if not regions:
            report(f'images: {costume}', 'empty', 'only censored parts')
            continue

        missing = [r for r in regions if not os.path.exists(os.path.join(folder, r.get('file', '')))]
        atlases = sorted({r.get('atlas', '') for r in regions})

        # The 16:31 failure: an identity must survive being spelled the way Spine spells the page.
        spelled = sum(1 for r in regions
                      if bare(r.get('atlas', '')) == bare(r.get('atlas', '') + '.png'))

        if missing:
            report(f'images: {costume}', 'FAIL',
                   f'{len(missing)} of {len(regions)} part file(s) missing, e.g. {missing[0].get("file")}')
        elif spelled != len(regions):
            report(f'images: {costume}', 'FAIL', f'page spelling mismatch on {len(regions) - spelled} part(s)')
        else:
            report(f'images: {costume}', 'ok',
                   f'{len(regions)} parts, {len(atlases)} atlas(es): {", ".join(atlases[:4])}'
                   + (' ...' if len(atlases) > 4 else ''))

    report('images vs selections', 'ok' if with_content >= total_set else 'FAIL',
           f'{total_set} costume(s) set to a skin, {with_content} folder(s) hold parts')


# Folders under Images\ that are NOT costumes and have no manifest by design.
NOT_A_COSTUME = {
    'Cursor': "SkinForger.Core's own cursor replacement (CursorDump); SeenTextures skips this folder",
}


def censored(value):
    """The same rule as DumpNaming.IsCensoredName, which HexieBlock and the exporter also use."""
    return 'hexie' in (value or '').lower()


def bare(name):
    name = name.replace('\\', '/')
    name = name[name.rfind('/') + 1:]
    dot = name.rfind('.')
    return name[:dot] if dot > 0 else name


# ---------------------------------------------------------------- text, fonts, sounds

def texts(layout):
    if not os.path.isdir(layout.text):
        report('text', 'empty', f'{layout.text} does not exist')
        return
    for language in sorted(name for name in os.listdir(layout.text)
                           if os.path.isdir(os.path.join(layout.text, name)) and name != 'Fonts'):
        folder = os.path.join(layout.text, language)
        files, entries, broken, edited = 0, 0, [], 0
        for root, _dirs, names in os.walk(folder):
            for name in names:
                if not name.endswith('.json'):
                    continue
                files += 1
                data = read_json(os.path.join(root, name))
                if '__error__' in data:
                    broken.append(f'{name}: {data["__error__"]}')
                    continue
                # Every shape used here is "key -> replacement text" or a list of such objects.
                for key, value in flatten(data):
                    entries += 1
                    if isinstance(value, str) and value.strip():
                        edited += 1
                    _ = key
        if broken:
            report(f'text: {language}', 'FAIL', f'{len(broken)} unreadable file(s): {broken[0]}')
        else:
            report(f'text: {language}', 'ok' if entries else 'empty',
                   f'{files} file(s), {entries} entries, {edited} non-empty')

    half_edited(layout)


# Fields that say the same thing in two places, because the game has both a single value and a per-costume
# list. Editing one and not the other is the commonest way for an edit to "not work": the screen reads the
# other one. Found on 2026-11-19 -- Bassist's `skill` said the new name while `skills` line 0, which is
# what the base costume's panel shows, still said the old one.
TWINS = (('skill', 'skills'), ('cosName', 'cosNames'), ('characterName', 'name'))


def half_edited(layout):
    for language in sorted(name for name in os.listdir(layout.text)
                           if os.path.isdir(os.path.join(layout.text, name)) and name != 'Fonts'):
        odd = []
        for root, _dirs, names in os.walk(os.path.join(layout.text, language)):
            for name in sorted(names):
                if not name.endswith('.json'):
                    continue
                data = read_json(os.path.join(root, name))
                if '__error__' in data:
                    continue
                single = {}
                listed = {}
                for entry in (data.get('Texts') or []):
                    field, text = entry.get('Field'), entry.get('Text')
                    if entry.get('Line') in (-1, '-1', None):
                        single[field] = text
                    elif str(entry.get('Line')) == '0':
                        listed[field] = text

                # The twins also differ for reasons of the game's own (Maid's `skills[0]` says "first
                # collecting" where `skill` does not), so a plain difference is not a finding. What IS a
                # finding is the player's new character name appearing in one twin and not the other:
                # that is an edit that landed in only one of the two places the game reads.
                who = single.get('characterName') or single.get('name') or ''
                for one, many in TWINS:
                    if one not in single or many not in listed:
                        continue
                    a, b = single[one] or '', listed[many] or ''
                    if a == b or not who:
                        continue
                    if (who in a) != (who in b):
                        odd.append(f'{name[:-5]}/{language}: "{who}" is in {one if who in a else many} '
                                   f'but not in {many if who in a else one}')
        report(f'text twins: {language}', 'ok' if not odd else 'FAIL',
               'no half-finished edit found' if not odd
               else f'{len(odd)} field(s) edited in one place only: ' + '; '.join(odd[:4]))


def folder_anchor_entry(png):
    """
    A part's anchor record when the folder keeps every anchor in ONE file.

    A frame-by-frame folder (a cat skin: hundreds of frames) keeps its anchors in <folder>nchors.json
    instead of one <part>.json beside every picture -- the author's rule of 2026-11-20, which the Main
    honours through RegionAnchorFile.ReadIfExists -> FolderAnchorFile.ReadEntryAsSidecar.

    This function exists because this sweep did not know that. On 2026-11-21 it reported Bassist/eye_L and
    Bassist/body_1 as "expanded canvas with no sidecar, can only be centred" while both were pinned in
    1_rock_main_show_catnchors.json -- the single FAIL of a 44-check sweep, and a false one. A checker
    that cries wolf about pinned parts is worse than no checker, because the rule here is "nothing ships
    until the sweep is green".
    """
    folder = read_json(os.path.join(os.path.dirname(png), 'anchors.json'))
    if '__error__' in folder:
        return None
    entry = (folder.get('parts') or {}).get(os.path.splitext(os.path.basename(png))[0])
    return entry if isinstance(entry, dict) else None


def anchors(layout):
    """
    Which parts have a canvas LARGER than the game's (the expansion feature), and whether their anchor
    sidecar is the canvas centre.

    The authored Main grows a part's quad by png / canvas and centres it, so a centred anchor is honoured
    exactly. An anchor that is NOT the centre means the author moved the part inside its canvas, and that
    offset is not expressed yet -- so it is reported here rather than silently ignored.
    """
    import struct
    if not os.path.isdir(layout.images):
        return
    grown, off_centre, checked, missing, one_file = 0, [], 0, [], []
    for costume in sorted(os.listdir(layout.images)):
        folder = os.path.join(layout.images, costume)
        manifest = read_json(os.path.join(folder, 'manifest.json'))
        if '__error__' in manifest:
            continue
        for region in (manifest.get('regions') or []):
            png = os.path.join(folder, region.get('file', ''))
            if not os.path.exists(png):
                continue
            try:
                with open(png, 'rb') as handle:
                    width, height = struct.unpack('>II', handle.read(26)[16:24])
            except Exception:                                  # noqa: BLE001
                continue
            checked += 1
            canvas = (region.get('originalWidth'), region.get('originalHeight'))
            if (width, height) == canvas:
                continue
            grown += 1
            side = read_json(os.path.splitext(png)[0] + '.json')
            if '__error__' in side:
                # No sidecar of its own: the folder may keep every anchor in one anchors.json.
                side = folder_anchor_entry(png)
                if side is None:
                    missing.append(f'{costume}/{region.get("attachmentName")}')
                    continue
                one_file.append(f'{costume}/{region.get("attachmentName")}')
            ax, ay = side.get('anchorX'), side.get('anchorY')
            if ax is None or ay is None:
                continue
            if abs(ax - width / 2) > 0.51 or abs(ay - height / 2) > 0.51:
                off_centre.append(f'{costume}/{region.get("attachmentName")} '
                                  f'anchor ({ax:.1f},{ay:.1f}) vs centre ({width/2:.1f},{height/2:.1f})')
    # Off-centre anchors ARE expressed since 2026-11-19 (RegionGeometry.AfterReplacement takes the
    # sidecar's anchor), so this is a census, not a failure. A part with a bigger canvas and NO sidecar is
    # the thing to worry about: it can only be centred, and the author may have meant otherwise.
    report('expanded canvases without a sidecar', 'ok' if not missing else 'FAIL',
           'every expanded part has one' if not missing
           else f'{len(missing)} can only be centred: ' + '; '.join(missing[:4]))
    report('expanded canvases', 'ok',
           f'{grown} of {checked} part(s) have a bigger canvas than the game, '
           f'{len(off_centre)} of them anchored away from the centre (all honoured via their sidecar)'
           + (f'; {len(one_file)} pinned in the folder anchors.json ('
              + '; '.join(one_file[:3]) + ')' if one_file else ''))


def adjustments(layout):
    """
    Per-part adjustments the author made by hand, and whether anything still carries them.

    A region's sidecar holds more than an anchor: offsetX / offsetY (a rigid nudge), Scale,
    MovementAmplitude, X / Y and Rotate. The authored Main passed none of them until 2026-11-20 — so every
    part the author had nudged was quietly drawn un-nudged. That is why parts that had never been wrong
    became wrong after the cut, with no error anywhere.

    A nudge takes TWO hands, and this checks for both:
      * a MESH is moved by SkinForger.Core (PartTransforms), from the placement Main hands over;
      * a QUAD is moved by Main itself — Core says so where it declines to ("region parts: unchanged
        (Main moves region quads by offset itself)"). Main had no such writer at all until 2026-11-20,
        which is the whole of "the JSON values are not being applied".
    """
    if not os.path.isdir(layout.images):
        return
    counts = {'offset': [], 'scale': [], 'movement': [], 'xy': [], 'rotate': []}
    checked = 0
    for root, dirs, names in os.walk(layout.images):
        # replaced\ holds the sidecars the Dump wrote BEFORE the author edited anything: backups, not
        # live parts. Counting them doubled every number here (26 "nudged" parts where 15 are live).
        dirs[:] = [d for d in dirs if d.lower() != 'replaced']
        for name in names:
            if not name.endswith('.json') or name == 'manifest.json':
                continue
            side = read_json(os.path.join(root, name))
            if '__error__' in side or 'anchorX' not in side:
                continue
            checked += 1
            where = os.path.basename(root) + '/' + name[:-5]
            if side.get('offsetX') or side.get('offsetY'):
                counts['offset'].append(f"{where}({side.get('offsetX')},{side.get('offsetY')})")
            if abs((side.get('Scale') if side.get('Scale') is not None else 1) - 1) > 1e-6:
                counts['scale'].append(where)
            if side.get('MovementAmplitude'):
                counts['movement'].append(where)
            if side.get('X') or side.get('Y'):
                counts['xy'].append(where)
            if side.get('Rotate') or side.get('rotation'):
                counts['rotate'].append(where)

    # What reaches SkinForger.Core today, and how:
    #   offset  -- on the placement object (PartTransforms / OffsetCanvas / DriftProbe read OffsetX/OffsetY)
    #   rotate  -- NOT on the placement: PartTransforms.ReadRotation opens the sidecar itself from the
    #              region path Main passes, and adds "rotation" and "Rotate" together.
    # Scale, MovementAmplitude and X / Y have no reader on either side; a part using them would be drawn
    # unadjusted, so they stay reported until something carries them.
    # Both halves have to exist in source, or the adjustment reaches only one kind of attachment.
    main_source = ''
    for root, dirs, names in os.walk(MAIN):
        dirs[:] = [d for d in dirs if d not in ('obj', 'bin', 'Build', 'Tests', 'Analysis')]
        for name in names:
            if name.endswith('.cs'):
                with open(os.path.join(root, name), encoding='utf-8-sig', errors='replace') as handle:
                    main_source += handle.read()
    core = _pick(os.path.join(os.path.dirname(MAIN), 'SkinForgerCore-20261114', 'Modules',
                              'R4_12MeshCanvas', 'PartTransforms.cs'),
                 os.path.join(os.path.dirname(MAIN), 'core', 'Modules',
                              'R4_12MeshCanvas', 'PartTransforms.cs'))
    core_source = ''
    if os.path.isfile(core):
        with open(core, encoding='utf-8-sig', errors='replace') as handle:
            core_source = handle.read()

    mesh_hand = 'OffsetX' in core_source and 'Apply(' in core_source
    quad_hand = 'QuadNudge' in main_source and 'quad.x =' in main_source

    # The anchor has ONE source, and it is the seam Core patches. Reading the sidecar straight in the
    # replacement path is what left AutoAnchor and OffsetCanvas dead (2026-11-20).
    seam = ('SkinForgerMod.GetRegionAnchor(' in main_source and
            'layout.Defaults' in main_source)
    report('anchors come from the GetRegionAnchor seam', 'ok' if seam else 'FAIL',
           "Core's AutoAnchor and OffsetCanvas hang off it and are reached"
           if seam else 'the replacement path reads the sidecar itself; Core postfixes never run')

    # And that seam must measure a quad from the placement the GAME gave it, or its answer drifts by the
    # author's nudge on every apply.
    pristine = 'RuntimeAtlas.TryPristineQuad(' in main_source
    report('the quad default anchor is measured from the game', 'ok' if pristine else 'FAIL',
           'GetRegionAnchor asks RuntimeAtlas for the pristine placement'
           if pristine else 'it reads the live attachment, which Main itself has already moved')

    # An anchor that cannot place the canvas used to mean "centre it", which is a guess. The pixels are
    # not: the game's own drawing and the author's picture are both in memory at that moment.
    matched = 'CanvasMatch.Locate(' in main_source
    report('an unusable anchor falls back to the pixels', 'ok' if matched else 'FAIL',
           "the game's drawing is looked for inside the author's picture before centring"
           if matched else 'the part is centred on a guess')

    # A region CAN hang off its own page -- the game's own char_1_rock does. Refusing such a part drops
    # it out of SkinForger entirely: not replaced, and not rebased when the atlas grows, so it reads the
    # old layout out of the new texture and comes apart on screen.
    # Stage 6 of the cut: nothing the Cecil toolchain produced may take part in building the product.
    # The tools are kept as reference -- they are the author's own sources and the only readable record
    # of how the old Main behaved, which is what settled two bugs on 2026-11-20 -- but a folder that is
    # both a tool and a compiled source would quietly put them back in.
    project = os.path.join(MAIN, 'SkinForger.Main.csproj')
    compiled, tools = set(), set()
    if os.path.isfile(project):
        with open(project, encoding='utf-8-sig', errors='replace') as handle:
            text = handle.read()
        for include in re.findall(r'Include="(\.\.\\[^"]*)"', text):
            parts = include.split('\\')
            if len(parts) > 2:
                compiled.add((parts[1] + '/' + parts[2]).lower())
    for folder, dirs, names in os.walk(SOURCE):
        dirs[:] = [d for d in dirs if d not in ('obj', 'bin', '.git')]
        for name in names:
            if not name.endswith('.csproj'):
                continue
            path = os.path.join(folder, name)
            with open(path, encoding='utf-8-sig', errors='replace') as handle:
                if 'Mono.Cecil' not in handle.read():
                    continue
            relative = os.path.relpath(folder, SOURCE).replace(os.sep, '/').lower()
            tools.add('/'.join(relative.split('/')[:2]))
    overlap = sorted(compiled & tools)
    report('no Cecil tool takes part in the build', 'ok' if not overlap else 'FAIL',
           f'{len(tools)} tool folder(s) kept as reference, {len(compiled)} folder(s) compiled in, '
           f'nothing in both' if not overlap else 'both a tool and compiled: ' + ', '.join(overlap))

    # The licence paperwork stage 6 asks for.
    for name in ('LICENSE', 'NOTICE.md'):
        there = os.path.isfile(os.path.join(DOCROOT, name))
        report(f'licence paperwork: {name}', 'ok' if there else 'FAIL',
               'present' if there else 'missing -- stage 6 of the cut asks for it before release')
    # The licence is SkinForger's own now, not the GPL text it started under.
    licence = os.path.join(DOCROOT, 'LICENSE')
    own = os.path.isfile(licence) and 'SkinForger Terms of Use' in open(
        licence, encoding='utf-8', errors='replace').read()
    report("LICENSE is SkinForger's own terms", 'ok' if own else 'FAIL',
           'SkinForger Terms of Use (source-available)' if own
           else 'still the licence the project started under')
    readme = os.path.join(DOCROOT, 'README.md')
    upstream_readme = os.path.isfile(readme) and '# MuseDashSkinChanger' in open(
        readme, encoding='utf-8', errors='replace').read()
    report("README.md is SkinForger's own", 'FAIL' if upstream_readme else 'ok',
           "it is still the upstream project's README" if upstream_readme
           else 'no upstream README text in it')

    # Images\Other was write-only until 2026-11-20: the "pictures seen on screen" list dumped into it and
    # nothing read it back, so an author could edit those pictures forever and never see a change.
    others = os.path.join(layout.images, 'Other')
    pictures = [f for f in os.listdir(others) if f.lower().endswith('.png')]         if os.path.isdir(others) else []
    reads_back = 'OtherTextures.Sweep(' in main_source and 'OtherPictures.FileStemOf(' in main_source
    report('pictures in Images\Other are put back', 'ok' if reads_back else 'FAIL',
           f'{len(pictures)} picture(s), matched by texture name and applied'
           if reads_back else f'{len(pictures)} picture(s) that nothing reads: the folder is write-only')
    if pictures and reads_back:
        missing = [p for p in pictures
                   if not os.path.isfile(os.path.join(others, os.path.splitext(p)[0] + '.json'))]
        report('Images\Other sidecars', 'ok' if not missing else 'ok',
               'every picture has one' if not missing
               else f'{len(missing)} without one (they still work, the size comes from the game): '
                    + ', '.join(missing[:3]))

    clamps = 'ClampedTo(' in main_source
    overflowing = []
    for root, dirs, names in os.walk(layout.images):
        dirs[:] = [d for d in dirs if d.lower() != 'replaced']
        if 'manifest.json' not in names:
            continue
        manifest = read_json(os.path.join(root, 'manifest.json'))
        for region in (manifest.get('regions') or []):
            u, u2 = region.get('u'), region.get('u2')
            v, v2 = region.get('v'), region.get('v2')
            if None in (u, u2, v, v2):
                continue
            if max(u, u2) > 1.0001 or min(u, u2) < -0.0001 or \
               max(v, v2) > 1.0001 or min(v, v2) < -0.0001:
                overflowing.append(f"{region.get('atlas')}/{region.get('attachmentName')}")
    report('regions that hang off their own page', 'ok' if clamps else 'FAIL',
           (f'{len(overflowing)} in the player\'s skins, kept by trimming to the page'
            + (' -- e.g. ' + ', '.join(overflowing[:3]) if overflowing else ''))
           if clamps else f'{len(overflowing)} found and the Main REFUSES them, so they are never '
                          f'replaced and never rebased: ' + ', '.join(overflowing[:3]))
    hands = ('ok' if mesh_hand else 'FAIL', 'ok' if quad_hand else 'FAIL')
    report('nudge: who moves a MESH', hands[0],
           'SkinForger.Core PartTransforms, from the placement Main hands over'
           if mesh_hand else 'PartTransforms does not read OffsetX / does not apply')
    report('nudge: who moves a QUAD', hands[1],
           'Main itself (RegionGeometry.QuadNudge -> quad.x / y / rotation), which Core leaves to it'
           if quad_hand else 'NOBODY: Core leaves region quads to Main and Main has no writer')

    carried = {'offset', 'rotate'} if (mesh_hand and quad_hand) else set()
    for kind, found in counts.items():
        if not found:
            continue
        state = 'ok' if kind in carried else 'FAIL'
        note = ('applied to meshes by Core and to quads by Main' if kind in ('offset', 'rotate')
                else 'NO reader on either side: these adjustments are silently ignored')
        report(f'sidecar {kind} ({len(found)} of {checked} parts)', state,
               note + ' -- e.g. ' + ', '.join(found[:3]))
    if not any(counts.values()):
        report('sidecar adjustments', 'ok', f'none of the {checked} parts is adjusted by hand')


def fonts(layout):
    folder = os.path.join(layout.text, 'Fonts')
    if os.path.isdir(folder):
        found = [f for f in os.listdir(folder) if f.lower().endswith(('.ttf', '.otf', '.json'))]
        report('fonts', 'ok' if found else 'empty', f'{len(found)} file(s): {", ".join(found[:4])}')
    else:
        report('fonts', 'empty', 'no Text\\Fonts folder (the game font is kept)')


def flatten(data, prefix=''):
    if isinstance(data, dict):
        for key, value in data.items():
            if isinstance(value, (dict, list)):
                yield from flatten(value, f'{prefix}{key}.')
            else:
                yield f'{prefix}{key}', value
    elif isinstance(data, list):
        for index, value in enumerate(data):
            yield from flatten(value, f'{prefix}{index}.')


def sounds(layout):
    if not os.path.isdir(layout.sounds):
        report('sounds', 'empty', f'{layout.sounds} does not exist')
        return
    per = {}
    for costume in sorted(os.listdir(layout.sounds)):
        folder = os.path.join(layout.sounds, costume)
        if not os.path.isdir(folder):
            continue
        files = [f for f in os.listdir(folder) if f.lower().endswith(('.wav', '.ogg', '.mp3'))]
        if files:
            per[costume] = len(files)
    report('sounds', 'ok' if per else 'empty',
           ', '.join(f'{name}: {count}' for name, count in list(per.items())[:6]) or 'no audio files')


def loading_pictures(layout):
    if not os.path.isdir(layout.loading):
        report('loading pictures', 'FAIL', f'{layout.loading} does not exist -- the feature reads THIS path')
        return
    config = os.path.join(layout.loading, 'config.json')
    if not os.path.exists(config):
        report('loading pictures', 'FAIL', 'no config.json')
        return
    data = read_json(config)
    if '__error__' in data:
        report('loading pictures', 'FAIL', data['__error__'])
        return
    names = [n if os.path.splitext(n)[1] else n + '.png'
             for n in (data.get('LoadingPictures') or []) if isinstance(n, str) and n.strip()]
    present = [n for n in names if os.path.exists(os.path.join(layout.loading, n))]
    report('loading pictures', 'ok' if names and len(present) == len(names) else 'FAIL',
           f'{len(present)} of {len(names)} named picture(s) present')


def packages(layout):
    if not os.path.isdir(layout.packages):
        report('skin packages', 'empty', f'{layout.packages} does not exist')
        return
    found = [f for f in os.listdir(layout.packages) if f.lower().endswith(('.sfskin', '.sfpkg', '.zip'))]
    report('skin packages', 'ok' if found else 'empty',
           f'{len(found)} package file(s): {", ".join(found[:4])}' if found else 'no package installed')

    # A .sfskin is mounted into an IN-MEMORY file tree (SkinPackages\VirtualFiles: "Package contents are
    # never written to disk"). The OLD Main reached it because the Cecil tool REWROTE Main's File.Exists /
    # File.ReadAllBytes calls into VirtualFiles calls -- a Cecil-only contract, like the renamed methods and
    # the injected Initialize() calls. The authored Main is compiled, so it reads the real file system only
    # and a path inside a package simply does not exist for it: every part of a packaged skin is skipped,
    # with no error anywhere.
    source = ''
    for root, dirs, names in os.walk(MAIN):
        dirs[:] = [d for d in dirs if d not in ('obj', 'bin', 'Build', 'Analysis')]
        for name in names:
            if name.endswith('.cs'):
                with open(os.path.join(root, name), encoding='utf-8-sig', errors='replace') as handle:
                    source += handle.read()
    # Not just the name in a comment: the shim has to name Core's type AND the reads that matter have to
    # go through it -- the sidecar, the manifest, the PNG and the per-skin file lookup.
    routed = ('"SkinForger.SkinPackages.VirtualFiles"' in source and
              'SkinFiles.ReadAllBytes' in source and
              'SkinIndex.Read(directory, SkinFiles.Exists' in source)
    if found:
        report('packaged skins readable by Main', 'ok' if routed else 'FAIL',
               'Main routes skin file reads through VirtualFiles' if routed else
               'Main reads the real file system only (File.Exists / File.ReadAllBytes); a .sfskin lives in '
               'memory, so a packaged skin replaces nothing. Cecil used to rewrite these calls.')


def icons(layout):
    found = {}
    if os.path.isdir(layout.images):
        for costume in sorted(os.listdir(layout.images)):
            folder = os.path.join(layout.images, costume, 'SkinSwitchIcons')
            if os.path.isdir(folder):
                count = len([f for f in os.listdir(folder) if f.lower().endswith('.png')])
                if count:
                    found[costume] = count
    report('skin icons', 'ok' if found else 'empty',
           ', '.join(f'{k}: {v}' for k, v in found.items()) or 'no SkinSwitchIcons folder')


# ---------------------------------------------------------------- the deployed build

def deployed(layout):
    main = os.path.join(layout.mods, 'SkinForger.Melon.dll')
    core = os.path.join(layout.mods, 'SkinForger.Core.dll')
    built = os.path.join(MAIN, 'obj', 'Debug', 'SkinForger.Melon.dll')
    for label, path in (('deployed Main', main), ('deployed Core', core)):
        report(label, 'ok' if os.path.exists(path) else 'FAIL',
               f'{os.path.getsize(path)} bytes' if os.path.exists(path) else 'not in Mods\\SkinForger')
    if os.path.exists(main) and os.path.exists(built):
        same = open(main, 'rb').read() == open(built, 'rb').read()
        report('deployed Main is the current build', 'ok' if same else 'FAIL',
               'byte-identical to obj\\Debug' if same else 'the build has changed since it was deployed')


# ---------------------------------------------------------------- what only the game can answer

SUMMARY = re.compile(r'\[SkinForge\]\[(?P<module>[A-Za-z0-9_]+)\][^\n]*?'
                     r'(?P<count>\d+) (?:text\(s\)|replaced|audio hooks)')


def log_regression(layout):
    """
    Compares what each feature reported in the last session that ran the OLD Main with the last session
    that ran the authored one. Both logs are already on disk, so this costs nothing and is the only
    evidence available for the features that only announce themselves at runtime.

    It settled the "text is not replaced" question on 2026-11-19: both sessions replaced 43 character,
    2 elfin and 43 typed-table texts -- identical, so the cut did not lose any text.
    """
    folder = os.path.join(layout.root, 'MelonLoader', 'Logs')
    if not os.path.isdir(folder):
        report('log comparison', 'empty', 'no MelonLoader\\Logs folder')
        return

    files = sorted((os.path.join(folder, f) for f in os.listdir(folder) if f.endswith('.log')),
                   key=os.path.getmtime, reverse=True)
    old_main, new_main = None, None
    for path in files:
        try:
            text = open(path, encoding='utf-8', errors='replace').read()
        except OSError:
            continue
        authored = 'authored Main' in text
        if authored and new_main is None:
            new_main = (path, text)
        if not authored and 'SkinForge' in text and old_main is None:
            old_main = (path, text)
        if old_main and new_main:
            break

    if not new_main:
        report('log comparison', 'empty', 'no session of the authored Main has been logged yet')
        return
    if not old_main:
        report('log comparison', 'empty', 'no session of the old Main left to compare with')
        return

    def counts(text):
        found = {}
        for line in text.splitlines():
            match = SUMMARY.search(line)
            if match:
                key = match.group('module') + ': ' + re.sub(r'\d+', 'N', line.split(']')[-1].strip())
                found[key] = max(found.get(key, 0), int(match.group('count')))
        return found

    before, after = counts(old_main[1]), counts(new_main[1])
    lost = sorted(key for key in before if key not in after)
    lower = sorted(f'{key} ({before[key]} -> {after[key]})' for key in before
                   if key in after and after[key] < before[key])

    detail = f'old {os.path.basename(old_main[0])} vs authored {os.path.basename(new_main[0])}'
    if lost or lower:
        report('log comparison', 'FAIL',
               f'{detail}: {len(lost)} line(s) gone, {len(lower)} lower: ' + '; '.join((lost + lower)[:3]))
    else:
        report('log comparison', 'ok', f'{detail}: nothing the old Main reported got smaller')


def needs_game():
    for feature, why in (
        ('how it looks on screen', 'parts in the right place, nothing overlapping, no missing piece'),
        ('speed and heat', 'the extra texture round trip per part has never been measured'),
        ('texture binding', 'whether a composed atlas reaches the right renderer (SkeletonGraphic binds differently)'),
        ('Preview window', 'drawing, rotating and the launcher'),
        ('Dump and export', 'they write files, so they are checked by running them'),
    ):
        report(feature, 'needs game', why)


def main():
    game_root = sys.argv[1] if len(sys.argv) > 1 else os.path.abspath(os.path.join(MAIN, '..', '..'))
    layout = Layout(game_root)
    print(f'SkinForger QA sweep -- {game_root}\n')
    if not os.path.isdir(layout.sf):
        print(f'no SkinForger folder under {game_root}')
        return 2

    chosen = selections(layout)
    costume_images(layout, chosen)
    texts(layout)
    fonts(layout)
    anchors(layout)
    adjustments(layout)
    sounds(layout)
    loading_pictures(layout)
    packages(layout)
    icons(layout)
    deployed(layout)
    log_regression(layout)
    needs_game()

    order = {'FAIL': 0, 'empty': 1, 'ok': 2, 'needs game': 3}
    width = max(len(feature) for feature, _s, _d in _results)
    for state in ('FAIL', 'empty', 'ok', 'needs game'):
        rows = [row for row in _results if row[1] == state]
        if not rows:
            continue
        print(f'\n{state.upper()} ({len(rows)})')
        print('-' * (len(state) + 8))
        for feature, _state, detail in sorted(rows, key=lambda r: (order[r[1]], r[0])):
            print(f'  {feature:{width}}  {detail}')

    failures = sum(1 for _f, state, _d in _results if state == 'FAIL')
    print(f'\n{len(_results)} checks: {failures} FAIL, '
          f'{sum(1 for r in _results if r[1] == "ok")} ok, '
          f'{sum(1 for r in _results if r[1] == "empty")} nothing to replace, '
          f'{sum(1 for r in _results if r[1] == "needs game")} need the game')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
