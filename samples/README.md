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

| File | What it demonstrates |
|---|---|
| `patrol.fsm` | 4-state cycle, state-level temps, `goTo` / `stopMovement` claims |
| `hunter.fsm` | if/else-if/else, block-scoped temps (state body / Actions / else body), vector math, `&&` |
| `guard.fsm` | bool temps, else-if chain, Tier 2 mutation (`emit`, `setAnimation`, `setVisible`) |

Full syntax reference: [`../fsmc/LANGUAGE.md`](../fsmc/LANGUAGE.md).
