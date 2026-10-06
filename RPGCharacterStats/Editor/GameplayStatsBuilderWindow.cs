// RPG Character & Stats System — Gameplay Stats Builder (section 10).
//
// Pick a .charstat, get one editable row per character stat: a "[type] [name]:"
// label (type defaults to the stat's, name to stat + "Gameplay", both
// editable) and a large code field for the formula text. The parser + checker
// run on every change and the errors are shown inline with line:column
// (section 10.1). Empty rows are skipped on save. New/Load/Save/presets work
// on .gameplaystat text.

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using RPGCharacterStats;

namespace RPGCharacterStats.EditorTools
{
    public class GameplayStatsBuilderWindow : EditorWindow
    {
        private const string PresetsFolder = "Assets/RPGPresets/GameplayStats";

        private string _charStatText = "";
        private string _charStatPath = "";
        private StatSchema _schema = new StatSchema();

        private List<Row> _rows = new List<Row>();
        private Vector2 _scroll;
        private string _currentPath = "";
        private string _status = "Select a .charstat to begin";

        /// <summary>Every non-empty row's formula name → declared type, so a
        /// formula can reference another row's output (the checker injects it
        /// as an implicit GameplayStatInput).</summary>
        private Dictionary<string, StatType> _formulaTypes = new Dictionary<string, StatType>();

        private sealed class Row
        {
            public string statName;          // the character stat this row came from
            public StatType type;
            public string formulaName;
            public string text = "";
            public List<string> errors = new List<string>();
        }

        [MenuItem("RPG/Gameplay Stats Builder", priority = 1)]
        public static void Open()
        {
            GetWindow<GameplayStatsBuilderWindow>("Gameplay Stats Builder");
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("New", EditorStyles.toolbarButton))
                {
                    EditorActionGuard.Run("New document failed", NewDocument);
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("Load", EditorStyles.toolbarButton))
                {
                    EditorActionGuard.Run("Load failed", LoadFile);
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("Save", EditorStyles.toolbarButton))
                    EditorActionGuard.Run("Save failed", SaveFile);

