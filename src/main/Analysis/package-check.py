"""Every installed .sfskin, what is really inside it, and whether it can show anything.

Run from this folder:

    python -I Analysis/package-check.py ["<game root>"]

Why this exists: a player enabled a package and said "the textures did not change". There are two
separate reasons that can happen, and neither one logs anything:

  1. the package cannot be READ. A .sfskin is decoded into memory and never written to disk, so the
     Main has to read skin files through SkinForger.Core's VirtualFiles. The authored Main read the
     real file system only until 2026-11-20, so every packaged skin was silently skipped;

  2. the package IS read, and still shows nothing, because every image in it is also in the player's
     own SkinCreator folder -- and the documented order is
     SkinCreator (the player's own work) > enabled packages > the game's own art.
     A package whose images are all shadowed is working perfectly and is invisible.

This decodes each package the way Core does and reports both: what it would replace, what the library
says about it, and how much of it the player's own folder covers.

Decoding needs the `cryptography` module. Without it the inventory is skipped and the rest still runs.
"""

import hashlib
import io
import json
import os
import sys
import zipfile

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

HERE = os.path.dirname(os.path.abspath(__file__))
MAIN = os.path.dirname(HERE)
SOURCE = os.path.dirname(MAIN)

MAGIC = b'SFSKIN\x1a\x0a'
KEY_SEED = b'SkinForger/sfskin/format-1/Muse Dash Steam release'
HEADER = 8 + 2 + 2 + 16 + 16 + 8
BACKSLASH = chr(92)


def decode(path):
    """(info, {entry path: size}) exactly as SkinForger.Core reads it, or an error string."""
    try:
        from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
    except ImportError:
        return None, 'the cryptography module is not installed here'
    data = open(path, 'rb').read()
    if len(data) < HEADER + 32 or data[:8] != MAGIC:
        return None, 'not a SkinForger package'
    salt, iv = data[12:28], data[28:44]
    length = int.from_bytes(data[44:52], 'little')
    if HEADER + length + 32 != len(data):
        return None, 'length mismatch'
    if hashlib.sha256(data[:HEADER + length]).digest() != data[HEADER + length:]:
        return None, 'checksum mismatch (damaged or modified)'
    key = hashlib.sha256(KEY_SEED + salt).digest()
    worker = Cipher(algorithms.AES(key), modes.CBC(iv)).decryptor()
    plain = worker.update(data[HEADER:HEADER + length]) + worker.finalize()
    plain = plain[:-plain[-1]]
    archive = zipfile.ZipFile(io.BytesIO(plain))
    names = archive.namelist()
    info = json.loads(archive.read('package.json')) if 'package.json' in names else {}
    return (info, [n for n in names if n != 'package.json']), None


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.abspath(os.path.join(MAIN, '..', '..'))
    skins = os.path.join(root, 'SkinForger', 'Skins')
    creator = os.path.join(root, 'SkinForger', 'SkinCreator', 'Images')
    state_path = os.path.join(root, 'UserData', 'SkinForger', 'skin-library.json')

    enabled = []
    if os.path.isfile(state_path):
        try:
            enabled = json.load(open(state_path, encoding='utf-8')).get('Enabled', [])
        except Exception:
            enabled = []
    print(f'packages in {skins}')
    print(f'library state: {len(enabled)} enabled' + (f' -- {", ".join(enabled)}' if enabled else ''))

    if not os.path.isdir(skins):
        print('  (no Skins folder)')
        return 0
    files = sorted(f for f in os.listdir(skins) if f.lower().endswith('.sfskin'))
    if not files:
        print('  (no packages installed)')
        return 0

    problems = 0
    for name in files:
        print('\n' + '=' * 72)
        on = any(name.lower() == e.lower() for e in enabled)
        print(f'{name}   [{"ENABLED" if on else "off"}]')
        result, error = decode(os.path.join(skins, name))
        if error:
            print(f'  cannot inspect: {error}')
            continue
        info, entries = result
        game = (info.get('Game') or '').strip()
        print(f'  made for: {game or "(unset)"}   generator: {info.get("Generator", "?")}')
        if game and game != 'Muse Dash (Steam)' and (info.get('Redistribution') or '') != 'any':
            print('  WOULD BE REFUSED: made for another game/platform and not released for this one')
            problems += 1

        images = [e for e in entries if e.lower().endswith('.png')]
        print(f'  {len(entries)} file(s), {len(images)} image(s)')

        shadowed, live = [], []
        for entry in images:
            parts = entry.split('/')
            if len(parts) < 3 or parts[0] != 'skins':
                continue
            own = os.path.join(creator, *parts[1:])
            (shadowed if os.path.isfile(own) else live).append('/'.join(parts[1:]))
        if images:
            print(f'  of those, {len(shadowed)} are ALSO in the player\'s own SkinCreator folder')
            print(f'            {len(live)} are not, so only those can ever show')
        for item in live[:5]:
            print(f'      would show: {item}')
        for item in shadowed[:5]:
            print(f'      shadowed by the player\'s own file: {item}')
        if images and not live:
            print('  NOTHING THIS PACKAGE CONTAINS CAN SHOW: every image in it is also in the player\'s')
            print('  own SkinCreator folder, and SkinCreator outranks enabled packages. Working as')
            print('  designed -- but indistinguishable from "the package does nothing".')
            problems += 1

    routed = 'VirtualFiles' in open(
        os.path.join(MAIN, 'Pure', 'SkinFiles.cs'), encoding='utf-8', errors='replace').read() \
        if os.path.isfile(os.path.join(MAIN, 'Pure', 'SkinFiles.cs')) else False
    print('\n' + '=' * 72)
    print(f'can the Main read a package at all: {"yes" if routed else "NO -- it reads the real file system only"}')
    print(f'{len(files)} package(s), {problems} that cannot show anything as things stand')
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
