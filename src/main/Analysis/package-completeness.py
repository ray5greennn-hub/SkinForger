"""Does an exported package carry everything the skin needs?

Run from this folder:

    python -I Analysis/package-completeness.py ["<game root>"]

Why this exists (2026-11-21). The export packs only the images it believes were edited. That belief is
a guess made from four signals, and a skin author found the hole in it: the way to make a part INVISIBLE
is to erase it -- keep the file, keep its size, make every pixel transparent. Such a file

  * leaves no replaced\\ backup once that history has been cleaned,
  * matches the recorded content hash as soon as one Reload records it,
  * has exactly the original size, and
  * needs no offset, rotation or scale in its sidecar,

so all four signals said "untouched" and the erased hair, eyes and mouth were left out of the package.
On the author's own machine the skin looked right; on anyone else's, the game's own hair came back on
top of the new head. Nothing logged a word.

The runtime fix is rule 5 in EditDetector (a fully transparent image counts as edited). This tool is the
regression check: it decodes each package, compares it with the creator folder it came from, and reports
every part that did not travel -- loudly when the part is blank, because that one inverts the author's
intent instead of merely falling back to the original.

Decoding needs the `cryptography` module. Alpha is read with zlib only, no image library.
"""

import hashlib
import io
import json
import os
import struct
import sys
import zipfile
import zlib

if hasattr(sys.stdout, 'reconfigure'):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')

HERE = os.path.dirname(os.path.abspath(__file__))
MAIN = os.path.dirname(HERE)

MAGIC = b'SFSKIN\x1a\x0a'
KEY_SEED = b'SkinForger/sfskin/format-1/Muse Dash Steam release'
HEADER = 8 + 2 + 2 + 16 + 16 + 8


def decode(path):
    """(package.json, {entry path: bytes}) the way SkinForger.Core reads it, or (None, error)."""
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
    info = json.loads(archive.read('package.json')) if 'package.json' in archive.namelist() else {}
    return (info, {n: archive.read(n) for n in archive.namelist() if n != 'package.json'}), None


def paeth(left, up, up_left):
    estimate = left + up - up_left
    to_left, to_up, to_corner = abs(estimate - left), abs(estimate - up), abs(estimate - up_left)
    if to_left <= to_up and to_left <= to_corner:
        return left
    return up if to_up <= to_corner else up_left


def fully_transparent(data):
    """True when every pixel's alpha is 0. False for anything this cannot decide -- the same rule as
    PngAlpha.cs in Core, so the two agree about a given file."""
    try:
        if data[:8] != b'\x89PNG\r\n\x1a\n':
            return False
        at, width, height, depth, colour, pixels = 8, 0, 0, 0, -1, bytearray()
        while at + 8 <= len(data):
            length, kind = struct.unpack('>I4s', data[at:at + 8])
            body = data[at + 8:at + 8 + length]
            at += 12 + length
            if kind == b'IHDR':
                width, height, depth, colour = struct.unpack('>IIBB', body[:10])
                if body[12] != 0 or depth != 8 or colour not in (4, 6):
                    return False
            elif kind == b'IDAT':
                pixels += body
            elif kind == b'IEND':
                break
        if colour not in (4, 6) or not pixels or width <= 0 or height <= 0:
            return False
        samples = 4 if colour == 6 else 2
        raw = zlib.decompress(bytes(pixels))
        stride = width * samples
        if len(raw) < (stride + 1) * height:
            return False
        previous, at = bytearray(stride), 0
        for _ in range(height):
            filter_type = raw[at]
            line = bytearray(raw[at + 1:at + 1 + stride])
            at += 1 + stride
            for x in range(stride):
                left = line[x - samples] if x >= samples else 0
                up = previous[x]
                up_left = previous[x - samples] if x >= samples else 0
                if filter_type == 1:
                    line[x] = (line[x] + left) & 0xFF
                elif filter_type == 2:
                    line[x] = (line[x] + up) & 0xFF
                elif filter_type == 3:
                    line[x] = (line[x] + ((left + up) >> 1)) & 0xFF
                elif filter_type == 4:
                    line[x] = (line[x] + paeth(left, up, up_left)) & 0xFF
                elif filter_type != 0:
                    return False
            if any(line[x] for x in range(samples - 1, stride, samples)):
                return False
            previous = line
        return True
    except Exception:
        return False


