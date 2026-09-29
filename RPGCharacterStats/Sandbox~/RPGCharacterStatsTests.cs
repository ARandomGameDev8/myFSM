// RPG Character & Stats System — headless smoke tests (dotnet run).
//
// Exercises the whole package without Unity: the .charstat round-trip, the
// formula DSL (lex, parse, check, compile, dependency order, runtime recalc,
// blackboard inputs), the registry and its tag allocation, the section 2.5
// spawn component matrix for all four dimension/kind/physics combinations,
// the AIInstance contract, and the stat-bar animation. Exit code 0 = green.

using System;
using System.Collections.Generic;
using UnityEngine;
using RPGCharacterStats;

namespace RPGCharacterStatsTests
{
    public static class RPGCharacterStatsTests
    {
        private static int _passed;
        private static int _failed;
        private static string _section = "";

        public static int RunAll()
        {
            Section("charstat format");
            CharStatRoundTrip();
            CharStatParseErrors();

            Section("formula language");
            LexerBasics();
            ParserSignatures();
            CheckerValidation();
            CompileAndEvaluate();
            DependencyOrder();
            CircularDependencyRejected();
            Builtins();

            Section("blackboard");
            BlackboardSetGet();
            BlackboardBinding();
            BlackboardDrivenFormula();

            Section("character stats");
            CharacterStatsClamping();
            CharacterStatsEvents();

            Section("registry");
            RegistryTags();
            RegistrySearch();

            Section("spawn pipeline (section 2.5 matrix)");
            Spawn3DPhysicsNPC();
            Spawn3DNonPhysicsPlayer();
            Spawn2DPhysicsPlayer();
            Spawn2DNonPhysicsPlayer();
            Spawn3DNonPhysicsNPC();
            SpawnInitializesStats();
            SpawnWithoutAIWarnsButSpawns();
            DefinitionToCharacterConvention();

            Section("AIInstance contract");
            ContractAcceptsValid();
            ContractRejectsNonPartial();
            ContractRejectsWrongBase();

            Section("stat bars");
            StatBarTraceAndFill();

            Console.WriteLine();
            Console.WriteLine(_passed + " passed, " + _failed + " failed");
            return _failed == 0 ? 0 : 1;
        }

        private static void Section(string name)
        {
            _section = name;
            Console.WriteLine();
            Console.WriteLine("== " + name + " ==");
        }

        private static void Ok(string what)
        {
            _passed++;
            Console.WriteLine("  ok  " + what);
        }

        private static void Fail(string what, string detail)
        {
            _failed++;
            Console.WriteLine("FAIL  " + what + " — " + detail);
        }

        private static void Expect(bool condition, string what)
        {
            if (condition) Ok(what);
            else Fail(what, "condition was false");
        }

        private static void ExpectNear(float actual, float expected, float epsilon, string what)
        {
            if (Math.Abs(actual - expected) <= epsilon) Ok(what);
            else Fail(what, "expected " + expected + " ± " + epsilon + ", got " + actual);
        }

