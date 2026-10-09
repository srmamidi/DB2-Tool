@echo off
rem IBM i Turnover - Production smoke test / non-IIS host: runs the PUBLISHED build. Under IIS use web.config / app pool variables instead.
rem All settings are in prod.env next to this file.
call "%~dp0_run.bat" prod.env
exit /b %ERRORLEVEL%

