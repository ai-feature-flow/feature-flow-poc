# syntax=docker/dockerfile:1
# The images of the three hosts (docs/02 section 1): HOST is Api, OrchestrationWorker or AgentWorker. Built from the
# repository root, for the builder's platform or any other (--platform):
#
#   docker build -f deploy/images/host.Dockerfile --build-arg HOST=Api -t feature-flow-api .
#
# Nothing secret is baked in: the installation and organisation files and the secrets are mounted at run time and
# named by FF_INSTALLATION and FF_ORGANISATION. The process runs as the base image's unprivileged user and exits 0
# on SIGTERM. The standard library ships in /opt/feature-flow/library/std (docs/08 section 6).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG HOST
ARG TARGETARCH
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
COPY hosts/ hosts/
RUN dotnet publish hosts/FeatureFlow.Host.$HOST -c Release -a $TARGETARCH --no-self-contained -p:DebugType=none -o /out \
    && ln -s FeatureFlow.Host.$HOST /out/feature-flow-host
# The agent worker copies the sandbox client into each sandbox (LOG 67); sandboxes run on the worker's architecture.
RUN if [ "$HOST" = AgentWorker ]; then \
      dotnet publish src/FeatureFlow.SandboxClient -c Release -a $TARGETARCH --self-contained -p:PublishSingleFile=true -p:DebugType=none -o /out/sandbox-client; \
    fi

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# git: the engine's clone cache and commit detection run git (docs/02 sections 4.3 and 6). tini: the host must not be
# process 1, where the kernel ignores a signal without a handler, so a crash's abort() spun forever instead of exiting.
RUN apt-get update \
    && apt-get install -y --no-install-recommends git tini \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
COPY library/std /opt/feature-flow/library/std
ENV ASPNETCORE_HTTP_PORTS= \
    FF_API_LISTEN=http://0.0.0.0:8480 \
    FF_INSTALLATION=/etc/feature-flow/installation.yaml \
    FF_ORGANISATION=/etc/feature-flow/organisation.yaml
USER $APP_UID
ENTRYPOINT ["/usr/bin/tini", "--", "/app/feature-flow-host"]
