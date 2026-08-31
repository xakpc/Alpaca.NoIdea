# Open Strategy Questions

The code is not the place to guess these answers. Real paper-trade outcomes must provide the
evidence.

```mermaid
flowchart LR
    T[Real paper trades] --> A[Durable audit]
    A --> M[Measured outcomes]
    M --> P[Policy decision]
```

## Questions

- Do 2 to 10 days to expiration provide enough liquidity and time?
- Are the 50% take-profit and 40% stop-loss defaults suitable?
- Does the 15% spread cap admit poor fills?
- Which personas add information after costs?
- Does confidence-weighted sizing improve account equity?
- When is a defined-risk multi-leg structure justified?

```sql
SELECT action, outcome, reason, risk_result
FROM decision_events
WHERE mode = 'live'
ORDER BY timestamp_utc;
```

## Invariants

- A risk appetite value is a human decision.
- A strategy calibration needs enough real outcomes.
- A model claim needs comparison with a simple reference.
- The agent can narrow policy within hard bounds but cannot widen risk.

## Related lodes

- [Strategy parameters](../trading/strategy-parameters.md)
- [Historical model evidence](../research/historical-model-evidence.md)
- [Audit schema](../storage/schema.md)
