# Broadcast servers — live state changes, push not poll

`Runtime/Core/Broadcast.cs`, owned by the main server (`OrderedBroadcast`,
`PriorityBroadcast`). Two independent servers push a `StateChangeEvent` to
subscribers **only on ticks where a head state actually changes** — the
ordered server first, then the priority server, both receiving the same
event object. No change, no calls: self-goto "stay" produces no event, and
paused/unbooted AIs produce none.

## The event

`StateChangeEvent`: `InstanceId`, `InstanceName` (`Module@GameObject`),
`FromState`, `ToState`, `Reason` (`"goto X"`, `"external command"`, or
`"query client #N"`), `Tick` (main-server frame), `Time` (provider clock).

## Subscribing

```csharp
MainServer main = MainServer.EnsureExists();

// 0-arg callback on one AI:
var s1 = new ActionSubscriber(ai.InstanceId, () => Debug.Log("changed!"));
// 1-string-arg callback (receives the NEW state name) on one AI:
var s2 = new ActionWithStateSubscriber(ai.InstanceId,
    state => Debug.Log("now in " + state));
// ...or listen to EVERY AI:
var s3 = new ActionSubscriber(StateSubscriber.AnyTarget, OnAnyChange);

main.OrderedBroadcast.Subscribe(s2);
main.OrderedBroadcast.Subscribe(s3);

// Unsubscribe by id. Each Subscribe adds one entry: subscribing the same
// object twice means it is called twice.
bool removed = main.OrderedBroadcast.Unsubscribe(s2.SubscriberId);
int total = main.OrderedBroadcast.SubscriberCount;
```

Subscriptions live in a per-server dictionary: target instance id →
subscriber list. `AnyTarget (-1)` subscribers are served after
target-specific ones. Subscriber ids come from a global counter (start at
1) and are unique across both servers.

## Ordered server: subscription order, every change

`OrderedBroadcastServer.Publish` serves the target's list in subscription
order, then the `AnyTarget` list in subscription order. Delivery uses a
snapshot, so subscribing or unsubscribing from inside a callback is safe —
with one asymmetry to know: an unsubscribe mid-publish does **not** stop
that in-flight call (the snapshot was already taken), while a mid-publish
subscribe takes effect from the next publish.

## Priority server: higher first, with verdicts

`PriorityBroadcastServer.Publish` merges the target + any-target lists,
sorts higher `Priority` first (ties keep subscription order via an internal
sequence), and after each subscriber reads its `NextVerdict` — once per
publish, then reset to `ServeRest`:

| Verdict | Meaning |
|---|---|
| `ServeRest` (A) | Lower priorities are served normally |
| `SkipRestThisTick` (B) | Lower priorities are skipped for this tick |
| `DeregisterLower` (C) | Strictly-lower priorities are de-registered (equal priorities survive) |

Before every call the server re-checks that the subscriber is still
registered, so mid-publish unsubscribes — including `DeregisterLower` —
take effect immediately (unlike the ordered server's snapshot).

`PriorityActionSubscriber(target, priority, callback)` accepts an `Action`
or `Action<string>`, but plain callbacks can't vote — they always behave as
A. To vote, subclass:

```csharp
public sealed class GuardAlert : PriorityStateSubscriber
{
    public GuardAlert(int target) : base(target, 10) { }
    public override void Notify(StateChangeEvent e)
    {
        Alarm.Raise(e.ToState);
        NextVerdict = e.ToState == "Chase"
            ? PriorityVerdict.SkipRestThisTick   // B: juniors stay quiet
            : PriorityVerdict.ServeRest;         // A: juniors informed
    }
}
main.PriorityBroadcast.Subscribe(new GuardAlert(ai.InstanceId));
```

## Rules & pitfalls

- **Publish trigger**: only real transitions. A tick with no change publishes
  nothing to anyone.
- **Exceptions**: subscriber callbacks are *not* wrapped — a throwing
  callback aborts that publish loop and the exception reaches
  `MainServer.Update`. Keep callbacks exception-free.
- **No dedup**: `Subscribe` always appends. Guard against double
  subscription on your side if you subscribe from repeated code paths.
- **Unsubscribe what you subscribe**: subscriber objects hold your
  delegates; dropping a listener without `Unsubscribe` leaks it until the
  server dies (i.e. app quit, since the server is `DontDestroyOnLoad`).
- **Cost**: publish allocates a snapshot per publish; verdicts and sorting
  are per-publish too. All trivial next to a frame — subscribe freely, but
  don't publish-generate heavy work inside callbacks.

## Testing it headless

`Sandbox/SmokeTest.cs` (`TestBroadcast`) covers all of the above without
Unity: ordered + any-target order, priority order with a late high-priority
subscriber, all three verdicts, and the subscriber count after
deregister-lower. Run with `dotnet run -- Vectors/`.
