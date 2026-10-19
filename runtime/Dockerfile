FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY runtime ./runtime
RUN dotnet publish runtime/src/Luftpause.Runtime -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=dev
LABEL org.opencontainers.image.source=https://github.com/luftpause/luftpause
LABEL org.opencontainers.image.version=$VERSION
LABEL org.opencontainers.image.licenses=BUSL-1.1
LABEL io.modelcontextprotocol.server.name="io.github.luftpause/luftpause"
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 python3-venv \
 && rm -rf /var/lib/apt/lists/* \
 && python3 -m venv /opt/tools \
 && /opt/tools/bin/pip install --no-cache-dir wikipedia-mcp==2.0.1
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=
ENV Runtime__StateDir=/var/lib/luftpause
VOLUME /var/lib/luftpause
COPY --from=build /out .
ENTRYPOINT ["dotnet", "Luftpause.Runtime.dll"]
