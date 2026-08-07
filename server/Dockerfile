# Modul: THE BUILD CONTEXT IS THE REPOSITORY ROOT, not server/.
#
# It was server/, and the csproj pulls two things from OUTSIDE that directory:
#
#   <None Include="../../client/Assets/Resources/Audio/*.wav">
#   <None Include="../../client/Assets/Images/SpritesWeb/**/*.webp">
#
# Outside the context those paths do not exist, so the globs matched nothing,
# `dotnet publish` emitted no Audio folder, and every request to /audio/*.wav
# answered 404. The game has never made a sound in production - ten clips, a
# Web Audio mixer, a volume slider in Settings and a Caddy route, all correct,
# all wired to an empty directory. Nothing failed loudly because an MSBuild
# glob that matches nothing is not an error.
#
# The sprites glob has the same problem and was survivable only because the WEB
# image builds from the repository root and ships its own copy.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore as distinct layers
COPY ["server/FolkIdle.Server/FolkIdle.Server.csproj", "server/FolkIdle.Server/"]
RUN dotnet restore "server/FolkIdle.Server/FolkIdle.Server.csproj"

# Copy everything else and build
COPY . .
WORKDIR "/src/server/FolkIdle.Server"
RUN dotnet publish "FolkIdle.Server.csproj" -c Release -o /app/publish /p:UseAppHost=false

# The clips have to be IN the image, and a glob that silently matches nothing
# is what hid this for months. Fail the build instead.
RUN ls /app/publish/Audio/*.wav > /dev/null 2>&1 || (echo 'BUILD FAILED: no audio clips in the publish output.' && exit 1)

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