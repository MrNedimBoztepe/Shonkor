# syntax=docker/dockerfile:1

# ---- Build stage ----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Node for the TypeScript plugin's Node sidecar (#292/#388). The plugin's build runs `npm ci`
# (NpmInstallSidecar) and VerifySidecarReleaseDeps HARD-FAILS a Release build when the pinned `typescript`
# is not materialised — deliberately, so a silently degraded plugin package can never ship. The .NET SDK
# image carries no Node, and .dockerignore excludes **/node_modules/, so npm has to run inside this stage.
# Copied from the official Node image instead of `apt-get install nodejs npm`, because Ubuntu 24.04 ships
# Node 18 while sidecar/package.json declares "engines": { "node": ">=20" }.
# BUILD STAGE ONLY: the runtime image intentionally ships no Node runtime (docs/user/setup_guide.md).
COPY --from=node:22-bookworm-slim /usr/local/bin/node /usr/local/bin/node
COPY --from=node:22-bookworm-slim /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm

# Restore against the project files first so the (slow) restore layer is cached and only re-runs when a
# project file actually changes. ALL of them, via a glob — an enumerated list rots silently the moment a
# ProjectReference is added, which is exactly what happened with Shonkor.Plugin.TypeScript (#313/#388):
# a missing reference does NOT fail the restore (NuGet skips it: "because it was not found"), it fails the
# later --no-restore publish with NETSDK1004.
# Directory.Build.props belongs in this layer too: it carries the SQLitePCLRaw.lib.e_sqlite3 3.50.3 pin
# against CVE-2025-6965. Without it the restore resolved the vulnerable 2.1.11 into the image while local
# and CI builds got 3.50.3 (the NU1903 warning does not fail the build here — TreatWarningsAsErrors from
# that same missing file is what would have escalated it).
# `COPY --parents` preserves the directory structure of the globbed paths; it needs Dockerfile frontend
# >= 1.20, which the `# syntax=docker/dockerfile:1` tag above resolves to (latest 1.x).
COPY --parents Directory.Build.props src/*/*.csproj ./
RUN dotnet restore "src/Shonkor.Web/Shonkor.Web.csproj" \
 && dotnet restore "src/Shonkor.CLI/Shonkor.CLI.csproj"

# Copy the rest of the source and publish both the web app and the CLI.
COPY . .
RUN dotnet publish "src/Shonkor.Web/Shonkor.Web.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false \
 && dotnet publish "src/Shonkor.CLI/Shonkor.CLI.csproj" -c Release -o /app/cli-publish --no-restore /p:UseAppHost=false

# ---- Runtime stage --------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# curl is used by the container HEALTHCHECK below.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
COPY --from=build /app/cli-publish /app/cli

# Global 'shonkor' command so the CLI can be invoked from anywhere in the container.
# printf (not echo -e) so the newline is written literally and the shebang is valid.
RUN printf '#!/bin/sh\nexec dotnet /app/cli/Shonkor.CLI.dll "$@"\n' > /usr/local/bin/shonkor \
 && chmod +x /usr/local/bin/shonkor

# Run as the image's built-in non-root user (UID 1654) for defense-in-depth.
# NOTE: bind-mounted project directories must be writable by this user. Docker
# Desktop (Windows/macOS) handles this transparently; on native Linux either
# chown the host dir to 1654 or set `user:` in compose to match the host UID.
USER app

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Readiness probe: confirms the workspace is writable and the graph store answers.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "Shonkor.Web.dll"]
