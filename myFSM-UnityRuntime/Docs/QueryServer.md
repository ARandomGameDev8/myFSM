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
