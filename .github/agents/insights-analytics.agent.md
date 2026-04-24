# Insights Analytics Agent — Application Insights Query Knowledge Base

## Purpose

Query and analyze telemetry from the ContactCenterAgent platform stored in **Azure Application Insights** (`appi-contactcenteragent`). This agent knows the data model, custom dimensions, and KQL patterns for investigating voice call quality, conversation analysis, error tracking, and usage metrics across both the admin chat and caller agent services.

---

## Reference Documentation

| Topic                    | URL                                                                                          |
| ------------------------ | -------------------------------------------------------------------------------------------- |
| KQL Quick Reference      | https://learn.microsoft.com/en-us/azure/data-explorer/kusto/query/                           |
| App Insights Data Model  | https://learn.microsoft.com/en-us/azure/azure-monitor/app/data-model-complete                |
| App Insights REST API    | https://learn.microsoft.com/en-us/rest/api/application-insights/query                        |
| Azure CLI — App Insights | https://learn.microsoft.com/en-us/cli/azure/monitor/app-insights                             |
| KQL String Operators     | https://learn.microsoft.com/en-us/azure/data-explorer/kusto/query/datatypes-string-operators |
| KQL Summarize            | https://learn.microsoft.com/en-us/azure/data-explorer/kusto/query/summarize-operator         |

---

## App Insights Resource

| Field            | Value                                  |
| ---------------- | -------------------------------------- |
| Resource Name    | `appi-contactcenteragent`                        |
| Resource Group   | `rg-contactcenteragent`                          |
| Region           | `swedencentral`                        |
| App ID           | `<your-app-insights-app-id>` |
| Subscription     | `<your-subscription-id>` |
| Log Analytics WS | `log-contactcenteragent`                         |
| Workspace Type   | Workspace-based (NOT classic)          |

> **CRITICAL — Workspace-Based App Insights**: This App Insights resource is **workspace-based**, meaning all telemetry is stored in the Log Analytics workspace `log-contactcenteragent`. You **MUST** query via `az monitor log-analytics query` against the workspace — NOT via `az monitor app-insights query`. The App Insights REST API returns **empty results** for workspace-based resources. The table names and column names also differ (see table mapping below).

---

## Data Model — Where Events Live

The ContactCenterAgent platform logs to three distinct telemetry tables depending on the source. Since this is a **workspace-based** App Insights, the tables use the `App*` naming convention in Log Analytics.

### Table Name Mapping (Classic → Workspace)

| Classic Table   | Workspace Table   | Column: timestamp | Column: name    | Column: customDimensions |
| --------------- | ----------------- | ----------------- | --------------- | ------------------------ |
| `customEvents`  | `AppEvents`       | `TimeGenerated`   | `Name`          | `Properties`             |
| `dependencies`  | `AppDependencies` | `TimeGenerated`   | `Name`          | `Properties`             |
| `exceptions`    | `AppExceptions`   | `TimeGenerated`   | `ExceptionType` | `Properties`             |
| `pageViews`     | `AppPageViews`    | `TimeGenerated`   | `Name`          | `Properties`             |
| `customMetrics` | `AppMetrics`      | `TimeGenerated`   | `Name`          | `Properties`             |

### Additional Column Mappings (AppExceptions)

| Classic Column     | Workspace Column   |
| ------------------ | ------------------ |
| `type`             | `ExceptionType`    |
| `outerMessage`     | `OuterMessage`     |
| `innermostMessage` | `InnermostMessage` |
| `cloud_RoleName`   | `AppRoleName`      |

### Source → Table Mapping

