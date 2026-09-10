# Production PostgreSQL Docker Design

## Goal

Run PostgreSQL continuously in Docker Desktop on a Windows 10/11 workstation. TelegramBot.Server and TelegramBot.Worker remain Windows processes on the same computer and connect through `localhost`.

## Scope

The change hardens the existing single-service Docker Compose setup. It does not containerize Server or Worker and does not add backups, pgAdmin, monitoring, or other services.

## Architecture

Docker Compose manages one PostgreSQL container. PostgreSQL publishes port 5432 only on `127.0.0.1`, so local Windows processes can reach it while the database is not exposed to the LAN. Database files remain in the named `pgdata` volume across container replacement and restarts.

The container uses `restart: unless-stopped` and retains a PostgreSQL readiness health check. Docker Desktop must be configured to start with Windows for automatic recovery after a workstation reboot.

## Configuration and secrets

`docker-compose.yml` reads the database name, user, and password from Compose environment variables. Database name and user have development-compatible defaults. The password has no committed default and Compose fails configuration validation when it is missing.

A committed `.env.example` documents the required variables without containing a real secret. The local `.env` remains ignored by Git. Because the repository currently ignores `.env.*`, `.gitignore` will explicitly allow `.env.example`.

Server and Worker continue to use their existing local `appsettings.Local.json` files. Their PostgreSQL connection strings must use `Host=localhost` and credentials matching `.env`.

## Operations and failure handling

The existing named volume is preserved to avoid accidental data loss. Normal startup is `docker compose up -d`; status and health are inspected with `docker compose ps`. `restart: unless-stopped` recovers the database after crashes and Docker Desktop restarts, except when an operator intentionally stops the service.

The health check uses the configured database user and database, so it detects authentication or database-name configuration mistakes. Secrets are not printed in the README or committed files.

## Documentation

README receives production-oriented instructions for:

- creating `.env` from `.env.example` and setting a strong password;
- starting and inspecting PostgreSQL;
- matching the Server and Worker connection strings;
- configuring Docker Desktop to start with Windows;
- locating the persistent named volume and noting that backups are outside this change.

## Verification

Before completion:

1. Validate the resolved Compose model with `docker compose config` using non-secret temporary environment values.
2. Confirm that the published port resolves to `127.0.0.1:5432`, the volume remains named and persistent, the health check uses configured values, and no password default is committed.
3. Run the repository-required full build: `dotnet build TelegramBot.slnx`.
4. Inspect the final Git diff and ensure the unrelated existing change in `scripts/CodeSigning.ps1` is untouched.
