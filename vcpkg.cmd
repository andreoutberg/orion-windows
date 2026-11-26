@echo off
setlocal
rem Wrapper that runs vcpkg.exe with reproducible VS-bundled tools in PATH.

call "%~dp0scripts\ensure_vcpkg.bat" || exit /b 1

call "%~dp0vsenv.bat" || exit /b 1

if /i "%OrionBuildWebKit%"=="false" (
    if not defined VCPKG_BINARY_SOURCES set "VCPKG_BINARY_SOURCES=default,readwrite"
    if /i "%~1"=="install" set "ORION_VCPKG_FEATURE_FLAGS=--x-no-default-features"
)
if not defined VCPKG_BINARY_SOURCES set "VCPKG_BINARY_SOURCES=clear;x-gcs,gs://kagi-orion-windows-artifacts/,readwrite"

set VCPKG_FORCE_DOWNLOADED_BINARIES=1

"%~dp0external\vcpkg\vcpkg.exe" %* %ORION_VCPKG_FEATURE_FLAGS% --z-tool-data-file="%~dp0vcpkg-tools.json"
exit /b %errorlevel%
