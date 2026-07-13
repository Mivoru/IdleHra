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
FROM mcr.microsoft.com/dotnet/runtime:8.0-chiseled AS final
WORKDIR /app
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "FolkIdle.Server.dll"]
