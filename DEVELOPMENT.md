# Development

How to run this project on a workstation.

## Prerequisites

- Docker Desktop
- .NET 10 SDK
- An Alpaca **development** paper account (not the official competition account)

## First-time setup

```bash
git submodule update --init --recursive     # gets external/alpaca-mcp-server
cp .env.example .env                        # then add the paper account keys
```

`.env` is git-ignored. It holds:

```text
ALPACA_API_KEY=...
ALPACA_SECRET_KEY=...
ALPACA_PAPER_TRADE=true
```

## Start the Alpaca MCP servers

Two containers stay up between debug runs, so the .NET host can start and stop freely.

```bash
docker compose -f compose.dev.yaml up -d --build
docker compose -f compose.dev.yaml ps        # both must say (healthy)
```

**The `-f compose.dev.yaml` is required.** Docker Compose auto-discovers only
`compose.yaml`, `compose.yml`, `docker-compose.yaml`, and `docker-compose.yml`. Without the
flag you get `no configuration file provided: not found`.

| Container | URL | Toolsets | Who may use it |
|---|---|---|---|
| `noidea-mcp-readonly` | `http://127.0.0.1:8100/mcp` | `assets,stock-data,options-data,news,corporate-actions` | The LLM agents and `IMarketDataGateway` |
| `noidea-mcp-trading` | `http://127.0.0.1:8101/mcp` | `account,trading,assets` | `ITradingGateway` only. No LLM. |

Both ports bind to `127.0.0.1` only. They have no authentication, and the trading server can
place orders. Never publish them to `0.0.0.0`.

Other commands:

```bash
docker compose -f compose.dev.yaml logs -f            # follow both servers
docker compose -f compose.dev.yaml restart mcp-trading
docker compose -f compose.dev.yaml down               # stop
```

The servers restart with Docker Desktop (`restart: unless-stopped`), so this is normally a
one-time command.

## Test the servers by hand

Open `alpaca-mcp.http` and run the requests from the top. It covers both servers:
`initialize`, `notifications/initialized`, `tools/list`, and example `tools/call` requests.

The most important request is `tools/list` on port 8100. The read-only server must expose
**no** order, position, or account tool.

Notes on the protocol:

- The path is `/mcp`. A request to `/mcp/` gets a `307` redirect.
- `Accept` must contain `application/json, text/event-stream`.
- `initialize` returns the header `mcp-session-id`. Send it back as `Mcp-Session-Id`.
- The answer is a `text/event-stream`. The JSON is on the `data:` line.
- Visual Studio Code (REST Client) fills the session id in by itself. Visual Studio cannot
  chain responses, so paste the value into the manual variables at the top of the file.

## Run the application

```bash
dotnet build
dotnet run --project src/Xakpc.Alpaca.NøIdea
```

The host reads the two server URLs from configuration. See
`.lode/alpaca/mcp-run-modes.md` for the configuration keys.

## Build the deployed image

The deployed image holds the .NET host **and** the pinned MCP server, which it starts as two
stdio child processes. It needs no compose file and no Docker socket.

```bash
docker build -f src/Xakpc.Alpaca.NøIdea/Dockerfile -t noidea/trader:dev .
docker run --rm -e ALPACA_API_KEY=... -e ALPACA_SECRET_KEY=... noidea/trader:dev
```

**The build context is the repository root** for both images, because both need
`external/alpaca-mcp-server/`.

## Common problems

| Symptom | Cause |
|---|---|
| `no configuration file provided: not found` | The `-f compose.dev.yaml` flag is missing. |
| `env file .env not found` | `.env` does not exist. Copy `.env.example`. |
| A container restarts in a loop | The API keys are empty or wrong. Read the logs. |
| `COPY external/... not found` during a build | The build ran from `src/`. Build from the repository root. |
| A tool is missing from a server | `ALPACA_TOOLSETS` in `compose.dev.yaml` selects the tools. |

## More

The full project memory is in `.lode/`. Start with `.lode/lode-map.md`.
