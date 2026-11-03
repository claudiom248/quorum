# Handoff in Quorum: notes on MAF and Swarms

**Status:** research notes, not a spec. Written 2026-11-03 from a design discussion; the next step is a spec (014) and a plan.
**Goal:** introduce **handoff** between agents, running in-process in the Host (no distributed runtime), also against locally hosted models.

> "Local" is read here as *in-process inside the modular monolith*, working with local endpoints (for example Qwen served by a local server). If it means something else, revisit the runtime section.

Provenance of the facts below:

- **[code]** read in this repository during the discussion.
- **[sample]** read in the MAF handoff sample on GitHub, through a summarising tool, not line by line.
- **[search]** from web search results, not confirmed in primary documentation.
- **[memory]** from general knowledge; verify before relying on it.

`learn.microsoft.com` was **not reachable** from the session, so MAF's official documentation was never read. Everything about MAF behaviour that is not in the sample is a hypothesis for the spike (see [Spike](#spike-before-the-spec)).

---

## 1. Where Quorum stands

Spec 013 (agents and delegation) is approved; PRs #154 to #157 merged model endpoints, the agents foundation and the agents API.

Relevant decisions of spec 013:

| Decision | Content |
|---|---|
| D-2 | No `Topology` entity. The graph is the set of edges declared on agents; an assistant points to a root agent. |
| D-3, D-4 | Delegation is **agent-as-tool**. A sub-agent receives only the task text and returns its final message as the tool result. |
| D-5 | **No nesting**: a sub-agent cannot call sub-agents. |
| D-7 | **Handoff is out of 013.** Direction: the user sees which agent is answering, and the current agent decides when to hand the conversation back. The design of 013 must not preclude it. |
| D-8 | The data model must serialise to a future declarative format without restructuring (stable slugs, no runtime state in definitions). |
| D-10 | An agent can be a root and also a sub-agent of another. |
| D-18, D-24 | Agents are identified by a slug unique per tenant; the edges live inside the agent resource and a `PUT` replaces them atomically. |
| D-26, D-28 | `agents.manage` permission; tool audit entries record the invoking `AgentId`. |

**Not found in the code [code]:** the delegation runtime. A search for `subagent`, `sub_agent` and `delegat` in `Chat/` (including `AnswerPipeline.cs`) found nothing. The tool-per-sub-agent, the isolated sub-agent run and the nested ledger entries of D-3/D-4/D-11/D-32 look unimplemented. Re-check at the start of the work; it may live elsewhere.

### Code anchors [code]

| What | Where |
|---|---|
| One agent per answer: `ChatClientAgent` over `FunctionInvokingChatClient` over `ContextGuardChatClient` over `OpenAiChatClient` | `Chat/AnswerPipeline.cs:76` (`ProduceAsync`), agent built at `:91` |
| The history is the whole conversation, no agent session | `AnswerPipeline.cs:104-106` (`session: null`) |
| Output as one channel of `AnswerChunk` (Text, Reasoning, ToolCall, ToolResult, UserQuestion, Sources) | `AnswerPipeline.cs:145` (`ObserveAsync`), `Contracts/IAssistantChat.cs:87-109` |
| A call the invoker did not handle is shown as a failed call to a tool "not offered" | `AnswerPipeline.cs:161-178` |
| Every tool call goes through one choke point (policy, validation, confirmation, audit, timeouts, result cap) | `Tools/ToolCallInvoker.cs:49` (`InvokeCoreAsync`) |
| Model client: OpenAI-compatible `POST {baseUrl}/chat/completions` only; one client per answer | `Chat/OpenAiChatClient.cs:51` |
| Packages: `Microsoft.Agents.AI` 1.25.0, `Microsoft.Extensions.AI` 10.11.0; **no** `Microsoft.Agents.AI.Workflows` | `Directory.Packages.props:30-32` |

`AnswerChunk` has no field for who is speaking, and `Message` stores only text, status and sources (no agent).

---

## 2. What handoff means, and how it differs from delegation

| Pattern | Who holds control | What the user sees |
|---|---|---|
| Delegation (spec 013) | The caller keeps it; the sub-agent is a tool and returns one message | Always the root agent |
| **Handoff** | **Passes to the other agent**, which continues the conversation | The answering agent changes |
| Group chat (future) | Nobody; a shared transcript and a policy that picks the speaker | Several agents alternate |

Consequences specific to handoff:

- The new agent receives the **whole conversation**. This is the opposite of D-4 (sub-agent gets only the task text).
- There is **state across turns**: which agent is active. It is runtime state, not part of the definitions (D-8).
- Handoff edges can form **cycles** (A to B to A). D-5 does not apply to them, so a per-turn cap is required.

