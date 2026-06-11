FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files for restore
COPY ["src/Shonkor.Core/Shonkor.Core.csproj", "src/Shonkor.Core/"]
COPY ["src/Shonkor.Infrastructure/Shonkor.Infrastructure.csproj", "src/Shonkor.Infrastructure/"]
COPY ["src/Shonkor.CLI/Shonkor.CLI.csproj", "src/Shonkor.CLI/"]
COPY ["src/Shonkor.Web/Shonkor.Web.csproj", "src/Shonkor.Web/"]

RUN dotnet restore "src/Shonkor.Web/Shonkor.Web.csproj"

# Copy all source files
COPY . .

# Publish the Web project
WORKDIR "/src/src/Shonkor.Web"
RUN dotnet publish "Shonkor.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Publish the CLI project
WORKDIR "/src/src/Shonkor.CLI"
RUN dotnet publish "Shonkor.CLI.csproj" -c Release -o /app/cli-publish /p:UseAppHost=false

# Final stage runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /app/cli-publish /app/cli

# Create a global 'shonkor' command so you can call it from anywhere inside the container
RUN echo '#!/bin/bash\ndotnet /app/cli/Shonkor.CLI.dll "$@"' > /usr/local/bin/shonkor && \
    chmod +x /usr/local/bin/shonkor

# Expose standard port 8080 used by ASP.NET Core in Docker
EXPOSE 8080

ENTRYPOINT ["dotnet", "Shonkor.Web.dll"]
