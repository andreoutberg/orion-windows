@echo off
setlocal
set "ORION_DESIGN_PREVIEW=1"
set "ORION_DESIGN_FIRST_RUN="
set "LOCALAPPDATA=%~dp0profile-data"
if not exist "%LOCALAPPDATA%\com.kagi.orion\data\orion.db" set "ORION_DESIGN_FIRST_RUN=1"
start "" /D "%~dp0." "%~dp0Orion.exe"
