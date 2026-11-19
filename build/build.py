#!/usr/bin/env python3
"""
build.py: Avatar Studio from source to the zip players download, on Windows.

    python build/build.py              # -> dist/Avatar Studio/ and dist/AvatarStudio.zip

Needs (all free): Python 3.9 or newer, git, and Visual Studio 2022 or its Build Tools with the
"Desktop development with C++" workload (MSVC, CMake and Ninja). The .NET Framework 4.x compiler and
d3dcompiler_47.dll are part of every Windows 10 / 11. Everything else is fetched into build/work:

  - FNA 26.10 and its native libraries, built from their own sources (build/fna_build.py: FNA,
    FNA3D, FAudio; SDL3 3.4.0 from libsdl-org's release);
  - ToneLUT.fxb (shaders/ToneLUT.fx: the console's display gamma, our own shader) through Windows'
    d3dcompiler_47.dll, fx_2_0 without preshaders (fxc's /Op; the same bytes as fxc).

The setup converts the console's lighting shaders on the player's PC with XenosRecomp (hedge-dev, MIT),
ported to C# in src/XenosRecompiler*.cs; its shader_common.h is embedded in the exe.

The zip holds this project's code, those open-source parts, their licences and a README. No file of
Microsoft's is in this repository or in the zip: the studio downloads the Xbox 360 files it needs onto
the player's own PC on its first start.
"""
import argparse
import ctypes
import glob
import os
import shutil
import subprocess
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
CSC = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), r"Microsoft.NET\Framework64\v4.0.30319\csc.exe")
FRAMEWORK_REFERENCES = ["System.Drawing.dll", "System.Windows.Forms.dll", "System.IO.Compression.dll", "System.Numerics.dll"]
FNA_FILES = ["FNA.dll", "FNA.dll.config", "FNA3D.dll", "FAudio.dll", "SDL3.dll"]
ICON = "AvatarStudio.ico"                              # src/AvatarStudio.ico, the exe's icon (assets/icon.svg)
RESOURCES = ["shader_common.h"]                        # src/shader_common.h, XenosRecomp's (MIT), embedded


def run(cmd, **kw):
    r = subprocess.run(cmd, capture_output=True, text=True, errors="replace", **kw)
    if r.returncode != 0:
        print((r.stdout + r.stderr).strip()[-4000:])
        raise SystemExit("failed: " + " ".join(cmd))
    return r


def step(text):
    print("== " + text, flush=True)


# ---- FNA ----------------------------------------------------------------------------------------

def build_fna(work):
    step("FNA 26.10, FNA3D, FAudio and SDL3 from their sources")
    w = os.path.join(work, "fna")
    run([sys.executable, os.path.join(HERE, "fna_build.py"), "--work", w])
    return os.path.join(w, "out")


# ---- ToneLUT.fxb ----------------------------------------------------------------------------------

def d3dcompile(source, name, target, flags1):
    """D3DCompile in Windows' own d3dcompiler_47.dll (the compiler fxc drives); the code blob's bytes."""
    from ctypes import c_void_p, c_char_p, c_size_t, c_uint, POINTER, byref
    f = ctypes.WinDLL("d3dcompiler_47.dll").D3DCompile
    f.restype = ctypes.c_long
    f.argtypes = [c_char_p, c_size_t, c_char_p, c_void_p, c_void_p, c_char_p, c_char_p, c_uint, c_uint, POINTER(c_void_p), POINTER(c_void_p)]
    code, errs = c_void_p(), c_void_p()
    hr = f(source, len(source), name, None, None, None, target, flags1, 0, byref(code), byref(errs))

    def blob(p):                                        # ID3DBlob: GetBufferPointer 3, GetBufferSize 4, Release 2
        if not p.value:
            return b""
        vt = ctypes.cast(ctypes.cast(p, POINTER(c_void_p))[0], POINTER(c_void_p))
        data = ctypes.string_at(ctypes.WINFUNCTYPE(c_void_p, c_void_p)(vt[3])(p), ctypes.WINFUNCTYPE(c_size_t, c_void_p)(vt[4])(p))
        ctypes.WINFUNCTYPE(c_uint, c_void_p)(vt[2])(p)
        return data
    out, err = blob(code), blob(errs)
    if hr < 0:
        raise SystemExit("D3DCompile failed: " + err.decode("ascii", "replace"))
    return out


