@echo off
REM build-app.bat -- one-click wrapper for the Windows client build.
REM
REM Runs scripts\build-app.ps1 with the right PowerShell flags so a developer
REM who does not live in PowerShell can double-click or run this from a shell
REM and get a self-contained Windows app output without remembering the
REM -NoProfile / -ExecutionPolicy pair.
REM
REM Why a .bat wraps the .ps1 instead of just calling it directly: the .ps1
REM file association is what Windows decides when the user double-clicks, and
REM a fresh Windows install with no developer changes will not run .ps1 on a
REM double-click. A .bat, on the other hand, always runs. The .ps1 stays the
REM source of truth so a CI pipeline can still call it directly; this wrapper
REM only adds the "human" affordance.

setlocal

REM The script lives next to the .ps1, so %~dp0 is the scripts directory.
REM Resolve the .ps1 path from the same place, and the repo root from there.
set "SCRIPT_DIR=%~dp0"
set "PS1=%SCRIPT_DIR%build-app.ps1"

if not exist "%PS1%" (
    echo Cannot find %PS1%
    pause
    exit /b 1
)

REM PowerShell from the standard install path. The wrapper only calls
REM -NoProfile and -ExecutionPolicy Bypass; the .ps1 owns every other flag.
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
set "EXITCODE=%ERRORLEVEL%"

REM A non-zero exit from the script is the script's own throw surfacing.
REM Surface it to the caller the same way PowerShell would, then pause so a
REM developer who launched this by double-click can read the message before
REM the window disappears.
if not "%EXITCODE%"=="0" (
    echo.
    echo build-app.ps1 failed with exit code %EXITCODE%
    pause
)

endlocal & exit /b %EXITCODE%
