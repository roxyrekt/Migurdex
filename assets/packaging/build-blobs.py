#!/usr/bin/env python3
"""
Build a content-addressed blob store for delta updates.

Input  : publish directory (contains cli/, api/, and the migurdex launcher)
Output : <runtime>.blobs.tar       (uncompressed tar; one <sha>.xz per unique blob)
         <runtime>.manifest.json.gz (path -> sha, sha -> offset/len)

Why uncompressed tar: each blob is already xz-compressed individually. Keeping
the tar uncompressed lets the updater fetch a single blob with an HTTP Range
request (`bytes=off-(off+len-1)`) without touching the rest of the store.

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

    # ----- 2. write blobs.tar (per-blob xz, uncompressed outer tar) -----
    blobs_tar = os.path.join(out, f"migurdex-{args.runtime}.blobs.tar")
    blobs: dict[str, dict] = {}

    compressor = pick_compressor(args.xz_level)
    print(f"compressor    : {compressor.__name__}")

    with tarfile.open(blobs_tar, "w", format=tarfile.GNU_FORMAT) as tf:
        for sha in sorted(sha_to_path):
            try:
                data = compressor(sha_to_path[sha], args.xz_level)
            except Exception as exc:  # noqa: BLE001
                print(f"error: compress failed for {sha}: {exc}", file=sys.stderr)
                return 1

            info = tarfile.TarInfo(name=f"{sha}.xz")
            info.size = len(data)
            info.mode = 0o644
            info.mtime = 0
            info.uid = 0
            info.gid = 0
            info.uname = "root"
            info.gname = "root"
            tf.addfile(info, io.BytesIO(data))
            blobs[sha] = {"sha": sha, "len": len(data)}

    # ----- 3. read offsets back (tarfile doesn't expose them while writing) -----
    with tarfile.open(blobs_tar, "r") as tf:
        for member in tf:
            if not member.name.endswith(".xz"):
                continue
            sha = member.name[:-3]
            if sha in blobs:
                blobs[sha]["off"] = member.offset_data

    missing = [b["sha"] for b in blobs.values() if "off" not in b]
    if missing:
        print(f"error: {len(missing)} blob(s) missing offset", file=sys.stderr)
        return 1

    # ----- 4. manifest -----
    manifest = {
        "version": 1,
        "runtime": args.runtime,
        "blobs": sorted(blobs.values(), key=lambda b: b["sha"]),
        "paths": dict(sorted(path_to_sha.items())),
        # Icerik adresli depo mod biti tasimaz; delta yazimi 644 birakir.
        # Calistirilabilir dosyalar burada listelenir ki updater modu geri kursun.
        "exec": sorted(exec_paths),
    }
    raw = json.dumps(manifest, separators=(",", ":")).encode("utf-8")
    man_path = os.path.join(out, f"migurdex-{args.runtime}.manifest.json.gz")
    with open(man_path, "wb") as fh:
        with gzip.GzipFile(fileobj=fh, mode="wb", compresslevel=9, mtime=0) as gz:
            gz.write(raw)

    # ----- 5. report -----
    tar_sz = os.path.getsize(blobs_tar)
    man_sz = os.path.getsize(man_path)
    payload = sum(b["len"] for b in blobs.values())
    print(f"paths         : {len(path_to_sha)}")
    print(f"unique blobs  : {len(blobs)}")
    print(f"blobs.tar     : {tar_sz/1048576:.2f} MB  (payload {payload/1048576:.2f} MB)")
    print(f"manifest.json : {man_sz/1024:.1f} KB  (raw {len(raw)/1024:.0f} KB)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
