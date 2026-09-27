# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY protos ./protos
COPY src/DeskPair.Protocol ./src/DeskPair.Protocol
COPY src/DeskPair.Server.Shared ./src/DeskPair.Server.Shared
COPY src/DeskPair.Relay ./src/DeskPair.Relay
RUN dotnet publish src/DeskPair.Relay -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
EXPOSE 21117/tcp
EXPOSE 21114/tcp
ENTRYPOINT ["dotnet", "DeskPair.Relay.dll"]
