1>2# : vim: set ft=python: ^
'''
@echo off & cd %~dp0.. & bin\\uv.exe run python "%~dpf0" %* & exit /b ::'''
"""Verifies the published one-exe layout: each engine's payload is complete
in its engines\\ subdirectory and no engine binary leaks into the app root.
Both engines ship libEGL.dll/libGLESv2.dll, which collide in a flat layout —
the old two-flavor merge silently shipped OrionB running against WebKit's
ANGLE."""
import filecmp
import sys
from pathlib import Path


def main() -> int:
    if len(sys.argv) != 2:
        print(f"usage: {sys.argv[0]} <publish-dir>")
        return 2
    root = Path(sys.argv[1])
    errors = []

    for rel in [
        "Orion.exe",
        "OrionBackend.dll",
        "nethost.dll",
        "engines/webkit/OrionEngineWK.dll",
        "engines/webkit/WebKit2.dll",
        "engines/webkit/WebKitWebProcess.exe",
        "engines/webkit/MiniBrowserInjectedBundle.dll",
        "engines/webkit/libEGL.dll",
        "engines/webkit/WebKit.resources",
        "engines/blink/OrionEngineB.dll",
        "engines/blink/OrionEngineB.exe",
        "engines/blink/OrionEngineB.pri",
        "engines/blink/libcef.dll",
        "engines/blink/chrome_elf.dll",
        "engines/blink/icudtl.dat",
        "engines/blink/libEGL.dll",
        "engines/blink/locales",
    ]:
        if not (root / rel).exists():
            errors.append(f"missing: {rel}")

    for name in [
        "WebKit2.dll", "libcef.dll", "libEGL.dll", "libGLESv2.dll",
        "OrionBackendWK.dll", "OrionBackendB.dll", "OrionWK.exe", "OrionB.exe",
    ]:
        if (root / name).exists():
            errors.append(f"engine binary at root: {name}")

    # CEF must run against its own ANGLE build, not WebKit's.
    cef_egl = Path("vendor/cef/Release/libEGL.dll")
    pub_egl = root / "engines/blink/libEGL.dll"
    if cef_egl.exists() and pub_egl.exists() and not filecmp.cmp(cef_egl, pub_egl, shallow=False):
        errors.append("engines/blink/libEGL.dll does not match vendor/cef/Release/libEGL.dll")

    for e in errors:
        print(f"ERROR: {e}")
    if not errors:
        print(f"publish layout OK: {root}")
    return 1 if errors else 0


sys.exit(main())
