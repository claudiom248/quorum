# Sample reference-data MCP server

A read-only MCP server (Streamable HTTP, stateless) with the reference data the sample company's corpora cannot hold:
currency rates, price indices and working-day calendars. It is not part of the product and is not shipped: it lives here
only as a test aid for the chat.

Why these tools and not more contracts: a knowledge base or a lookup already says what a contract costs, in euros, in
its own year. What no indexed chunk can say is what those euros were worth in dollars in 2019, what the same rent is
worth at the prices of 2024, or how many days a Milan office really has between two dates to send a notice. This service
holds exactly that, invented but fixed: the same question always gives the same answer, so tests are reproducible.

It changes nothing — there is no writing anywhere — and it never repeats a fact that the KBs or the lookups own, so
re-seeding the corpora cannot make anything here stale.

```bash
dotnet run --project tools/Quorum.SampleContractReferenceData
```

Listens on `http://127.0.0.1:3002`. Register `http://host.docker.internal:3002/mcp` from the Quorum container (run it
with `--urls http://0.0.0.0:3002` so the container can reach it, like `tests/McpDemoServer` does), or
`http://127.0.0.1:3002/mcp` when Quorum runs on the host.

`-- --require-token <secret>` puts a bearer check on `/mcp`: put the same value in the server's registration headers in
Quorum (`Authorization: Bearer <secret>`). `-- --help` prints the options.

| Tool | Answers | Needs |
|---|---|---|
| `reference_data_summary` | What the service is, and its exact codes: currencies, countries, series years, local holidays | nothing |
| `fx_rate` | The monthly rate between two currencies at a date (2015–2025) | a date, and the two currencies |
| `price_index` | The consumer price index of a country between two years (2018 = 100), optionally applied to an amount | a country and two years |
| `working_days_between` | How many days of a window are worked in a country (and a site city's extra day) | two dates and a country |
| `notice_deadline` | The date a notice must be in to end an agreement on its end date, slid back off a non-working day | the contract's end date, its notice days, a country |

All five arrive as read-only (`readOnlyHint`, `openWorldHint: false`), and new MCP tools arrive `PendingReview` with the
`Confirm` policy: enable them and activate them on the assistant before the chat can use them.

## Multi-hop questions this makes answerable

The server is most useful when the chat must combine it with what the corpora hold:

- *"By which date must we warn Vendor X to end the contract on its end date, given the 90-day notice in Milan?"* — a lookup or
  the KB gives `end_date` and the notice period; `notice_deadline(end_date, 90, "IT", "Milan")` dates the deadline.
- *"What did the 2019 annual fee of the Turin contract cost in dollars at the time?"* — the KB gives the euro amount
  and the year; `fx_rate("2019", "EUR", "USD")` converts it.
- *"How much is that 2019 fee at 2024 Italian prices?"* — `price_index("IT", 2019, 2024, amount)`.
- *"How many working days did the supplier actually have in that quarter?"* — `working_days_between(...)` with the
  window from the contract and the country of the site.

## Limits

The figures are invented, not real (a sample should not pretend to know the market), and the series stop at 2025: a
question dated after that fails with the coverage in the error, which is itself a good test of how the chat reports a
tool error. The sample company lives around 2025-10-31, so ask about contract dates, not about today. Calendars follow a
plausible Europe — Easter computus, national fixed days, weekend holidays observed on Monday, the patron-day extra of
each site city — not an official gazette.

For the generic plumbing paths (huge results, failures, confirmation) use [`tests/McpDemoServer`](../../tests/McpDemoServer/README.md)
instead; this server is for content-shaped tests, where the question mixes the corpora with data only here can give.
