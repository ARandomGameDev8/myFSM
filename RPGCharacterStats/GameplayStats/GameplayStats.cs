// RPG Character & Stats System — GameplayStats runtime (sections 12 and 19).
//
// GameplayStatFormula (data) is compiled once into GameplayStatClient
// (strategy, section 20.3): name, return type, delegate, output field. The
// GameplayStatsServer holds the clients in TOPOLOGICAL order — a stat that
// reads another stat's output is evaluated after it — and Recalculate()
// walks that order, executing each delegate and writing its result.
//
// A Set on CharacterStats fires OnStatChanged → server.Recalculate() →
// OnStatChanged/OnRecalculated: the observer flow of section 19. Loops in the
// dependency graph are a build error (section 11: circular dependency), so
// the walk always terminates.

using System;
using System.Collections.Generic;

namespace RPGCharacterStats
{
    /// <summary>Section 12: one compiled calculation. The delegate is the
    /// strategy; the output field is what listeners read.</summary>
    public class GameplayStatClient
    {
        public string name;
        public StatType returnType;

        public Func<GameplayEvalContext, float> callback;
        public StatField output;

        /// <summary>Names of other gameplay stats this one reads (from the
        /// checker's GameplayStatInput classifications) — the dependency edges.</summary>
        public List<string> dependencies = new List<string>();

        public void Execute(GameplayEvalContext ctx)
        {
            float result = callback != null ? callback(ctx) : 0f;
            output.SetFromNumber(result);
        }
    }

    /// <summary>Everything a compiled delegate may read at evaluation time.
    /// One context object is reused across clients per recalculation.</summary>
    public class GameplayEvalContext
    {
        public CharacterStats CharacterStats;
        public Blackboard Board;
        public GameplayStatsServer Server;

        public float ReadStat(string name)
        {
            StatField f = CharacterStats != null ? CharacterStats.Find(name) : null;
            return f != null ? f.NumericValue : 0f;
        }

        public float ReadGameplayStat(string name)
        {
            GameplayStatClient other = Server != null ? Server.Find(name) : null;
            return other != null ? other.output.NumericValue : 0f;
        }

        public float ReadBlackboard(string key)
        {
            BlackboardVariable v = Board != null ? Board.Find(key) : null;
            if (v == null)
            {
                // A blackboard input with no variable yet reads zero — the
                // runtime self-heals when the variable is declared.
                return 0f;
            }
            return v.Numeric;
        }

        public float ReadConstant(string name)
        {
            return Constants != null && Constants.TryGetValue(name, out float v) ? v : 0f;
        }

        /// <summary>Secondary constants supplied per character definition.</summary>
        public Dictionary<string, float> Constants;
    }

    /// <summary>Section 12: the server. It never contains formulas — only
    /// compiled clients in evaluation order.</summary>
    public class GameplayStatsServer
    {
        public readonly List<GameplayStatClient> clients = new List<GameplayStatClient>();
        public Blackboard board = new Blackboard();

        /// <summary>The CharacterStats the clients read their inputs from;
        /// GameplayStats.Bind sets this before the first recalculation.</summary>
        public CharacterStats characterStats;

        /// <summary>Values for secondary parameters that match neither a
        /// character stat nor a blackboard variable — per-character constant
        /// data (a multiplier, an offset), supplied by the definition.</summary>
        public readonly Dictionary<string, float> constantDefaults = new Dictionary<string, float>();

        /// <summary>Fired after any single client wrote a new output value.</summary>
        public event Action<StatField> OnStatChanged;

        /// <summary>Fired at the end of each full Recalculate pass.</summary>
        public event Action OnRecalculated;

        public GameplayStatClient Find(string name)
        {
            for (int i = 0; i < clients.Count; i++)
            {
                if (clients[i].name == name) return clients[i];
            }
            return null;
        }

        /// <summary>Run every client in topological order (section 12).</summary>
        public void Recalculate()
        {
            if (board != null) board.UpdateExternalBindings();

            for (int i = 0; i < clients.Count; i++)
            {
                GameplayStatClient c = clients[i];
                float before = c.output.NumericValue;
                c.Execute(BuildContext());
                if (c.output.NumericValue != before)
                {
                    Action<StatField> handler = OnStatChanged;
                    if (handler != null) handler(c.output);
                }
            }

            Action done = OnRecalculated;
            if (done != null) done();
        }

