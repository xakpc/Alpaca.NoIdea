# MVP Roadmap

Eight phases. Each phase has an exit condition. Do not start a phase before the previous
exit condition is true.

**Current position: inside Phase 1.** The MCP servers run. `Program.cs` prints `Hello, World!`.

```mermaid
flowchart LR
    P1[1 Alpaca MCP access] --> P2[2 SQLite and data]
    P2 --> P3[3 ML.NET]
    P3 --> P4[4 Research Agent]
    P4 --> P5[5 Critic Agent]
    P5 --> P6[6 Combine and score]
    P6 --> P7[7 Options and risk]
    P7 --> P8[8 Full rehearsal]
```

## Phase 1: Alpaca MCP access

**Current position: inside Phase 1.** Steps 1 to 5 are complete.

1. Create the .NET 10 console project. *(Done.)*
2. Add the Alpaca MCP server as a git submodule at `external/alpaca-mcp-server`. Pin the
   commit. *(Done.)*
3. Build the development MCP image and run the two permanent servers with
   `compose.dev.yaml`. *(Done. Ports 8100 and 8101.)*
4. Put the same pinned server into the application image for the deployed stdio mode.
   *(Done.)*
5. Confirm with `alpaca-mcp.http` that the read-only server exposes no order, position, or
   account tool. *(Done.)*
6. Configure a development paper account and put the keys in `.env`.
7. Add the C# MCP SDK (`ModelContextProtocol`).
8. Connect a read-only `McpClient`. List and filter the research tools.
9. Connect a trading `McpClient` on the second connection.
10. Fail startup if the read-only connection exposes a forbidden tool.
11. Implement `IMarketDataGateway`.
12. Implement `ITradingGateway`.
13. Read the account state, bars, news, and option chains.
14. Submit one controlled test option order in the development paper account.
15. Read and close that position.

> **Exit:** C# can read research data through the read-only MCP connection and can perform
> the required paper-trading actions through the separate trading MCP connection.

## Phase 2: SQLite and historical data

1. Create the SQLite schema. Add `Microsoft.Data.Sqlite` and Dapper.
2. Download historical bars through the market-data gateway.
3. Download historical news through the market-data gateway.
4. Cache the data.
5. Implement replay time.
6. Implement the replay market and trading gateways.

> **Exit:** The program can replay a past market period with no live MCP call.

## Phase 3: ML.NET

1. Implement feature generation.
2. Generate the historical labels.
3. Split the data by time.
4. Train the SDCA logistic regression model.
5. Save the model.
6. Evaluate the probability quality.

> **Exit:** The Historical ML Expert returns a probability for a candidate event.

## Phase 4: Research Agent

1. Configure `IChatClient`.
2. Discover and filter the approved read-only MCP tools.
3. Add the approved MCP tools to `ChatOptions.Tools`.
4. Add `FunctionInvokingChatClient`.
5. Add a strict structured response.
6. Test current research and replay research.

> **Exit:** The Research Agent can choose approved Alpaca MCP tools and return one valid
> probability.

## Phase 5: Critic Agent

1. Add the critic prompt.
2. Give it the candidate and the prior evidence.
3. Give it the same approved read-only MCP tools.
4. Return one valid probability.

> **Exit:** The Critic can challenge a candidate and return a measurable forecast.

## Phase 6: Combine and score

1. Implement the Brier score.
2. Implement the expert history.
3. Implement equal weights for the cold start.
4. Implement reliability weights after the sample threshold.
5. Record all outputs.

> **Exit:** The system can produce one combined probability and show why.

## Phase 7: Options and risk

1. Implement option candidate selection.
2. Implement the market probability reference.
3. Implement the quote-quality checks.
4. Implement the risk rules.
5. Implement order idempotency.
6. Implement position management.

> **Exit:** The system can run a complete autonomous paper trade.

## Phase 8: Full rehearsal

1. Run a complete market session on the development paper account.
2. Restart the .NET host during an open position.
3. Restart an MCP server during a test.
4. Confirm recovery.
5. Review all skip records and trade records.
6. Tune the strategy parameters from the replay data.
7. Create the official $100,000 paper account.
8. **Do not use the official account for development trades.**

> **Exit:** [Definition of done](definition-of-done.md) is complete.

## Related

- [Open strategy questions](open-strategy-questions.md)
- [Main risks](main-risks.md)