        private static void ExpectThrows<T>(Action action, string what) where T : Exception
        {
            try
            {
                action();
                Fail(what, "no exception was thrown");
            }
            catch (T)
            {
                Ok(what);
            }
            catch (Exception ex)
            {
                Fail(what, "wrong exception type: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ---- fixtures ----

        private const string SampleCharStat =
            "float Strength:  (min: 0, max: 999, default: 10)\n" +
            "float Vitality:  (min: 0, max: 999, default: 10)\n" +
            "float Dexterity: (min: 0, max: 999, default: 10)\n" +
            "int Level:       (min: 1, max: 100, default: 1)\n" +
            "bool IsUndead:   (default: false)\n";

        private const string SampleFormulas =
            "float MaxHP: (primary float Vitality, float K) => {\n" +
            "    float base = Vitality * 10;\n" +
            "    return clamp(base + K, 0, 9999);\n" +
            "}\n" +
            "\n" +
            "bool ImmuneToPoison: (primary bool IsUndead) => {\n" +
            "    return IsUndead;\n" +
            "}\n" +
            "\n" +
            "int XPToNextLevel: (primary int Level, float Multiplier) => {\n" +
            "    int base = Level * 100;\n" +
            "    return floor(base * Multiplier);\n" +
            "}\n" +
            "\n" +
            "float HalfHP: (primary float Vitality) => {\n" +
            "    return MaxHP / 2;\n" +
            "}\n";

        private static CharacterStats NewStats()
        {
            CharacterStats stats = new CharacterStats();
            stats.SetSchemaText(SampleCharStat);
            stats.Recalculate();
            return stats;
        }

        private static List<FormulaCheckResult> CheckFormulas(string text, StatSchema schema, Blackboard board)
        {
            List<ParsedFormula> parsed = GameplayStatFormat.Parse(text);

            Dictionary<string, StatType> types = new Dictionary<string, StatType>();
            for (int i = 0; i < parsed.Count; i++) types[parsed[i].Name] = parsed[i].ReturnType;

            List<FormulaCheckResult> results = new List<FormulaCheckResult>(parsed.Count);
            for (int i = 0; i < parsed.Count; i++)
            {
                results.Add(FormulaChecker.Check(parsed[i], schema, board, types));
            }
            return results;
        }

        // ---- charstat format ----

        private static void CharStatRoundTrip()
        {
            StatSchema schema = CharStatFormat.Parse(SampleCharStat);
            Expect(schema.entries.Count == 5, "parses five stats");

            StatSchemaEntry strength = schema.Find("Strength");
            Expect(strength != null && strength.type == StatType.Float, "Strength is float");
            Expect(strength.hasMin && strength.minValue == 0f && strength.hasMax && strength.maxValue == 999f, "Strength min/max");
            Expect(strength.hasDefault && strength.defaultFloat == 10f, "Strength default 10");

            StatSchemaEntry undead = schema.Find("IsUndead");
            Expect(undead.type == StatType.Bool && !undead.hasMin && !undead.hasMax, "bool has no min/max");
            Expect(undead.hasDefault && !undead.defaultBool, "IsUndead defaults false");

            string written = CharStatFormat.Write(schema);
            StatSchema reparsed = CharStatFormat.Parse(written);
            Expect(reparsed.entries.Count == 5, "write → parse round-trips");
            Expect(CharStatFormat.Parse(CharStatFormat.Write(reparsed)).Find("Level").defaultInt == 1,
                "int default survives the round-trip");
        }

        private static void CharStatParseErrors()
        {
            ExpectThrows<CharStatFormat.ParseException>(
                delegate { CharStatFormat.Parse("float : (default: 1)"); },
                "missing name rejected");

            ExpectThrows<CharStatFormat.ParseException>(
                delegate { CharStatFormat.Parse("float Str: (min: 10, max: 5)"); },
                "min > max rejected");

            ExpectThrows<CharStatFormat.ParseException>(
                delegate { CharStatFormat.Parse("float A: (default: 1)\nfloat A: (default: 2)"); },
                "duplicate name rejected");

            ExpectThrows<CharStatFormat.ParseException>(
                delegate { CharStatFormat.Parse("string Name: (default: 1)"); },
                "unknown type rejected");
        }

        // ---- formula language ----

        private static void LexerBasics()
        {
            List<FormulaToken> tokens = FormulaLexer.Lex("(primary float X) => { return X * 2.5; } // hi");
            Expect(tokens[0].Type == FormulaTokenType.LParen, "lexes '('");
            Expect(tokens[2].Type == FormulaTokenType.Float, "lexes 'float' keyword");
            bool sawArrow = false, sawNumber = false;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Type == FormulaTokenType.Arrow) sawArrow = true;
                if (tokens[i].Type == FormulaTokenType.Number && tokens[i].Number == 2.5f) sawNumber = true;
            }
            Expect(sawArrow, "lexes '=>'");
            Expect(sawNumber, "lexes 2.5");
            Expect(tokens[tokens.Count - 1].Type == FormulaTokenType.EndOfFile, "ends with EOF");
        }

        private static void ParserSignatures()
        {
            ParsedFormula f = GameplayStatFormat.ParseRow(StatType.Float, "MaxHP",
                "(primary float Vitality, float K) => { return Vitality * 10 + K; }");
            Expect(f.Name == "MaxHP" && f.ReturnType == StatType.Float, "row label applied");
            Expect(f.Params.Count == 2 && f.Primary.Name == "Vitality", "primary parsed first");
            Expect(f.Params[1].Name == "K" && f.Params[1].ParamType == StatType.Float, "secondary parsed");

            // The text is the source of truth: a label in the text wins.
            ParsedFormula renamed = GameplayStatFormat.ParseRow(StatType.Float, "Ignored",
                "float RealName: (primary float Vitality) => { return Vitality; }");
            Expect(renamed.Name == "RealName", "label in text overrides builder label");

            List<ParsedFormula> doc = GameplayStatFormat.Parse(SampleFormulas);
            Expect(doc.Count == 4, "document parses four formulas");
        }

        private static void CheckerValidation()
        {
            StatSchema schema = CharStatFormat.Parse(SampleCharStat);

            // 9.1: primary must match by name AND type.
            FormulaCheckResult wrongName = FormulaChecker.Check(
                GameplayStatFormat.ParseRow(StatType.Float, "X",
                    "(primary float Health) => { return Health; }"), schema, new Blackboard());
            Expect(!wrongName.Ok && wrongName.Errors[0].Message.Contains("not a character stat"),
                "primary with unknown name rejected");

            FormulaCheckResult wrongType = FormulaChecker.Check(
                GameplayStatFormat.ParseRow(StatType.Float, "X",
                    "(primary int Vitality) => { return Vitality; }"), schema, new Blackboard());
            Expect(!wrongType.Ok && wrongType.Errors[0].Message.Contains("section 9.1"),
                "primary with wrong type rejected");

            FormulaCheckResult good = FormulaChecker.Check(
                GameplayStatFormat.ParseRow(StatType.Float, "X",
                    "(primary float Vitality, float K) => { return Vitality + K; }"), schema, new Blackboard());
            Expect(good.Ok, "valid formula checks clean");
            Expect(good.Signature.secondaries.Count == 1 &&
                good.Signature.secondaries[0] is ConstantInput, "unmatched secondary becomes a constant");

            // Secondary that names a real stat with the wrong type: rejected.
            FormulaCheckResult statTypeClash = FormulaChecker.Check(
                GameplayStatFormat.ParseRow(StatType.Float, "X",
                    "(primary float Vitality, int Strength) => { return Strength; }"), schema, new Blackboard());
            Expect(!statTypeClash.Ok, "secondary redeclaring a stat with wrong type rejected");

            // Missing return.
            FormulaCheckResult noReturn = FormulaChecker.Check(
                GameplayStatFormat.ParseRow(StatType.Float, "X",
                    "(primary float Vitality) => { float x = Vitality; }"), schema, new Blackboard());
            Expect(!noReturn.Ok && noReturn.Errors[0].Message.Contains("missing return"), "missing return rejected");

            // Unknown variable / function.
            FormulaCheckResult unknownVar = FormulaChecker.Check(
                GameplayStatFormat.ParseRow(StatType.Float, "X",
                    "(primary float Vitality) => { return Nope; }"), schema, new Blackboard());
            Expect(!unknownVar.Ok && unknownVar.Errors[0].Message.Contains("unknown variable"), "unknown variable rejected");

            FormulaCheckResult unknownFn = FormulaChecker.Check(
                GameplayStatFormat.ParseRow(StatType.Float, "X",
                    "(primary float Vitality) => { return frobnicate(Vitality); }"), schema, new Blackboard());
            Expect(!unknownFn.Ok && unknownFn.Errors[0].Message.Contains("unknown function"), "unknown function rejected");

            // Return-type mismatch.
            FormulaCheckResult badReturn = FormulaChecker.Check(
                GameplayStatFormat.ParseRow(StatType.Bool, "X",
                    "(primary float Vitality) => { return Vitality; }"), schema, new Blackboard());
            Expect(!badReturn.Ok && badReturn.Errors[0].Message.Contains("return type mismatch"),
                "bool formula returning number rejected");

            // Errors carry line:column (section 11).
            FormulaCheckResult located = FormulaChecker.Check(
                GameplayStatFormat.ParseRow(StatType.Float, "X",
                    "(primary float Vitality) => {\n    return Nope;\n}"), schema, new Blackboard());
            Expect(!located.Ok && located.Errors[0].Line == 2, "error line number is 1-based and correct");
        }

        private static void CompileAndEvaluate()
        {
            CharacterStats stats = NewStats();
            stats.SetFloat("Vitality", 30f);

            List<FormulaCheckResult> checkedFormulas = CheckFormulas(SampleFormulas, stats.Schema, new Blackboard());
            for (int i = 0; i < checkedFormulas.Count; i++)
            {
                Expect(checkedFormulas[i].Ok, checkedFormulas[i].Formula.Name + " checks clean");
            }

            GameplayStats gameplay = new GameplayStats();
            gameplay.Bind(stats, checkedFormulas);

            // "Multiplier" matches no stat/blackboard: a per-character constant.
            // A definition supplies it through its overrides; here, by hand.
            gameplay.server.constantDefaults["Multiplier"] = 1f;
            gameplay.Recalculate();

            // MaxHP = clamp(30*10 + 0, 0, 9999) = 300; HalfHP = 150 (dependency!).
            ExpectNear(gameplay.GetFloat("MaxHP"), 300f, 0.01f, "MaxHP evaluates");
            ExpectNear(gameplay.GetFloat("HalfHP"), 150f, 0.01f, "HalfHP reads MaxHP's output");
            Expect(gameplay.GetBool("ImmuneToPoison") == false, "ImmuneToPoison is false");
            Expect(gameplay.GetInt("XPToNextLevel") == 100, "XPToNextLevel = 100 at level 1");

            // A change cascades through the section 19 flow.
            stats.SetInt("Level", 7);
            Expect(gameplay.GetInt("XPToNextLevel") == 700, "level 7 → XP 700 (recalc cascades)");

            // Primary reads live from CharacterStats.
            stats.SetFloat("Vitality", 50f);
            ExpectNear(gameplay.GetFloat("MaxHP"), 500f, 0.01f, "MaxHP follows Vitality change");
            ExpectNear(gameplay.GetFloat("HalfHP"), 250f, 0.01f, "HalfHP follows transitively");
        }

        private static void DependencyOrder()
        {
            CharacterStats stats = NewStats();
            List<FormulaCheckResult> checkedFormulas = CheckFormulas(SampleFormulas, stats.Schema, new Blackboard());
            List<GameplayStatClient> clients = GameplayStatsServer.Compile(checkedFormulas, stats.Schema, new Blackboard());

            int maxHp = clients.FindIndex(delegate (GameplayStatClient c) { return c.name == "MaxHP"; });
            int halfHp = clients.FindIndex(delegate (GameplayStatClient c) { return c.name == "HalfHP"; });
            Expect(maxHp >= 0 && halfHp >= 0, "both dependency-linked clients compiled");
            Expect(maxHp < halfHp, "MaxHP evaluates BEFORE HalfHP (topological order)");
        }

        private static void CircularDependencyRejected()
        {
            string cyclic =
                "float A: (primary float Vitality) => { return B + 1; }\n" +
                "float B: (primary float Vitality) => { return A + 1; }\n";
            StatSchema schema = CharStatFormat.Parse(SampleCharStat);

            ExpectThrows<InvalidOperationException>(
                delegate
                {
                    List<FormulaCheckResult> results = CheckFormulas(cyclic, schema, new Blackboard());
                    GameplayStatsServer.Compile(results, schema, new Blackboard());
                },
                "circular dependency throws");
        }

        private static void Builtins()
        {
            StatSchema schema = CharStatFormat.Parse(SampleCharStat);
            ParsedFormula f = GameplayStatFormat.ParseRow(StatType.Float, "T",
                "(primary float Vitality) => {" +
                " float a = sqrt(16);" +
                " float b = pow(2, 5);" +
                " float c = abs(0 - 3);" +
                " float d = min(2, 9);" +
                " float e = max(2, 9);" +
                " float g = clamp(50, 0, 10);" +
                " float h = floor(2.7);" +
                " float i = ceil(2.1);" +
                " float j = round(2.5);" +
                " float k = log(1);" +
                " float l = sin(0);" +
                " float m = cos(0);" +
                " return a + b + c + d + e + g + h + i + j + k + l + m;" +
                "}");
            FormulaCheckResult r = FormulaChecker.Check(f, schema, new Blackboard());
            Expect(r.Ok, "all twelve builtins check clean");
            if (!r.Ok) return;

            CompiledFormula compiled = FormulaCompiler.Compile(f);
            float result = compiled.Evaluate(new float[] { 10f });
            // 4 + 32 + 3 + 2 + 9 + 10 + 2 + 3 + 2(.NET banker's round) + 0 + 0 + 1 = 68
            ExpectNear(result, 68f, 0.01f, "builtin values evaluate correctly");

            // ^ is pow sugar; % works; unary minus works.
            ParsedFormula ops = GameplayStatFormat.ParseRow(StatType.Float, "T2",
                "(primary float Vitality) => { return 2 ^ 3 % 5 - (0 - 1); }");
            FormulaCheckResult opsCheck = FormulaChecker.Check(ops, schema, new Blackboard());
            Expect(opsCheck.Ok, "operators check clean");
            ExpectNear(FormulaCompiler.Compile(ops).Evaluate(new float[] { 0f }), 4f, 0.01f,
                "(2^3) % 5 - (-1) = 4 (power binds tighter than %, left-assoc chain)");
        }

        // ---- blackboard ----

        private static void BlackboardSetGet()
        {
            Blackboard board = new Blackboard();
            board.Declare("Rage", StatType.Float);
            board.Set("Rage", 2.5f);
            ExpectNear(board.Get("Rage"), 2.5f, 0.001f, "Set/Get round-trip");
            board.Set("Rage", 4f);
            ExpectNear(board.Get("Rage"), 4f, 0.001f, "Set overwrites");
        }

        private static void BlackboardBinding()
        {
            Blackboard board = new Blackboard();
            board.Declare("Hour", StatType.Float);
            GameObject clock = new GameObject("clock");
            ClockComponent component = clock.AddComponent<ClockComponent>();
            board.Bind("Hour", component, "hour");
            board.UpdateExternalBindings();
            ExpectNear(board.Get("Hour"), 12f, 0.001f, "external binding reads the member");
            component.hour = 20f;
            board.UpdateExternalBindings();
            ExpectNear(board.Get("Hour"), 20f, 0.001f, "binding re-reads on Update");
        }

        public class ClockComponent : MonoBehaviour
        {
            public float hour = 12f;
        }

        private static void BlackboardDrivenFormula()
        {
            CharacterStats stats = NewStats();
            Blackboard board = new Blackboard();
            board.Declare("Rage", StatType.Float);
            board.Set("Rage", 2f);

            // Power's secondaries: Dexterity (a real stat), Rage (blackboard).
            string formula =
                "float Power: (primary float Strength, float Dexterity, float Rage) => {\n" +
                "    return Strength * 2 + Dexterity * 0.5 + Rage * 10;\n" +
                "}\n";
            List<FormulaCheckResult> results = CheckFormulas(formula, stats.Schema, board);
            Expect(results[0].Ok, "mixed-stat/blackboard formula checks clean");
            Expect(results[0].Signature.secondaries[1] is BlackboardInput, "Rage classified as blackboard input");

            GameplayStats gameplay = new GameplayStats();
            gameplay.server.board = board; // same blackboard the checker saw
            gameplay.Bind(stats, results);
            // Strength 10 (default) * 2 + Dex 10 * 0.5 + Rage 2 * 10 = 45.
            ExpectNear(gameplay.GetFloat("Power"), 45f, 0.01f, "blackboard value feeds the formula");

            board.Set("Rage", 5f);
            gameplay.Recalculate();
            ExpectNear(gameplay.GetFloat("Power"), 75f, 0.01f, "blackboard change recalculates");
        }

        // ---- character stats ----

        private static void CharacterStatsClamping()
        {
            CharacterStats stats = NewStats();
            stats.SetFloat("Strength", 5000f);
            Expect(stats.GetFloat("Strength") == 999f, "float clamped to max 999");
            stats.SetFloat("Strength", -50f);
            Expect(stats.GetFloat("Strength") == 0f, "float clamped to min 0");

            stats.SetInt("Level", 500);
            Expect(stats.GetInt("Level") == 100, "int clamped to max 100");
            stats.SetInt("Level", 0);
            Expect(stats.GetInt("Level") == 1, "int clamped to min 1");

            Expect(stats.GetBool("IsUndead") == false, "bool defaults false");
            stats.SetBool("IsUndead", true);
            Expect(stats.GetBool("IsUndead"), "bool set true");

            ExpectThrows<KeyNotFoundException>(delegate { stats.SetFloat("Nope", 1f); },
                "unknown stat setter throws");
            ExpectThrows<KeyNotFoundException>(delegate { stats.GetFloat("Nope"); },
                "unknown stat getter throws");
        }

        private static void CharacterStatsEvents()
        {
            CharacterStats stats = NewStats();
            List<string> changed = new List<string>();
            stats.OnStatChanged += delegate (StatField f) { changed.Add(f.name); };

            stats.SetFloat("Strength", 42f);
            stats.SetFloat("Strength", 42f); // same value: no event
            stats.SetBool("IsUndead", true);
            stats.SetInt("Level", 9);
            Expect(changed.Count == 3, "exactly three change events fired");
            Expect(changed.Contains("Strength") && changed.Contains("IsUndead") && changed.Contains("Level"),
                "events name the changed stats");
        }

        // ---- registry ----

        private static void RegistryTags()
        {
            CharacterRegistry registry = new CharacterRegistry();
            CharacterTag first = registry.AllocateTag("OrcWarrior");
            CharacterTag second = registry.AllocateTag("OrcWarrior");
            Expect(first.ToString() == "OrcWarrior_001", "first tag is _001");
            Expect(second.ToString() == "OrcWarrior_002", "second tag is _002");
            Expect(CharacterTag.Parse("OrcWarrior_002").serialNumber == 2, "tag parse round-trips");
        }

        private static void RegistrySearch()
        {
            CharacterRegistry registry = new CharacterRegistry();
            PlayerCharacterDefinition player = ScriptableObject.CreateInstance<PlayerCharacterDefinition>();
            player.characterID = "player_hero";
            player.characterName = "Hero";
            registry.Add(player);

            EnemyCharacterDefinition orc = ScriptableObject.CreateInstance<EnemyCharacterDefinition>();
            orc.characterID = "orc_warrior";
            orc.characterName = "Orc Warrior";
            registry.Add(orc);

            Expect(registry.Search("orc").Count == 1, "search matches case-insensitively");
            Expect(registry.ContainsId("orc_warrior"), "ContainsId finds the ID");
            Expect(!registry.ContainsId("troll"), "ContainsId rejects unknown IDs");
            Expect(registry.FindById("orc_warrior").definition == orc, "FindById returns the entry");
        }

        // ---- spawn pipeline ----

        private static EnemyCharacterDefinition NewOrcDefinition()
        {
            EnemyCharacterDefinition def = ScriptableObject.CreateInstance<EnemyCharacterDefinition>();
            def.characterID = "orc_warrior";
            def.characterName = "OrcWarrior";
            def.dimension = CharacterDimension.ThreeD;
            def.kind = CharacterKind.NPC;
            def.physicsMode = PhysicsMode.PhysicsBased;
            def.charStatText = SampleCharStat;
            def.gameplayStatText = SampleFormulas;
            return def;
        }

        private static CharacterBuilderServer NewServer()
        {
            GameObject host = new GameObject("CharacterBuilderServer");
            return host.AddComponent<CharacterBuilderServer>();
        }

        private static void Spawn3DPhysicsNPC()
        {
            CharacterBuilderServer server = NewServer();
            EnemyCharacterDefinition def = NewOrcDefinition();
            server.registry.Add(def);

            Character c = server.Spawn("orc_warrior");
            Expect(c != null, "3D physics NPC spawns");
            if (c == null) return;
            Expect(c.rigidbody3D != null, "3D NPC has a Rigidbody");
            Expect(c.rigidbody3D.useGravity, "physics mode = gravity ON");
            Expect(!c.rigidbody3D.isKinematic, "physics NPC body is not kinematic");
            Expect(c.collider3D != null, "3D NPC has a collider");
            Expect(c.controller == null, "NPC never gets a CharacterController");
            Expect(c.animator != null, "animator attached");
            Expect(c.attachedAI == null, "no AI script assigned in this definition");
            Expect(c.healthBar != null, "health bar attached");
        }

        private static void Spawn3DNonPhysicsPlayer()
        {
            CharacterBuilderServer server = NewServer();
            PlayerCharacterDefinition def = ScriptableObject.CreateInstance<PlayerCharacterDefinition>();
            def.characterID = "player_hero";
            def.characterName = "Hero";
            def.dimension = CharacterDimension.ThreeD;
            def.kind = CharacterKind.Player;
            def.physicsMode = PhysicsMode.NonPhysics;
            def.charStatText = SampleCharStat;
            server.registry.Add(def);

            Character c = server.Spawn("player_hero");
            Expect(c != null, "3D non-physics Player spawns");
            if (c == null) return;
            Expect(c.controller != null, "3D non-physics Player has a CharacterController");
            Expect(c.collider3D == null, "no second collider — the CC is the collider");
            Expect(c.rigidbody3D != null && c.rigidbody3D.isKinematic, "rigidbody is kinematic (no physics fight)");
            Expect(!c.rigidbody3D.useGravity, "gravity OFF");
            Expect(c is PlayerCharacter, "spawned as PlayerCharacter");
            Expect(((PlayerCharacter)c).movement is CharacterControllerMovement, "CC movement strategy attached");
        }

        private static void Spawn2DPhysicsPlayer()
        {
            CharacterBuilderServer server = NewServer();
            PlayerCharacterDefinition def = ScriptableObject.CreateInstance<PlayerCharacterDefinition>();
            def.characterID = "player_2d";
            def.characterName = "Hero2D";
            def.dimension = CharacterDimension.TwoD;
            def.kind = CharacterKind.Player;
            def.physicsMode = PhysicsMode.PhysicsBased;
            def.charStatText = SampleCharStat;
            server.registry.Add(def);

            Character c = server.Spawn("player_2d");
            Expect(c != null, "2D physics Player spawns");
            if (c == null) return;
            Expect(c.rigidbody2D != null, "2D Player has a Rigidbody2D");
            Expect(Mathf.Approximately(c.rigidbody2D.gravityScale, 1f), "2D gravity scale 1");
            Expect(c.collider2D != null, "2D Player has a Collider2D");
            Expect(c.controller == null, "2D Player never gets a 3D CharacterController");
            Expect(((PlayerCharacter)c).movement is PhysicsPlayerMovement, "physics movement strategy attached");
        }

        private static void Spawn2DNonPhysicsPlayer()
        {
            CharacterBuilderServer server = NewServer();
            PlayerCharacterDefinition def = ScriptableObject.CreateInstance<PlayerCharacterDefinition>();
            def.characterID = "player_2d_np";
            def.characterName = "Hero2DNP";
            def.dimension = CharacterDimension.TwoD;
            def.kind = CharacterKind.Player;
            def.physicsMode = PhysicsMode.NonPhysics;
            def.charStatText = SampleCharStat;
            server.registry.Add(def);

            Character c = server.Spawn("player_2d_np");
            Expect(c != null, "2D non-physics Player spawns");
            if (c == null) return;
            Expect(Mathf.Approximately(c.rigidbody2D.gravityScale, 0f), "2D gravity scale 0");
            Expect(c.rigidbody2D.isKinematic, "2D non-physics Player body is kinematic");
            Expect(c.collider2D != null, "2D non-physics Player still has a collider");
            Expect(((PlayerCharacter)c).movement is Kinematic2DMovement, "kinematic 2D movement attached");
        }

        private static void Spawn3DNonPhysicsNPC()
        {
            CharacterBuilderServer server = NewServer();
            EnemyCharacterDefinition def = NewOrcDefinition();
            def.physicsMode = PhysicsMode.NonPhysics;
            server.registry.Add(def);

            Character c = server.Spawn("orc_warrior");
            Expect(c != null, "3D non-physics NPC spawns");
            if (c == null) return;
            Expect(!c.rigidbody3D.useGravity, "non-physics NPC gravity OFF");
            Expect(!c.rigidbody3D.isKinematic, "non-physics NPC body is NOT kinematic (AI drives it)");
            Expect(c.collider3D != null, "non-physics NPC keeps its collider");
        }

        private static void SpawnInitializesStats()
        {
            CharacterBuilderServer server = NewServer();
            EnemyCharacterDefinition def = NewOrcDefinition();
            def.statValues.Add(StatValueOverride.Float("Vitality", 30f));
            def.statValues.Add(StatValueOverride.Int("Level", 5));
            def.statValues.Add(StatValueOverride.Bool("IsUndead", true));
            // Not a stat anywhere: per-character constant for XPToNextLevel.
            def.statValues.Add(StatValueOverride.Float("Multiplier", 1f));
            server.registry.Add(def);

            Character c = server.Spawn("orc_warrior");
            if (c == null) { Fail("spawn initializes stats", "spawn failed"); return; }

            Expect(c.characterStats.GetFloat("Vitality") == 30f, "override applied (Vitality 30)");
            Expect(c.characterStats.GetInt("Level") == 5, "override applied (Level 5)");
            Expect(c.characterStats.GetBool("IsUndead"), "override applied (IsUndead true)");
            Expect(c.characterStats.GetFloat("Strength") == 10f, "schema default applied (Strength 10)");

            ExpectNear(c.gameplayStats.GetFloat("MaxHP"), 300f, 0.01f, "gameplay stats compiled and calculated");
            ExpectNear(c.gameplayStats.GetInt("XPToNextLevel"), 500f, 0.01f, "XPToNextLevel follows Level 5");
            Expect(c.gameplayStats.GetBool("ImmuneToPoison"), "ImmuneToPoison follows IsUndead");

            // Section 19: a post-spawn change cascades.
            c.characterStats.SetFloat("Vitality", 40f);
            ExpectNear(c.gameplayStats.GetFloat("MaxHP"), 400f, 0.01f, "post-spawn stat change recalculates");

            // Blackboard mirror.
            Expect(Mathf.Approximately(c.gameplayStats.server.board.Get("MaxHP"), 400f),
                "gameplay outputs mirrored into the blackboard");
        }

        private static void SpawnWithoutAIWarnsButSpawns()
        {
            CharacterBuilderServer server = NewServer();
            EnemyCharacterDefinition def = NewOrcDefinition();
            def.aiInstanceType = new SerializedType { typeName = "" };
            server.registry.Add(def);

            Debug.Messages.Clear();
            Character c = server.Spawn("orc_warrior");
            Expect(c != null, "NPC without AI still spawns");
            bool warned = Debug.Messages.Exists(delegate (string m) { return m.Contains("no AI script"); });
            Expect(warned, "missing AI logs a warning");
        }

        private static void DefinitionToCharacterConvention()
        {
            // EnemyCharacterDefinition + Player suffix rule → EnemyCharacter.
            CharacterBuilderServer server = NewServer();
            EnemyCharacterDefinition def = NewOrcDefinition();
            server.registry.Add(def);
            Character c = server.Spawn("orc_warrior");
            Expect(c is EnemyCharacter, "EnemyCharacterDefinition materializes EnemyCharacter");

            // A definition whose name maps to no class falls back by kind.
            FriendlyNPCDefinition friendly = ScriptableObject.CreateInstance<FriendlyNPCDefinition>();
            friendly.characterID = "village_elder";
            friendly.characterName = "VillageElder";
            friendly.charStatText = SampleCharStat;
            server.registry.Add(friendly);
            Character f = server.Spawn("village_elder");
            Expect(f != null && f is NPCCharacter, "unmapped definition falls back to an NPC character");
        }

        // ---- AIInstance contract ----

        private static void ContractAcceptsValid()
        {
            string problem = AIInstanceContract.Validate(typeof(OrcWarriorFSM), "public partial class OrcWarriorFSM : AIInstance {}");
            Expect(problem == null, "valid partial AIInstance subclass accepted");
        }

        private static void ContractRejectsNonPartial()
        {
            string problem = AIInstanceContract.Validate(typeof(OrcWarriorFSM), "public class OrcWarriorFSM : AIInstance {}");
            Expect(problem != null && problem.Contains("partial"), "non-partial source rejected");
        }

        private static void ContractRejectsWrongBase()
        {
            string problem = AIInstanceContract.Validate(typeof(NotAnAI), null);
            Expect(problem != null && problem.Contains("AIInstance"), "non-AIInstance type rejected");

            Expect(AIInstanceContract.Validate(null, null) != null, "null type rejected");
        }

        private sealed class NotAnAI : MonoBehaviour
        {
        }

        // ---- stat bars ----

        private static void StatBarTraceAndFill()
        {
            GameObject go = new GameObject("bar");
            HealthBar bar = go.AddComponent<HealthBar>();
            bar.SetMaxValue(100f);
            bar.UpdateValue(100f);
            bar.AnimateTick(0.5f);
            Expect(bar.DisplayedFill > 99f, "fill reaches full");

            bar.UpdateValue(50f);
            bar.AnimateTick(0.016f);
            Expect(bar.DisplayedFill > 50f && bar.DisplayedFill < 100f, "fill lerps down gradually");
            Expect(bar.DisplayedTrace >= bar.DisplayedFill - 0.01f, "trace lags behind the fill");

            for (int i = 0; i < 200; i++) bar.AnimateTick(0.1f);
            ExpectNear(bar.DisplayedFill, 50f, 0.5f, "fill settles at the new value");
            ExpectNear(bar.DisplayedTrace, 50f, 0.5f, "trace decays to the fill after the delay");
        }
    }

    public static class Program
    {
        public static int Main()
        {
            return RPGCharacterStatsTests.RunAll();
        }
    }
}
