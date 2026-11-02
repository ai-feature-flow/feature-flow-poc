# The solo profile's sandbox image (docs/06 section 3.1): Claude Code, git, and an unprivileged user (node, uid
# 1000) the agent runs as. Nothing of the engine is baked in: the sandbox client is copied into each sandbox
# (LOG 67), and the home directory, settings and checkout are created by the engine per step (DR19).
#
#   docker build -t feature-flow-sandbox:m4 -f src/Providers/FeatureFlow.Providers.Docker/sandbox.Dockerfile src/Providers/FeatureFlow.Providers.Docker
FROM node:24-bookworm-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates procps \
    && rm -rf /var/lib/apt/lists/*
RUN npm install -g @anthropic-ai/claude-code@2.1.316 && npm cache clean --force && claude --version
