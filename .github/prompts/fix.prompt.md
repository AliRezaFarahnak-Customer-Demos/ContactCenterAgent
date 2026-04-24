# Fix the Broken Phone Audio

The last commit broke the solution — the AI voice can no longer be heard on the phone. It was working an hour ago.

## Steps

1. Use the Azure CLI to query Application Insights for recent exceptions and errors from the caller agent.
2. Compare with logs/telemetry from the last known working window (~1 hour ago).
3. Identify the regression introduced by the most recent commit.
4. Propose and apply the fix.

See [insights-analytics.agent.md](../agents/insights-analytics.agent.md) for the KQL queries and CLI patterns.
