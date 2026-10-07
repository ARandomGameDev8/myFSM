// RPG Character & Stats System — Character Builder (section 7).
//
// The workflow window: metadata → dimension → kind → physics → (3D visual /
// player camera choices) → (NPC: AI script) → .charstat file → per-stat value fields generated from it →
// .gameplaystat file → Build. Build-time validation (7.5): unique ID, values
// within min/max, gameplaystat formulas compatible with the charstat (9.1).
// Built characters land in the registry held by CharacterBuilderServer; the
// Registry window (section 17) edits and spawns them.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using RPGCharacterStats;

namespace RPGCharacterStats.EditorTools
{
    public class CharacterBuilderWindow : ServerOwnedBuilderWindow
    {
        // metadata
        private string _name = "";
        private string _id = "";
        private string _description = "";

        // configuration
        private CharacterDimension _dimension = CharacterDimension.ThreeD;
        private CharacterKind _kind = CharacterKind.NPC;
        private PhysicsMode _physics = PhysicsMode.PhysicsBased;
        private CharacterVisual3D _visual3D = CharacterVisual3D.Capsule;
        private CharacterCameraMode _cameraMode = CharacterCameraMode.DoNotAlter;
        private GameObject _modelPrefab;
        private string _modelResourcesPath = "";

        private static readonly string[] CameraModeLabels =
        {
            "A) Do not alter camera",
            "B) First person",
            "C) Third person",
        };

        // NPC AI
        private MonoScript _aiScript;

        // stat sources
        private string _charStatPath = "";
        private StatSchema _schema = new StatSchema();
        private readonly List<StatValueOverride> _values = new List<StatValueOverride>();

        private string _gameplayStatPath = "";
        private string _gameplayStatText = "";

        private Vector2 _scroll;
        private List<string> _buildErrors = new List<string>();

        [MenuItem("RPG/Character Builder", priority = 2)]
        public static void Open()
        {
            CharacterServerAccess.Resolve(true).OpenCharacterBuilder();
        }

        internal static IEditorBuilderProduct CreateFromFactory(CharacterBuilderServer owner)
        {
            CharacterBuilderWindow window = GetWindow<CharacterBuilderWindow>("Character Builder");
            window.BindOwner(owner);
            return window;
        }

        private void OnGUI()
        {
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;

                EditorGUILayout.LabelField("Identity", EditorStyles.boldLabel);
                _name = EditorGUILayout.TextField("Name", _name);
                _id = EditorGUILayout.TextField("Character ID", _id);
                _description = EditorGUILayout.TextField("Description", _description);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel);
                CharacterDimension selectedDimension = (CharacterDimension)EditorGUILayout.EnumPopup(
                    "Dimension", _dimension);
                if (selectedDimension != _dimension)
                {
                    _dimension = selectedDimension;
                    GUIUtility.ExitGUI();
                }
                CharacterKind selectedKind = (CharacterKind)EditorGUILayout.EnumPopup("Kind", _kind);
                if (selectedKind != _kind)
                {
                    _kind = selectedKind;
                    GUIUtility.ExitGUI();
                }
                _physics = (PhysicsMode)EditorGUILayout.EnumPopup("Physics", _physics);

                if (_dimension == CharacterDimension.ThreeD)
                {
                    CharacterVisual3D selectedVisual = (CharacterVisual3D)EditorGUILayout.EnumPopup(
                        "3D Visual", _visual3D);
                    if (selectedVisual != _visual3D)
                    {
                        _visual3D = selectedVisual;
                        GUIUtility.ExitGUI();
                    }
                    if (_visual3D == CharacterVisual3D.Model)
                    {
                        GameObject selectedModel = (GameObject)EditorGUILayout.ObjectField(
                            "Model Prefab", _modelPrefab, typeof(GameObject), false);
                        if (selectedModel != _modelPrefab)
                        {
                            _modelPrefab = selectedModel;
                            GUIUtility.ExitGUI();
                        }

                        if (_modelPrefab != null)
                        {
                            string resourcePath;
                            string problem;
                            if (TryGetModelResourcesPath(out resourcePath, out problem))
                                EditorGUILayout.HelpBox("Loaded at runtime from Resources/" + resourcePath, MessageType.Info);
                            else
                                EditorGUILayout.HelpBox(problem, MessageType.Warning);
                        }
                        else
                        {
                            EditorGUILayout.HelpBox("Choose a model asset located inside a Resources folder.", MessageType.None);
                        }
                    }
                }

