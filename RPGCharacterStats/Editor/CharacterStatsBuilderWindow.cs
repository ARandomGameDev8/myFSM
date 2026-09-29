// RPG Character & Stats System — Character Stats Builder (section 6).
//
// The visual panel: one row per stat (name, type, min, max, default — min/max
// hidden for bools), live validation flags duplicate/invalid names, min > max,
// and defaults outside the range. New/Load/Save work on .charstat text;
// presets are ordinary .charstat files in Assets/RPGPresets/CharStats, listed
// by the Presets menu and written by "Save as Preset".

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using RPGCharacterStats;

namespace RPGCharacterStats.EditorTools
{
    public class CharacterStatsBuilderWindow : EditorWindow
    {
        private const string PresetsFolder = "Assets/RPGPresets/CharStats";

        private Vector2 _scroll;
        private List<Row> _rows = new List<Row>();
        private string _currentPath = "";
        private string _status = "New schema";

        private sealed class Row
        {
            public string name = "";
            public StatType type;
            public bool hasMin;
            public float min;
            public bool hasMax;
            public float max;
            public string defaultValue = "0";
            public string error;
        }

        [MenuItem("RPG/Character Stats Builder", priority = 0)]
        public static void Open()
        {
            GetWindow<CharacterStatsBuilderWindow>("Character Stats Builder");
        }

        private void OnGUI()
        {
            DrawToolbar();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.HelpBox("What stats does this character type have? "
                + "This file is the vocabulary every character of its kind shares.", MessageType.None);

            DrawHeader();
            for (int i = 0; i < _rows.Count; i++) DrawRow(_rows[i], i);

            if (GUILayout.Button("+ Add Stat", GUILayout.Width(110)))
            {
                _rows.Add(new Row { defaultValue = "0" });
            }
            EditorGUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.Info);
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("New", EditorStyles.toolbarButton)) NewSchema();
            if (GUILayout.Button("Load", EditorStyles.toolbarButton)) LoadFile();
            if (GUILayout.Button("Save", EditorStyles.toolbarButton)) SaveFile();

