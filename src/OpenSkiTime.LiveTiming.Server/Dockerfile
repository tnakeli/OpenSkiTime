FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props Directory.Packages.props ./
COPY assets/icons/live-timing.ico assets/icons/
COPY src/OpenSkiTime.LiveTiming.Contracts/ src/OpenSkiTime.LiveTiming.Contracts/
COPY src/OpenSkiTime.LiveTiming.Server/ src/OpenSkiTime.LiveTiming.Server/
RUN dotnet publish src/OpenSkiTime.LiveTiming.Server/OpenSkiTime.LiveTiming.Server.csproj -c Release --no-self-contained -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
ENV Logging__LogLevel__Default=Warning
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "OpenSkiTime.LiveTiming.Server.dll"]
