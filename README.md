# DB2 Tool: IBM i Turnover

Compares IBM i (Db2 for i) environments (Dev, QA, Stage, Prod, each with several libraries) and promotes changes
between them through a governed workflow: compare, package, approve, deploy, roll back. Everything except data:
tables, columns and types, keys, foreign keys, indexes, sequences, procedures, functions, views, triggers,
RPG / CL / DDS / commands, data areas and queues, job descriptions, messages, user profiles, authorities,
menus, binding directories, authorization lists, job schedules, system values and IFS files.

- `src/IbmTurnover.Core`: the engine (catalog readers, comparer, planner, workflow). Add to any ASP.NET Core app
  with `services.AddIbmTurnover(configuration, contentRoot)`.
- `src/IbmTurnover.Web`: ASP.NET Core 8 MVC front end, hosted in IIS. All settings in `web.config`;
  secrets in Azure Key Vault.
- `tests/IbmTurnover.Tests`: xUnit tests, including the live IBM i reader and executor against a fake ODBC driver.

## Run locally (sample data, demo users)

```bash
dotnet run --project src/IbmTurnover.Web --urls http://localhost:5199
```

Set `ASPNETCORE_ENVIRONMENT=Development` and `Turnover__Auth__Mode=Demo` first. With only a newer .NET runtime
installed, also set `DOTNET_ROLL_FORWARD=Major`.

## Test

```bash
dotnet test tests/IbmTurnover.Tests
```

## Documents

- `docs/production-handoff.html`: going to production and integrating into an existing MVC application
- `docs/ibm-turnover-features.html`: features, rules, settings
- `docs/turnover-gap-analysis.html`: tool versus a classic turnover process
