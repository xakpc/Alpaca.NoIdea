# Local Development

How to run the Alpaca MCP server on the developer workstation.

The human-facing version of this file is `DEVELOPMENT.md` in the repository root. Keep the
two files in agreement.

## The development loop

The MCP server stays up between debug runs. The .NET host starts and stops many times, but
the server keeps running. This is different from the deployed mode, where the host owns the
server process. See [MCP integration](../alpaca/mcp-integration.md).

**There is one server, and it is read-only.** Deterministic C# reaches Alpaca through the
typed `Alpaca.Markets` SDK, so nothing consumed the trading server and it was deleted
(ADR-001, ADR-012). No MCP server this host runs holds an order tool at all.

```mermaid
flowchart LR
    VS[".NET host on the workstation"] -->|streamable-http 8100| RO["noidea-mcp-readonly"]
    HTTP["alpaca-mcp.http"] -.manual test.-> RO
    RO -->|HTTPS| A["Alpaca paper account"]
    VS -->|"Alpaca.Markets SDK, HTTPS"| A
```

## Start

```bash
cp .env.example .env          # then add the Alpaca and model keys
docker compose -f compose.dev.yaml up -d --build
docker compose -f compose.dev.yaml ps       # must report healthy
```

**The `-f compose.dev.yaml` flag is necessary.** Docker Compose finds only `compose.yaml`,
`compose.yml`, `docker-compose.yaml`, and `docker-compose.yml` by itself. Without the flag
the command stops with `no configuration file provided: not found`.

`restart: unless-stopped` starts the server again after a reboot of the workstation.

| Service | Port | `ALPACA_TOOLSETS` |
|---|---|---|
| `noidea-mcp-readonly` | `127.0.0.1:8100` | `assets,stock-data,options-data,news,corporate-actions` |

**The server binds to `127.0.0.1` only** and has no authentication. Never publish the port to
`0.0.0.0`.

## Keys

`.env` holds the Alpaca paper keys and **three** model keys. The room seats its four models
on three providers on purpose (ADR-020), so one key is not enough:

| Variable | Seats |
|---|---|
| `ANTHROPIC_API_KEY` | `proposer` (Opus 5), `skeptic` (Sonnet 5) |
| `OPENAI_API_KEY` | `quant` (GPT-5.6-terra) |
| `XAI_API_KEY` | `market` (Grok 4.6) |

`ChatClientFactory.MissingKeys` fails startup and names what is missing. A seat without a key
is a dead seat, so the failure belongs before the open. `--agent stub` needs none of them.

## The endpoint

- The path is `/mcp`. A request to `/mcp/` gets a `307` redirect to `/mcp`.
- `Accept` must contain `application/json, text/event-stream`.
- `initialize` returns the header `mcp-session-id`. Each later request must send it back in
  the header `Mcp-Session-Id`.
- The response body is a `text/event-stream`. The JSON is on the `data:` line.

```bash
curl -s -D - -o /dev/null -X POST http://127.0.0.1:8100/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"0.1"}}}'
```

## Manual tests

`alpaca-mcp.http` in the repository root holds the full sequence for the read-only server:
`initialize`, `notifications/initialized`, `tools/list`, and example `tools/call` requests.
Visual Studio Code reads the session id from the response by itself. Visual Studio cannot
chain responses, so paste the `mcp-session-id` value into the manual variables at the top.

The most important request in that file is `tools/list` against port 8100. It proves the
read-only guarantee. See [MCP safety](../alpaca/mcp-safety.md).

## The image

`docker/alpaca-mcp.dev.Dockerfile` builds the pinned submodule and serves it over
`streamable-http`. `docker/mcp-healthcheck.py` is the container healthcheck: any HTTP answer
proves that the server listens, because a bare `GET /mcp` correctly returns an error.

The build context for every image is the **repository root**, because the images need
`external/alpaca-mcp-server/`.

## Credentials

`.env` is git-ignored and holds `ALPACA_API_KEY`, `ALPACA_SECRET_KEY`,
`ALPACA_PAPER_TRADE=true`, and the three model keys above. Use the **development** paper
account here, not the official competition account. See [operations summary](summary.md).

## Related

- [MCP integration](../alpaca/mcp-integration.md)
- [MCP safety](../alpaca/mcp-safety.md)
- [Application structure](../architecture/application-structure.md)
- [Fault handling](fault-handling.md)
