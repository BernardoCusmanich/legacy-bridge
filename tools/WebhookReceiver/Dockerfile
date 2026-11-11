# Build a partir da raiz do repositório.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY tools/WebhookReceiver/WebhookReceiver.csproj tools/WebhookReceiver/
RUN dotnet restore tools/WebhookReceiver/WebhookReceiver.csproj
COPY tools/WebhookReceiver/ tools/WebhookReceiver/
RUN dotnet publish tools/WebhookReceiver/WebhookReceiver.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "WebhookReceiver.dll"]
