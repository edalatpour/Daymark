FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore dependencies first for better layer caching
COPY Ben.PaperSync.Contracts/Ben.PaperSync.Contracts.csproj Ben.PaperSync.Contracts/
COPY datasync-server/src/Ben.Datasync.Server/Ben.Datasync.Server.csproj datasync-server/src/Ben.Datasync.Server/
RUN dotnet restore datasync-server/src/Ben.Datasync.Server/Ben.Datasync.Server.csproj

# Copy source and publish
COPY Ben.PaperSync.Contracts/ Ben.PaperSync.Contracts/
COPY docs/schemas/ docs/schemas/
COPY datasync-server/src/Ben.Datasync.Server/ datasync-server/src/Ben.Datasync.Server/
RUN dotnet publish datasync-server/src/Ben.Datasync.Server/Ben.Datasync.Server.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# .NET 8+ runtime images default to port 8080 (non-root user)
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Ben.Datasync.Server.dll"]
