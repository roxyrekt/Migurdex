#!/usr/bin/env python3
"""
Build a content-addressed blob store for delta updates.

Input  : publish directory (contains cli/, api/, and the migurdex launcher)
Output : <runtime>.delta.tar  (uncompressed tar)

Layout of delta.tar:

    [512B header: manifest.json.gz][manifest gzip][pad to 512]
    [512B header: <sha>.xz]       [blob xz]      [pad to 512]
    ...

The manifest is the first tar entry, so a client reads one 512-byte header to
learn its size and offset, then fetches only that entry. Blob offsets in the
manifest are relative to the start of the blob section (the byte right after
the padded manifest entry), which keeps the document independent of its own
size.

Why an uncompressed tar: each blob is already xz-compressed individually, so a
single blob can be fetched with one HTTP Range request
(`bytes=(payload+off)-(payload+off+len-1)`) without touching the rest of the
archive. Keeping the container a real tar means `tar -tf` and `tar -x` still
work for inspection.

Same-content files (including hardlinks from the split cli/api layout) share a
single blob, so a typical delta update only fetches the handful of files that
actually changed.
"""

from __future__ import annotations

import argparse
import fnmatch
import gzip
import lzma
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import tarfile


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def walk_source(src: str, exclude: list[str]):
    """Yield (rel_path, sha, physical_path) for regular files, deduplicating hardlinks."""
    seen_inode: dict[tuple[int, int], str] = {}
    sha_to_path: dict[str, str] = {}

    for dirpath, _dirnames, filenames in os.walk(src):
        for name in filenames:
            full = os.path.join(dirpath, name)
            if os.path.islink(full):
                continue

            rel = os.path.relpath(full, src).replace(os.sep, "/")

            if any(fnmatch.fnmatch(rel, pat) for pat in exclude):
                continue

            st = os.stat(full)
            key = (st.st_dev, st.st_ino)

            if key in seen_inode:
                sha = seen_inode[key]
            else:
                sha = sha256_file(full)
                seen_inode[key] = sha
                if sha not in sha_to_path:
                    sha_to_path[sha] = full

            yield rel, sha, full

    return sha_to_path


def is_executable(path: str) -> bool:
    """Ger\u00e7ek giri\u015f noktas\u0131 m\u0131?

    'dotnet publish' veri dosyalar\u0131na 744 (sadece sahibi +x), giri\u015f
    noktalar\u0131na 755 (herkes +x) veriyor. Mod bitinin tamam\u0131na bakmak
    ikisini ay\u0131r\u0131r; tek ba\u015f\u0131na 0o111 kontrol\u00fc 744'\u00fc de yakalar.

    Windows'ta mod biti yok; uzant\u0131ya bak\u0131l\u0131r.
    """
    if os.name == "nt":
        return os.path.basename(path).lower().endswith((".exe", ".bat", ".cmd"))

    try:
        return os.stat(path).st_mode & 0o111 == 0o111
    except OSError:
        return False


def compress_xz_binary(path: str, level: str) -> bytes:
    """Compress via the `xz` binary (fastest, multithreaded)."""
    r = subprocess.run(["xz", f"-{level}", "-T0", "-c", path], capture_output=True, check=False)
    if r.returncode != 0:
        raise RuntimeError(r.stderr.decode()[:400])
    return r.stdout


def compress_xz_python(path: str, level: str) -> bytes:
    """Pure-Python fallback: same XZ container, so the updater cannot tell the difference."""
    with open(path, "rb") as fh:
        return lzma.compress(fh.read(), format=lzma.FORMAT_XZ, preset=int(level))