| Source              | SDK                                         | Table                                              | Event Names                                                                | Properties (Custom Dimensions)                                         |
| ------------------- | ------------------------------------------- | -------------------------------------------------- | -------------------------------------------------------------------------- | ---------------------------------------------------------------------- |
| Admin chat backend  | OpenTelemetry spans                         | `AppDependencies`                                  | `UserMessage`, `AiMessage`                                                 | `chat.event_type`, `chat.content`                                      |
| Admin chat frontend | `@microsoft/applicationinsights-web`        | `AppExceptions`, `AppPageViews`, `AppDependencies` | (auto)                                                                     | `component`, `vueInfo`, `contextId`                                    |
| Caller agent        | `TelemetryClient.TrackEvent/TrackException` | `AppEvents`                                        | `UserMessage`, `AiMessage`, `OutboundCallInitiated`, `CallAnsweredSuccess` | `chat.event_type`, `chat.content`, `chat.channel`, `chat.phone_number` |
| Caller agent errors | `TelemetryClient.TrackException`            | `AppExceptions`                                    | (auto)                                                                     | `Component` (e.g., `AzureVoiceLiveService.InitializeAsync`)            |

### Custom Dimensions Reference

| Dimension           | Found In                                       | Values / Description                                                     |
| ------------------- | ---------------------------------------------- | ------------------------------------------------------------------------ |
| `chat.event_type`   | Both                                           | `UserMessage` or `AiMessage`                                             |
| `chat.content`      | Both                                           | The actual transcript text of the message                                |
| `chat.channel`      | Caller agent                                   | Always `"voice"` for phone calls                                         |
| `chat.phone_number` | Caller agent                                   | E.164 phone number (e.g., `<your-alert-phone>`)                                 |
| `Component`         | Exceptions                                     | Source class/method (e.g., `AzureVoiceLiveService.ReceiveMessagesAsync`) |
| `PhoneNumber`       | `OutboundCallInitiated`                        | Target phone number                                                      |
| `CallConnectionId`  | `OutboundCallInitiated`, `CallAnsweredSuccess` | ACS call connection ID                                                   |
| `Purpose`           | `OutboundCallInitiated`                        | Call purpose text                                                        |
| `AnswerLatencyMs`   | `CallAnsweredSuccess`                          | Milliseconds to answer the call                                          |
| `CallerId`          | `CallAnsweredSuccess`                          | Inbound caller's phone number                                            |

---

## How to Query — Azure CLI

All queries use `az monitor log-analytics query` against the **Log Analytics workspace** (NOT `az monitor app-insights query`, which returns empty for workspace-based resources).

### Setup (PowerShell)

```powershell
# Get the workspace ID (run once per session)
$WSID = az monitor log-analytics workspace show -g rg-contactcenteragent -n log-contactcenteragent --query customerId -o tsv
```

### Query Pattern

```powershell
az monitor log-analytics query -w $WSID --analytics-query "<KQL_QUERY>" -o json
```

> **WARNING**: Do NOT use `az monitor app-insights query --app $APP_ID`. This queries the classic App Insights API which returns **empty results** for this workspace-based setup. Always use `az monitor log-analytics query -w $WSID`.

**Important notes:**

- Use `-o json` (not `-o table`) — table format often returns empty results for complex queries
- Inline KQL on a single line works best in PowerShell — avoid multi-line strings with special chars
- Use single quotes inside KQL strings (e.g., `'UserMessage'`), double quotes around the whole query
- Use workspace table names: `AppEvents` (not `customEvents`), `AppDependencies` (not `dependencies`), `AppExceptions` (not `exceptions`)
- Use workspace column names: `TimeGenerated` (not `timestamp`), `Name` (not `name`), `Properties` (not `customDimensions`)

---

## Query Catalog

### 1. Recent Voice Call Transcripts

Get the latest messages from phone calls, ordered by time.

```kql
AppEvents
| where Name in ("UserMessage", "AiMessage")
| where TimeGenerated > ago(2h)
| extend content = tostring(Properties["chat.content"]),
         eventType = tostring(Properties["chat.event_type"]),
         phone = tostring(Properties["chat.phone_number"])
| project TimeGenerated, eventType, phone, content
| order by TimeGenerated desc
| take 30
```