def build_tonelut(work):
    step("ToneLUT.fxb")
    src = os.path.join(ROOT, "shaders", "ToneLUT.fx")
    D3DCOMPILE_NO_PRESHADER = 1 << 8
    data = d3dcompile(open(src, "rb").read(), b"ToneLUT.fx", b"fx_2_0", D3DCOMPILE_NO_PRESHADER)
    out = os.path.join(work, "ToneLUT.fxb")
    open(out, "wb").write(data)
    return out


# ---- the studio -----------------------------------------------------------------------------------

def build_studio(work, fna):
    step("AvatarStudio.exe")
    sources = sorted(glob.glob(os.path.join(ROOT, "src", "**", "*.cs"), recursive=True))
    names = [os.path.basename(s) for s in sources]
    dup = sorted({n for n in names if names.count(n) > 1})
    if dup:
        raise SystemExit("source names must be unique (csc is given them flat): " + ", ".join(dup))
    stage = os.path.join(work, "csc-stage")
    if os.path.isdir(stage):
        shutil.rmtree(stage)
    os.makedirs(stage)
    for s in sources + [os.path.join(ROOT, "src", f) for f in [ICON] + RESOURCES]:   # csc cannot take paths with spaces bare
        shutil.copy(s, stage)
    exe = os.path.join(work, "AvatarStudio.exe")
    cmd = [CSC, "-nologo", "-target:winexe", "-optimize+", "-platform:x64", "-out:" + exe, "-win32icon:" + ICON,
           "-reference:" + os.path.join(fna, "FNA.dll")]
    cmd += ["-resource:" + r for r in RESOURCES]
    cmd += ["-reference:" + r for r in FRAMEWORK_REFERENCES] + names
    r = subprocess.run(cmd, cwd=stage, capture_output=True, text=True, errors="replace")
    if r.returncode != 0:
        print((r.stdout + r.stderr).strip())
        raise SystemExit("the studio did not compile")
    return exe


# ---- the zip --------------------------------------------------------------------------------------

def package(exe, fna, tonelut, dist):
    step("dist/Avatar Studio and dist/AvatarStudio.zip")
    pkg = os.path.join(dist, "Avatar Studio")
    if os.path.isdir(pkg):
        shutil.rmtree(pkg)
    os.makedirs(os.path.join(pkg, "licenses"))
    shutil.copy(exe, pkg)
    shutil.copy(os.path.join(ROOT, "packaging", "AvatarStudio.exe.config"), pkg)
    shutil.copy(os.path.join(ROOT, "packaging", "README.txt"), pkg)
    shutil.copy(tonelut, pkg)
    for f in FNA_FILES:
        shutil.copy(os.path.join(fna, f), pkg)
    for f in os.listdir(os.path.join(fna, "licenses")):
        shutil.copy(os.path.join(fna, "licenses", f), os.path.join(pkg, "licenses", f))
    shutil.copy(os.path.join(fna, "SOURCE.txt"), os.path.join(pkg, "licenses", "FNA-SOURCE.txt"))
    shutil.copy(os.path.join(ROOT, "LICENSE"), os.path.join(pkg, "licenses", "AvatarStudio.LICENSE"))
    shutil.copy(os.path.join(ROOT, "src", "XenosRecomp.LICENSE"), os.path.join(pkg, "licenses", "XenosRecomp.LICENSE"))
    z = os.path.join(dist, "AvatarStudio.zip")
    with zipfile.ZipFile(z, "w", zipfile.ZIP_DEFLATED) as zf:
        for root, _, files in os.walk(pkg):
            for f in sorted(files):
                p = os.path.join(root, f)
                zf.write(p, "Avatar Studio/" + os.path.relpath(p, pkg).replace(os.sep, "/"))
    n = sum(len(fs) for _, _, fs in os.walk(pkg))
    print("   %s (%d files), %s (%.1f MB)" % (pkg, n, z, os.path.getsize(z) / 1e6))
    return pkg


def main():
    ap = argparse.ArgumentParser(description="Avatar Studio from source to dist/AvatarStudio.zip")
    ap.add_argument("--work", default=os.path.join(HERE, "work"))
    ap.add_argument("--dist", default=os.path.join(ROOT, "dist"))
    a = ap.parse_args()
    if os.name != "nt":
        raise SystemExit("Avatar Studio builds on Windows")
    if not os.path.exists(CSC):
        raise SystemExit("no .NET Framework 4 compiler at " + CSC)
    work = os.path.abspath(a.work)
    os.makedirs(work, exist_ok=True)
    fna = build_fna(work)
    tonelut = build_tonelut(work)
    exe = build_studio(work, fna)
    package(exe, fna, tonelut, os.path.abspath(a.dist))
    return 0


if __name__ == "__main__":
    sys.exit(main())
