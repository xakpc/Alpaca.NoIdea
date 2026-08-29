# Terminal User Interface

**Technology:** Spectre.Console.

The TUI is **read-only for trading decisions**. The operator uses it to monitor the process.
The operator does not approve a trade. A graphical user interface is not a hackathon
requirement.

**The TUI must not be required for system operation.** The trading loop must run correctly
if nobody watches the terminal.

## View shape

```text
ALPACA AUTONOMOUS OPTIONS AGENT
Mode: PAPER / LIVE COMPETITION

Market: OPEN
Next cycle: 00:12:34

Equity:      $102,430
Cash:         $71,200
Open trades:       3
Day P&L:       +2.43%

CURRENT CANDIDATE
NVDA  Call  Strike 185  Exp 2026-09-02

Historical ML:  63%
Research:       59%
Critic:         46%
Combined:       56%
Market ref:     39%
Edge:          +17 pp

Option check:   PASS
Risk check:     PASS
Decision:       ORDER SUBMITTED

OPEN POSITIONS
...
```

## Purpose

The TUI shows the whole decision path in one screen. This makes the demo easy: a viewer can
see each expert probability, the market reference, the edge, and the two gates. This
supports the *creativity* and *autonomy* judging criteria.

## Related

- [Observability](../operations/observability.md)
- [Component model](../architecture/component-model.md)
