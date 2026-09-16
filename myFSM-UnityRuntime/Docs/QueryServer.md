# Query server — the service desk for external systems

`Runtime/Core/QueryServer.cs`, owned by the main server (`MainServer.Queries`,
ticked every frame after all AIs). External systems — UI, tools, game code —
use the AI system as a service through here: connect once, enqueue requests,
collect responses. Scheduling is OS-like: per-client caps, a ready set with a
round-robin quantum, congestion adaptation, and a starvation guard.

## Client lifecycle

```csharp
MainServer main = MainServer.EnsureExists();
QueryClient cli = main.ConnectExternal("ui");   // once per system

bool accepted = main.Queries.Enqueue(cli.ClientId, new ServerRequest {
    IsCommand = false, Code = (int)QueryCode.GetStats });
// accepted == false  =>  refused (cap/congestion); a failure response was
// ALSO pushed to you immediately. Always check the return value.

ServerResponse r;                               // usually next frame(s)
while (cli.TryTakeResponse(out r))
{
    if (!r.Ok) { Debug.LogError(r.Error); continue; }
    Debug.Log(r.Text);
    if (r.Lines != null) foreach (string l in r.Lines) Debug.Log("  " + l);
}
```

`QueryClient` exposes `ClientId`, `Name`, `InternalCount`, `PendingCount`,
`ResponseCount`, and `TryTakeResponse`. Every response echoes the request's
`Sequence`/`ClientId` so you can match them.

## Request pipeline: what a request goes through

Three structures, three caps. Requests always live *inside their client* —
the server queues only move client IDs around.

| Structure | Holds | Cap | Grows/shrinks |
|---|---|---|---|
| `QueryClient` (id + name) | `_internal` FIFO + `_pending` buffer (inbound), `_responses` (outbound) | **15** inbound (`internal + pending` combined); responses uncapped | No — fixed per client |
| Ready scheduler (`_ready`) | Client IDs currently being served | **N**, adaptive **8–20** (starts 15) | Yes — ±2 per tick |
| Long-term queue (`_longTerm`) | Unique waiting client IDs (+ a wait-start stamp each) | **64** clients | Yes — heads leave on refill, tails join on enqueue |

Client IDs are ints assigned 1, 2, 3… at `RegisterClient`; registration is
permanent (there is no unregister — a client object lives as long as the
server). A client sits in at most one place: it joins the long-term tail
only if it is in neither queue, and refill moves (not copies) it to ready.

### Submit: `Enqueue(clientId, req)` runs five gates

0. **Unknown id** → returns `false` silently (nothing queued, no response).
1. **New client + long-term full (64)** → stamps the request, pushes an
   immediate `server congested: long-term queue is full` failure, returns
   `false`. The request is never half-queued.
2. **Stamp** — the server fills `Sequence` (global 1, 2, 3…), `ClientId`,
   `EnqueuedTick`. You never set those.
3. **Near/far split** — if the sender *is* the client being served right now,
   the request lands in `_pending` and waits a tick (slices can never
   recurse); otherwise it joins `_internal` immediately and may run later in
   the *same* tick if your slice hasn't run yet.
4. **Client cap** — `internal + pending ≥ 15` → immediate
   `client queue is capped at 15; retry when drained` failure, `false`.
5. **Track** — if the client wasn't already ready or waiting, its id joins
   the long-term tail (uniqueness enforced).

Accepted (`true`) means: queued, and a response *will* come — success,
backend failure, or starvation eviction.

### Serve: one `Tick()` per frame, six phases

Short version (`Scheduling, step by step` below has the details):

1. **Flush** every client's `_pending` into its `_internal`.
2. **Adapt N**: congested (`waiting > N`) → N += 2 (max 20); idle (nobody
   waiting, ready < 15) → N −= 2 (min 8).
3. **Mark** the shrink tail: ready entries past N get at most M this round.
4. **Serve** each ready client once, round-robin from a rotating cursor, up
   to M (`RequestsPerClient`, 1–3, default 2) dequeues → `backend.Execute`
   → response pushed to that client. Empty clients pop; shrink-tail pops
   with work left rejoin the long-term tail with a fresh stamp.
5. **Refill** ready up to N from the long-term head (skipping gone/empty).
6. **Evict**: while congested, back-half waiters older than 300 ticks fail
   *all* their requests and leave the queue.

```
you ──Enqueue──▶ [gates 0–5] ──▶ client._internal ──▶ long-term tail (if new)
                                                          │ Tick: refill
                                                          ▼
                                              ready set (≤ N, round-robin, ≤ M each)
                                                          │ Tick: serve → backend.Execute
                                                          ▼
                                              client._responses ──TryTakeResponse──▶ you
```

### Collect: `TryTakeResponse`

Responses are pushed in the order requests are *served*, which is FIFO per
client — except the near/far split can let a later "far" request overtake
an earlier deferred "near" one. Always match responses with `Sequence`.

## Request / response fields

`ServerRequest`: `IsCommand`, `Code`, `TargetInstanceId` (default -1, and
-1 also means "all instances" where that applies), `TargetAsset`,
`StringArg`, `ValueArg` (`FsmValue`), `IntArg`, `BytesArg`. The server fills
`Sequence`, `ClientId`, `EnqueuedTick` — you don't set those.

`ServerResponse`: `Ok`, `Error` (set when `!Ok`), `Text` (short human
summary, always set on success), `Value` (`FsmValue`, only `GetVariable`
sets it), `Lines` (`List<string>`, set by the listing queries, else null).