                if (_kind == CharacterKind.Player)
                {
                    int cameraSelection = EditorGUILayout.Popup("Player Camera",
                        (int)_cameraMode, CameraModeLabels);
                    if (cameraSelection != (int)_cameraMode)
                    {
                        _cameraMode = (CharacterCameraMode)cameraSelection;
                        GUIUtility.ExitGUI();
                    }
                    if (_dimension != CharacterDimension.ThreeD
                        && _cameraMode != CharacterCameraMode.DoNotAlter)
                        EditorGUILayout.HelpBox("First- and third-person modes require a 3D Player. Choose DoNotAlter or switch Dimension to 3D.", MessageType.Warning);
                    else if (_cameraMode == CharacterCameraMode.DoNotAlter)
                        EditorGUILayout.HelpBox("Leaves every scene camera and the current movement behavior untouched.", MessageType.None);
                    else if (_cameraMode == CharacterCameraMode.FirstPerson)
                        EditorGUILayout.HelpBox("Locks the view to first person. Mouse looks; WASD moves relative to the view.", MessageType.None);
                    else
                        EditorGUILayout.HelpBox("Follows the player in third person. Mouse orbits; WASD moves relative to the view.", MessageType.None);
                }

                if (_kind == CharacterKind.NPC)
                {
                    MonoScript selectedAIScript = (MonoScript)EditorGUILayout.ObjectField(
                        "AI Script", _aiScript, typeof(MonoScript), false);
                    if (selectedAIScript != _aiScript)
                    {
                        _aiScript = selectedAIScript;
                        GUIUtility.ExitGUI();
                    }
                    if (_aiScript != null)
                    {
                        string problem = AIInstanceContract.Validate(
                            _aiScript.GetClass(), _aiScript.text);
                        if (problem != null)
                            EditorGUILayout.HelpBox(problem, MessageType.Error);
                        else
                            EditorGUILayout.HelpBox(_aiScript.name + " is a valid partial AIInstance subclass.", MessageType.Info);
                    }
                }

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("CharStat file", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    _charStatPath = EditorGUILayout.TextField(_charStatPath);
                    if (GUILayout.Button("Browse...", GUILayout.Width(80)))
                    {
                        string path = EditorUtility.OpenFilePanel("Load .charstat", "Assets", "charstat");
                        if (!string.IsNullOrEmpty(path))
                        {
                            LoadCharStat(path);
                            GUIUtility.ExitGUI();
                        }
                    }
                }

                if (_schema.entries.Count > 0)
                {
                    EditorGUILayout.LabelField("Character values", EditorStyles.boldLabel);
                    DrawValueFields();
                }

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("GameplayStat file", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    _gameplayStatPath = EditorGUILayout.TextField(_gameplayStatPath);
                    if (GUILayout.Button("Browse...", GUILayout.Width(80)))
                    {
                        string path = EditorUtility.OpenFilePanel("Load .gameplaystat", "Assets", "gameplaystat");
                        if (!string.IsNullOrEmpty(path))
                        {
                            _gameplayStatPath = path;
                            EditorActionGuard.Run("Load GameplayStat failed", delegate
                            {
                                _gameplayStatText = File.ReadAllText(path);
                            });
                            GUIUtility.ExitGUI();
                        }
                    }
                }

                if (!string.IsNullOrEmpty(_gameplayStatText))
                    EditorGUILayout.HelpBox("GameplayStat file loaded (" +
                        _gameplayStatText.Split('\n').Length + " lines). Compatibility is validated on Build.", MessageType.None);

                EditorGUILayout.Space();
                if (GUILayout.Button("Build", GUILayout.Height(30)))
                {
                    EditorActionGuard.Run("Build failed", Build);
                    GUIUtility.ExitGUI();
                }

