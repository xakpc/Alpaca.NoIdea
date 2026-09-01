# Development

How to run this project on a workstation.

## Prerequisites

- Docker Desktop
- .NET 10 SDK
- An Alpaca **development** paper account (not the official competition account)

## First-time setup

```bash
git submodule update --init --recursive     # gets external/alpaca-mcp-server
cp .env.example .env                        # then add the Alpaca and model keys
```

`.env` is git-ignored. It holds:

```text
ALPACA_API_KEY=...
ALPACA_SECRET_KEY=...
ALPACA_PAPER_TRADE=true

ANTHROPIC_API_KEY=...   # proposer and skeptic
OPENAI_API_KEY=...      # quant
XAI_API_KEY=...         # market (Grok 4.6)
```

**All three model keys are needed, not just the first.** The war room runs its seats on
three different providers on purpose: a room of one model arguing with itself shares that
model's blind spots (ADR-020). `--live --agent llm` refuses to start when any
is missing and name the ones they could not find, because a seat without a key is a dead
seat and that failure belongs before the open rather than at 09:31.

To run with no model keys at all, pass `--agent stub`.

The standard profile uses Claude Sonnet 5, GPT-5.6-terra, and Grok 4.6. Add `--cheap` to use
Claude Haiku 4.5 and GPT-5.4-nano. Grok stays on Grok 4.6. The log names every selected model
at startup.

`KEENABLE_API_KEY` is separate and optional. It is the web-research MCP server that gives the
seats `search_web_pages` and `fetch_page_content`. Without it the run warns once and decides
from Alpaca data alone; `--no-web-search` leaves it out deliberately.

## Start the Alpaca MCP server

One container stays up between debug runs, so the .NET host can start and stop freely. There
is only one, and it is read-only: deterministic C# moved to the `Alpaca.Markets` SDK, which
left the trading server with no consumer, so it was deleted (ADR-001, ADR-012). No MCP server
this host runs holds an order tool at all.

```bash
docker compose -f compose.dev.yaml up -d --build
docker compose -f compose.dev.yaml ps        # must say (healthy)
```

**The `-f compose.dev.yaml` is required.** Docker Compose auto-discovers only
`compose.yaml`, `compose.yml`, `docker-compose.yaml`, and `docker-compose.yml`. Without the
flag you get `no configuration file provided: not found`.

| Container | URL | Toolsets | Who may use it |
|---|---|---|---|
| `noidea-mcp-readonly` | `http://127.0.0.1:8100/mcp` | `assets,stock-data,options-data,news,corporate-actions` | The LLM agents |

There is no second container. `ITradingGateway` reaches Alpaca through the typed
`Alpaca.Markets` SDK, so nothing needed the trading server and it was deleted (ADR-001).

The port binds to `127.0.0.1` only and has no authentication. Never publish it to `0.0.0.0`.

Other commands:

```bash
docker compose -f compose.dev.yaml logs -f            # follow the server
docker compose -f compose.dev.yaml restart mcp-readonly
docker compose -f compose.dev.yaml down               # stop
```

The server restarts with Docker Desktop (`restart: unless-stopped`), so this is normally a
one-time command.

## Test the server by hand

Use the host diagnostic. It connects to the read-only server, lists its tools, and rejects an
order, position, or account tool:

```bash
dotnet run --project src/Xakpc.Alpaca.NøIdea -- --check-mcp
```

Notes on the protocol:

- The path is `/mcp`. A request to `/mcp/` gets a `307` redirect.
- `Accept` must contain `application/json, text/event-stream`.
- `initialize` returns the header `mcp-session-id`. Send it back as `Mcp-Session-Id`.
- The answer is a `text/event-stream`. The JSON is on the `data:` line.

## Run the application

```bash
dotnet build
dotnet run --project src/Xakpc.Alpaca.NøIdea
```

With no arguments the host prints what it can do and exits. The usual runs:

```bash
# Decide everything, send nothing. Safe out of hours.
dotnet run --project src/Xakpc.Alpaca.NøIdea -- --live --dry-run --once --allow-stale-quotes

# Trade the paper account.
dotnet run --project src/Xakpc.Alpaca.NøIdea -- --live

# Read the audit trail back.
dotnet run --project src/Xakpc.Alpaca.NøIdea -- --audit --last 20
```

`--smoke` is not read-only. It submits an option buy to the paper account. It then cancels an
unfilled order or closes a fill. Use `--check-mcp` for a read-only MCP diagnostic.

A normal live process stops when the Alpaca market clock reports a closed market. Start a new
process for the next session. `--once` is the explicit diagnostic override for an out-of-hours
cycle.

`data/trader.db` is a live and dry-run audit database. The host creates the current schema in
an empty file. It does not migrate an obsolete schema. Archive or remove an obsolete file
before startup. The host does not import `data/raw/`.

Every durable audit failure stops the live session. `--audit` opens the database in read-only
mode and returns a failure code for an incomplete sitting, missing tool result, missing
decision link, or unlinked live or dry-run order.

A run writes to stdout, which is what `docker logs` collects. Every line that tells the
story of the run carries an event id from `Observability/RunEvents.cs`, so a later view can
select on the id. See `.lode/operations/observability.md`.

The host reads the server URL from configuration. See `.lode/alpaca/mcp-integration.md` for
the configuration keys.

## Build the deployed image

The deployed image holds the .NET host **and** the pinned MCP server, which it starts as a
single stdio child process. It needs no compose file and no Docker socket.

```bash
docker build -f src/Xakpc.Alpaca.NøIdea/Dockerfile -t noidea/trader:dev .
docker run --rm --env-file .env noidea/trader:dev --live --dry-run --once
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
| `Missing model keys: ...` | The war room needs all three of `ANTHROPIC_API_KEY`, `OPENAI_API_KEY` and `XAI_API_KEY`. Add them, or pass `--agent stub`. |

## More

The full project memory is in `.lode/`. Start with `.lode/lode-map.md`.
