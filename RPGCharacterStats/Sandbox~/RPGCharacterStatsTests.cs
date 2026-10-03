// RPG Character & Stats System — headless smoke tests (dotnet run).
//
// Exercises the whole package without Unity: the .charstat round-trip, the
// formula DSL (lex, parse, check, compile, dependency order, runtime recalc,
// blackboard inputs), the registry and its tag allocation, the section 2.5
// spawn component matrix for all four dimension/kind/physics combinations,
// the AIInstance contract, and the stat-bar animation. Exit code 0 = green.

using System;
using System.Collections.Generic;
using System.Reflection;
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

            Section("character DB + LRU cache");
            DbCacheMiss();
            LruEviction();
            SpawnedEntriesPinned();
            ServerSaveDefinitionOwnsWrites();
            RegistrySurvivesDeserialization();
            ServerReloadsColdRegistryOnEnable();
            ServerReplacesDanglingDefinitionRows();

            Section("character record format (JSON)");
            RecordRoundTripsPlayer();
            RecordRoundTripsNPC();
            RecordSurvivesMissingScript();
            CorruptRecordsNeverThrow();

            Section("spawn pipeline (section 2.5 matrix)");
            Spawn3DPhysicsNPC();
            Spawn3DNonPhysicsPlayer();
            PlayerControllerDrivesAndFalls();
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

        // ---- character DB + LRU cache ----

        private static void DbCacheMiss()
        {
            PlayerCharacterDefinition hero = ScriptableObject.CreateInstance<PlayerCharacterDefinition>();
            hero.characterID = "db_hero";
            hero.characterName = "DbHero";
            Resources.store["CharacterDB/db_hero"] = new TextAsset(CharacterDB.EncodeRecord(hero));

            CharacterRegistry cache = new CharacterRegistry();
            Expect(cache.entries.Count == 0, "cold cache starts empty");
            CharacterEntry hit = cache.FindById("db_hero");
            Expect(hit != null && hit.definition != null
                && hit.definition is PlayerCharacterDefinition
                && hit.definition.characterID == "db_hero"
                && hit.definition.characterName == "DbHero",
                "cache miss loads the JSON record from the DB");
            Expect(cache.entries.Count == 1, "DB hit is cached");
            Expect(!cache.ContainsId("ghost"), "ContainsId stays cache-only");
            Expect(cache.FindById("ghost") == null, "unknown ID misses cleanly");
        }

        private static void LruEviction()
        {
            CharacterRegistry cache = new CharacterRegistry { capacity = 2 };

            EnemyCharacterDefinition a = ScriptableObject.CreateInstance<EnemyCharacterDefinition>();
            a.characterID = "lru_a"; a.characterName = "A";
            EnemyCharacterDefinition b = ScriptableObject.CreateInstance<EnemyCharacterDefinition>();
            b.characterID = "lru_b"; b.characterName = "B";
            EnemyCharacterDefinition c = ScriptableObject.CreateInstance<EnemyCharacterDefinition>();
            c.characterID = "lru_c"; c.characterName = "C";
            Resources.store["CharacterDB/lru_a"] = new TextAsset(CharacterDB.EncodeRecord(a));
            Resources.store["CharacterDB/lru_b"] = new TextAsset(CharacterDB.EncodeRecord(b));
            Resources.store["CharacterDB/lru_c"] = new TextAsset(CharacterDB.EncodeRecord(c));

            cache.Put(a);
            cache.Put(b);
            cache.FindById("lru_a");            // a is now most recently used
            cache.Put(c);                        // overflow: b is the LRU → evicted
            Expect(cache.ContainsId("lru_a"), "recently used survives eviction");
            Expect(cache.ContainsId("lru_c"), "newest survives eviction");
            Expect(!cache.ContainsId("lru_b"), "least recently used is evicted");
            CharacterEntry reloaded = cache.FindById("lru_b");
            Expect(reloaded != null && reloaded.definition != null && reloaded.definition.characterID == "lru_b",
                "evicted entry reloads from the DB");
        }

        private static void SpawnedEntriesPinned()
        {
            CharacterRegistry cache = new CharacterRegistry { capacity = 1 };

            EnemyCharacterDefinition held = ScriptableObject.CreateInstance<EnemyCharacterDefinition>();
            held.characterID = "pinned"; held.characterName = "Pinned";
            CharacterEntry heldEntry = cache.Add(held);
            heldEntry.isSpawned = true;          // materialized → pinned

            EnemyCharacterDefinition alsoHeld = ScriptableObject.CreateInstance<EnemyCharacterDefinition>();
            alsoHeld.characterID = "pinned2"; alsoHeld.characterName = "Pinned2";
            CharacterEntry alsoEntry = cache.Add(alsoHeld);
            alsoEntry.isSpawned = true;          // pinned too → nothing evictable

            Expect(cache.ContainsId("pinned"), "spawned entry is pinned (never evicted)");
            Expect(cache.ContainsId("pinned2"), "all-pinned cache overflows softly");
            Expect(cache.entries.Count == 2, "capacity is soft when everything is pinned");
        }

        private static void ServerSaveDefinitionOwnsWrites()
        {
            CharacterBuilderServer server = NewServer();
            EnemyCharacterDefinition def = NewOrcDefinition();
            def.characterID = "cache_orc";
            def.characterName = "CacheOrc";

            Expect(server.SaveDefinition(def) != null, "SaveDefinition caches the character");
            Expect(server.registry.ContainsId("cache_orc"), "SaveDefinition registers the ID");
            Expect(server.IsIdTaken("cache_orc"), "IsIdTaken sees the cache");
            Expect(server.IsIdTaken("db_hero"), "IsIdTaken sees the DB");
            Expect(!server.IsIdTaken("ghost"), "unknown ID is free");

            Expect(server.Spawn("cache_orc") != null, "spawn goes through the cache");
            CharacterEntry spawned = server.registry.FindById("cache_orc");
            Expect(spawned != null && spawned.isSpawned, "spawn marks the entry");
        }

        private static void RegistrySurvivesDeserialization()
        {
            CharacterRegistry original = new CharacterRegistry { capacity = 2 };

            EnemyCharacterDefinition orc = ScriptableObject.CreateInstance<EnemyCharacterDefinition>();
            orc.characterID = "ser_orc"; orc.characterName = "Orc";
            PlayerCharacterDefinition hero = ScriptableObject.CreateInstance<PlayerCharacterDefinition>();
            hero.characterID = "ser_hero"; hero.characterName = "Hero";
            Resources.store["CharacterDB/ser_orc"] = new TextAsset(CharacterDB.EncodeRecord(orc));
            Resources.store["CharacterDB/ser_hero"] = new TextAsset(CharacterDB.EncodeRecord(hero));

            original.Put(orc);
            CharacterEntry heroEntry = original.Put(hero);
            original.AllocateTag("Orc");            // _001
            original.AllocateTag("Orc");            // _002 — next is _003

            GameObject instance = new GameObject("SerHero");
            PlayerCharacter heroChar = instance.AddComponent<PlayerCharacter>();
            original.MarkSpawned(heroEntry, new CharacterTag("Hero", 1), heroChar);

            // What Unity does on a play-mode scene reload: a FRESH object whose
            // serialized fields (capacity, entries, tagSerials) came back. The
            // runtime-only LRU/dictionary state rebuilds lazily.
            CharacterRegistry reloaded = new CharacterRegistry { capacity = original.capacity };
            reloaded.entries.AddRange(original.entries);
            for (int i = 0; i < original.tagSerials.Count; i++)
            {
                reloaded.tagSerials.Add(new CharacterRegistry.TagSerial
                {
                    name = original.tagSerials[i].name,
                    nextSerial = original.tagSerials[i].nextSerial
                });
            }
            reloaded.ForgetRuntimeState();

            Expect(reloaded.ContainsId("ser_orc") && reloaded.ContainsId("ser_hero"),
                "definitions survive a deserialization round-trip");
            CharacterEntry reloadedHero = reloaded.FindById("ser_hero");
            Expect(reloadedHero.isSpawned && reloadedHero.instance == heroChar,
                "spawned bookkeeping survives the round-trip");
            Expect(reloaded.AllocateTag("Orc").ToString() == "Orc_003",
                "tag serials continue after a reload (not _001 again)");

            // The rebuilt LRU still respects recency + pinning + DB fallback.
            reloaded.FindById("ser_orc");            // orc → most recently used
            EnemyCharacterDefinition third = ScriptableObject.CreateInstance<EnemyCharacterDefinition>();
            third.characterID = "ser_third"; third.characterName = "Third";
            Resources.store["CharacterDB/ser_third"] = new TextAsset(CharacterDB.EncodeRecord(third));
            reloaded.Put(third);                     // capacity 2: hero is LRU-front but PINNED → orc goes
            Expect(reloaded.ContainsId("ser_hero") && reloaded.ContainsId("ser_third"),
                "post-reload eviction respects pinning");
            Expect(!reloaded.ContainsId("ser_orc"), "post-reload eviction still drops the LRU entry");
            Expect(reloaded.FindById("ser_orc") != null,
                "evicted entry still reloads from the DB after a reload");
        }

        private static void ServerReloadsColdRegistryOnEnable()
        {
            PlayerCharacterDefinition hero = ScriptableObject.CreateInstance<PlayerCharacterDefinition>();
            hero.characterID = "cold_hero"; hero.characterName = "ColdHero";
            Resources.store["CharacterDB/cold_hero"] = new TextAsset(CharacterDB.EncodeRecord(hero));

            CharacterBuilderServer server = NewServer();
            server.registry = new CharacterRegistry();   // the Play → Stop scenario: cache came back empty
            InvokeOnEnable(server);

            CharacterEntry entry = server.registry.FindById("cold_hero");
            Expect(entry != null, "cold cache auto-reloads from the CharacterDB on enable");

            // Stale bookkeeping (the instance died with play mode) is cleaned up.
            entry.isSpawned = true;
            entry.instance = null;
            entry.spawnedAs = new CharacterTag("ColdHero", 1);
            InvokeOnEnable(server);
            Expect(!entry.isSpawned && entry.instance == null && entry.spawnedAs == null,
                "stale spawn bookkeeping is cleared on enable");

            // A warm cache is neither duplicated nor clobbered.
            int before = server.registry.entries.Count;
            InvokeOnEnable(server);
            Expect(server.registry.entries.Count == before, "warm cache is left alone on enable");
        }

        private static void ServerReplacesDanglingDefinitionRows()
        {
            PlayerCharacterDefinition hero = ScriptableObject.CreateInstance<PlayerCharacterDefinition>();
            hero.characterID = "dangle_hero";
            hero.characterName = "DangleHero";
            Resources.store["CharacterDB/dangle_hero"] = new TextAsset(CharacterDB.EncodeRecord(hero));

            // What a domain reload can leave behind: a cached row whose
            // definition reference died (in-memory definition / broken asset ref).
            CharacterBuilderServer server = NewServer();
            CharacterRegistry cache = new CharacterRegistry();
            CharacterEntry dangling = new CharacterEntry();
            dangling.tag = new CharacterTag("DeadRow", 0);
            cache.entries.Add(dangling);
            server.registry = cache;
            InvokeOnEnable(server);

            Expect(!server.registry.HasNullDefinitions(), "dangling definition rows are dropped on enable");
            CharacterEntry loaded = server.registry.FindById("dangle_hero");
            Expect(loaded != null && loaded.definition != null && loaded.definition.characterID == "dangle_hero",
                "the DB record replaces the dangling row");
        }

        private static void InvokeOnEnable(CharacterBuilderServer server)
        {
            typeof(CharacterBuilderServer)
                .GetMethod("OnEnable", BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                .Invoke(server, null);
        }

        // ---- character record format (JSON, GUID-proof) ----

        private static void RecordRoundTripsPlayer()
        {
            PlayerCharacterDefinition def = ScriptableObject.CreateInstance<PlayerCharacterDefinition>();
            def.characterID = "rec_hero";
            def.characterName = "RecHero";
            def.description = "hero\nwith \"quotes\" and\nnewlines";
            def.dimension = CharacterDimension.ThreeD;
            def.kind = CharacterKind.Player;
            def.physicsMode = PhysicsMode.NonPhysics;
            def.charStatText = SampleCharStat;
            def.gameplayStatText = SampleFormulas;
            def.inputAxisPrefix = "P1_";
            def.statValues.Add(StatValueOverride.Float("Vitality", 42.5f));
            def.statValues.Add(StatValueOverride.Int("Level", 7));
            def.statValues.Add(StatValueOverride.Bool("IsUndead", true));

            CharacterDefinition decoded = CharacterDB.DecodeRecord(CharacterDB.EncodeRecord(def));

            Expect(decoded is PlayerCharacterDefinition, "record decodes back to the same class");
            if (decoded == null) return;
            Expect(decoded.characterID == "rec_hero" && decoded.characterName == "RecHero",
                "identity survives the record round-trip");
            Expect(decoded.description == def.description, "text with quotes/newlines survives");
            Expect(decoded.dimension == def.dimension && decoded.kind == def.kind
                && decoded.physicsMode == def.physicsMode, "configuration survives");
            Expect(decoded.charStatText == def.charStatText
                && decoded.gameplayStatText == def.gameplayStatText, "stat sources survive");
            Expect(((PlayerCharacterDefinition)decoded).inputAxisPrefix == "P1_", "player field survives");

            Expect(decoded.statValues.Count == 3, "all three overrides survive");
            Expect(decoded.statValues[0].statName == "Vitality"
                && Mathf.Approximately(decoded.statValues[0].floatValue, 42.5f), "float override survives");
            Expect(decoded.statValues[1].statName == "Level" && decoded.statValues[1].intValue == 7,
                "int override survives");
            Expect(decoded.statValues[2].statName == "IsUndead" && decoded.statValues[2].boolValue,
                "bool override survives");

            Expect(CharacterDB.DecodeRecord(CharacterDB.EncodeRecord(decoded)).characterID == "rec_hero",
                "re-saving a decoded record round-trips again");
        }

        private static void RecordRoundTripsNPC()
        {
            EnemyCharacterDefinition orc = ScriptableObject.CreateInstance<EnemyCharacterDefinition>();
            orc.characterID = "rec_orc";
            orc.characterName = "RecOrc";
            orc.kind = CharacterKind.NPC;
            orc.aiInstanceType = new SerializedType { typeName = "RPGCharacterStats.OrcWarriorFSM, Assembly-CSharp" };

            CharacterDefinition decoded = CharacterDB.DecodeRecord(CharacterDB.EncodeRecord(orc));

            Expect(decoded is EnemyCharacterDefinition, "NPC record decodes back to the same class");
            Expect(decoded != null && ((NPCCharacterDefinition)decoded).aiInstanceType.typeName == orc.aiInstanceType.typeName,
                "AI script reference survives (stored by NAME, not by GUID)");
        }

        private static void RecordSurvivesMissingScript()
        {
            PlayerCharacterDefinition def = ScriptableObject.CreateInstance<PlayerCharacterDefinition>();
            def.characterID = "ghosted";
            def.characterName = "Ghosted";
            def.kind = CharacterKind.Player;
            def.statValues.Add(StatValueOverride.Float("Vitality", 9f));

            // Simulate the class disappearing (script deleted / package moved):
            // the saved class name no longer resolves anywhere.
            string orphaned = CharacterDB.EncodeRecord(def)
                .Replace("RPGCharacterStats.PlayerCharacterDefinition", "Gone.WithTheRefactor");

            CharacterDefinition decoded = CharacterDB.DecodeRecord(orphaned);
            Expect(decoded != null && decoded is PlayerCharacterDefinition,
                "record whose class vanished still loads on the kind fallback");
            Expect(decoded.characterID == "ghosted" && decoded.characterName == "Ghosted",
                "fallback keeps every field the fallback class has");
            Expect(decoded.statValues.Count == 1 && Mathf.Approximately(decoded.statValues[0].floatValue, 9f),
                "overrides survive the fallback load");
        }

        private static void CorruptRecordsNeverThrow()
        {
            Expect(CharacterDB.DecodeRecord(null) == null, "null record decodes to nothing");
            Expect(CharacterDB.DecodeRecord("") == null, "empty record decodes to nothing");
            Expect(CharacterDB.DecodeRecord("not json at all") == null, "garbage record decodes to nothing");

            // LoadAll skips unreadable rows instead of blowing up the server.
            PlayerCharacterDefinition good = ScriptableObject.CreateInstance<PlayerCharacterDefinition>();
            good.characterID = "good_row";
            good.characterName = "GoodRow";
            Resources.store["CharacterDB/good_row"] = new TextAsset(CharacterDB.EncodeRecord(good));
            Resources.store["CharacterDB/garbage_row"] = new TextAsset("{{{ nope");

            List<CharacterDefinition> all = CharacterDB.LoadAll();
            Expect(all.Exists(delegate (CharacterDefinition d) { return d != null && d.characterID == "good_row"; }),
                "LoadAll reads healthy records");
            Expect(!all.Exists(delegate (CharacterDefinition d) { return d != null && d.characterID == "garbage_row"; }),
                "LoadAll skips unreadable records");
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
            PlayerCharacter pc = (PlayerCharacter)c;
            PlayerController controller = c.GetComponent<PlayerController>();
            Expect(controller != null, "PlayerController attached (WASD + Space + gravity, no camera)");
            Expect(pc.movement == null, "no internal strategy — PlayerController is the single mover");
            Expect(pc.input.x == 0f && !pc.input.jumpHeld, "PlayerInput field starts neutral");
        }

        private static void PlayerControllerDrivesAndFalls()
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
            PlayerController controller = c.GetComponent<PlayerController>();
            if (controller == null) { Fail("player controller ticks", "no PlayerController on the spawn"); return; }

            Time.deltaTime = 0.1f;
            Vector3 start = c.transform.position;

            controller.Tick(new PlayerInput { x = 1f, y = 0f, jumpHeld = false });
            ExpectNear(c.transform.position.x - start.x, controller.moveSpeed * 0.1f, 0.001f,
                "one tick moves +X by moveSpeed · dt");
            Expect(c.controller.moveCalls == 1, "exactly one CharacterController.Move per tick");
            Expect(Mathf.Approximately(c.GetComponent<PlayerCharacter>().input.x, 1f),
                "input adapter fills PlayerCharacter.input");

            // Not grounded: gravity accumulates downward every tick.
            float yAfterFirst = c.transform.position.y;
            controller.Tick(new PlayerInput());
            Expect(c.transform.position.y < yAfterFirst, "gravity pulls the player down");

            // Grounded + Space: the jump reaches the configured height.
            c.controller.isGrounded = true;
            controller.Tick(new PlayerInput { jumpHeld = true });
            Expect(c.transform.position.y > yAfterFirst, "grounded + Space jumps");
            Time.deltaTime = 0f;
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