            if (EditorGUILayout.DropdownButton(new GUIContent("Presets"), FocusType.Passive, EditorStyles.toolbarDropDown))
            {
                GenericMenu menu = new GenericMenu();
                menu.AddItem(new GUIContent("Save as Preset..."), false, SaveAsPreset);
                string[] presets = FindPresets();
                if (presets.Length > 0) menu.AddSeparator("");
                for (int i = 0; i < presets.Length; i++)
                {
                    string path = presets[i];
                    menu.AddItem(new GUIContent(Path.GetFileNameWithoutExtension(path)), false,
                        delegate { LoadFrom(path); });
                }
                menu.ShowAsContext();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Name", EditorStyles.boldLabel, GUILayout.MinWidth(120));
            EditorGUILayout.LabelField("Type", EditorStyles.boldLabel, GUILayout.Width(70));
            EditorGUILayout.LabelField("Min", EditorStyles.boldLabel, GUILayout.Width(90));
            EditorGUILayout.LabelField("Max", EditorStyles.boldLabel, GUILayout.Width(90));
            EditorGUILayout.LabelField("Default", EditorStyles.boldLabel, GUILayout.Width(90));
            GUILayout.Space(60);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawRow(Row row, int index)
        {
            EditorGUILayout.BeginHorizontal();
            row.name = EditorGUILayout.TextField(row.name, GUILayout.MinWidth(120));

            StatType newType = (StatType)EditorGUILayout.EnumPopup(row.type, GUILayout.Width(70));
            if (newType != row.type)
            {
                row.type = newType;
                row.defaultValue = newType == StatType.Bool ? "false" : "0";
            }

            if (row.type == StatType.Bool)
            {
                GUILayout.Space(90);
                GUILayout.Space(90);
                row.defaultValue = EditorGUILayout.Toggle(
                    row.defaultValue == "true", GUILayout.Width(90)) ? "true" : "false";
            }
            else
            {
                row.hasMin = EditorGUILayout.ToggleLeft("", row.hasMin, GUILayout.Width(14));
                row.min = EditorGUILayout.FloatField(row.hasMin ? row.min : 0f, GUILayout.Width(76));
                row.hasMax = EditorGUILayout.ToggleLeft("", row.hasMax, GUILayout.Width(14));
                row.max = EditorGUILayout.FloatField(row.hasMax ? row.max : 0f, GUILayout.Width(76));
                row.defaultValue = EditorGUILayout.TextField(row.defaultValue, GUILayout.Width(90));
            }

            if (GUILayout.Button("Remove", GUILayout.Width(60))) _rows.RemoveAt(index);
            EditorGUILayout.EndHorizontal();

            row.error = Validate(row);
            if (!string.IsNullOrEmpty(row.error))
                EditorGUILayout.HelpBox(row.error, MessageType.Error);
        }

        private static string Validate(Row row)
        {
            if (string.IsNullOrEmpty(row.name)) return "name is empty";
            if (!CharStatFormat.IsValidName(row.name))
                return "\"" + row.name + "\" is not a valid identifier";
            if (row.type != StatType.Bool && row.hasMin && row.hasMax && row.min > row.max)
                return "min > max";

            float d;
            if (row.type != StatType.Bool && !float.TryParse(row.defaultValue, out d))
                return "default \"" + row.defaultValue + "\" is not a number";

            if (row.type != StatType.Bool)
            {
                if (row.hasMin && d < row.min) return "default below min";
                if (row.hasMax && d > row.max) return "default above max";
            }
            return null;
        }

        private void ValidateAll()
        {
            HashSet<string> seen = new HashSet<string>();
            for (int i = 0; i < _rows.Count; i++)
            {
                _rows[i].error = Validate(_rows[i]);
                if (_rows[i].error == null && !seen.Add(_rows[i].name))
                    _rows[i].error = "duplicate name";
            }
        }

        // ---- persistence ----

        private void NewSchema()
        {
            _rows.Clear();
            _currentPath = "";
            _status = "New schema";
        }

        private void LoadFile()
        {
            string path = EditorUtility.OpenFilePanel("Load .charstat", "Assets", "charstat");
            if (!string.IsNullOrEmpty(path)) LoadFrom(path);
        }

        private void LoadFrom(string path)
        {
            try
            {
                StatSchema schema = CharStatFormat.Parse(File.ReadAllText(path));
                _rows.Clear();
                for (int i = 0; i < schema.entries.Count; i++)
                {
                    StatSchemaEntry e = schema.entries[i];
                    Row row = new Row();
                    row.name = e.name;
                    row.type = e.type;
                    row.hasMin = e.hasMin;
                    row.min = e.minValue;
                    row.hasMax = e.hasMax;
                    row.max = e.maxValue;
                    row.defaultValue = e.type == StatType.Bool
                        ? (e.hasDefault && e.defaultBool ? "true" : "false")
                        : e.hasDefault ? e.defaultFloat.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
                    _rows.Add(row);
                }
                _currentPath = path;
                _status = "Loaded " + path;
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Load failed", ex.Message, "OK");
            }
        }

        private void SaveFile()
        {
            ValidateAll();
            string text = BuildText();
            if (_currentPath == "")
            {
                string abs = EditorUtility.SaveFilePanel("Save .charstat", "Assets", "RPGStats", "charstat");
                if (abs == "") return;
                _currentPath = abs.Replace('\\', '/');
            }
            File.WriteAllText(_currentPath, text);
            AssetDatabase.Refresh();
            _status = "Saved " + _currentPath;
        }

        private void SaveAsPreset()
        {
            ValidateAll();
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
            string abs = EditorUtility.SaveFilePanel("Save preset", PresetsFolder, "NewPreset", "charstat");
            if (abs == "") return;
            File.WriteAllText(abs, BuildText());
            AssetDatabase.Refresh();
            _status = "Preset saved: " + abs;
        }

        private string BuildText()
        {
            StatSchema schema = new StatSchema();
            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                StatSchemaEntry e = new StatSchemaEntry();
                e.name = row.name;
                e.type = row.type;
                e.hasMin = row.hasMin;
                e.minValue = row.min;
                e.hasMax = row.hasMax;
                e.maxValue = row.max;
                if (row.type != StatType.Bool || row.defaultValue == "true")
                {
                    e.hasDefault = true;
                    if (row.type == StatType.Bool) e.defaultBool = row.defaultValue == "true";
                    else
                    {
                        float d;
                        float.TryParse(row.defaultValue, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out d);
                        e.defaultFloat = d;
                        e.defaultInt = (int)d;
                    }
                }
                schema.entries.Add(e);
            }
            return CharStatFormat.Write(schema);
        }

        private static string[] FindPresets()
        {
            if (!AssetDatabase.IsValidFolder(PresetsFolder)) return new string[0];
            return AssetDatabase.FindAssets("t:TextAsset", new[] { PresetsFolder });
        }
    }
}
