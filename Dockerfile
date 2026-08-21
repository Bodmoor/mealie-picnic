# ---------------------------------------------------------------- build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, on its own layer: this only re-runs when the csproj changes,
# so editing a .cs file does not re-download every package.
COPY src/MealiePicnic/MealiePicnic.csproj src/MealiePicnic/
RUN dotnet restore src/MealiePicnic/MealiePicnic.csproj

COPY src/ src/
RUN dotnet publish src/MealiePicnic/MealiePicnic.csproj \
        -c Release \
        --no-restore \
        -o /app

# ---------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

COPY --from=build /app .

# The Picnic auth token is written here; it must survive restarts or every
# restart would demand 2FA again. Owned by the non-root app user (UID 1654,
# $APP_UID in Microsoft's images) so a fresh named volume inherits that owner.
RUN mkdir -p /data && chown -R 1654:1654 /data

ENV ASPNETCORE_URLS=http://+:8080 \
    DATA_DIR=/data \
    DOTNET_gcServer=0
EXPOSE 8080

# No HEALTHCHECK on purpose: the aspnet base image ships no curl/wget, and a
# probe that only proves the dotnet runtime exists (not that the app serves)
# would be worse than none. Add one at the orchestrator level if needed.

USER 1654:1654

ENTRYPOINT ["dotnet", "MealiePicnic.dll"]
