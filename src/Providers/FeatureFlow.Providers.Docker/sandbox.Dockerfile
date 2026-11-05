# The sandbox image (docs/06 section 3.1), for the Docker and Kubernetes sandboxes: Claude Code, Copilot CLI, git, and an
# unprivileged user (node, uid 1000) the agent runs as. iptables closes a Docker sandbox's network at creation (M4
# follow-up 12); tini is a Kubernetes sandbox's init. Nothing of the engine is baked in: the sandbox client is copied
# into each sandbox (LOG 67), and the home directory, settings and checkout are created by the engine per step (DR19).
#
#   docker build -t feature-flow-sandbox:m8 -f src/Providers/FeatureFlow.Providers.Docker/sandbox.Dockerfile src/Providers/FeatureFlow.Providers.Docker
FROM node:24-bookworm-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates procps iptables tini curl \
    && rm -rf /var/lib/apt/lists/*
RUN npm install -g @anthropic-ai/claude-code@2.1.316 @github/copilot@1.0.105 && npm cache clean --force && claude --version && copilot --version
