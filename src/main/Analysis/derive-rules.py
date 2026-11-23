"""
Derives the Dump's frozen rules from real files, and is the evidence for them.

Run:  python -I derive-rules.py "<game>\SkinForger\SkinCreator\Images"

Why this script exists: the authored Main is written without transcribing the old Main (clean-room rule,
SF日志\SF切割SkinChanger计划.md part 6). The conventions it must reproduce were therefore measured from
the files the old Dump actually wrote, not read out of its code. This script is that measurement; the
numbers in its output are quoted in Pure\DumpNaming.cs and Pure\RegionGeometry.cs, and the C# Tests re-check
every rule on whatever files are present.

Result on the development machine, 2026-11-17 (4 costumes, 23 atlases, 1134 manifest entries):

  naming
    file is "<atlas>\<name>.png", two segments, folder == atlas          0 violations
    displayName == last '/' segment of attachmentName                    0 violations
    PNG stem == displayName, "_2" only on a repeat in the same atlas     0 violations (1 repeat: hair_B)
    no single-use name carries a suffix                                  0 violations

  geometry
    x = round(min(u,u2) * pageW)                                         0 violations
    y = pageH - round(max(v,v2) * pageH)                                 0 violations
    width/height = the UV span, transposed iff rotate                    0 violations
    rotate == (degrees == 90)                                            exactly true
    degrees seen: 0 (794), 90 (280), 180 (25), 270 (35)
    width/height != original size (trimmed parts)                        127 entries

  the dumped PNG
    canvas == untrimmed size (originalWidth x originalHeight)
    trimmed drawing placed at (offsetX, originalHeight - offsetY - height)
      -> fits the alpha bounds of 118 of 119 real trimmed PNGs
         (the "offsetY from the top" reading fits only 84, so it is wrong)
      -> the one miss has drawing outside the original trim: a creator painted past the silhouette
    PNGs larger than the untrimmed size are the creator's enlarged canvases (the expansion feature)
"""
import collections
import glob
import json
import os
import struct
import sys

SEP = chr(92)


def load(images_folder):
    rows = []
    for manifest in glob.glob(os.path.join(images_folder, "*", "manifest.json")):
        with open(manifest, encoding="utf-8-sig") as handle:
            data = json.load(handle)
        costume = os.path.basename(os.path.dirname(manifest))
        for region in data.get("regions", []):
            rows.append((costume, os.path.dirname(manifest), region))
    return rows


def png_size(path):
    with open(path, "rb") as handle:
        header = handle.read(26)
    return struct.unpack(">II", header[16:24])


def check_naming(rows):
    violations = collections.Counter()
    replay = collections.defaultdict(set)
    repeats = 0
    for costume, _folder, region in rows:
        parts = region["file"].split(SEP)
        if len(parts) != 2 or parts[0] != region["atlas"] or not parts[1].endswith(".png"):
            violations["file shape"] += 1
            continue
        stem = parts[1][:-4]
        tail = region["attachmentName"].replace(SEP, "/").split("/")[-1]
        if region["displayName"] != tail:
            violations["displayName != tail(attachmentName)"] += 1
        used = replay[(costume, region["atlas"])]
        candidate, suffix = region["displayName"], 2
        while candidate in used:
            candidate = f"{region['displayName']}_{suffix}"
            suffix += 1
        if suffix > 2:
            repeats += 1
        used.add(candidate)
        if stem != candidate:
            violations["name not reproduced by the numbering rule"] += 1
    print("naming violations:", dict(violations) or "NONE", f"({repeats} repeated names)")


def check_geometry(rows):
    violations = collections.Counter()
    degrees = collections.Counter()
    trimmed = 0
    pages = {}
    for costume, _folder, region in rows:
        degrees[(region["rotate"], region["degrees"])] += 1
        if region["rotate"] != (region["degrees"] == 90):
            violations["rotate != (degrees == 90)"] += 1
        du = abs(region["u2"] - region["u"])
        dv = abs(region["v"] - region["v2"])
        if du <= 0 or dv <= 0:
            violations["degenerate UVs"] += 1
            continue
        stored_w = region["height"] if region["rotate"] else region["width"]
        stored_h = region["width"] if region["rotate"] else region["height"]
        page_w, page_h = round(stored_w / du), round(stored_h / dv)
        pages[(costume, region["atlas"])] = (page_w, page_h)
        if round(min(region["u"], region["u2"]) * page_w) != region["x"]:
            violations["x"] += 1
        if page_h - round(max(region["v"], region["v2"]) * page_h) != region["y"]:
            violations["y"] += 1
        if round(du * page_w) != stored_w or round(dv * page_h) != stored_h:
            violations["span"] += 1
        if (region["width"], region["height"]) != (region["originalWidth"], region["originalHeight"]):
            trimmed += 1
    print("geometry violations:", dict(violations) or "NONE")
    print("  degrees seen:", {f"rotate={r} degrees={d}": n for (r, d), n in sorted(degrees.items())})
    print("  trimmed entries:", trimmed, " atlases:", len(pages))


def check_dumped_images(rows):
    try:
        from PIL import Image
    except ImportError:
        print("dumped images: skipped (Pillow not installed)")
        return
    from_bottom = from_top = other_canvas = 0
    misses = []
    for _costume, folder, region in rows:
        path = os.path.join(folder, region["file"].replace(SEP, os.sep))
        if not os.path.exists(path):
            continue                                    # censored parts are listed but never written
        width, height = png_size(path)
        original = (region["originalWidth"], region["originalHeight"])
        if (region["width"], region["height"]) == original:
            continue                                    # untrimmed: tells us nothing about the offset
        if (width, height) != original:
            other_canvas += 1                           # the creator enlarged this canvas
            continue
        box = Image.open(path).convert("RGBA").getbbox()
        if box is None:
            continue
        left, top, right, bottom = box
        ox, oy = int(region["offsetX"]), int(region["offsetY"])
        w, h = region["width"], region["height"]
        inside_top = left >= ox - 1 and top >= oy - 1 and right <= ox + w + 1 and bottom <= oy + h + 1
        top_from_bottom = original[1] - oy - h
        inside_bottom = (left >= ox - 1 and top >= top_from_bottom - 1 and
                         right <= ox + w + 1 and bottom <= top_from_bottom + h + 1)
        from_top += 1 if inside_top else 0
        from_bottom += 1 if inside_bottom else 0
        if not inside_bottom:
            misses.append(region["file"])
    print(f"dumped images: offsetY from the BOTTOM fits {from_bottom}, from the TOP fits {from_top}")
    print(f"  enlarged canvases skipped: {other_canvas}; misses: {misses[:5]}")


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    images = sys.argv[1]
    rows = load(images)
    if not rows:
        print(f"no manifest.json found under {images}")
        return 1
    print(f"{len(rows)} manifest entries under {images}")
    check_naming(rows)
    check_geometry(rows)
    check_dumped_images(rows)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
