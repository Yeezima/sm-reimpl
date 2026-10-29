# Dedicated server (SmServer) for linux-x64. Preparation only: nothing is built or deployed from this file yet
# Base images are pinned to exact patch versions (sdk 10.0.403, runtime 10.0.14); bump them on purpose, or pin by digest (image@sha256:...).
# The image holds NO game files, no config/local.json and no secrets:
# the game install is mounted read-only at /game at run time.
#
#   docker build -t smserver .
#   docker run --rm -p 27015:27015/udp -v /path/to/the/game/install:/game:ro smserver --map mp_tdm_09 --mode annihilation
#
# Optional: -v smdata:/data keeps the private caches (/data, SM_DATA_DIR) across runs.

FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /src
COPY global.json ./
COPY src/ src/
RUN dotnet publish src/SmServer/SmServer.csproj -c Release -r linux-x64 --self-contained false -o /out

FROM mcr.microsoft.com/dotnet/runtime:10.0.14
WORKDIR /app
COPY --from=build /out/ ./
# /game: the player's own Space Marine install, mounted read-only (the server only reads it). /data: caches the server may write.
ENV SM_GAME_DIR=/game \
    SM_DATA_DIR=/data
RUN mkdir -p /game /data && chown "$APP_UID" /data
VOLUME ["/data"]
# Never root: APP_UID is the image's non-root "app" user.
USER $APP_UID
EXPOSE 27015/udp
ENTRYPOINT ["dotnet", "SmServer.dll", "--port", "27015"]
