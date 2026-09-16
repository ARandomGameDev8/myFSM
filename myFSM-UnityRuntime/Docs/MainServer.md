# Main server — registry, tick loop, DB owner, query backend

`Runtime/Unity/MainServer.cs`. The main server is the root of the runtime:
it owns the AI registry, the internal DB, the query server and both
broadcast servers, and executes every query and command. It never ticks AIs
— each AI ticks itself in its own `Update()`. There is exactly one server
at runtime.

## Lifecycle & access

```csharp
MainServer main = MainServer.EnsureExists(); // create-or-find, never null
MainServer same = MainServer.Instance;       // set by Awake / EnsureExists
```

- `EnsureExists()` returns the instance, finds a scene-placed one, or
  creates `GameObject("MyFSM MainServer")` + component.
- `Awake()` enforces the singleton (duplicates self-destruct), builds the
  `QueryServer`, and calls `DontDestroyOnLoad` — the server (and its DB)
  survives scene loads.
- `TimeProvider` (`ITimeProvider`, default `UnityTimeProvider` wrapping
  `Time.time`/`Time.deltaTime`) is a settable field, so tests drive time by
  hand.

## Registry

- `AllocInstanceId()` — hands out 1, 2, 3, … (id 0 is never allocated;
  instance ids stay stable across hot reloads).
- `RegisterAsset(name, module, bytes)` — snapshots an asset record: state
  names, entry state (from the STATE token flags), runtime slot schema,
  FNV-1a hash of the bytes, load tick. Re-registering (after reload) keeps
  the live `InstanceCount`.
- `Register(ai)` / `Unregister(ai)` — called by `AIInstance` on boot /
  destroy / reboot. Registration snapshots an instance record (id, object
  name + full hierarchy path, asset, current state, slot counts, …).
- `TryGetAI(id, out ai)` — lookup for queries/commands/game code.
- `ConnectExternal(name)` — shorthand for `Queries.RegisterClient(name)`.

## Frame tick

Each AI ticks itself in its own `Update()` (`AIInstance.Update` →
`TickInternal()`), in Unity's own component order — not registration order.
Use Unity's Script Execution Order settings if some AI must tick before
another. One AI's frame:

1. Skip if unbooted; if `Paused`, record a paused tick-sample and skip (soft
   pause — the component still runs, it just idles; `enabled = false` is
   the hard pause: Unity never calls `Update` at all).
2. `Movement.Advance` (fresh positions, even while suspended), then
   `Execution.Tick` (suspension → external transition → Update round →
   Traversals round → transition).
3. If the head state changed: append a `StateChangeEntry` to the DB
   timetable, then publish one `StateChangeEvent` to the ordered server and
   then the priority server; record the AI's tick-sample (`OnInstanceTick`).

The server's own frame work is one private `LateUpdate()`, which Unity runs
after *every* AI's `Update()`: bump `Db.TotalTicks`, then `Queries.Tick()`
serves external clients.

So queries always observe post-tick state, and broadcasts always fire before
that frame's query responses are produced — the same guarantee a central
loop gave, while the server's own frame work never visits AIs at all.

## Query backend (`IQueryBackend.Execute`)

`MainServer` implements the query server's backend: `Execute` routes
`IsCommand=false` to the 8 query handlers (all read DB snapshots / live AI
state, never mutate) and `IsCommand=true` to the 6 command handlers (all
validate before applying). Full semantics live in
[`Docs/QueryServer.md`](QueryServer.md); the routing guarantees are:

- Unknown codes fail with `unknown query/command code N` (never throw).
- A null request fails; a backend exception is caught by the scheduler and
  fails just that request (`backend error: …`).
- `TransitionTo` is validated then honored at the next tick boundary.
- `SetVariable` delegates to `AIInstance.TrySetVariable` (runtime-only,
  tag-strict) and surfaces its error string.
- `ReloadModule` reads + validates the new bytes, schema-checks them
  against the stored asset (same state count/names/order, same runtime
  slots), then `RebootWithBytes` every instance of the asset (ids stable,
  binding journal replayed) and re-registers the asset. Per-instance
  failures are collected and reported together.

## Broadcast & query servers

Owned as readonly fields, ready immediately after `Awake`/`EnsureExists`:

```csharp
main.OrderedBroadcast.Subscribe(new ActionSubscriber(id, OnChange));
main.PriorityBroadcast.Subscribe(new GuardAlert(id));
QueryClient cli = main.ConnectExternal("tools");
main.Queries.Enqueue(cli.ClientId, req);
```

Details: [`Docs/BroadcastServers.md`](BroadcastServers.md) and
[`Docs/QueryServer.md`](QueryServer.md).

## Public API cheat sheet

| Member | Kind | Purpose |
|---|---|---|
| `Instance` / `EnsureExists()` | static | Singleton access / create-or-find |
| `Db` | field | `MainDatabase` (see main README's DB section) |
| `OrderedBroadcast` / `PriorityBroadcast` | fields | Live state-change servers |
| `Queries` | property | `QueryServer` (set in `Awake`) |
| `TimeProvider` | field | Clock (settable for tests) |
| `AllocInstanceId()` | method | Fresh instance id |
| `RegisterAsset/Register/Unregister` | methods | Registry writers (AI boot/destroy call these) |
| `TryGetAI` | method | Id → AI lookup |
| `ConnectExternal` | method | Register a query client |

## Driving it from game code

```csharp
MainServer main = MainServer.EnsureExists();
QueryClient cli = main.ConnectExternal("game");

// Command a transition, then confirm it happened via history.
main.Queries.Enqueue(cli.ClientId, new ServerRequest {
    IsCommand = true, Code = (int)CommandCode.TransitionTo,
    TargetInstanceId = guard.InstanceId, StringArg = GuardAI.State_Chase });

// ...later, after draining the command's "commanded …" response...
main.Queries.Enqueue(cli.ClientId, new ServerRequest {
    IsCommand = false, Code = (int)QueryCode.GetStateHistory,
    TargetInstanceId = guard.InstanceId, IntArg = 5 });
// -> Lines[0] == "t123 45.60 #2 'Guard' -> 'Chase' (query client #1)"
```