        private GameplayEvalContext _ctx = new GameplayEvalContext();

        private GameplayEvalContext BuildContext()
        {
            _ctx.Server = this;
            _ctx.Board = board;
            _ctx.CharacterStats = characterStats;
            _ctx.Constants = constantDefaults;
            return _ctx;
        }

        // ---- compilation pipeline (section 11) ----

        /// <summary>Compile a checked formula set into clients, ordered by the
        /// dependency graph. Throws on cycles. Returns the evaluation order.</summary>
        public static List<GameplayStatClient> Compile(
            List<FormulaCheckResult> checkedFormulas, StatSchema charStats, Blackboard board)
        {
            List<ParsedFormula> parsed = new List<ParsedFormula>(checkedFormulas.Count);
            for (int i = 0; i < checkedFormulas.Count; i++) parsed.Add(checkedFormulas[i].Formula);

            List<ParsedFormula> ordered = TopologicalSort(parsed, checkedFormulas);
            List<GameplayStatClient> clients = new List<GameplayStatClient>(ordered.Count);

            for (int i = 0; i < ordered.Count; i++)
            {
                ParsedFormula f = ordered[i];
                CompiledFormula compiled = FormulaCompiler.Compile(f);

                GameplayStatClient client = new GameplayStatClient();
                client.name = f.Name;
                client.returnType = f.ReturnType;
                client.output = new StatField(f.Name, f.ReturnType);
                client.dependencies = DependenciesOf(f, checkedFormulas);

                FormulaParam primary = f.Primary;
                int paramCount = f.Params.Count;
                List<SecondaryInput> secondaries = FindSignature(checkedFormulas, f.Name).secondaries;

                client.callback = delegate (GameplayEvalContext ctx)
                {
                    float[] args = new float[paramCount];
                    args[0] = ctx.ReadStat(primary.Name); // primary always rides CharacterStats
                    for (int s = 0; s < secondaries.Count && s < paramCount - 1; s++)
                    {
                        SecondaryInput input = secondaries[s];
                        switch (input.Kind)
                        {
                            case SecondaryInputKind.CharacterStat:
                                args[s + 1] = ctx.ReadStat(input.name);
                                break;
                            case SecondaryInputKind.GameplayStat:
                                args[s + 1] = ctx.ReadGameplayStat(input.name);
                                break;
                            case SecondaryInputKind.Blackboard:
                                args[s + 1] = ctx.ReadBlackboard(input.name);
                                break;
                            case SecondaryInputKind.Constant:
                                // Per-character constant first (definition- or
                                // Bind-supplied), then the formula's own value.
                                float constantValue;
                                if (ctx.Constants != null && ctx.Constants.TryGetValue(input.name, out constantValue))
                                {
                                    args[s + 1] = constantValue;
                                }
                                else
                                {
                                    ConstantInput constant = input as ConstantInput;
                                    args[s + 1] = constant != null ? constant.value : 0f;
                                }
                                break;
                            default:
                                args[s + 1] = ctx.ReadConstant(input.name);
                                break;
                        }
                    }
                    return compiled.Evaluate(args);
                };

                clients.Add(client);
            }
            return clients;
        }

