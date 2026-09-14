# samples — ready-to-compile .fsm files

Write your own `.fsm` files **anywhere you like** — the compiler takes any
path. These three are complete, working AIs you can compile right now:

```sh
# from the repo root (or 'fsmc' after 'make install'):
./bin/fsmc samples/patrol.fsm      # -> samples/patrol.fsmb
./bin/fsmc samples/hunter.fsm -o /tmp/hunter.fsmb
./bin/fsmc samples/guard.fsm
```

Each binary has a committed human-readable twin (a `.fsmd` disassembly —
header, section map, decoded values, resolved names, per-state instruction
streams, AST trees, transition table). Regenerate or create your own:

```sh
./bin/fsmc -d samples/guard.fsmb             # -> samples/guard.fsmd
./bin/fsmc -d samples/guard.fsmb -o -        # print to stdout
```

All three split every `Actions` block into the two mandatory phases: `Start{}`
runs once when the state is entered, `Update{}` runs every tick
([`../fsmc/LANGUAGE.md`](../fsmc/LANGUAGE.md) §8).

| File | What it demonstrates |
|---|---|
| `patrol.fsm` | 4-state cycle, `Start{}` issuing the move order once + an empty `Update{}`, state-level temps, Tier 3 `goTo` / `stopMovement` |
| `hunter.fsm` | if/else-if/else, block-scoped temps (state body / `Actions` body / else body), an `Actions`-body temp consumed by `Start{}`, vector math, `&&` |
| `guard.fsm` | bool temps, else-if chain, `Actions`-body temps queried by `Update{}`, a `Start{}` that resets the guard's visuals on entry, a teardown state whose whole job is one `Start{}` block, Tier 2 mutation (`emit`, `setAnimation`, `setVisible`) |

Full syntax reference: [`../fsmc/LANGUAGE.md`](../fsmc/LANGUAGE.md).
