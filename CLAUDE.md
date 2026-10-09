# CLAUDE.md: DB2 Tool (IBM i Turnover)

ASP.NET Core 8 MVC tool that compares IBM i (Db2 for i) environments (Dev, QA, Stage, Prod, each with several
libraries) and promotes everything except data through a governed workflow: compare, package, approve (frozen,
hashed script), deploy, roll back. C# only; there is no Python in this repository.

## Layout

| Path | What |
|---|---|
| `src/IbmTurnover.Core` | Engine: catalog readers (`Catalog/`), comparer (`Compare/`), planner (`Planning/`), executors (`Execution/`), workflow and stores (`Workflow/`), options (`TurnoverOptions.cs`). No web dependencies. |
| `src/IbmTurnover.Web` | MVC front end: `Program.cs`, `Controllers/Controllers.cs`, `Views/`, `Infrastructure/` (auth, web.config + Key Vault config, file logger, background services), `web.config`. |
| `tests/IbmTurnover.Tests` | xUnit. `LiveReaderTests.cs` holds the fake ODBC driver used to test the live IBM i reader and executor. |
| `launcher/` | `run-local.bat`, `run-dev.bat`, `run-prod.bat` + `local.env`, `dev.env`, `prod.env` (all settings), `_run.bat` (loads the env file and starts the app). |
| `docs/` | `production-handoff.html` (go-live and MVC integration runbook), `ibm-turnover-features.html`, `turnover-gap-analysis.html`. Living documents: update in place and add a Version History row. |

## Commands

```bat
launcher\run-local.bat                         :: sample data, demo users, http://localhost:5199
dotnet test tests\IbmTurnover.Tests            :: all tests (set DOTNET_ROLL_FORWARD=Major if only a newer runtime is installed)
dotnet publish src\IbmTurnover.Web -c Release -o <dir>
```

## Rules for changes

- **No hard-coded settings.** Every tunable (URLs, modes, timeouts, intervals, paths, log levels, Key Vault, role
  groups) comes from configuration: `launcher/*.env` (environment variables, `__` = section separator) over
  `web.config` over `appsettings.json`, with Azure Key Vault last. A new setting gets a default in its options class,
  a line in all three env files (the `Every_launcher_env_file_has_the_same_keys_and_no_secrets` test checks this) and
  a row in `docs/ibm-turnover-features.html`.
- **No secrets in the repo.** Connection strings with UID/PWD and client secrets live only in Key Vault
  (`ConnectionStrings--Db2Prod`, `AzureAd--ClientSecret`; set `KeyVault:SecretPrefix`).
- **Fail closed.** A missing secret, bad environment map, missing role group, unreachable Key Vault or failed catalog
  read stops the action or the app; never fall back to "allow" or to a partial snapshot.
- **Exceptions and logging.**
  - Logs go to `Logging:File:Path` (default `logs/` under the web project, one file per day, `Logging:File:*`).
    A startup failure is written to `<prefix>-startup-<date>.log` in the same folder before the process exits.
  - Expected failures are `TurnoverException` (business refusal, incl. `PackageConflictException`,
    `DeployBusyException`), `CatalogLoadException`, `DbException` (IBM i driver) and `IOException` (data folder).
    Controllers handle them with `Expected(ex)` + `Report(ex)` (log and show) or `Log(ex)` (log, then show a neutral
    page); refusals log at Information, the rest at Warning. Driver and file-system text is never shown to users.
    Anything else reaches the exception handler, which logs it; the error page shows the request id that every log
    line of that request carries (`trace=`).
  - Never swallow an exception silently: log it with context, or record it on the package audit trail.
  - Deploy and rollback steps must not observe the HTTP request token (`CancellationToken.None` once steps start),
    must record every step result, and must never leave a package in `Deploying`/`RollingBack`. Outcomes are saved
    through `SaveOutcome` (retries a concurrent save; if the store cannot be written, the runs go to the log at
    Critical and the user is told to use Mark failed).
  - Background services catch per item and log; one bad item never stops the loop.
  - Stores (`turnover-packages.json`, `checkouts.json`, source and snapshot indexes) are opened at startup; a damaged
    file stops the app with its name. Writes go to a temp file and replace the original; memory changes only after
    the write succeeded.
  - File logging never stops the app: an unwritable folder switches it off with a warning on the console / event log.
    Messages are written on one line (CR/LF escaped) so user input cannot forge log entries.
- **IBM i SQL / CL**: identifiers from the catalog are validated (`SqlText.ClName`, `IsValidLibraryName`,
  `IsSafeIfsPath`) before they go into SQL or CL; everything else is a parameter. Verify QSYS2 column names against
  IBM's documentation before using them.
- **Never run against a real IBM i or call a live model from a developer machine.** Live behaviour is tested through
  the fake ODBC driver; real partitions are exercised only in the rollout phases in the handoff.
- **Every fix gets a test that fails without the fix.**
- Single instance only: JSON stores under `Turnover:DataDirectory` and an in-memory deploy gate.

## Git

- `main` is the default branch: work on a feature branch and open a pull request.
- Remote: https://github.com/srmamidi/DB2-Tool (push as `srmamidi`).