        private static FormulaSignature FindSignature(List<FormulaCheckResult> results, string name)
        {
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i].Formula.Name == name) return results[i].Signature;
            }
            return new FormulaSignature();
        }

        private static List<string> DependenciesOf(ParsedFormula f, List<FormulaCheckResult> results)
        {
            List<string> deps = new List<string>();
            FormulaSignature sig = FindSignature(results, f.Name);
            for (int i = 0; i < sig.secondaries.Count; i++)
            {
                GameplayStatInput gsi = sig.secondaries[i] as GameplayStatInput;
                if (gsi != null) deps.Add(gsi.gameplayStatName);
            }
            return deps;
        }

        /// <summary>Kahn's algorithm over the gameplay-stat→gameplay-stat
        /// edges. Unknown dependencies are ignored (the checker already
        /// flagged them); cycles throw — section 11's "circular dependency".</summary>
        public static List<ParsedFormula> TopologicalSort(
            List<ParsedFormula> formulas, List<FormulaCheckResult> results)
        {
            Dictionary<string, ParsedFormula> byName = new Dictionary<string, ParsedFormula>();
            for (int i = 0; i < formulas.Count; i++)
            {
                if (!string.IsNullOrEmpty(formulas[i].Name)) byName[formulas[i].Name] = formulas[i];
            }

            Dictionary<string, int> pending = new Dictionary<string, int>();
            Dictionary<string, List<string>> edges = new Dictionary<string, List<string>>();
            foreach (KeyValuePair<string, ParsedFormula> kv in byName)
            {
                List<string> deps = DependenciesOf(kv.Value, results);
                List<string> clean = new List<string>();
                for (int i = 0; i < deps.Count; i++)
                {
                    if (byName.ContainsKey(deps[i])) clean.Add(deps[i]);
                }
                pending[kv.Key] = clean.Count;
                edges[kv.Key] = clean;
            }

            List<ParsedFormula> ordered = new List<ParsedFormula>(formulas.Count);
            bool progress = true;
            while (ordered.Count < byName.Count && progress)
            {
                progress = false;
                foreach (KeyValuePair<string, ParsedFormula> kv in byName)
                {
                    if (!pending.ContainsKey(kv.Key) || pending[kv.Key] != 0) continue;

                    // Ready: consume it, release its dependents.
                    ordered.Add(kv.Value);
                    pending[kv.Key] = -1;
                    foreach (KeyValuePair<string, ParsedFormula> other in byName)
                    {
                        if (pending.ContainsKey(other.Key) && pending[other.Key] > 0
                            && edges[other.Key].Contains(kv.Key))
                        {
                            pending[other.Key]--;
                        }
                    }
                    progress = true;
                }
            }

            if (ordered.Count != byName.Count)
            {
                List<string> stuck = new List<string>();
                foreach (KeyValuePair<string, int> kv in pending)
                {
                    if (kv.Value > 0) stuck.Add(kv.Key);
                }
                throw new InvalidOperationException(
                    "circular dependency between gameplay stats: " + string.Join(", ", stuck.ToArray()));
            }

            // Stable order: keep the document order for independents.
            List<ParsedFormula> stable = new List<ParsedFormula>(formulas.Count);
            for (int i = 0; i < formulas.Count; i++)
            {
                if (ordered.Contains(formulas[i])) stable.Add(formulas[i]);
            }
            return stable;
        }
    }

    /// <summary>The Stats subclass a Character carries (section 4.4): a live,
    /// recalculating view over the character's own CharacterStats.</summary>
    public class GameplayStats : Stats
    {
        public GameplayStatsServer server = new GameplayStatsServer();

        public CharacterStats Source
        {
            get { return _source; }
        }

        private CharacterStats _source;

        public void Bind(CharacterStats source, List<FormulaCheckResult> checkedFormulas)
        {
            if (_source != null) _source.OnStatChanged -= OnSourceChanged;

            _source = source;
            server.characterStats = source; // the clients read THEIR inputs here
            server.clients.Clear();
            server.clients.AddRange(GameplayStatsServer.Compile(checkedFormulas, source != null ? source.Schema : null, server.board));

            // Section 19's event flow: CharacterStats.Set → Recalculate →
            // OnStatChanged/OnRecalculated. Subscribing here is what makes a
            // level-up cascade without any manual recalculation.
            if (_source != null) _source.OnStatChanged += OnSourceChanged;

            // Constant secondaries read zero until a definition (or test)
            // supplies the per-character value through server.constantDefaults
            // — there is no universal identity value: an additive offset wants
            // 0, a multiplicative factor wants 1. Zero is the safer default.
            server.constantDefaults.Clear();

            Recalculate();
        }

        private void OnSourceChanged(StatField f)
        {
            Recalculate();
        }

        public override void Recalculate()
        {
            server.Recalculate();
        }

        public override float GetFloat(string name)
        {
            GameplayStatClient c = server.Find(name);
            if (c == null) throw new KeyNotFoundException("no gameplay stat named \"" + name + "\"");
            return c.output.NumericValue;
        }

        public override int GetInt(string name)
        {
            return (int)GetFloat(name);
        }

        public override bool GetBool(string name)
        {
            GameplayStatClient c = server.Find(name);
            if (c == null) throw new KeyNotFoundException("no gameplay stat named \"" + name + "\"");
            return c.output.BoolValue;
        }
    }
}