def inspect(path, creator):
    info, entries = path[1]
    name = os.path.basename(path[0])
    print('\n' + '=' * 72)
    print(f'{name}   [{info.get("Contents") or "whole folder"}]')

    packed = {}
    for entry in entries:
        parts = entry.split('/')
        if len(parts) >= 3 and parts[0] == 'skins' and parts[-1].lower().endswith('.png'):
            packed.setdefault(parts[1], set()).add('/'.join(parts[2:]))
    if not packed:
        print('  no skin images in this package (texts / fonts / other pictures only)')
        return 0

    problems = 0
    for costume, files in sorted(packed.items()):
        folder = os.path.join(creator, costume)
        if not os.path.isdir(folder):
            print(f'  {costume}: the creator folder is gone, nothing to compare with')
            continue
        own = {}
        for root, _, names in os.walk(folder):
            if 'replaced' in os.path.relpath(root, folder).split(os.sep):
                continue
            for leaf in names:
                # Not parts of the skeleton: the library thumbnail and the skin-switch icons.
                if leaf.lower() in ('selectionthumbnail.png', 'preview.png'):
                    continue
                if os.path.basename(root).lower() in ('icon', 'skinswitchicons'):
                    continue
                if leaf.lower().endswith('.png'):
                    rel = os.path.relpath(os.path.join(root, leaf), folder).replace(os.sep, '/')
                    own[rel] = os.path.join(root, leaf)
        missing = sorted(set(own) - files)
        print(f'  {costume}: {len(files)} packed, {len(own)} in the folder, {len(missing)} not packed')
        blank = [m for m in missing if fully_transparent(open(own[m], 'rb').read())]
        for item in blank:
            print(f'      ERASED BUT NOT PACKED: {item}')
            print('        -> you made this part invisible; whoever installs the package gets the')
            print("           game's own art back instead. Re-export (Core FFE71798 or newer).")
        problems += len(blank)
        plain = [m for m in missing if m not in blank]
        for item in plain[:5]:
            print(f'      not packed (looks unchanged, the original shows): {item}')
        if len(plain) > 5:
            print(f'      ... and {len(plain) - 5} more that look unchanged')
        for item in sorted(files):
            sidecar = os.path.splitext(item)[0] + '.json'
            if os.path.isfile(os.path.join(folder, sidecar)) and \
                    f'skins/{costume}/{sidecar}' not in entries:
                print(f'      SIDECAR DID NOT TRAVEL: {sidecar} (the part will sit in the wrong place)')
                problems += 1
    return problems


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.abspath(os.path.join(MAIN, '..', '..'))
    creator = os.path.join(root, 'SkinForger', 'SkinCreator', 'Images')
    folders = [os.path.join(root, 'SkinForger', 'ExportSkin'),
               os.path.join(root, 'SkinForger', 'Skins')]

    packages = []
    for folder in folders:
        if os.path.isdir(folder):
            packages += [os.path.join(folder, f) for f in sorted(os.listdir(folder))
                         if f.lower().endswith('.sfskin')]
    print(f'creator folder: {creator}')
    print(f'{len(packages)} package(s) in ExportSkin + Skins')
    if not packages:
        return 0

    problems = 0
    for package in packages:
        result, error = decode(package)
        if error:
            print(f'\n{os.path.basename(package)}: cannot inspect: {error}')
            continue
        problems += inspect((package, result), creator)

    print('\n' + '=' * 72)
    if problems:
        print(f'{problems} part(s) that will look WRONG on someone else\'s machine. Re-export those packages.')
    else:
        print('every erased part travelled, and every packed part brought its sidecar')
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
