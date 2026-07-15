FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Build context is server/ (see docker-compose.yml) so this can also see the
# sibling GameData/ directory - Content Pipeline JSON data copied via
# FolkIdle.Server.csproj's CopyToOutputDirectory item, not an explicit COPY
# here.
COPY ["FolkIdle.Server/FolkIdle.Server.csproj", "FolkIdle.Server/"]
RUN dotnet restore "FolkIdle.Server/FolkIdle.Server.csproj"

# Copy everything else and build
COPY . .
WORKDIR /src/FolkIdle.Server
RUN dotnet build "FolkIdle.Server.csproj" -c Release -o /app/build

# Publish
FROM build AS publish
RUN dotnet publish "FolkIdle.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Final stage/image
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Run as non-root user
RUN adduser -D -u 1000 appuser
USER appuser

ENTRYPOINT ["dotnet", "FolkIdle.Server.dll"]
