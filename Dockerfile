# syntax=docker/dockerfile:1

# ---- Build stage ----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the csproj files first so the (slow) restore layer is cached
# and only re-runs when a project file actually changes.
COPY ["src/Shonkor.Core/Shonkor.Core.csproj", "src/Shonkor.Core/"]
COPY ["src/Shonkor.Infrastructure/Shonkor.Infrastructure.csproj", "src/Shonkor.Infrastructure/"]
COPY ["src/Shonkor.CLI/Shonkor.CLI.csproj", "src/Shonkor.CLI/"]
COPY ["src/Shonkor.Web/Shonkor.Web.csproj", "src/Shonkor.Web/"]
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