---

## 3. Swarms

Two different projects are easy to confuse.

**OpenAI Swarm** [memory]: an experimental, educational framework later superseded by the OpenAI Agents SDK, which keeps its core idea. A handoff is a function that returns another agent; the conversation continues with that agent. Agents do **not** talk to each other. (The "social" behaviour discussed in the conversation is closer to group chat, see section 8.)

**Swarms** (`kyegomez/swarms`) [sample-level, via a summarising tool on `swarms/structs/debate_with_judge.py`]: `DebateWithJudge` is a **code-driven loop**, not an LLM-driven one.

- `for round_num in range(max_loops)` (default 3). Pro speaks, then Con receives Pro's argument, then the Judge evaluates both.
- Each agent gets a freshly built prompt with only the latest pieces, not the full transcript.
- The judge's synthesis becomes the topic of the next round (`current_topic = judge_synthesis`), so the original question survives only if the judge carries it forward.
- No early exit and no convergence check. The result is the formatted conversation history; the last judge entry is the answer.
- "Optimised for debate" in its docs [search] means preset Pro/Con/Judge role prompts. No benchmark or training was found behind the claim.

What to take from it: orchestration in code, not in a coordinating LLM. What not to copy: losing the original question after round one, and no early termination.

---

## 4. Microsoft Agent Framework (MAF)

Constraint from `CLAUDE.md`: stay as aligned with MAF as possible; MAF and Extensions.AI types never cross `Quorum.Modules.Agents.Contracts`.

Orchestration patterns [search]: sequential, concurrent, handoff, group chat, magentic (the last still in progress). One source notes handoff is the only pattern that pauses for user input between turns unless you opt out. Workflows live in a separate package, `Microsoft.Agents.AI.Workflows`; the sources cite versions 1.9 to 1.13, which do not obviously match the 1.25.0 of `Microsoft.Agents.AI` in the repo. **Check on NuGet before adding it.**

### Handoff API, from the official sample [sample]

`dotnet/samples/03-workflows/Orchestration/Handoff` (`Program.cs`, `AgentRegistry.cs`).

```csharp
// Agents: stable Id and Name, so the workflow can be rebuilt and checkpoints resumed
chatClient.AsAIAgent(new ChatClientAgentOptions { Id = "...", Name = "...", ChatOptions = new() { Instructions = "..." } });

// Graph
HandoffWorkflowBuilder builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(intakeAgent);
builder.WithHandoffs(sourceAgents, targetExpert);   // called once per target in the sample
builder.EnableReturnToPrevious();                    // an expert can hand back to the caller
Workflow workflow = builder.Build();

// Run, streaming, in-process
await using StreamingRun run = await InProcessExecution.OpenStreamingAsync(workflow, ct);
await run.TrySendMessageAsync(userInput);
await run.TrySendMessageAsync(new TurnToken(emitEvents: true));   // agents cache messages and run only on a TurnToken
await foreach (WorkflowEvent evt in run.WatchStreamAsync(ct)) { ... }
```

Events: `AgentResponseUpdateEvent` (text in `update.Update.Text`, speaker in `update.Update.AuthorName`), `WorkflowErrorEvent`, `WorkflowWarningEvent`.

What the sample does **not** show: how a handoff is decided internally (presumably tools added to each agent by the framework), when a turn ends, who is active after a turn, how history is passed in, and whether agents with different `IChatClient`s work. The sample shares one client across all agents.

Other samples in `dotnet/samples/03-workflows` worth reading: `Agents/WorkflowAsAnAgent` (relevant to D-5), `Agents/GroupChatToolApproval` (relevant to D-37), `HumanInTheLoop/HumanInTheLoopBasic`, `Checkpoint`, `Declarative`.

---

## 5. Proposed design for handoff

### 5.1 Definitions

