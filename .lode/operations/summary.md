# Operations

How the system starts, runs, fails, and gets tested.

## Deployment

The system does not need to run 24 hours each day. It must operate during the required US
market sessions. The process can stay running and use the Alpaca clock to wait while the
market is closed.

The operator starts the program. **After startup the system does not require trade
approval.**

The deployed image holds the .NET host **and** the pinned Alpaca MCP server. The host starts
two `stdio` server children inside its own container, so the deployment needs no Docker
socket and no second container. The process can run on the developer workstation, an existing
server, or a small VM. A paid deployment platform is not required.

Development is different: two permanent MCP containers serve `streamable-http` on
`127.0.0.1:8100` and `127.0.0.1:8101`. See [local development](local-development.md) and
[MCP run modes](../alpaca/mcp-run-modes.md).

## Submission

A **hosted application is not required.** The agent runs autonomously and only places
orders, so a GitHub repository is a sufficient submission. A hosted link is needed only for
a demo application that the judges must open. Per
[KISS and YAGNI](../practices.md), do not build one.

The repository can stay private during the hackathon.

Pre-event infrastructure, boilerplate, and existing libraries can be reused. **Pre-event
work used in the submission must be disclosed.** The submission must also state the use of
the free Indicative options feed.

## Accounts

Use two paper accounts:

| Account | Use |
|---|---|
| Development paper account | All integration tests and all rehearsal trades. |
| Official $100,000 paper account | The competition window only. No development trades. |

Both Alpaca MCP server instances use the same account credentials. The difference is the
toolset, not the account. See [MCP safety](../alpaca/mcp-safety.md).

The official Q&A permits a separate development account.

## Related

- [Competition constraints](competition-constraints.md)
- [Local development](local-development.md)
- [Restart and recovery](restart-recovery.md)
- [Fault handling](fault-handling.md)
- [Testing strategy](testing-strategy.md)
- [Observability](observability.md)