**Azure CLI:**

```powershell
$WSID = az monitor log-analytics workspace show -g rg-contactcenteragent -n log-contactcenteragent --query customerId -o tsv; az monitor log-analytics query -w $WSID --analytics-query "AppEvents | where Name in ('UserMessage','AiMessage') | where TimeGenerated > ago(2h) | extend content=tostring(Properties['chat.content']), eventType=tostring(Properties['chat.event_type']), phone=tostring(Properties['chat.phone_number']) | project TimeGenerated, eventType, phone, content | order by TimeGenerated desc | take 30" -o json
```

---

### 2. Full Conversation by Phone Number

Reconstruct a complete conversation for a specific phone number, ordered chronologically.

```kql
AppEvents
| where Name in ("UserMessage", "AiMessage")
| extend content = tostring(Properties["chat.content"]),
         eventType = tostring(Properties["chat.event_type"]),
         phone = tostring(Properties["chat.phone_number"])
| where phone == "<your-alert-phone>"
| project TimeGenerated, eventType, content
| order by TimeGenerated asc
```

**Azure CLI:**

```powershell
$WSID = az monitor log-analytics workspace show -g rg-contactcenteragent -n log-contactcenteragent --query customerId -o tsv; az monitor log-analytics query -w $WSID --analytics-query "AppEvents | where Name in ('UserMessage','AiMessage') | extend content=tostring(Properties['chat.content']), eventType=tostring(Properties['chat.event_type']), phone=tostring(Properties['chat.phone_number']) | where phone == '<your-alert-phone>' | project TimeGenerated, eventType, content | order by TimeGenerated asc" -o json
```

---

### 3. All Conversations Grouped by Phone Number

See all conversations today, grouped and ordered per caller.

```kql
AppEvents
| where Name in ("UserMessage", "AiMessage")
| where TimeGenerated > ago(24h)
| extend content = tostring(Properties["chat.content"]),
         eventType = tostring(Properties["chat.event_type"]),
         phone = tostring(Properties["chat.phone_number"])
| project TimeGenerated, eventType, phone, content
| order by phone asc, TimeGenerated asc
```

---

### 4. AI Response Length Analysis (Word Count)

Measure how verbose the AI is — useful for validating brevity rules.

```kql
AppEvents
| where Name == "AiMessage"
| where TimeGenerated > ago(24h)
| extend content = tostring(Properties["chat.content"]),
         phone = tostring(Properties["chat.phone_number"]),
         wordCount = array_length(split(tostring(Properties["chat.content"]), " "))
| project TimeGenerated, phone, wordCount, content
| order by TimeGenerated desc
```

**Summary stats:**

```kql
AppEvents
| where Name == "AiMessage"
| where TimeGenerated > ago(24h)
| extend wordCount = array_length(split(tostring(Properties["chat.content"]), " "))
| summarize avgWords = avg(wordCount),
            maxWords = max(wordCount),
            minWords = min(wordCount),
            p50 = percentile(wordCount, 50),
            p95 = percentile(wordCount, 95),
            totalResponses = count()
```

---

### 5. Verbose AI Responses (Over 15 Words)

Find AI responses that are too long — indicates brevity rules aren't being followed.

```kql
AppEvents
| where Name == "AiMessage"
| where TimeGenerated > ago(24h)
| extend content = tostring(Properties["chat.content"]),
         phone = tostring(Properties["chat.phone_number"]),
         wordCount = array_length(split(tostring(Properties["chat.content"]), " "))
| where wordCount > 15
| project TimeGenerated, phone, wordCount, content
| order by wordCount desc
```

---

### 6. Conversation Duration & Message Count per Call

Estimate call duration and turns per phone number.

