#!/usr/bin/env python3
"""
fna_build.py: FNA and the native libraries it runs on, built from their upstream sources, for Avatar
Studio's player package: every file in it is open source, shipped with its licence. The studio builds
against FNA's XNA surface only.

    python fna_build.py --work <folder>        # -> <folder>/out

The steps (everything under --work, outside the source tree):
 1. FNA at tag 26.10 (github.com/FNA-XNA/FNA, 2026-10-01) with its submodules (FNA3D, FAudio,
    MojoShader inside FNA3D, the SDL bindings), shallow.
 2. SDL3 3.4.0, libsdl-org's own Windows release (github.com/libsdl-org/SDL/releases/tag/release-3.4.0):
    SDL3-3.4.0-win32-x64.zip (SDL3.dll, shipped) and SDL3-devel-3.4.0-VC.zip (headers, import library
    and CMake package, to build against), each held to the SHA-256 recorded below (taken 2026-10-24 from
    the first download; [ours] a pin, so a changed release is noticed).
 3. FNA3D and FAudio, FNA 26.10's submodules, with MSVC (vcvars64), CMake and Ninja: Release, the C
    runtime linked statically ([ours]: a player needs no Visual C++ redistributable). Both import only
    KERNEL32 and SDL3; SDL3.dll imports only Windows' own DLLs.
 4. FNA.dll with the .NET Framework csc, from FNA's own Makefile: its source and resource lists, /unsafe,
    optimised (the Makefile asks mcs for language version 4; csc 4.x is C# 5).
 5. <work>/out: FNA.dll, FNA.dll.config, FNA3D.dll, FAudio.dll, SDL3.dll, licenses/ and SOURCE.txt.

libtheorafile (FNA's VideoPlayer) is not built: the studio plays no video, and FNA loads it only then.
"""
import argparse
import hashlib
import os
import re
import shutil
import subprocess
import sys
import urllib.request
import zipfile

FNA_TAG = "26.10"
FNA_GIT = "https://github.com/FNA-XNA/FNA"
SDL_VERSION = "3.4.0"
SDL_URL = "https://github.com/libsdl-org/SDL/releases/download/release-%s/" % SDL_VERSION
SDL_ZIPS = {
    "SDL3-%s-win32-x64.zip" % SDL_VERSION: "fdb00563e2a17e7125681da0e8b06afcc7e0ef7ab73b7edcd8f2f7bd0ae73d3e",
    "SDL3-devel-%s-VC.zip" % SDL_VERSION: "4489ba0070b8ce047b8717e4b71fa7e35ac71362e7a6bb2a90a7a27f624b3d5d",
}
CSC = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), r"Microsoft.NET\Framework64\v4.0.30319\csc.exe")


