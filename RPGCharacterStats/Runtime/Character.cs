// RPG Character & Stats System — the abstract Character (section 4.4).
//
// The runtime character a player sees: identity, the two stats containers,
// and the physics/collision/movement components the spawn pipeline attached
// (which of them are non-null depends on dimension + kind + physics mode —
// see the component matrix in section 2.5).
//
// InitializeStats wires the whole section 19 event flow: CharacterStats.Set*
// → GameplayStats.Recalculate → OnStatChanged / OnRecalculated, with gameplay
// outputs mirrored into the blackboard so FSMs and UI read derived stats
// without reaching into this class.

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace RPGCharacterStats
{
    public abstract class Character : MonoBehaviour
    {
        [Header("Identity")]
        public string characterID;
        public string characterName;
        [TextArea(3, 6)] public string description;

        [Header("Configuration (read-only after spawn)")]
        public CharacterDimension dimension;
        public PhysicsMode physicsMode;

        [Header("Stats (populated by the spawn pipeline)")]
        public CharacterStats characterStats = new CharacterStats();
        public GameplayStats gameplayStats = new GameplayStats();

        [Header("Engine components (dimension/kind dependent)")]
        public CharacterController controller;   // 3D non-physics Player only
        public Animator animator;
        public Rigidbody rigidbody3D;            // set when dimension == ThreeD
        public Rigidbody2D rigidbody2D;          // set when dimension == TwoD
        public Collider collider3D;              // set when dimension == ThreeD
        public Collider2D collider2D;            // set when dimension == TwoD
        public HealthBar healthBar;

        /// <summary>The attached AIInstance subclass component (NPC only), as
        /// the loose Component reference — the RPG system never names the
        /// project's AI type, it only checks the "AIInstance" contract.</summary>
        public Component attachedAI;

        public abstract CharacterKind Kind { get; }

        /// <summary>Fired for every changed stat, after gameplay stats
        /// recalculated (so derived values are fresh when listeners run).</summary>
        public event Action<Character, StatField> OnStatChanged;

        public bool StatsInitialized { get; private set; }

        /// <summary>
        /// Wire the stats pipeline: apply the schema, the definition's value
        /// overrides, then compile the .gameplaystat text into the recalc
        /// server. Throws with every formula error joined when the formula
        /// set doesn't validate — build-time validation (section 7.5) should
        /// have caught these, so a runtime failure is a real bug upstream.
        /// </summary>
        public void InitializeStats(string charStatText, List<StatValueOverride> overrides,
            string gameplayStatText)
        {
            characterStats.SetSchemaText(charStatText);
            characterStats.Recalculate();
            characterStats.ApplyOverrides(overrides);

            if (!string.IsNullOrEmpty(gameplayStatText))
            {
                List<ParsedFormula> parsed = GameplayStatFormat.Parse(gameplayStatText);

                // Names of all formulas in the document, for cross-formula reads.
                Dictionary<string, StatType> gameplayStatTypes = new Dictionary<string, StatType>();
                for (int i = 0; i < parsed.Count; i++)
                {
                    gameplayStatTypes[parsed[i].Name] = parsed[i].ReturnType;
                }

                List<FormulaCheckResult> checkedFormulas = new List<FormulaCheckResult>(parsed.Count);
                StringBuilder problems = new StringBuilder();

                for (int i = 0; i < parsed.Count; i++)
                {
                    FormulaCheckResult r = FormulaChecker.Check(parsed[i], characterStats.Schema, gameplayStats.server.board, gameplayStatTypes);
                    checkedFormulas.Add(r);
                    for (int e = 0; e < r.Errors.Count; e++)
                    {
                        problems.Append(r.Formula.Name).Append(": ").AppendLine(r.Errors[e].ToString());
                    }
                }

                if (problems.Length > 0)
                    throw new InvalidOperationException("gameplay stat errors:\n" + problems);

                gameplayStats.Bind(characterStats, checkedFormulas);

                // Definition overrides that name no character stat are constant
                // data for the formulas ("Multiplier": 1.5) — hand them to the
                // server AFTER Bind (which resets the table), then refresh so
                // the first visible outputs already include them.
                if (overrides != null)
                {
                    bool seeded = false;
                    for (int i = 0; i < overrides.Count; i++)
                    {
                        StatValueOverride o = overrides[i];
                        if (o == null || string.IsNullOrEmpty(o.statName)) continue;
                        if (characterStats.Schema.Find(o.statName) != null) continue; // a real stat, already applied
                        switch (o.type)
                        {
                            case StatType.Int:
                                gameplayStats.server.constantDefaults[o.statName] = o.intValue;
                                seeded = true;
                                break;
                            case StatType.Bool:
                                gameplayStats.server.constantDefaults[o.statName] = o.boolValue ? 1f : 0f;
                                seeded = true;
                                break;
                            default:
                                gameplayStats.server.constantDefaults[o.statName] = o.floatValue;
                                seeded = true;
                                break;
                        }
                    }
                    if (seeded) gameplayStats.Recalculate();
                }
            }

            // Mirror gameplay outputs into the blackboard after every pass —
            // the FSM blackboard leg of section 19's fan-out.
            gameplayStats.server.OnRecalculated += SyncBlackboard;
            SyncBlackboard();

            // HealthBar hookup (section 18: bars react, never calculate).
            if (healthBar != null)
            {
                StatField hp = characterStats.Find("Health");
                if (hp != null) healthBar.Watch(gameplayStats, characterStats);
            }

            StatsInitialized = true;
        }

        /// <summary>Copy every gameplay stat's current output into the
        /// blackboard under the same name (declaring it on first use).</summary>
        public void SyncBlackboard()
        {
            if (gameplayStats == null || gameplayStats.server == null) return;
            for (int i = 0; i < gameplayStats.server.clients.Count; i++)
            {
                GameplayStatClient c = gameplayStats.server.clients[i];
                BlackboardVariable v = gameplayStats.server.board.Declare(c.name, c.returnType);
                v.Numeric = c.output.NumericValue;
            }
        }

        protected void NotifyStatChanged(StatField f)
        {
            Action<Character, StatField> handler = OnStatChanged;
            if (handler != null) handler(this, f);
        }

        public override string ToString()
        {
            return (characterName ?? "Character") + " [" + Kind + " " +
                (dimension == CharacterDimension.TwoD ? "2D" : "3D") + " " +
                (physicsMode == PhysicsMode.PhysicsBased ? "physics" : "non-physics") + "]";
        }
    }
}
