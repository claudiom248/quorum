# Quorum image from an already published Host (fast, no downloads inside the build):
#
#   dotnet publish src/Quorum.Host -c Release -o artifacts/publish
#   docker compose --profile bundled-db up --build
#
# Dockerfile.ci builds everything from the sources instead (used for releases).
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Data Protection key ring: the master key of stored secrets, kept on a volume (never in the image or database).
RUN mkdir -p /var/lib/quorum/keys && chown -R app:app /var/lib/quorum
VOLUME /var/lib/quorum/keys

COPY artifacts/publish/ .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Quorum.Host.dll"]
