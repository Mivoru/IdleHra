FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore as distinct layers
COPY ["FolkIdle.Server/FolkIdle.Server.csproj", "FolkIdle.Server/"]
RUN dotnet restore "FolkIdle.Server/FolkIdle.Server.csproj"

# Copy everything else and build
COPY . .
WORKDIR "/src/FolkIdle.Server"
RUN dotnet publish "FolkIdle.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime Environment
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080

COPY --from=build /app/publish .

# Modul: migrate-then-serve, for single-instance PaaS hosts (Render) that have
# no pre-deploy hook and no shell to run one from. Program.cs's normal boot
# path deliberately never auto-migrates, because several server replicas
# starting at once would race to migrate the same database - but that race
# needs more than one replica to exist, and this image is deployed at
# numInstances: 1. Where more than one replica IS possible, keep using the
# separate one-shot --migrate container instead (ops/jobs/apply-migrations.yaml,
# docker-compose.yml's folk-idle-migrate) and revert this to a bare start.
#
# "&&" rather than ";" so an unmigratable database halts the container instead
# of starting a server that will throw 42P01 on its first query. "exec" so the
# server replaces the shell as PID 1 and keeps receiving SIGTERM - Program.cs
# traps it to flush checkpoints and the write-behind queue before exiting.
ENTRYPOINT ["/bin/sh", "-c", "dotnet FolkIdle.Server.dll --migrate && exec dotnet FolkIdle.Server.dll"]