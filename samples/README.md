# samples — ready-to-compile .fsm files

Write your own `.fsm` files **anywhere you like** — the compiler takes any
path. These three are complete, working AIs you can compile right now:

```sh
# from the repo root (or 'fsmc' after 'make install'):
./bin/fsmc samples/patrol.fsm      # -> samples/patrol.fsmb
./bin/fsmc samples/hunter.fsm -o /tmp/hunter.fsmb
./bin/fsmc samples/guard.fsm
```

| File | What it demonstrates |
|---|---|
| `patrol.fsm` | 4-state cycle, state-level temps, `goTo` / `stopMovement` claims |
| `hunter.fsm` | if/else-if/else, temps at all 3 depths, vector math, `&&` |
| `guard.fsm` | bool temps, else-if chain, Tier 2 mutation (`emit`, `setAnimation`, `setVisible`) |

Full syntax reference: [`../fsmc/LANGUAGE.md`](../fsmc/LANGUAGE.md).
