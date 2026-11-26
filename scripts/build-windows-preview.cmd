@echo off
setlocal
cd /d "%~dp0.." || exit /b 1
set "OrionBuildWebKit=false"
set /p PINNED_MSVC=<MSVC_VERSION
set "SOLUTION_DIRECTORY=%CD%"
if not "%SOLUTION_DIRECTORY:~-1%"=="\" set "SOLUTION_DIRECTORY=%SOLUTION_DIRECTORY%\"

call "%ORION_PREVIEW_VC_INIT%" -vcvars_ver=%PINNED_MSVC% || exit /b 1
call vsenv.bat || exit /b 1
call cef.py.cmd setup || exit /b 1
call vcpkg.cmd install --triplet x64-windows --x-wait-for-lock || exit /b 1
call noenv_update_core_module.bat || exit /b 1
msbuild Orion\Orion.csproj -restore -t:Publish -m -v:minimal -p:Configuration=Release -p:Platform=x64 -p:RuntimeIdentifiers=win-x64 -p:SolutionDir=%SOLUTION_DIRECTORY% "-p:PublishDir=%ORION_PREVIEW_PUBLISH%/"
exit /b %ERRORLEVEL%
