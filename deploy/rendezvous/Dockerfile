# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY protos ./protos
COPY src/DeskPair.Protocol ./src/DeskPair.Protocol
COPY src/DeskPair.Server.Shared ./src/DeskPair.Server.Shared
COPY src/DeskPair.Rendezvous ./src/DeskPair.Rendezvous
RUN dotnet publish src/DeskPair.Rendezvous -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
VOLUME ["/data"]
ENV Rendezvous__KeyPath=/data/server.key \
    Rendezvous__DatabasePath=/data/peers.db
EXPOSE 21114/tcp 21115/tcp 21116/tcp 21116/udp
ENTRYPOINT ["dotnet", "DeskPair.Rendezvous.dll"]
