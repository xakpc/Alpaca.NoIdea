# Terminology

Short definitions of the domain language of this project. Keep the lines short.

## Finance terms

- **Stock** — A share in a company. Example: `AAPL`, `MSFT`, `NVDA`.
- **ETF** — An exchange-traded fund. `SPY` and `QQQ` are ETFs. The system treats them as
  tracked market symbols.
- **Option** — A contract linked to the future price of a stock or an ETF.
- **Call** — An option that usually gains value when the stock price goes up.
- **Put** — An option that usually gains value when the stock price goes down.
- **Strike** — The price in the option contract.
- **Expiration** — The date when the option contract ends.
- **Premium** — The price of the option. One US equity option normally represents 100
  shares. A price of $3.00 therefore normally costs about $300.
- **Equity (account)** — The current total value of the account. It includes cash, the
  current value of open positions, and unrealized P&L. The hackathon scores total equity.
- **P&L** — Profit and loss.
- **Greek** — A number that describes how an option price reacts to a change.
- **Delta** — Sensitivity of the option price to stock price movement.
- **Gamma** — Sensitivity of delta.
- **Theta** — Sensitivity to time decay.
- **Vega** — Sensitivity to expected volatility.
- **Implied volatility** — The volatility that the current option price suggests.
- **Spread (bid/ask)** — The difference between the bid price and the ask price.
- **OPRA** — The paid professional US options data feed. This project does not use it.
- **Indicative feed** — The free Alpaca options feed. This project uses this feed only. The
  **latest** quote and chain are real time on the Basic plan. The 15-minute restriction
  applies to **historical** option bars and trades. The feed is not consolidated OPRA.
- **Basic plan** — The free Alpaca market-data tier. It serves the Indicative feed.
- **Thursday EOD** — End of day Thursday 2026-09-03. Alpaca uses the total equity at that
  moment as the effective final portfolio state, so it is the real finish line.

## Project terms

- **Edge** — The difference between the probability that the system estimates and the
  market probability reference. Example: system 60%, market 42%, edge +18 percentage
  points. A large edge does not prove that the trade makes money.
- **Alpha** — A useful advantage that can produce better returns than a simple market
  approach. The alpha hypothesis of this project is that the combined experts can estimate
  a short-term price outcome better than the current option pricing can.
- **MCP** — Model Context Protocol. The protocol that gives a tool catalog to a model. The
  Alpaca MCP server exposes the Alpaca APIs as MCP tools.
- **Expert** — One independent source of an opinion. The system has four experts. See
  [experts](war-room/summary.md).
- **Forecast** — One probability from one expert for one candidate event.
- **Market probability reference** — A probability derived from current option market data.
  The first implementation uses the absolute delta as a proxy. Delta is not an exact
  probability.
- **Brier score** — The reliability metric. `error = (predicted - actual)^2`. The actual
  value is 1 or 0. A lower score is better.
- **Reliability weight** — The influence of one expert in the combination. It comes from
  the average Brier score. See [forecast combination](war-room/summary.md).
- **Candidate** — One option contract and one price question under evaluation.
- **Evaluation run** — One row in the `evaluation_runs` table. It is one evaluated option
  event.
- **Cheap filter** — The step that compares the ML probability with the market reference
  before the system calls an LLM. It prevents unnecessary LLM cost.
- **Cycle** — One pass of the live trading loop. The target interval is 30 minutes.
- **Replay** — The historical mode. The same strategy code runs against stored SQLite data
  and a replay `TimeProvider`. See [replay mode](replay/replay-mode.md).
- **Fail closed** — The rule that the system skips a trade when any required input is
  missing, stale, or invalid.
- **Gateway** — A typed interface to Alpaca. Two exist: `IMarketDataGateway` for reads and
  `ITradingGateway` for account and order actions. Each has a live MCP implementation and a
  replay implementation.
- **Read-only connection** — The Alpaca MCP client that exposes market data, news, and
  option tools. The LLM agents use this connection only.
- **Trading connection** — The Alpaca MCP client that exposes account and order tools. Only
  deterministic C# uses it.
- **Toolset** — A named group of Alpaca MCP tools. `ALPACA_TOOLSETS` selects them, for
  example `account,trading,assets`. It is the server-side half of the read-only rule.
- **Streamable-http** — The MCP transport that the development servers use. The endpoint is
  `/mcp`. The answer is a `text/event-stream`, and the session id arrives in the
  `mcp-session-id` header.
- **Tool allowlist** — `McpToolCatalog`. It filters the discovered MCP tools before the host
  gives them to an LLM agent.
- **Guardrail** — A hard risk rule in C#. An LLM cannot change a guardrail.
- **TBD** — A strategy value that replay tests must set. It must not be guessed.
