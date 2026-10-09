@echo off
rem IBM i Turnover launcher core. Called by run-local.bat / run-dev.bat / run-prod.bat with the env file name.
rem Loads every KEY=VALUE line of the env file into this process, then starts the app. Nothing is hard-coded here:
rem the env file decides environment, URLs, mode, login, logging, Key Vault and how to start.
setlocal EnableExtensions DisableDelayedExpansion

set "LAUNCHER_DIR=%~dp0"
set "REPO_DIR=%~dp0.."
set "ENV_FILE=%LAUNCHER_DIR%%~1"

if "%~1"=="" (
  echo [launcher] Usage: _run.bat ^<env file^>   e.g. _run.bat local.env
  exit /b 2
)
if not exist "%ENV_FILE%" (
  echo [launcher] Env file not found: "%ENV_FILE%"
  exit /b 2
)

rem Lines starting with # are comments; blank lines are skipped by FOR /F. "tokens=1,*" keeps any '=' inside the value.
for /f "usebackq eol=# tokens=1,* delims==" %%A in ("%ENV_FILE%") do set "%%A=%%B"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [launcher] The dotnet command was not found. Install the .NET 8 SDK ^(local/dev^) or runtime ^(production^).
  exit /b 3
)

rem Log folder: create it up front so a startup failure always has somewhere to go. No ( ) blocks here: a path such as
rem C:\Program Files (x86)\Logs would close the block early. Relative paths are under the app's content root, which is
rem the project folder for LAUNCH_MODE=run and PUBLISH_DIR for LAUNCH_MODE=dll.
if /i "%LAUNCH_MODE%"=="dll" if not defined PUBLISH_DIR goto :nopublish
if not defined Logging__File__Path goto :logdone
if /i "%Logging__File__Path%"=="none" goto :logdone
set "LOG_DIR=%Logging__File__Path%"
if "%LOG_DIR:~1,1%"==":" goto :logmk
if "%LOG_DIR:~0,2%"=="\\" goto :logmk
if /i "%LAUNCH_MODE%"=="dll" goto :logdll
set "LOG_DIR=%REPO_DIR%\src\IbmTurnover.Web\%Logging__File__Path%"
goto :logmk
:logdll
set "LOG_DIR=%PUBLISH_DIR%\%Logging__File__Path%"
:logmk
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"
if errorlevel 1 echo [launcher] Could not create the log folder "%LOG_DIR%"; the app reports this at startup too.
:logdone

echo [launcher] %~1: ASPNETCORE_ENVIRONMENT=%ASPNETCORE_ENVIRONMENT%  Mode=%Turnover__Mode%  Auth=%Turnover__Auth__Mode%  URLs=%ASPNETCORE_URLS%

if /i "%LAUNCH_MODE%"=="dll" goto :dll
if /i "%LAUNCH_MODE%"=="run" goto :run
echo [launcher] LAUNCH_MODE must be "run" or "dll" (got "%LAUNCH_MODE%").
exit /b 2

:run
if not defined LAUNCH_CONFIGURATION set "LAUNCH_CONFIGURATION=Debug"
dotnet run --project "%REPO_DIR%\src\IbmTurnover.Web" -c %LAUNCH_CONFIGURATION% --no-launch-profile
exit /b %ERRORLEVEL%

:dll
if not defined PUBLISH_DIR goto :nopublish
if not exist "%PUBLISH_DIR%\IbmTurnover.Web.dll" (
  echo [launcher] "%PUBLISH_DIR%\IbmTurnover.Web.dll" not found. Publish first:
  echo            dotnet publish "%REPO_DIR%\src\IbmTurnover.Web" -c Release -o "%PUBLISH_DIR%"
  exit /b 2
)
pushd "%PUBLISH_DIR%"
dotnet IbmTurnover.Web.dll
set "RC=%ERRORLEVEL%"
popd
exit /b %RC%

:nopublish
echo [launcher] LAUNCH_MODE=dll needs PUBLISH_DIR ^(the folder dotnet publish wrote to^).
exit /b 2