def pick_compressor(level: str):
    """Prefer the `xz` binary (multithreaded) but never depend on it existing."""
    if shutil.which("xz"):
        return compress_xz_binary
    return compress_xz_python


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--input", required=True, help="publish directory (cli/, api/, migurdex)")
    ap.add_argument("--output-dir", required=True, help="where to write the two files")
    ap.add_argument("--runtime", required=True, help="runtime identifier, e.g. linux-x64")
    ap.add_argument("--xz-level", default="9")
    ap.add_argument(
        "--exclude",
        action="append",
        default=[],
        help="glob pattern (relative path) to skip; repeatable, e.g. --exclude '*.tar.xz'",
    )
    args = ap.parse_args()

    src = os.path.abspath(args.input)
    out = os.path.abspath(args.output_dir)
    os.makedirs(out, exist_ok=True)

    if not os.path.isdir(src):
        print(f"error: input not a directory: {src}", file=sys.stderr)
        return 1

    # ----- 1. inventory -----
    sha_to_path: dict[str, str] = {}
    path_to_sha: dict[str, str] = {}
    exec_paths: list[str] = []

    for rel, sha, full in walk_source(src, args.exclude):
        path_to_sha[rel] = sha
        if is_executable(full):
            exec_paths.append(rel)
        if sha not in sha_to_path:
            sha_to_path[sha] = full

    if not path_to_sha:
        print("error: no regular files found", file=sys.stderr)
        return 1

    compressor = pick_compressor(args.xz_level)
    print(f"compressor    : {compressor.__name__}")

    compressed: dict[str, bytes] = {}
    for sha in sorted(sha_to_path):
        try:
            compressed[sha] = compressor(sha_to_path[sha], args.xz_level)
        except Exception as exc:  # noqa: BLE001
            print(f"error: compress failed for {sha}: {exc}", file=sys.stderr)
            return 1

    blobs: list[dict] = []
    running = 0
    for sha in sorted(compressed):
        blobs.append({"sha": sha, "off": running + 512, "len": len(compressed[sha])})
        running += 512 + (len(compressed[sha]) + 511) // 512 * 512

    manifest = {
        "version": 1,
        "runtime": args.runtime,
        "blobs": blobs,
        "paths": dict(sorted(path_to_sha.items())),
        "exec": sorted(exec_paths),
    }
    raw = json.dumps(manifest, separators=(",", ":")).encode("utf-8")

    man_buf = io.BytesIO()
    with gzip.GzipFile(fileobj=man_buf, mode="wb", compresslevel=9, mtime=0) as gz:
        gz.write(raw)
    man_gz = man_buf.getvalue()

    delta_tar = os.path.join(out, f"migurdex-{args.runtime}.delta.tar")

    with tarfile.open(delta_tar, "w", format=tarfile.GNU_FORMAT) as tf:
        def add(name: str, data: bytes) -> None:
            info = tarfile.TarInfo(name=name)
            info.size = len(data)
            info.mode = 0o644
            info.mtime = 0
            info.uid = 0
            info.gid = 0
            info.uname = "root"
            info.gname = "root"
            tf.addfile(info, io.BytesIO(data))

        add("manifest.json.gz", man_gz)
        for sha in sorted(compressed):
            add(f"{sha}.xz", compressed[sha])

    payload_start = 512 + (len(man_gz) + 511) // 512 * 512

    with tarfile.open(delta_tar, "r") as tf:
        members = {m.name: m for m in tf if m.isfile()}

    if "manifest.json.gz" not in members:
        print("error: manifest entry missing from delta.tar", file=sys.stderr)
        return 1

    actual_start = members["manifest.json.gz"].offset_data + (len(man_gz) + 511) // 512 * 512
    if actual_start != payload_start:
        print(f"error: payload start mismatch: {actual_start} != {payload_start}", file=sys.stderr)
        return 1

    for blob in blobs:
        member = members.get(f"{blob['sha']}.xz")
        if member is None:
            print(f"error: blob missing from delta.tar: {blob['sha'][:12]}", file=sys.stderr)
            return 1
        if member.offset_data - payload_start != blob["off"]:
            print(f"error: offset mismatch for {blob['sha'][:12]}", file=sys.stderr)
            return 1

    tar_sz = os.path.getsize(delta_tar)
    payload = sum(len(v) for v in compressed.values())
    print(f"paths         : {len(path_to_sha)}")
    print(f"unique blobs  : {len(compressed)}")
    print(f"delta.tar     : {tar_sz/1048576:.2f} MB  (payload {payload/1048576:.2f} MB)")
    print(f"manifest      : {len(man_gz)/1024:.1f} KB at +512  (raw {len(raw)/1024:.0f} KB)")
    print(f"payload start : {payload_start}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