```kql
AppEvents
| where Name in ("UserMessage", "AiMessage")
| where TimeGenerated > ago(24h)
| extend phone = tostring(Properties["chat.phone_number"]),
         eventType = tostring(Properties["chat.event_type"])
| summarize callStart = min(TimeGenerated),
            callEnd = max(TimeGenerated),
            totalMessages = count(),
            userMessages = countif(Name == "UserMessage"),
            aiMessages = countif(Name == "AiMessage")
  by phone
| extend durationSec = datetime_diff("second", callEnd, callStart)
| project phone, callStart, durationSec, totalMessages, userMessages, aiMessages
| order by callStart desc
```

---

### 7. Calls Per Day (Volume Trend)

```kql
AppEvents
| where Name == "OutboundCallInitiated"
| summarize CallCount = count() by bin(TimeGenerated, 1d)
| order by TimeGenerated desc
```

---

### 8. Message Volume Per Hour (Dashboard)

```kql
AppEvents
| where Name in ("UserMessage", "AiMessage")
| where TimeGenerated > ago(7d)
| summarize count() by bin(TimeGenerated, 1h), Name
| render timechart
```

---

### 9. Outbound Calls Initiated

Track all outbound calls placed by the system.

```kql
AppEvents
| where Name == "OutboundCallInitiated"
| extend phone = tostring(Properties["PhoneNumber"]),
         purpose = tostring(Properties["Purpose"]),
         connectionId = tostring(Properties["CallConnectionId"])
| project TimeGenerated, phone, purpose, connectionId
| order by TimeGenerated desc
```

---

### 10. Inbound Call Answer Latency

Monitor how fast inbound calls are answered.

```kql
AppEvents
| where Name == "CallAnsweredSuccess"
| extend callerId = tostring(Properties["CallerId"]),
         latencyMs = todouble(Properties["AnswerLatencyMs"]),
         connectionId = tostring(Properties["CallConnectionId"])
| project TimeGenerated, callerId, latencyMs, connectionId
| order by TimeGenerated desc
```

**Latency stats:**

```kql
AppEvents
| where Name == "CallAnsweredSuccess"
| extend latencyMs = todouble(Properties["AnswerLatencyMs"])
| summarize avgLatency = avg(latencyMs),
            p50 = percentile(latencyMs, 50),
            p95 = percentile(latencyMs, 95),
            p99 = percentile(latencyMs, 99),
            totalCalls = count()
```

---

### 11. Caller Agent Exceptions

Find all errors from the caller agent, grouped by component.

```kql
AppExceptions
| where TimeGenerated > ago(24h)
| extend component = tostring(Properties["Component"])
| where isnotempty(component)
| project TimeGenerated, component, ExceptionType, OuterMessage, InnermostMessage
| order by TimeGenerated desc
```

**Error summary by component:**

```kql
AppExceptions
| where TimeGenerated > ago(7d)
| extend component = tostring(Properties["Component"])
| where isnotempty(component)
| summarize errorCount = count(), lastSeen = max(TimeGenerated) by component, ExceptionType
| order by errorCount desc
```

---

### 12. Voice Live API Errors

Find errors specifically from the WebSocket receive loop.

```kql
AppExceptions
| where TimeGenerated > ago(24h)
| extend component = tostring(Properties["Component"])
| where component has "VoiceLive" or component has "ReceiveMessages"
| project TimeGenerated, ExceptionType, OuterMessage, InnermostMessage
| order by TimeGenerated desc
```

---

### 13. Combined View — Admin Chat + Caller Agent

See all messages across both services in one view.

```kql
let textChat = AppDependencies
| where Name in ("UserMessage", "AiMessage")
| extend eventType = tostring(Properties["chat.event_type"]),
         content   = tostring(Properties["chat.content"]),
         channel   = "text",
         phone     = "",
         agent     = "admin-chat";
let voiceChat = AppEvents
| where Name in ("UserMessage", "AiMessage")
| extend eventType = tostring(Properties["chat.event_type"]),
         content   = tostring(Properties["chat.content"]),
         channel   = tostring(Properties["chat.channel"]),
         phone     = tostring(Properties["chat.phone_number"]),
         agent     = "caller";
union textChat, voiceChat
| where TimeGenerated > ago(24h)
| project TimeGenerated, agent, channel, eventType, content, phone
| order by TimeGenerated desc
```

