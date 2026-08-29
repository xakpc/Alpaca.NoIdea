# syntax=docker/dockerfile:1
#
# Development image for the Alpaca MCP server.
#
# It builds the pinned submodule `external/alpaca-mcp-server` and serves it over
# streamable-http, so that the server can stay up between debug runs.
# The build context is the repository root.
#
#   docker compose -f compose.dev.yaml up -d --build
#
# The deployed image does not use this file. It puts the same server into the
# application image and starts it with the stdio transport. See
# `src/Xakpc.Alpaca.NøIdea/Dockerfile`.

FROM python:3.11-slim

COPY --from=ghcr.io/astral-sh/uv:0.9 /uv /uvx /bin/

# The uv cache mount and the target live on different filesystems.
ENV UV_LINK_MODE=copy

WORKDIR /opt/alpaca-mcp

# Dependencies first. This layer stays in the cache while the server source changes.
COPY external/alpaca-mcp-server/pyproject.toml \
     external/alpaca-mcp-server/uv.lock \
     external/alpaca-mcp-server/README.md ./
RUN --mount=type=cache,target=/root/.cache/uv \
    uv sync --frozen --no-dev --no-install-project

COPY external/alpaca-mcp-server/src/ ./src/
RUN --mount=type=cache,target=/root/.cache/uv \
    uv sync --frozen --no-dev

COPY docker/mcp-healthcheck.py /opt/mcp-healthcheck.py

ENV PATH="/opt/alpaca-mcp/.venv/bin:$PATH" \
    ALPACA_PAPER_TRADE=true \
    MCP_HEALTHCHECK_URL=http://127.0.0.1:8000/mcp

EXPOSE 8000

HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=3 \
    CMD ["python", "/opt/mcp-healthcheck.py"]

# The container binds 0.0.0.0 because Docker gives it a private network namespace.
# compose.dev.yaml publishes the port to 127.0.0.1 only.
CMD ["alpaca-mcp-server", "--transport", "streamable-http", "--host", "0.0.0.0", "--port", "8000"]
