# syntax=docker/dockerfile:1
#
# The deployed image. It holds the trading host AND the pinned Alpaca MCP server.
#
# The host starts ONE MCP server as a stdio child process, read-only, for the LLM
# agents. Deterministic C# uses the typed Alpaca.Markets SDK instead, so no MCP
# server holds an order tool. A container cannot start a sibling container without
# the Docker socket, so the server lives in this image instead.
#
# The build context is the REPOSITORY ROOT, not `src/`:
#
#   docker build -f src/Xakpc.Alpaca.NøIdea/Dockerfile -t noidea/trader:dev .
#
# Development uses long-running HTTP servers instead. See compose.dev.yaml.

# --- Alpaca MCP server, from the pinned submodule -----------------------------
# uv installs its own CPython, so the image does not depend on the Python version
# of the .NET base image. Both directories move to the final stage at the same
# paths, because the virtual environment points at the interpreter by path.
FROM python:3.11-slim AS mcp
COPY --from=ghcr.io/astral-sh/uv:0.9 /uv /uvx /bin/

ENV UV_PYTHON=3.11 \
    UV_PYTHON_PREFERENCE=only-managed \
    UV_PYTHON_INSTALL_DIR=/opt/python \
    UV_LINK_MODE=copy

WORKDIR /opt/alpaca-mcp
COPY external/alpaca-mcp-server/pyproject.toml \
     external/alpaca-mcp-server/uv.lock \
     external/alpaca-mcp-server/README.md ./
RUN --mount=type=cache,target=/root/.cache/uv \
    uv python install 3.11 && uv sync --frozen --no-dev --no-install-project

COPY external/alpaca-mcp-server/src/ ./src/
RUN --mount=type=cache,target=/root/.cache/uv \
    uv sync --frozen --no-dev \
 && chmod -R a+rX /opt/python /opt/alpaca-mcp \
 && /opt/alpaca-mcp/.venv/bin/alpaca-mcp-server --version

# --- .NET build ---------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Xakpc.Alpaca.NøIdea/Xakpc.Alpaca.NøIdea.csproj", "src/Xakpc.Alpaca.NøIdea/"]
RUN dotnet restore "./src/Xakpc.Alpaca.NøIdea/Xakpc.Alpaca.NøIdea.csproj"
COPY Directory.Build.props ./
COPY src/ src/
WORKDIR "/src/src/Xakpc.Alpaca.NøIdea"
RUN dotnet build "./Xakpc.Alpaca.NøIdea.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Xakpc.Alpaca.NøIdea.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# --- final --------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

COPY --from=mcp /opt/python /opt/python
COPY --from=mcp /opt/alpaca-mcp /opt/alpaca-mcp
COPY --from=publish /app/publish .

# The host reads these. A command selects the stdio transport; a URL would select
# the HTTP transport (development only). Set one, never both.
ENV Alpaca__Mcp__ServerCommand=/opt/alpaca-mcp/.venv/bin/alpaca-mcp-server \
    Alpaca__Mcp__ReadOnlyToolsets=assets,stock-data,options-data,news,corporate-actions \
    ALPACA_PAPER_TRADE=true

USER $APP_UID
ENTRYPOINT ["dotnet", "Xakpc.Alpaca.NøIdea.dll"]
