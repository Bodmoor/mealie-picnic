# Multi-arch (linux/amd64 + linux/arm64).
#
# The build stage is pinned to $BUILDPLATFORM so the .NET compiler always runs
# natively on the runner, and `dotnet publish -a $TARGETARCH` cross-compiles for
# the target. Building the SDK stage under QEMU emulation instead would work but
# takes many minutes for a .NET build.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore on its own layer so editing a .cs file does not re-download packages.
# The arch must match the publish below: restore resolves RID-specific assets.
COPY src/MealiePicnic/MealiePicnic.csproj src/MealiePicnic/
RUN dotnet restore src/MealiePicnic/MealiePicnic.csproj -a $TARGETARCH

COPY src/ src/
# The PWA icons are EmbeddedResource items referenced from the csproj.
COPY assets/ assets/
RUN dotnet publish src/MealiePicnic/MealiePicnic.csproj \
        -c Release \
        -a $TARGETARCH \
        --no-restore \
        -o /app

# An empty, correctly-owned directory to seed the volume mount point. Prepared
# here so the runtime stage needs no RUN at all -- that keeps the final image
# free of any emulated execution.
RUN mkdir -p /data-empty

# ---------------------------------------------------------------- runtime
# No --platform: buildx resolves the right arch from the manifest list.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

COPY --from=build /app .

# The Picnic auth token lives here and must survive restarts, or every restart
# demands 2FA again. Owned by the non-root app user (UID 1654, $APP_UID in
# Microsoft's images) so a fresh named volume inherits that owner.
COPY --from=build --chown=1654:1654 /data-empty /data

ENV ASPNETCORE_URLS=http://+:8080 \
    DATA_DIR=/data \
    DOTNET_gcServer=0
EXPOSE 8080

# No HEALTHCHECK on purpose: the aspnet base image ships no curl/wget, and a
# probe that only proves the dotnet runtime exists (not that the app serves)
# would be worse than none. Add one at the orchestrator level if needed.

USER 1654:1654

ENTRYPOINT ["dotnet", "MealiePicnic.dll"]