- Add a **kind** to the edges of an agent: `delegate` (default, today's behaviour) or `handoff`. The edge already lives inside the agent resource (D-24); add `kind` next to the slug and the failure policy.
- Open question A: does the assistant need an explicit `orchestration` property? Handoff edges alone are enough to express handoff (the MAF builder is edges only). An explicit property becomes necessary for group chat. Recommendation: **edges only** for now.
- Edge option `returnToPrevious` maps to MAF's `EnableReturnToPrevious()` and to D-7 ("the current agent decides when to hand back").
- Per-assistant `MaxHandoffsPerTurn` (cycle guard).
- Validation: the target of a handoff edge follows the same visibility rules as roots and sub-agents (D-21, D-25); narrowing a scope that breaks an edge stays a 409 (D-16 style).
- Open question B: an agent reached by handoff is **active**. May it delegate to its own sub-agents like a root (D-10)? Recommendation: yes, it behaves as a root while active; delegation inside it keeps D-5.

YAML sketch (declarative format, D-8):

```yaml
assistant:
  name: support
  root: triage
  maxHandoffsPerTurn: 3
agents:
  - slug: triage
    description: Routes the request to the right specialist
    endpoint: local-llm
    subAgents:
      - { slug: billing, kind: handoff, returnToPrevious: true }
      - { slug: research, kind: delegate }
```

### 5.2 Conversation state and contracts

- **Active agent per conversation**: `ActiveAgentSlug` (null means the root). It lives with the conversation (Conversations module, its own DbContext and migration).
- `IAssistantChat.StartAnswerAsync` receives the active agent; the answer reports the agent that is active at the end. Additive contract changes only:
  - `AnswerChunk` gets an optional `Speaker` (agent slug), and a chunk kind or event for "handoff from A to B" (with the reason the model gave, shown in the ledger).
  - Messages store `AgentId` (or slug) so the UI shows who wrote each message.
- The answer pipeline stays **stateless per answer**: rebuild the workflow from the definitions and start the handoff builder at the **stored active agent**, instead of keeping a long-lived `StreamingRun` in memory (it would not survive restarts or several instances).

### 5.3 Runtime

1. Extract the body of `ProduceAsync` into "run one agent turn" (model client, tools, invoker, limits) with no behaviour change. This is also a prerequisite of the delegation runtime.
2. Build one MAF agent per Quorum agent: stable `Id` derived from `Agent.Id`, `Name` = slug (so `AuthorName` maps straight to `Speaker`), each over its own `OpenAiChatClient` (endpoints are per agent, D-33) wrapped as today (function-invoking and context-guard clients).
3. Build the handoff workflow from the stored active agent and the declared edges; run it in-process; map `AgentResponseUpdateEvent` to `AnswerChunk` with `Speaker`; end the channel when the turn ends.
4. At the end, persist the new active agent.

### 5.4 Tools, audit, safety

- Tools resolve per agent (D-17); audit entries carry `AgentId` (D-28). A handoff is audited too: from, to, outcome.
- Confirmation (`Confirm` policy) works for the active agent as for a root.
- **Risk to design for:** a handoff is a privilege change. A prompt injection can push the model to hand off to an agent with more or riskier tools. Mitigations: handoffs only along declared edges; target visibility re-checked at call time (like `CurrentPolicyAsync`); an optional per-edge confirmation; the tool set of the target is the only one that applies after the switch.
- Failure policy: if the target agent cannot run (endpoint unreachable, tool-less endpoint, scope narrowed), the current agent stays active and the model gets a readable error (same idea as D-13 `ReturnError`).
- Limits: the answer's total time (D-39 style) covers the whole turn, including handoffs; each agent keeps its own `MaxRounds`; the context guard checks **per agent**, because the shared history must fit the window of the agent about to answer (different models, different windows). Decide: refuse with a clear error, or trim the oldest turns.

### 5.5 History given to the active agent

Open question C: how to show an agent the messages written by other agents. With `/chat/completions` there are only `system`, `user`, `assistant` and `tool` roles, and many local servers' chat templates require alternating roles. Options: keep other agents' messages as `assistant` with a speaker prefix, or as `user` with the speaker name. A thin `IChatClient` decorator at the model edge can normalise roles, in the style of `ContextGuardChatClient`. Strip reasoning (`TextReasoningContent`) from what other agents see.

### 5.6 UI (D-7)

Show the agent name on each message, a ledger entry for each handoff, and the current agent in the conversation header. Out of scope: letting the user force an agent.

---

## 6. Spike before the spec

A throwaway project: two or three `ChatClientAgent`s over `OpenAiChatClient` against a local Qwen endpoint, wired with the MAF handoff builder, streaming events. It must answer:

1. **Handoff tools and the invoker.** How does MAF expose handoffs to the model? If as functions, they pass through `FunctionInvokingChatClient` and therefore `ToolCallInvoker`, which knows nothing about them. `ObserveAsync` (`AnswerPipeline.cs:161`) would also show them to the user as failed calls to a tool "not offered". How do we let them through, audit them, and publish a handoff event?
2. **Who is active at the end of a turn?** Probably the last `AuthorName`; verify, because it is the value to persist.
3. **When does `WatchStreamAsync` end a turn?** (idle, end of turn, handoff?) It decides when the `AnswerChunk` channel closes.
4. **One `IChatClient` per agent**: works, or not?
5. **History in:** does `TrySendMessageAsync` accept a list of messages? How are other agents' messages presented, and does that survive local chat templates?
6. **Package versioning** of `Microsoft.Agents.AI.Workflows` against `Microsoft.Agents.AI` 1.25.0.
7. **Cancellation:** does cancelling the token stop running agents and model calls (D-22 equivalent)?
8. Can a workflow sit inside an agent (`WorkflowAsAnAgent`)? Relevant to D-5 if a later pattern needs nesting.

---

## 7. Suggested plan (one branch per PR)

Per `CLAUDE.md`: spec and plan on a `docs/spec-014` branch with its own PR first; implementation from `main`. Commits follow Conventional Commits and reference the spec.

0. **Spike** (not merged), answers section 6.
1. **Spec 014 + plan** (docs). Amends spec 013 where needed (D-7 becomes a decision; D-32 changes if text of agents must be shown).
2. Delegation runtime of spec 013, **if still missing**. Independent of handoff but shares step 3.
3. Refactor: extract "run one agent turn" from `ProduceAsync`. No behaviour change.
4. Contracts and persistence: `Speaker` on `AnswerChunk`, `AgentId` on messages, `ActiveAgentSlug` on conversations (migrations).
5. Edge `kind` and `returnToPrevious`: domain, API, validation, 409 rules, tests.
6. Handoff runtime on MAF, event mapping, audit, limits, failure policy. Integration tests with Testcontainers and a scripted fake `IChatClient` that issues handoffs.
7. UI: speaker on messages, handoff ledger entries.
8. Docs: administrator guide section on handoff.

Verify each step with `dotnet build`, `dotnet test` and `dotnet format --verify-no-changes`, as `CLAUDE.md` requires.

---

## 8. Not in this iteration (decided in discussion, for later)

- **Group chat and debate:** pattern is a code-driven loop with a speaker-selection manager (MAF group chat with a custom manager), Pro and Con alternating, optional judge, termination by max rounds or a closing signal. A shared transcript; the user may intervene between rounds. Known risks: forced convergence, correlated bias when all agents use the same model family, cost growth because every turn rereads the transcript.
- **Jury:** several independent judges in parallel (MAF concurrent pattern, or parallel delegation), structured votes, **deterministic aggregation in code**, a barrier so no juror sees another's vote. A 2025 paper [search, not read] reports majority voting matching multi-agent debate on several benchmarks, so a jury can be the cheaper default.
- **YAML editor, then visual graph editor:** build the YAML (slugs are stable references, D-8) before the graph editor, so the editor is designed once the edges have their three meanings (delegate, handoff, group-chat membership). Evaluate `Microsoft.Agents.AI.Workflows.Declarative` before inventing a schema; compatibility with Quorum's tenants, scopes and permissions is unknown.
- **Guardrails:** handoff edges and tool arguments are boundaries to guard (see the guardrail discussion: checkpoints at tool arguments and results inside `ToolCallInvoker`).

## 9. Open questions to settle in the spec

- A: explicit `orchestration` property, or edges only? (Recommendation: edges only for handoff.)
- B: can the active agent delegate as a root? (Recommendation: yes.)
- C: how other agents' messages appear in the history of the active agent.
- Context overflow when the new agent has a smaller window: refuse or trim?
- Per-edge confirmation: default on or off?
- Default for `MaxHandoffsPerTurn`.
- Is a handoff reason (the model's text) shown to the user, stored, or only audited?
- Does D-32 (sub-agent text is not streamed) stay for delegation while handoff streams the active agent's text normally? (Expected: yes, since the active agent *is* the answer.)

## Sources

- MAF handoff sample, `Program.cs`: https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/03-workflows/Orchestration/Handoff/Program.cs
- MAF handoff sample, `AgentRegistry.cs`: https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/03-workflows/Orchestration/Handoff/AgentRegistry.cs
- MAF workflow samples: https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/03-workflows
- `Microsoft.Agents.AI.Workflows` on NuGet: https://www.nuget.org/packages/Microsoft.Agents.AI.Workflows
- `Microsoft.Agents.AI.Workflows.Declarative` on NuGet: https://www.nuget.org/packages/microsoft.agents.ai.workflows.declarative
- MAF orchestration patterns compared: https://startdebugging.net/2026/07/agent-framework-orchestration-patterns-compared/
- Swarms `debate_with_judge.py`: https://github.com/kyegomez/swarms/blob/master/swarms/structs/debate_with_judge.py
- Swarms repository: https://github.com/kyegomez/swarms
- Majority voting vs multi-agent debate (arXiv 2508.17536): https://arxiv.org/pdf/2508.17536v2
