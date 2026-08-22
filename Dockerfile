# syntax=docker/dockerfile:1.7
ARG DOTNET_SDK_VERSION=10.0.400@sha256:e1fc6e423f543119c406d24e2e687d67c569f18f04a37a8b0005d80ad0dcee80
ARG DOTNET_RUNTIME_VERSION=10.0.11@sha256:207cc51496778557731c81ff670333d8ade4a4fec22768fd1be8e78474a84ecf

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
ARG PROJECT
ARG CONFIGURATION=Release
WORKDIR /src
# Application images receive only build policy and production source. Tests,
# infrastructure, generated evidence and local operational material never enter
# a Docker layer even when a future ignore rule is accidentally relaxed.
COPY .editorconfig Directory.Build.props global.json ./
COPY src ./src
RUN --mount=type=cache,id=post-nuget,target=/root/.nuget/packages,sharing=locked \
    test -n "$PROJECT" && dotnet restore "$PROJECT" --locked-mode
RUN --mount=type=cache,id=post-nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "$PROJECT" --configuration "$CONFIGURATION" --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_VERSION} AS runtime
WORKDIR /app
RUN mkdir -p /data/core /data/edge && chown -R "$APP_UID":"$APP_UID" /data /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet"]