---

### 14. Detect User Frustration / Negative Sentiment

Search for keywords indicating the user may be annoyed or frustrated.

```kql
AppEvents
| where Name == "UserMessage"
| extend content = tostring(Properties["chat.content"]),
         phone = tostring(Properties["chat.phone_number"])
| where content has_any ("stop", "shut up", "quiet", "enough", "annoyed",
        "frustrated", "idiot", "stupid", "hang up", "bye", "leave me",
        "don't call", "go away", "what do you want", "who is this",
        "lade v\u00e6re", "hold op", "stop", "nok", "farvel", "g\u00e5 v\u00e6k")
| project TimeGenerated, phone, content
| order by TimeGenerated desc
```

---

### 15. AI Filler Word Detection

Check if the AI is using filler phrases it shouldn't (post-brevity rules).

```kql
AppEvents
| where Name == "AiMessage"
| where TimeGenerated > ago(24h)
| extend content = tostring(Properties["chat.content"]),
         phone = tostring(Properties["chat.phone_number"])
| where content has_any ("absolutely", "sure thing", "great question",
        "of course", "glad to hear", "that's a great", "let me",
        "I'd be happy to", "no problem at all")
| project TimeGenerated, phone, content
| order by TimeGenerated desc
```

---

### 16. Barge-In Detection (User Interruptions)

Identify cases where the user likely interrupted the AI — user message arrives very shortly after an AI message started.

```kql
AppEvents
| where Name in ("UserMessage", "AiMessage")
| where TimeGenerated > ago(24h)
| extend content = tostring(Properties["chat.content"]),
         eventType = Name,
         phone = tostring(Properties["chat.phone_number"])
| order by phone asc, TimeGenerated asc
| serialize
| extend prevEventType = prev(eventType), prevTimestamp = prev(TimeGenerated), prevPhone = prev(phone)
| where eventType == "UserMessage" and prevEventType == "AiMessage" and phone == prevPhone
| extend gapMs = datetime_diff("millisecond", TimeGenerated, prevTimestamp)
| where gapMs < 2000
| project TimeGenerated, phone, gapMs, content
| order by gapMs asc
```

---

### 17. Conversation Turn Ratio (AI Talkativeness)

Compare how many times the AI speaks vs the user per call — high AI ratio = too talkative.

```kql
AppEvents
| where Name in ("UserMessage", "AiMessage")
| where TimeGenerated > ago(7d)
| extend phone = tostring(Properties["chat.phone_number"])
| summarize userTurns = countif(Name == "UserMessage"),
            aiTurns = countif(Name == "AiMessage")
  by phone
| extend aiToUserRatio = round(1.0 * aiTurns / userTurns, 2)
| project phone, userTurns, aiTurns, aiToUserRatio
| order by aiToUserRatio desc
```

---

### 18. Unique Phone Numbers Contacted

```kql
AppEvents
| where Name in ("UserMessage", "AiMessage")
| where TimeGenerated > ago(30d)
| extend phone = tostring(Properties["chat.phone_number"])
| where isnotempty(phone)
| summarize firstCall = min(TimeGenerated), lastCall = max(TimeGenerated), totalMessages = count() by phone
| order by lastCall desc
```

---

### 19. Frontend Errors (Admin Chat UI)

```kql
AppExceptions
| where TimeGenerated > ago(24h)
| where Properties has "component" or AppRoleName == "admin-chat-frontend"
| project TimeGenerated, ExceptionType, OuterMessage, InnermostMessage,
         component = tostring(Properties["component"])
| order by TimeGenerated desc
```

