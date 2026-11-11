# Build a partir da raiz do repositório:
#   docker build -f src/LegacyBridge.Worker/Dockerfile -t legacy-bridge .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore em camada separada para aproveitar cache.
COPY Directory.Build.props ./
COPY src/LegacyBridge.Core/LegacyBridge.Core.csproj src/LegacyBridge.Core/
COPY src/LegacyBridge.Infrastructure/LegacyBridge.Infrastructure.csproj src/LegacyBridge.Infrastructure/
COPY src/LegacyBridge.Worker/LegacyBridge.Worker.csproj src/LegacyBridge.Worker/
RUN dotnet restore src/LegacyBridge.Worker/LegacyBridge.Worker.csproj

COPY src/ src/
RUN dotnet publish src/LegacyBridge.Worker/LegacyBridge.Worker.csproj \
    -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app .

# Usuário sem privilégios que já vem nas imagens .NET 8.
RUN mkdir -p /app/data /app/logs && chown -R $APP_UID /app/data /app/logs
USER $APP_UID

VOLUME ["/app/data"]
ENTRYPOINT ["dotnet", "LegacyBridge.Worker.dll"]
