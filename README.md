# RISE application fork — Jari Roseleth

This repository is the application repository used by the individual DevOps
EP3 assignment. It is a fork of the HOGENT RISE .NET template and is deployed
by the Jenkins pipelines from the accompanying
[operations repository](https://github.com/HOGENT-RISE/devops-project-operations-25-26-ep3-JariRoseleth).

## What changed compared with upstream?

- SQLite was replaced with PostgreSQL through the EF Core Npgsql provider.
- Explicit production migrations run with `dotnet Rise.Server.dll --migrate`.
- Liveness and database-readiness endpoints were added.
- Reverse-proxy headers and shared data-protection keys support two application
  instances behind Nginx.
- Production logging writes to `/var/log/rise`.
- The Jenkins pipeline performs restore, formatting verification, dependency
  auditing, build, tests, publish, deployment and a smoke test.

The exact upstream provenance is recorded in [UPSTREAM.md](UPSTREAM.md).

## Prerequisites for local development

- .NET SDK 9.0
- Docker with Docker Compose, or a locally installed PostgreSQL 16 server

## Run locally

```bash
docker compose -f compose.development.yml up -d
dotnet restore Rise.sln
dotnet run --project src/Rise.Server -- --migrate
dotnet run --project src/Rise.Server
```

The development URLs are printed by ASP.NET. The database container is only a
developer convenience; production uses PostgreSQL as a native service on a
separate VM.

## Tests and quality checks

```bash
dotnet restore Rise.sln
dotnet format analyzers Rise.sln --verify-no-changes --no-restore --severity error
dotnet build Rise.sln --configuration Release --no-restore
dotnet test Rise.sln --configuration Release --no-build
```

## Health endpoints

| Endpoint | Meaning |
| --- | --- |
| `/health/live` | The ASP.NET process is running. |
| `/health/ready` | The process can also reach PostgreSQL. |

## Demo users

Demo users are created only when `SeedDemoData=true`. This is enabled for the
local Vagrant environment and disabled by default for cloud production.

| User | Password | Role |
| --- | --- | --- |
| `admin@example.com` | `A1b2C3!` | Administrator |
| `secretary@example.com` | `A1b2C3!` | Secretary |
| `technician1@example.com` | `A1b2C3!` | Technician |

Never enable the fixed demo accounts on an internet-facing deployment.

## Pipeline ownership

The `Jenkinsfile` is Pipeline as Code and intentionally lives in this
application fork. Jenkins creates two jobs from it:

- `rise-local` deploys to the local Vagrant application VM;
- `rise-cloud` deploys to the configured cloud application VM.

The job name determines the target. Both jobs poll `main` every two minutes and
can also be started manually.
