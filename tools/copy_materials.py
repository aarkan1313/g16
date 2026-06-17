#!/usr/bin/env python3
"""Regenerate assets/materials/ from D:\\assets (the texture library is gitignored).

Walks D:\\assets for PBR material folders (albedo+normal), collapses seed/variant
duplicates to one representative each, and copies albedo/normal/roughness/ao into
assets/materials/<name>/. The ACCEPTED subset is recorded in
data/material_library.json; full pass/fail in data/material_verdicts.json.

Usage:  python tools/copy_materials.py
Then:   <godot> --headless --path . --import
"""
import os, re, shutil

ROOTS = [r"D:\assets"]
DEST = os.path.join(os.path.dirname(__file__), "..", "assets", "materials")
EXCLUDE = re.compile(
    r"(_backup|_archive|\\venv|site-packages|\\.git|world\\worlds|world\\regions|godot_root_artifacts)",
    re.I)
MAPS = {
    "albedo":    ["albedo", "_color", "basecolor", "_col", "diffuse"],
    "normal":    ["normal", "_nor"],
    "roughness": ["roughness", "_rough"],
    "ao":        ["ambientocclusion", "_ao", "ao."],
}

def find_map(folder, keys):
    for f in os.listdir(folder):
        lf = f.lower()
        if lf.endswith(".png") and any(k in lf for k in keys):
            return os.path.join(folder, f)
    return None

def write_import(png_path):
    """Write a .import sidecar with mipmaps ON. Godot otherwise auto-generates
    these with mipmaps/generate=false, which makes terrain textures ALIAS under
    minification -> crawling speckle/shimmer in motion (the 'fuzziness' bug). The
    .ctex uid is filled in by Godot on --import; we only need to pin mipmaps on."""
    imp = png_path + ".import"
    if os.path.exists(imp):
        # already exists (e.g. prior import): just ensure mipmaps on
        with open(imp, "r", encoding="utf-8") as fh:
            txt = fh.read()
        if "mipmaps/generate=false" in txt:
            txt = txt.replace("mipmaps/generate=false", "mipmaps/generate=true")
            with open(imp, "w", encoding="utf-8") as fh:
                fh.write(txt)
        return
    with open(imp, "w", encoding="utf-8") as fh:
        fh.write(
            '[remap]\n\n'
            'importer="texture"\n'
            'type="CompressedTexture2D"\n\n'
            '[params]\n\n'
            'mipmaps/generate=true\n'
        )

def dedup_key(name):
    n = name.lower()
    n = re.sub(r'_(seed|v|flux|h)\d.*', '', n)
    n = re.sub(r'__.*', '', n)
    n = re.sub(r'_prompts.*', '', n)
    n = re.sub(r'_\d{6,}.*', '', n)
    return re.sub(r'[^a-z0-9]+', '_', n).strip('_')

def main():
    os.makedirs(DEST, exist_ok=True)
    seen, count = set(), 0
    for root in ROOTS:
        for dirpath, dirs, files in os.walk(root):
            if EXCLUDE.search(dirpath):
                dirs[:] = []
                continue
            alb, nrm = find_map(dirpath, MAPS["albedo"]), find_map(dirpath, MAPS["normal"])
            if not alb or not nrm:
                continue
            base = os.path.basename(dirpath)
            if base.lower() in ("ground", "mid", "rock", "high", "low"):
                base = os.path.basename(os.path.dirname(dirpath)) + "_" + base
            key = dedup_key(base)
            if not key or key in seen:
                continue
            seen.add(key)
            out = os.path.join(DEST, key)
            os.makedirs(out, exist_ok=True)
            alb_out = os.path.join(out, "albedo.png")
            nrm_out = os.path.join(out, "normal.png")
            shutil.copy(alb, alb_out); write_import(alb_out)
            shutil.copy(nrm, nrm_out); write_import(nrm_out)
            for m in ("roughness", "ao"):
                p = find_map(dirpath, MAPS[m])
                if p:
                    m_out = os.path.join(out, m + ".png")
                    shutil.copy(p, m_out); write_import(m_out)
            count += 1
    print(f"copied {count} distinct materials to {os.path.abspath(DEST)}")

if __name__ == "__main__":
    main()
