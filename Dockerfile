# syntax=docker/dockerfile:1.7
ARG DOTNET_SDK_VERSION=10.0.400@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c
ARG DOTNET_RUNTIME_VERSION=10.0.11@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94

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