                for (int i = 0; i < _buildErrors.Count; i++)
                    EditorGUILayout.HelpBox(_buildErrors[i], MessageType.Error);
            }
        }

        /// <section 7.2: value fields generated from the selected .charstat,
        /// typed, starting at the preset default, clamped to min/max.>
        private void DrawValueFields()
        {
            for (int i = 0; i < _schema.entries.Count; i++)
            {
                StatSchemaEntry e = _schema.entries[i];
                StatValueOverride value = FindValue(e.name);
                if (value == null)
                {
                    value = NewOverrideFromDefault(e);
                    _values.Add(value);
                }

                switch (e.type)
                {
                    case StatType.Bool:
                        value.boolValue = EditorGUILayout.Toggle(e.name, value.boolValue);
                        break;
                    case StatType.Int:
                        value.intValue = EditorGUILayout.IntField(e.name, value.intValue);
                        if (e.hasMin && value.intValue < (int)e.minValue) value.intValue = (int)e.minValue;
                        if (e.hasMax && value.intValue > (int)e.maxValue) value.intValue = (int)e.maxValue;
                        break;
                    default:
                        value.floatValue = EditorGUILayout.FloatField(e.name, value.floatValue);
                        if (e.hasMin && value.floatValue < e.minValue) value.floatValue = e.minValue;
                        if (e.hasMax && value.floatValue > e.maxValue) value.floatValue = e.maxValue;
                        break;
                }

                string range = "";
                if (e.hasMin || e.hasMax)
                    range = " (" + (e.hasMin ? e.minValue.ToString() : "−∞") + " … " +
                        (e.hasMax ? e.maxValue.ToString() : "∞") + ")";
                EditorGUILayout.LabelField("", e.type + range, EditorStyles.miniLabel);
            }
        }

        private static StatValueOverride NewOverrideFromDefault(StatSchemaEntry e)
        {
            switch (e.type)
            {
                case StatType.Bool:
                    return StatValueOverride.Bool(e.name, e.hasDefault && e.defaultBool);
                case StatType.Int:
                    return StatValueOverride.Int(e.name, e.hasDefault ? e.defaultInt : 0);
                default:
                    return StatValueOverride.Float(e.name, e.hasDefault ? e.defaultFloat : 0f);
            }
        }

        private StatValueOverride FindValue(string statName)
        {
            for (int i = 0; i < _values.Count; i++)
            {
                if (_values[i].statName == statName) return _values[i];
            }
            return null;
        }

        private void LoadCharStat(string path)
        {
            try
            {
                _schema = CharStatFormat.Parse(File.ReadAllText(path));
                _charStatPath = path;
                _values.Clear(); // regenerate fields from the new schema
            }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Bad .charstat", ex.Message, "OK");
            }
        }

        // ---- build (section 7.5 validation, then save) ----

        private void Build()
        {
            _buildErrors.Clear();
            CharacterBuilderServer server = OwnerServer;

            if (string.IsNullOrEmpty(_name)) _buildErrors.Add("Name is required.");
            if (string.IsNullOrEmpty(_id)) _buildErrors.Add("Character ID is required.");
            else if (server.IsIdTaken(_id)) _buildErrors.Add("Character ID \"" + _id + "\" is already in the database.");

            if (_schema.entries.Count == 0)
                _buildErrors.Add("A .charstat file must be selected before values can be entered (7.5).");

            if (_kind == CharacterKind.NPC && _aiScript == null)
                _buildErrors.Add("NPC characters need an AIInstance script assigned.");

            if (_kind == CharacterKind.Player && _dimension != CharacterDimension.ThreeD
                && _cameraMode != CharacterCameraMode.DoNotAlter)
                _buildErrors.Add("First- and third-person camera modes require a 3D Player.");

            _modelResourcesPath = "";
            if (_dimension == CharacterDimension.ThreeD && _visual3D == CharacterVisual3D.Model)
            {
                string resourcePath;
                string problem;
                if (_modelPrefab == null)
                    _buildErrors.Add("Choose a model prefab for the 3D Model visual.");
                else if (!TryGetModelResourcesPath(out resourcePath, out problem))
                    _buildErrors.Add(problem);
                else
                    _modelResourcesPath = resourcePath;
            }

            if (_schema.entries.Count > 0)
            {
                // Values already clamp in the UI; re-verify anyway (7.5).
                for (int i = 0; i < _values.Count; i++)
                {
                    StatSchemaEntry e = _schema.Find(_values[i].statName);
                    if (e == null) continue;
                    if (e.type != StatType.Bool)
                    {
                        float v = e.type == StatType.Int ? _values[i].intValue : _values[i].floatValue;
                        float clamped = e.Clamp(v);
                        if (clamped != v)
                            _buildErrors.Add(_values[i].statName + " is outside its min/max range.");
                    }
                }

                // Gameplaystat compatibility (9.1): every formula's primary
                // must match a stat by name AND type.
                if (!string.IsNullOrEmpty(_gameplayStatText))
                {
                    try
                    {
                        List<ParsedFormula> parsed = GameplayStatFormat.Parse(_gameplayStatText);
                        for (int i = 0; i < parsed.Count; i++)
                        {
                            FormulaParam primary = parsed[i].Primary;
                            if (primary == null) continue;
                            StatSchemaEntry stat = _schema.Find(primary.Name);
                            if (stat == null)
                                _buildErrors.Add(parsed[i].Name + ": primary \"" + primary.Name +
                                    "\" is not a stat in the selected .charstat.");
                            else if (stat.type != primary.ParamType)
                                _buildErrors.Add(parsed[i].Name + ": primary \"" + primary.Name +
                                    "\" is " + primary.ParamType + " but the stat is " + stat.type + ".");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        _buildErrors.Add("GameplayStat file does not parse: " + ex.Message);
                    }
                }
            }

            if (_buildErrors.Count > 0) return;

            // Single write path: the server persists the definition into the
            // CharacterDB (Assets/Resources/CharacterDB) and caches it in its
            // LRU registry. This window never writes the cache directly.
            CharacterDefinition def = CreateDefinitionAsset();
            if (def == null) return;

            CharacterEntry entry = server.SaveDefinition(def);
            Debug.Log("[RPGStats] built " + def.characterName + " → " +
                (entry != null ? "written to CharacterDB + cached in the registry"
                               : "cache FAILED"));
            _status = def.characterName + " saved to CharacterDB and registered.";
        }

        private string _status = "";

        private bool TryGetModelResourcesPath(out string resourcePath, out string error)
        {
            resourcePath = "";
            error = null;
            string assetPath = _modelPrefab != null ? AssetDatabase.GetAssetPath(_modelPrefab) : "";
            assetPath = (assetPath ?? "").Replace('\\', '/');
            if (string.IsNullOrEmpty(assetPath)
                || AssetDatabase.LoadMainAssetAtPath(assetPath) != _modelPrefab)
            {
                error = "Select the root model/prefab asset, not a child sub-asset or scene object.";
                return false;
            }

            const string resourcesSegment = "/Resources/";
            int resourcesIndex = assetPath.IndexOf(resourcesSegment, StringComparison.OrdinalIgnoreCase);
            if (resourcesIndex < 0)
            {
                error = "The model must be inside a Resources folder so the CharacterDB record can load it in a build.";
                return false;
            }

            resourcePath = assetPath.Substring(resourcesIndex + resourcesSegment.Length);
            string extension = Path.GetExtension(resourcePath);
            if (!string.IsNullOrEmpty(extension))
                resourcePath = resourcePath.Substring(0, resourcePath.Length - extension.Length);
            if (string.IsNullOrEmpty(resourcePath))
            {
                error = "Could not derive a Resources path from the selected model asset.";
                return false;
            }
            return true;
        }

        /// <summary>Creates and configures the in-memory definition. The
        /// SERVER persists it (CharacterDB.Save → Assets/Resources/CharacterDB);
        /// this method must not write assets itself, or characters would
        /// exist in two places.</summary>
        private CharacterDefinition CreateDefinitionAsset()
        {
            CharacterDefinition def;
            if (_kind == CharacterKind.Player)
            {
                def = ScriptableObject.CreateInstance<PlayerCharacterDefinition>();
            }
            else
            {
                // Enemy vs Friendly is a designer retarget in the inspector;
                // the builder defaults to Enemy.
                def = ScriptableObject.CreateInstance<EnemyCharacterDefinition>();
            }

            def.characterID = _id;
            def.characterName = _name;
            def.description = _description;
            def.dimension = _dimension;
            def.kind = _kind;
            def.physicsMode = _physics;
            def.visual3D = _visual3D;
            def.modelResourcesPath = _dimension == CharacterDimension.ThreeD
                && _visual3D == CharacterVisual3D.Model ? _modelResourcesPath : "";
            def.cameraMode = _kind == CharacterKind.Player && _dimension == CharacterDimension.ThreeD
                ? _cameraMode : CharacterCameraMode.DoNotAlter;
            def.charStatText = File.Exists(_charStatPath) ? File.ReadAllText(_charStatPath) : "";
            def.gameplayStatText = _gameplayStatText;
            def.statValues = new List<StatValueOverride>(_values);

            if (_kind == CharacterKind.NPC && _aiScript != null)
            {
                NPCCharacterDefinition npcDef = def as NPCCharacterDefinition;
                if (npcDef != null)
                {
                    npcDef.aiInstanceType = new SerializedType
                    {
                        typeName = _aiScript.GetClass().FullName + ", " + _aiScript.GetClass().Assembly.GetName().Name
                    };
                }
            }

            return def;
        }


    }
}
