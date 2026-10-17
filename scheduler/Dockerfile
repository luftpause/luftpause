FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY proto ./proto
COPY scheduler ./scheduler
RUN dotnet publish scheduler/src/Luftpause.Scheduler -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG VERSION=dev
LABEL org.opencontainers.image.source=https://github.com/luftpause/luftpause
LABEL org.opencontainers.image.version=$VERSION
LABEL org.opencontainers.image.licenses=Elastic-2.0
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=
COPY --from=build /out .
ENTRYPOINT ["dotnet", "Luftpause.Scheduler.dll"]
