# Build a partir da raiz do repositório.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY tools/DataSimulator/DataSimulator.csproj tools/DataSimulator/
RUN dotnet restore tools/DataSimulator/DataSimulator.csproj
COPY tools/DataSimulator/ tools/DataSimulator/
RUN dotnet publish tools/DataSimulator/DataSimulator.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "DataSimulator.dll"]
CMD ["3"]