---

### 20. AI Response Time (Latency Between User Message and AI Reply)

```kql
AppEvents
| where Name in ("UserMessage", "AiMessage")
| where TimeGenerated > ago(24h)
| extend phone = tostring(Properties["chat.phone_number"]),
         eventType = Name
| order by phone asc, TimeGenerated asc
| serialize
| extend prevEventType = prev(eventType), prevTimestamp = prev(TimeGenerated), prevPhone = prev(phone)
| where eventType == "AiMessage" and prevEventType == "UserMessage" and phone == prevPhone
| extend responseTimeMs = datetime_diff("millisecond", TimeGenerated, prevTimestamp)
| project TimeGenerated, phone, responseTimeMs
| summarize avgResponseMs = avg(responseTimeMs),
            p50 = percentile(responseTimeMs, 50),
            p95 = percentile(responseTimeMs, 95)
```

---

## Querying Tips

### PowerShell Best Practices

1. **Always use `-o json`** — `-o table` silently drops rows for complex queries
2. **Single-line KQL** works best in Azure CLI — avoid multi-line strings
3. **Use single quotes** inside KQL for string literals (e.g., `'UserMessage'`)
4. **Pipe to `Select-Object -First N`** in PowerShell to limit output size
5. **Store workspace ID** in a variable to avoid repeated lookups:
   ```powershell
   $WSID = az monitor log-analytics workspace show -g rg-contactcenteragent -n log-contactcenteragent --query customerId -o tsv
   ```
6. **NEVER use `az monitor app-insights query`** — it returns empty results for this workspace-based setup. Always use `az monitor log-analytics query -w $WSID`.

### Time Ranges

| KQL Expression | Period     |
| -------------- | ---------- |
| `ago(1h)`      | Last hour  |
| `ago(2h)`      | Last 2h    |
| `ago(24h)`     | Last day   |
| `ago(7d)`      | Last week  |
| `ago(30d)`     | Last month |

### Common Filters

```kql
// Filter by phone number
| where tostring(Properties["chat.phone_number"]) == "<your-alert-phone>"

// Only AI messages
| where Name == "AiMessage"

// Only user messages
| where Name == "UserMessage"

// Only voice channel
| where tostring(Properties["chat.channel"]) == "voice"

// Text search in content
| where tostring(Properties["chat.content"]) has "keyword"
```

### Output Formatting

```kql
// Limit rows
| take 20

// Sort newest first
| order by TimeGenerated desc

// Sort oldest first (for conversation replay)
| order by TimeGenerated asc

// Render as chart (App Insights portal only)
| render timechart
| render barchart
| render piechart
```

---

## Troubleshooting

### Empty Results

- **Wrong query API**: This is a **workspace-based** App Insights. Use `az monitor log-analytics query -w $WSID`, NOT `az monitor app-insights query --app $APP_ID`. The App Insights REST API returns **empty results** for workspace-based resources.
- **Wrong table names**: Use workspace table names: `AppEvents` (not `customEvents`), `AppDependencies` (not `dependencies`), `AppExceptions` (not `exceptions`)
- **Wrong column names**: Use `TimeGenerated` (not `timestamp`), `Name` (not `name`), `Properties` (not `customDimensions`)
- **Wrong table**: Admin chat uses `AppDependencies`, caller agent uses `AppEvents`
- **Wrong time range**: Widen from `ago(2h)` to `ago(24h)` or `ago(7d)`
- **Output format**: Use `-o json` instead of `-o table`
- **No data**: Check if the service is deployed and has received traffic

### Missing Phone Numbers

- Phone number is only populated for caller agent events, not admin chat text
- Inbound calls store the caller's number; outbound calls store the target number

### Truncated Content

- Long transcripts may be truncated in `Properties` — this is the full content logged by the agent
- Very short AI transcripts (e.g., `"Det ly"`) indicate barge-in interrupted the response mid-word