def find_vcvars():
    """vcvars64.bat of the newest Visual Studio with the C++ tools (vswhere, which every Visual Studio
    installer puts at the same place), else the usual 2022 folders."""
    vswhere = os.path.join(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"), r"Microsoft Visual Studio\Installer\vswhere.exe")
    if os.path.exists(vswhere):
        r = subprocess.run([vswhere, "-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                            "-property", "installationPath"], capture_output=True, text=True)
        for line in r.stdout.splitlines():
            p = os.path.join(line.strip(), r"VC\Auxiliary\Build\vcvars64.bat")
            if os.path.exists(p):
                return p
    for edition in ("Community", "Professional", "Enterprise", "BuildTools"):
        p = os.path.join(os.environ.get("ProgramFiles", r"C:\Program Files"), r"Microsoft Visual Studio\2022", edition, r"VC\Auxiliary\Build\vcvars64.bat")
        if os.path.exists(p):
            return p
    raise SystemExit("no Visual Studio with the C++ tools found (vcvars64.bat): install Visual Studio 2022 or its Build Tools, "
                     "with 'Desktop development with C++'")


VCVARS = None   # found when first needed (find_vcvars)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for b in iter(lambda: f.read(1 << 20), b""):
            h.update(b)
    return h.hexdigest()


def run(cmd, **kw):
    r = subprocess.run(cmd, capture_output=True, text=True, errors="replace", **kw)
    if r.returncode != 0:
        print((r.stdout + r.stderr).strip()[-4000:])
        raise SystemExit("failed: " + (cmd if isinstance(cmd, str) else " ".join(cmd)))
    return r


def fetch_fna(work):
    src = os.path.join(work, "FNA")
    if not os.path.isdir(os.path.join(src, ".git")):
        run(["git", "clone", "--depth", "1", "--branch", FNA_TAG, "--recurse-submodules", "--shallow-submodules", FNA_GIT, src])
    tag = run(["git", "-C", src, "describe", "--tags", "--exact-match"]).stdout.strip()
    if tag != FNA_TAG:
        raise SystemExit("%s is at %s, not %s" % (src, tag or "no tag", FNA_TAG))
    return src


def fetch_sdl(work):
    dl = os.path.join(work, "sdl3-download")
    os.makedirs(dl, exist_ok=True)
    for name, want in SDL_ZIPS.items():
        p = os.path.join(dl, name)
        if not os.path.exists(p) or sha256(p) != want:
            print("downloading " + SDL_URL + name)
            try:
                urllib.request.urlretrieve(SDL_URL + name, p + ".part")
            except OSError as e:
                # a firewall may let Windows' own curl.exe out where it stops python.exe (WinError 10013)
                print("  python's download failed (%s); with curl.exe" % e)
                run(["curl.exe", "-sSfL", "-o", p + ".part", SDL_URL + name])
            os.replace(p + ".part", p)
        got = sha256(p)
        if got != want:
            raise SystemExit("%s: SHA-256 %s, expected %s" % (name, got, want))
    bin_dir, devel = os.path.join(work, "sdl3-bin"), os.path.join(work, "sdl3-devel")
    for name, d in zip(SDL_ZIPS, (bin_dir, devel)):
        if not os.path.isdir(d):
            with zipfile.ZipFile(os.path.join(dl, name)) as z:
                z.extractall(d)
    return os.path.join(bin_dir, "SDL3.dll"), os.path.join(devel, "SDL3-%s" % SDL_VERSION, "cmake")


def build_natives(work, src, sdl_cmake):
    common = ('-G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl '
              '-DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded '
              '-DCMAKE_POLICY_VERSION_MINIMUM=3.5 -DSDL3_DIR="%s"' % sdl_cmake.replace("\\", "/"))
    out = {}
    for lib in ("FNA3D", "FAudio"):
        b = os.path.join(work, "build-" + lib.lower())
        script = os.path.join(work, "build-%s.cmd" % lib.lower())
        with open(script, "w", encoding="ascii") as f:
            f.write('@echo off\r\ncall "%s" >nul || exit /b 1\r\n' % (VCVARS or find_vcvars()))
            f.write('cmake -S "%s" -B "%s" %s || exit /b 1\r\n' % (os.path.join(src, "lib", lib), b, common))
            f.write('cmake --build "%s" || exit /b 1\r\n' % b)
        run(["cmd", "/c", script])
        out[lib] = os.path.join(b, lib + ".dll")
        print("built %s (%d bytes)" % (out[lib], os.path.getsize(out[lib])))
    return out


def build_fna(work, src):
    mk = open(os.path.join(src, "Makefile"), encoding="utf-8").read()
    srcs = re.findall(r"^\s*((?:src|lib)/\S+\.cs)", mk, re.M)
    resdir = re.search(r"^RESDIR = (\S+)", mk, re.M).group(1)
    resname = re.search(r"^RESNAME = (\S+)", mk, re.M).group(1)
    res = [r.replace("$(RESDIR)", resdir).replace("$(RESNAME)", resname) for r in re.findall(r"-resource:(\S+)", mk)]
    dll = os.path.join(work, "FNA.dll")
    rsp = os.path.join(work, "fna.rsp")
    with open(rsp, "w", encoding="utf-8") as f:
        f.write("-nologo -unsafe -optimize+ -target:library -out:\"%s\"\n" % dll)
        for r in res:
            p, n = r.split(",")
            f.write("\"-resource:%s,%s\"\n" % (os.path.join(src, p.replace("/", os.sep)), n))
        for s in srcs:
            f.write("\"%s\"\n" % os.path.join(src, s.replace("/", os.sep)))
    run([CSC, "@" + rsp])
    print("built %s from %d sources and %d resources (%d bytes)" % (dll, len(srcs), len(res), os.path.getsize(dll)))
    return dll


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[1])
    ap.add_argument("--work", required=True, help="a folder outside the source tree; the result is <work>/out")
    a = ap.parse_args()
    work = os.path.abspath(a.work)
    os.makedirs(work, exist_ok=True)
    src = fetch_fna(work)
    sdl_dll, sdl_cmake = fetch_sdl(work)
    natives = build_natives(work, src, sdl_cmake)
    fna = build_fna(work, src)
    out = os.path.join(work, "out")
    if os.path.isdir(out):
        shutil.rmtree(out)
    lic = os.path.join(out, "licenses")
    os.makedirs(lic)
    shutil.copy(fna, out)
    shutil.copy(os.path.join(src, "app.config"), os.path.join(out, "FNA.dll.config"))
    for p in (natives["FNA3D"], natives["FAudio"], sdl_dll):
        shutil.copy(p, out)
    for f in os.listdir(os.path.join(src, "licenses")):
        shutil.copy(os.path.join(src, "licenses", f), os.path.join(lic, "FNA." + f if f == "LICENSE" else f))
    shutil.copy(os.path.join(src, "lib", "FNA3D", "LICENSE"), os.path.join(lic, "FNA3D.LICENSE"))
    shutil.copy(os.path.join(src, "lib", "FNA3D", "MojoShader", "LICENSE.txt"), os.path.join(lic, "MojoShader.LICENSE"))
    shutil.copy(os.path.join(src, "lib", "FAudio", "LICENSE"), os.path.join(lic, "FAudio.LICENSE"))
    shutil.copy(os.path.join(os.path.dirname(sdl_dll), "LICENSE.txt"), os.path.join(lic, "SDL3.LICENSE"))
    commit = lambda d: run(["git", "-C", d, "rev-parse", "--short", "HEAD"]).stdout.strip()
    with open(os.path.join(out, "SOURCE.txt"), "w", encoding="ascii", newline="\r\n") as f:
        f.write("Built by Avatar Studio's fna_build.py from upstream sources, unchanged:\n")
        f.write("  FNA %s (%s, commit %s): FNA.dll, FNA.dll.config\n" % (FNA_TAG, FNA_GIT, commit(src)))
        f.write("  FNA3D (FNA %s's submodule, commit %s, MojoShader %s): FNA3D.dll\n"
                % (FNA_TAG, commit(os.path.join(src, "lib", "FNA3D")), commit(os.path.join(src, "lib", "FNA3D", "MojoShader"))))
        f.write("  FAudio (FNA %s's submodule, commit %s): FAudio.dll\n" % (FNA_TAG, commit(os.path.join(src, "lib", "FAudio"))))
        f.write("  SDL3 %s: SDL3.dll, libsdl-org's own Windows release (SDL3-%s-win32-x64.zip)\n" % (SDL_VERSION, SDL_VERSION))
        f.write("Licences: licenses/.\n")
    print("-> " + out)
    for f in sorted(os.listdir(out)):
        p = os.path.join(out, f)
        if os.path.isfile(p):
            print("   %-18s %9d  sha256 %s" % (f, os.path.getsize(p), sha256(p)[:16]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
