@echo off
rem IBM i Turnover - Local: sample data, demo users (alice / bob / carol / dave), http://localhost:5199
rem All settings are in local.env next to this file.
call "%~dp0_run.bat" local.env
exit /b %ERRORLEVEL%