                if (EditorGUILayout.DropdownButton(new GUIContent("Presets"), FocusType.Passive, EditorStyles.toolbarDropDown))
                {
                    GenericMenu menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Save as Preset..."), false,
                        delegate { EditorActionGuard.Run("Save preset failed", SaveAsPreset); });
                    string[] presets = FindPresets();
                    if (presets.Length > 0) menu.AddSeparator("");
                    for (int i = 0; i < presets.Length; i++)
                    {
                        string path = presets[i];
                        menu.AddItem(new GUIContent(Path.GetFileNameWithoutExtension(path)), false,
                            delegate { EditorActionGuard.Run("Load preset failed", delegate { LoadFrom(path); }); });
                    }
                    menu.ShowAsContext();
                }
                GUILayout.FlexibleSpace();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _charStatPath = EditorGUILayout.TextField("CharStat file", _charStatPath);
                if (GUILayout.Button("Browse...", GUILayout.Width(80)))
                {
                    string path = EditorUtility.OpenFilePanel("Load .charstat", "Assets", "charstat");
                    if (!string.IsNullOrEmpty(path))
                    {
                        EditorActionGuard.Run("Load .charstat failed", delegate
                        {
                            LoadCharStat(File.ReadAllText(path), path);
                        });
                        GUIUtility.ExitGUI();
                    }
                }
            }

            if (GUILayout.Button("Rebuild rows from CharStat", GUILayout.Width(200)))
            {
                EditorActionGuard.Run("Rebuild rows failed", RebuildRows);
                GUIUtility.ExitGUI();
            }

            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;
                for (int i = 0; i < _rows.Count; i++) DrawRow(_rows[i]);
            }

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.Info);
        }

        private void DrawRow(Row row)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    StatType newType = (StatType)EditorGUILayout.EnumPopup(row.type, GUILayout.Width(60));
                    if (newType != row.type)
                    {
                        row.type = newType;
                        ValidateRow(row);
                        GUIUtility.ExitGUI();
                    }

                    string newFormulaName = EditorGUILayout.TextField(row.formulaName);
                    if (newFormulaName != row.formulaName)
                    {
                        row.formulaName = newFormulaName;
                        ValidateRow(row);
                        GUIUtility.ExitGUI();
                    }
                    EditorGUILayout.LabelField(":", GUILayout.Width(8));
                }

                string newText = EditorGUILayout.TextArea(row.text ?? "", GUILayout.MinHeight(72));
                if (newText != row.text)
                {
                    row.text = newText;
                    ValidateRow(row);
                    GUIUtility.ExitGUI();
                }

                for (int i = 0; i < row.errors.Count; i++)
                    EditorGUILayout.HelpBox(row.errors[i], MessageType.Error);
            }
        }

        /// <summary>One row per character stat (section 10.1), names defaulted
        /// to stat + "Gameplay" so they never collide with the stat itself.</summary>
        private void RebuildRows()
        {
            List<Row> fresh = new List<Row>();
            for (int i = 0; i < _schema.entries.Count; i++)
            {
                StatSchemaEntry e = _schema.entries[i];
                Row existing = FindRow(e.name);
                Row row = existing ?? new Row();
                row.statName = e.name;
                row.type = existing != null ? existing.type : e.type;
                row.formulaName = existing != null ? existing.formulaName : e.name + "Gameplay";
                row.text = existing != null ? existing.text : "";
                ValidateRow(row);
                fresh.Add(row);
            }
            _rows = fresh;
            _status = _rows.Count + " formula rows (one per character stat)";
        }

        private Row FindRow(string statName)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].statName == statName) return _rows[i];
            }
            return null;
        }

        /// <summary>Live parse + check (section 11's error list). The row is
        /// skipped at save time when the text is empty — parsing only runs
        /// when there is something to parse.</summary>
        private void ValidateRow(Row row)
        {
            UpdateFormulaTypes();
            row.errors.Clear();
            if (string.IsNullOrWhiteSpace(row.text)) return;

            try
            {
                ParsedFormula parsed = GameplayStatFormat.ParseRow(row.type, row.formulaName, row.text);
                FormulaCheckResult result = FormulaChecker.Check(parsed, _schema, new Blackboard(), _formulaTypes);
                for (int i = 0; i < result.Errors.Count; i++)
                {
                    row.errors.Add(result.Errors[i].ToString());
                }
            }
            catch (FormulaParseException ex)
            {
                row.errors.Add(ex.Message);
            }
            catch (FormulaLexException ex)
            {
                row.errors.Add(ex.Message);
            }
        }

        /// <summary>Rebuild the name → type map the checker uses for
        /// cross-formula references, from the current non-empty rows.</summary>
        private void UpdateFormulaTypes()
        {
            _formulaTypes = new Dictionary<string, StatType>();
            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                if (!string.IsNullOrWhiteSpace(row.text) && !string.IsNullOrEmpty(row.formulaName))
                    _formulaTypes[row.formulaName] = row.type;
            }
        }

        // ---- persistence ----

        private void NewDocument()
        {
            _rows.Clear();
            _schema = new StatSchema();
            _formulaTypes = new Dictionary<string, StatType>();
            _charStatText = "";
            _charStatPath = "";
            _currentPath = "";
            _status = "New document — select a .charstat";
        }

        private void LoadCharStat(string text, string path)
        {
            try
            {
                _schema = CharStatFormat.Parse(text);
                _charStatText = text;
                _charStatPath = path;
                RebuildRows();
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Bad .charstat", ex.Message, "OK");
            }
        }

        private void LoadFile()
        {
            string path = EditorUtility.OpenFilePanel("Load .gameplaystat", "Assets", "gameplaystat");
            if (string.IsNullOrEmpty(path)) return;
            LoadFrom(path);
        }

        private void LoadFrom(string path)
        {
            try
            {
                string text = File.ReadAllText(path);
                _currentPath = path;

                // Loose read keeps formula texts editable even if one row
                // fails to parse right now (the builder is an editor, not a
                // gatekeeper).
                List<GameplayStatFormula> formulas = GameplayStatFormat.ReadLoose(text);
                _rows.Clear();
                for (int i = 0; i < formulas.Count; i++)
                {
                    GameplayStatFormula f = formulas[i];
                    Row row = new Row();
                    row.statName = f.primaryInput;
                    row.type = f.type;
                    row.formulaName = f.name;
                    row.text = f.formulaExpression;
                    ValidateRow(row);
                    _rows.Add(row);
                }
                _status = "Loaded " + path;
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Load failed", ex.Message, "OK");
            }
        }

        private void SaveFile()
        {
            string text = BuildText();
            if (text == null) return;
            if (_currentPath == "")
            {
                string abs = EditorUtility.SaveFilePanel("Save .gameplaystat", "Assets", "RPGGameplay", "gameplaystat");
                if (abs == "") return;
                _currentPath = abs.Replace('\\', '/');
            }
            File.WriteAllText(_currentPath, text);
            AssetDatabase.Refresh();
            _status = "Saved " + _currentPath;
        }

        private void SaveAsPreset()
        {
            string text = BuildText();
            if (text == null) return;

            if (!AssetDatabase.IsValidFolder(PresetsFolder))
            {
                string[] parts = PresetsFolder.Split('/');
                string accumulated = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = accumulated + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(accumulated, parts[i]);
                    accumulated = next;
                }
            }
            string abs = EditorUtility.SaveFilePanel("Save preset", PresetsFolder, "NewGameplayPreset", "gameplaystat");
            if (abs == "") return;
            File.WriteAllText(abs, text);
            AssetDatabase.Refresh();
            _status = "Preset saved: " + abs;
        }

        /// <summary>Serialize the non-empty rows: parse each row, harvest the
        /// checker's signature into the data model, write canonical text.</summary>
        private string BuildText()
        {
            List<GameplayStatFormula> formulas = new List<GameplayStatFormula>();
            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                if (string.IsNullOrWhiteSpace(row.text)) continue; // skipped (10.1)

                try
                {
                    ParsedFormula parsed = GameplayStatFormat.ParseRow(row.type, row.formulaName, row.text);
                    FormulaCheckResult result = FormulaChecker.Check(parsed, _schema, new Blackboard(), _formulaTypes);

                    GameplayStatFormula f = new GameplayStatFormula();
                    f.name = parsed.Name;
                    f.type = parsed.ReturnType;
                    f.primaryInput = FormulaChecker.TypeWord(parsed.Primary.ParamType) + " " + parsed.Primary.Name;
                    f.formulaExpression = row.text.Trim();

                    f.secondaryInputs = new List<SecondaryInput>();
                    for (int s = 0; s < result.Signature.secondaries.Count; s++)
                    {
                        SecondaryInput input = result.Signature.secondaries[s];
                        if (input is CharacterStatInput || input is GameplayStatInput || input is BlackboardInput)
                            f.secondaryInputs.Add(input);
                        else
                            f.secondaryInputs.Add(input); // ConstantInput keeps its default value
                    }
                    formulas.Add(f);
                }
                catch (System.Exception ex)
                {
                    EditorUtility.DisplayDialog("Cannot save — row \"" + row.formulaName + "\" has errors", ex.Message, "OK");
                    return null;
                }
            }
            return GameplayStatFormat.Write(formulas);
        }

        private static string[] FindPresets()
        {
            if (!AssetDatabase.IsValidFolder(PresetsFolder)) return new string[0];
            string[] guids = AssetDatabase.FindAssets("", new[] { PresetsFolder });
            List<string> paths = new List<string>();
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.Equals(Path.GetExtension(path), ".gameplaystat",
                    System.StringComparison.OrdinalIgnoreCase))
                    paths.Add(path);
            }
            return paths.ToArray();
        }
    }
}
