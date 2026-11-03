FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/ backend/
COPY frontend/ frontend/
RUN dotnet publish backend/src/LifeOS.Api/LifeOS.Api.csproj --configuration Release --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish/ .
RUN mkdir -p /data/documents /data/logs && chown -R app:app /data
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__LifeOS="Data Source=/data/lifeos.db" \
    Documents__StoragePath=/data/documents \
    Logging__FilePath=/data/logs
EXPOSE 8080
USER app
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} exec dotnet LifeOS.Api.dll"]
