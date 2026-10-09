@echo off
rem IBM i Turnover - Dev server: real IBM i (Live), Windows login, secrets from Key Vault
rem All settings are in dev.env next to this file.
call "%~dp0_run.bat" dev.env
exit /b %ERRORLEVEL%