## Queries (`IsCommand = false`, never mutate)

| Code | Name | Parameters | Result |
|---|---|---|---|
| 1 | `GetInstanceState` | `TargetInstanceId` | `Text` = head state name; `Lines[0]` = full instance row (below) |
| 2 | `ListInstances` | — | `Text` = `"N instance(s)"`; one row per AI |
| 3 | `GetAssetInfo` | `TargetAsset` = module name | `Text` = asset name; version/entry/instances line, `states: …` line, one `slot {n} {type} {name}` row per binding slot |
| 4 | `ListAssets` | — | One row per module: `name v0.5 \| states N \| instances M` |
| 5 | `GetStateHistory` | `TargetInstanceId` (-1 = all), `IntArg` = max rows (default 20, cap 200) | **Newest-first** rows: `t{tick} {time} #{id} 'from' -> 'to' (reason)` |
| 6 | `GetVariable` | `TargetInstanceId` + `StringArg` = name | `Value` = raw `FsmValue`, `Text` = printed form; reads consts, runtime slots, live temps |
| 7 | `GetStats` | — | Totals + scheduler + subscriber counts, then one instance row per AI (below) |
| 8 | `GetEmits` | `IntArg` = max rows (default 20, cap 200) | **Newest-first** rows: `t{tick} {time} #{id} event {id}` |

Instance row format (queries 1, 2, 7):

```
id | gameObject/path | asset | state | ticks N | transitions N | calls N [| paused] [| suspended]
```

`GetStats` first line:

```
ticks T | transitions T | instances N | ready R/N M=M | long-term L | ordered-subs O | priority-subs P
```

## Commands (`IsCommand = true`, validated before applying)

| Code | Name | Parameters | Effect |
|---|---|---|---|
| 101 | `TransitionTo` | `TargetInstanceId` + `StringArg` = state name | Honored at the next tick boundary, before Update; response: `commanded '…' (honored next tick)` |
| 102 | `SetVariable` | `TargetInstanceId` + `StringArg` + `ValueArg` | Runtime slots only, tag-strict; consts/temps rejected |
| 103 | `Emit` | `TargetInstanceId` + `IntArg` = event id | Records a DB emit entry exactly like DSL `emit()`; visible via `GetEmits` |
| 104 | `PauseAI` | `TargetInstanceId` | AI skips movement + ticks (still listed, `paused`) |
| 105 | `ResumeAI` | `TargetInstanceId` | Clears pause |
| 106 | `ReloadModule` | `TargetAsset` + `BytesArg` = new `.fsmb` | Schema-checked hot-reload (below) |

`ReloadModule` steps: read + full-validate the bytes → require the same
state count with identical names in order → require the same runtime slots
(slot/tag/name per index) → reboot every instance of the asset (ids stable,
binding journal replayed, entry state re-entered) → re-register the asset
(keeping `InstanceCount`). Per-instance failures are reported together
(`reloaded N, failed: #2: …`); schema problems fail before anything moves.

## Scheduling, step by step

Each `Queries.Tick()` (once per frame, after the AIs):

1. **Flush**: every client's deferred ("near") requests join its queue.
2. **Adapt N**: if congested (more waiting than N) and N < 20, N += 2; if
   nobody waits and the ready set is small and N > 8, N −= 2.
3. **Mark the shrink tail**: when ready shrinks, the last (ready − N)
   clients get at most M requests this round.
4. **Serve**: every ready client once, round-robin from a rotating cursor,
   up to M requests each. Finished (empty) clients pop; shrink-tail clients
   pop and — if they still hold work — rejoin the long-term tail.
5. **Refill**: pull long-term head clients into the ready set up to N
   (skipping gone/empty ones).
6. **Starvation guard**: under congestion, back-half long-term clients
   waiting over 300 ticks are evicted — all their requests fail fast.

## Caps & tuning

| Knob | Default | Range | Meaning |
|---|---|---|---|
| Client queue | 15 | fixed | Internal + pending requests per client; over-cap enqueues fail |
| N (ready target) | 15 | 8–20, ±2/tick | Ready-set size; grows under congestion, shrinks when idle |
| M (`RequestsPerClient`) | 2 | 1–3, settable | Max requests per ready client per tick |
| Long-term queue | 64 | fixed | Unique waiting clients; new clients refused past it |
| Starvation eviction | 300 ticks | fixed | Back-half waiters under congestion fail fast past it |

**Near/far rule**: a request enqueued from *inside your own slice*
(re-entrantly, from a backend `Execute` running for you) waits a tick —
slices can never recurse. Any other enqueue may run later in the same tick.

Backend contract (`IQueryBackend.Execute`): runs synchronously inside the
tick; return a response (null becomes `backend returned nothing`);
exceptions are caught and fail just that request (`backend error: …`).

## Error catalog (all `Error` strings)

- `unknown query code N` / `unknown command code N`
- `unknown instance N`, `unknown asset '…'`, `unknown state '…'`
- `no variable '…' (alive) on instance N`, `no writable runtime variable '…'`,
  `cannot assign {Kind} to '…' ({type})`
- `client queue is capped at 15; retry when drained`
- `server congested: long-term queue is full`
- `evicted: server congested, request starved`
- `missing asset name`, `missing module bytes`
- `schema mismatch: state count changed`, `schema mismatch: state '…' changed`,
  `schema mismatch: runtime slots changed`,
  `schema mismatch: slot N changed (rebind by restarting instead)`
