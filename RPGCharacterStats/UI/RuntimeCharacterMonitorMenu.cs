// RPG Character & Stats System — in-game character/stat monitor.
//
// This component is added automatically by CharacterBuilderServer in Play
// Mode. It uses UGUI (not IMGUI), so its runtime menu does not participate in
// Unity's editor IMGUI layout/event loop.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
#if UNITY_2019_4_OR_NEWER
using UnityEngine.EventSystems;
using UnityEngine.UI;
#endif

namespace RPGCharacterStats
{
#if UNITY_2019_4_OR_NEWER
    /// <summary>
    /// A small runtime inspector for spawned Character components. Press F2 or
    /// click the launcher, select a character, then switch between its base
    /// stats, derived gameplay stats, and the live server trace.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("RPG Character Stats/Runtime Character Monitor")]
    public class RuntimeCharacterMonitorMenu : MonoBehaviour
    {
        public KeyCode toggleKey = KeyCode.F2;
        [Range(8, 100)] public int historyLimit = 36;
        public float rosterRefreshInterval = 0.5f;
        public float liveSampleInterval = 0.2f;

        private enum InspectView
        {
            CharacterStats,
            GameplayStats,
            ServerMonitor,
        }

        private readonly List<Character> _characters = new List<Character>();
        private readonly List<string> _history = new List<string>();
        private readonly Dictionary<string, string> _characterSnapshots = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _gameplaySnapshots = new Dictionary<string, string>();
        private readonly Dictionary<GameplayStatClient, int> _clientPassCounts =
            new Dictionary<GameplayStatClient, int>();

        private Character _selected;
        private CharacterStats _observedCharacterStats;
        private GameplayStatsServer _observedServer;
        private int _recalculationPasses;
        private float _nextRosterRefresh;
        private float _nextLiveSample;
        private bool _detailsDirty = true;
        private bool _isOpen;

        private GameObject _canvasObject;
        private GameObject _panelObject;
        private GameObject _launcherObject;
        private GameObject _createdEventSystem;
        private Text _launcherLabel;
        private Text _rosterTitle;
        private Text _selectionTitle;
        private Text _detailText;
        private RectTransform _rosterContent;
        private RectTransform _rosterViewport;
        private RectTransform _detailContent;
        private RectTransform _detailViewport;
        private ScrollRect _rosterScroll;
        private ScrollRect _detailScroll;
        private Button _characterTab;
        private Button _gameplayTab;
        private Button _serverTab;
        private InspectView _view = InspectView.CharacterStats;

        private static Font _runtimeFont;

        private static readonly Color CanvasPanelColor = new Color(0.055f, 0.075f, 0.105f, 0.97f);
        private static readonly Color SectionColor = new Color(0.085f, 0.115f, 0.155f, 0.98f);
        private static readonly Color RowColor = new Color(0.12f, 0.16f, 0.21f, 1f);
        private static readonly Color SelectedRowColor = new Color(0.10f, 0.34f, 0.43f, 1f);
        private static readonly Color TabColor = new Color(0.12f, 0.16f, 0.21f, 1f);
        private static readonly Color ActiveTabColor = new Color(0.05f, 0.47f, 0.52f, 1f);
        private static readonly Color TextColor = new Color(0.88f, 0.93f, 0.97f, 1f);
        private static readonly Color MutedTextColor = new Color(0.58f, 0.68f, 0.77f, 1f);
        private static readonly Color AccentColor = new Color(0.30f, 0.85f, 0.84f, 1f);

        private void Start()
        {
            if (!Application.isPlaying) return;

            BuildUi();
            RefreshCharacters(true);
            SetOpen(false);
        }

        private void Update()
        {
            if (!Application.isPlaying) return;

            if (Input.GetKeyDown(toggleKey)) SetOpen(!_isOpen);

            if (Time.unscaledTime >= _nextRosterRefresh)
            {
                _nextRosterRefresh = Time.unscaledTime + Mathf.Max(0.1f, rosterRefreshInterval);
                RefreshCharacters(false);
            }

            if (_selected != null && Time.unscaledTime >= _nextLiveSample)
            {
                _nextLiveSample = Time.unscaledTime + Mathf.Max(0.05f, liveSampleInterval);
                SampleForUnannouncedChanges();
            }

            if (_detailsDirty && _isOpen) RefreshDetails();
        }

        private void OnDestroy()
        {
            UnsubscribeSelected();
            if (_createdEventSystem != null) UnityEngine.Object.Destroy(_createdEventSystem);
        }

        private void BuildUi()
        {
            EnsureEventSystem();

            _canvasObject = new GameObject("RPG Character Monitor Canvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            _canvasObject.transform.SetParent(transform, false);

            Canvas canvas = _canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 120;

            CanvasScaler scaler = _canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280f, 720f);
            scaler.matchWidthOrHeight = 0.5f;

            RectTransform canvasRect = _canvasObject.GetComponent<RectTransform>();

            _launcherObject = CreateButton(
                "Character Monitor Launcher", "CHARACTERS  (F2)", canvasRect,
                new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(20f, 18f),
                new Vector2(190f, 42f), ActiveTabColor,
                delegate { SetOpen(true); }, out _launcherLabel).gameObject;
            _launcherLabel.fontSize = 15;
            _launcherLabel.fontStyle = FontStyle.Bold;

            RectTransform panelRect = CreateRect("Monitor Panel", canvasRect);
            Place(panelRect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(1030f, 620f));
            Image panelImage = panelRect.gameObject.AddComponent<Image>();
            panelImage.color = CanvasPanelColor;
            _panelObject = panelRect.gameObject;

            Text title = CreateText(panelRect, "Panel Title", "RPG  /  LIVE CHARACTER MONITOR",
                22, AccentColor, TextAnchor.MiddleLeft);
            Place(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(20f, -15f), new Vector2(710f, 34f));
            title.fontStyle = FontStyle.Bold;

            Text subtitle = CreateText(panelRect, "Panel Subtitle",
                "Select a character, then inspect its base stats, derived stats, or calculation server.",
                13, MutedTextColor, TextAnchor.MiddleLeft);
            Place(subtitle.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(20f, -48f), new Vector2(760f, 24f));

            Text closeLabel;
            CreateButton("Close Monitor", "CLOSE", panelRect,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-16f, -18f),
                new Vector2(82f, 34f), RowColor,
                delegate { SetOpen(false); }, out closeLabel);

            RectTransform rosterPanel = CreateRect("Character Roster", panelRect);
            Place(rosterPanel, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(16f, -78f), new Vector2(260f, 526f));
            rosterPanel.gameObject.AddComponent<Image>().color = SectionColor;

            _rosterTitle = CreateText(rosterPanel, "Roster Heading", "SPAWNED CHARACTERS (0)",
                15, TextColor, TextAnchor.MiddleLeft);
            Place(_rosterTitle.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(13f, -11f), new Vector2(232f, 30f));
            _rosterTitle.fontStyle = FontStyle.Bold;

            _rosterScroll = CreateScrollView("Character Roster Scroll", rosterPanel,
                new Vector2(12f, -50f), new Vector2(236f, 462f),
                out _rosterContent, out _rosterViewport);

            RectTransform detailPanel = CreateRect("Character Details", panelRect);
            Place(detailPanel, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(292f, -78f), new Vector2(722f, 526f));
            detailPanel.gameObject.AddComponent<Image>().color = SectionColor;

            _selectionTitle = CreateText(detailPanel, "Selection Heading", "NO CHARACTER SELECTED",
                16, TextColor, TextAnchor.MiddleLeft);
            Place(_selectionTitle.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(16f, -12f), new Vector2(680f, 30f));
            _selectionTitle.fontStyle = FontStyle.Bold;

            Text tabLabel;
            _characterTab = CreateButton("Character Stats Tab", "CHARACTER STATS", detailPanel,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(16f, -52f),
                new Vector2(142f, 32f), ActiveTabColor,
                delegate { SetView(InspectView.CharacterStats); }, out tabLabel);
            tabLabel.fontSize = 12;

            _gameplayTab = CreateButton("Gameplay Stats Tab", "GAMEPLAY STATS", detailPanel,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(166f, -52f),
                new Vector2(142f, 32f), TabColor,
                delegate { SetView(InspectView.GameplayStats); }, out tabLabel);
            tabLabel.fontSize = 12;

            _serverTab = CreateButton("Gameplay Server Tab", "SERVER MONITOR", detailPanel,
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(316f, -52f),
                new Vector2(142f, 32f), TabColor,
                delegate { SetView(InspectView.ServerMonitor); }, out tabLabel);
            tabLabel.fontSize = 12;

            _detailScroll = CreateScrollView("Details Scroll", detailPanel,
                new Vector2(14f, -92f), new Vector2(694f, 418f),
                out _detailContent, out _detailViewport);
            _detailText = CreateText(_detailContent, "Details Text", "",
                15, TextColor, TextAnchor.UpperLeft);
            _detailText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _detailText.verticalOverflow = VerticalWrapMode.Overflow;
            _detailText.lineSpacing = 1.12f;
            _detailText.supportRichText = false;
            RectTransform detailTextRect = _detailText.rectTransform;
            detailTextRect.anchorMin = new Vector2(0f, 1f);
            detailTextRect.anchorMax = new Vector2(1f, 1f);
            detailTextRect.pivot = new Vector2(0f, 1f);
            detailTextRect.anchoredPosition = new Vector2(12f, -10f);
            detailTextRect.sizeDelta = new Vector2(-24f, 360f);

            _panelObject.SetActive(false);
            UpdateTabStyles();
            _detailsDirty = true;
        }

        private void EnsureEventSystem()
        {
            EventSystem eventSystem = UnityEngine.Object.FindObjectOfType<EventSystem>();
            if (eventSystem == null)
            {
                _createdEventSystem = new GameObject("RPG Character Monitor EventSystem");
                eventSystem = _createdEventSystem.AddComponent<EventSystem>();
            }

            if (eventSystem.GetComponent<BaseInputModule>() == null)
                eventSystem.gameObject.AddComponent<StandaloneInputModule>();
        }

        private void SetOpen(bool open)
        {
            _isOpen = open;
            if (_panelObject != null) _panelObject.SetActive(open);
            if (_launcherObject != null) _launcherObject.SetActive(!open);
            if (open)
            {
                _detailsDirty = true;
                RefreshDetails();
            }
        }

        private void SetView(InspectView view)
        {
            _view = view;
            UpdateTabStyles();
            _detailsDirty = true;
            if (_isOpen) RefreshDetails();
            if (_detailScroll != null) _detailScroll.verticalNormalizedPosition = 1f;
        }

        private void UpdateTabStyles()
        {
            SetButtonColor(_characterTab, _view == InspectView.CharacterStats ? ActiveTabColor : TabColor);
            SetButtonColor(_gameplayTab, _view == InspectView.GameplayStats ? ActiveTabColor : TabColor);
            SetButtonColor(_serverTab, _view == InspectView.ServerMonitor ? ActiveTabColor : TabColor);
        }

        private static void SetButtonColor(Button button, Color color)
        {
            if (button == null) return;
            Image image = button.targetGraphic as Image;
            if (image != null) image.color = color;
        }

        private void RefreshCharacters(bool force)
        {
            Character[] found = UnityEngine.Object.FindObjectsOfType<Character>();
            List<Character> current = new List<Character>();
            for (int i = 0; i < found.Length; i++) AddUnique(current, found[i]);

            // FindObjectsOfType excludes inactive GameObjects. The builder's
            // registry still knows about those spawned instances, so include
            // them as well when the runtime service is present.
            CharacterBuilderServer builder = CharacterBuilderServer.Instance;
            if (builder != null && builder.registry != null && builder.registry.entries != null)
            {
                for (int i = 0; i < builder.registry.entries.Count; i++)
                {
                    CharacterEntry entry = builder.registry.entries[i];
                    if (entry != null && entry.isSpawned) AddUnique(current, entry.instance);
                }
            }

            current.Sort(delegate (Character a, Character b)
            {
                int byName = string.Compare(DisplayName(a), DisplayName(b), StringComparison.OrdinalIgnoreCase);
                if (byName != 0) return byName;
                return string.Compare(a.gameObject.name, b.gameObject.name, StringComparison.OrdinalIgnoreCase);
            });

            bool changed = force || current.Count != _characters.Count;
            if (!changed)
            {
                for (int i = 0; i < current.Count; i++)
                {
                    if (!ReferenceEquals(current[i], _characters[i]))
                    {
                        changed = true;
                        break;
                    }
                }
            }
            if (!changed) return;

            _characters.Clear();
            _characters.AddRange(current);

            if (!ReferenceEquals(_selected, null) && !ContainsReference(_characters, _selected))
            {
                UnsubscribeSelected();
                _selected = null;
                _history.Clear();
                _detailsDirty = true;
            }

            RebuildRoster();
            UpdateLauncherLabel();
            if (_isOpen) RefreshDetails();
        }

        private void RebuildRoster()
        {
            if (_rosterContent == null) return;
            for (int i = _rosterContent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_rosterContent.GetChild(i).gameObject);

            _rosterTitle.text = "SPAWNED CHARACTERS (" + _characters.Count + ")";
            float rowStep = 48f;
            float viewportHeight = _rosterViewport != null ? _rosterViewport.rect.height : 0f;
            float contentHeight = Math.Max(Math.Max(56f, viewportHeight),
                12f + _characters.Count * rowStep);
            _rosterContent.sizeDelta = new Vector2(0f, contentHeight);

            if (_characters.Count == 0)
            {
                Text empty = CreateText(_rosterContent, "Empty Roster",
                    "No active Character components found.\n\nCharacters spawned through the RPG builder will appear here.",
                    13, MutedTextColor, TextAnchor.UpperLeft);
                Place(empty.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(8f, -10f), new Vector2(210f, 90f));
            }
            else
            {
                for (int i = 0; i < _characters.Count; i++)
                {
                    Character target = _characters[i];
                    Color rowColor = ReferenceEquals(target, _selected) ? SelectedRowColor : RowColor;
                    string caption = DisplayName(target) + "   [" + target.Kind + "]";
                    Text rowText;
                    Button row = CreateButton("Character Row " + i, caption, _rosterContent,
                        new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                        new Vector2(0f, -8f - i * rowStep), new Vector2(218f, 40f), rowColor,
                        delegate { SelectCharacter(target); }, out rowText);
                    rowText.alignment = TextAnchor.MiddleLeft;
                    rowText.fontSize = 13;
                }
            }

            if (_rosterScroll != null) _rosterScroll.verticalNormalizedPosition = 1f;
        }

        private void SelectCharacter(Character character)
        {
            if (character == null) return;
            UnsubscribeSelected();

            _selected = character;
            _observedCharacterStats = character.characterStats;
            _observedServer = character.gameplayStats != null ? character.gameplayStats.server : null;
            _recalculationPasses = 0;
            _clientPassCounts.Clear();
            _characterSnapshots.Clear();
            _gameplaySnapshots.Clear();
            _history.Clear();
            CaptureSnapshots();

            if (_observedCharacterStats != null)
                _observedCharacterStats.OnStatChanged += OnCharacterStatChanged;
            if (_observedServer != null)
            {
                _observedServer.OnStatChanged += OnGameplayStatChanged;
                _observedServer.OnRecalculated += OnServerRecalculated;
            }

            AddHistory("Monitoring started for " + DisplayName(character) + ".");
            _view = InspectView.CharacterStats;
            UpdateTabStyles();
            RebuildRoster();
            _detailsDirty = true;
            if (_isOpen) RefreshDetails();
            if (_detailScroll != null) _detailScroll.verticalNormalizedPosition = 1f;
        }

        private void UnsubscribeSelected()
        {
            if (_observedCharacterStats != null)
                _observedCharacterStats.OnStatChanged -= OnCharacterStatChanged;
            if (_observedServer != null)
            {
                _observedServer.OnStatChanged -= OnGameplayStatChanged;
                _observedServer.OnRecalculated -= OnServerRecalculated;
            }
            _observedCharacterStats = null;
            _observedServer = null;
        }

        private void OnCharacterStatChanged(StatField field)
        {
            RecordValueChange("BASE", field, _characterSnapshots);
        }

        private void OnGameplayStatChanged(StatField field)
        {
            RecordValueChange("DERIVED", field, _gameplaySnapshots);
        }

        private void OnServerRecalculated()
        {
            _recalculationPasses++;
            int executed = 0;
            if (_observedServer != null && _observedServer.clients != null)
            {
                for (int i = 0; i < _observedServer.clients.Count; i++)
                {
                    GameplayStatClient client = _observedServer.clients[i];
                    if (client == null) continue;
                    int count;
                    _clientPassCounts.TryGetValue(client, out count);
                    _clientPassCounts[client] = count + 1;
                    executed++;
                }
            }
            AddHistory("SERVER pass #" + _recalculationPasses + " completed; " +
                executed + " formula client(s) evaluated in dependency order.");
        }

        private void RecordValueChange(string category, StatField field, Dictionary<string, string> snapshots)
        {
            if (field == null) return;
            string oldValue;
            if (!snapshots.TryGetValue(field.name, out oldValue)) oldValue = "(initial)";
            string newValue = FormatValue(field);
            snapshots[field.name] = newValue;
            if (oldValue != newValue)
                AddHistory(category + " " + field.name + ": " + oldValue + " -> " + newValue);
        }

        private void CaptureSnapshots()
        {
            if (_observedCharacterStats != null)
                CaptureFields(_observedCharacterStats.fields, _characterSnapshots);
            if (_observedServer != null && _observedServer.clients != null)
            {
                for (int i = 0; i < _observedServer.clients.Count; i++)
                {
                    GameplayStatClient client = _observedServer.clients[i];
                    if (client != null && client.output != null)
                        _gameplaySnapshots[client.output.name] = FormatValue(client.output);
                }
            }
        }

        private static void CaptureFields(List<StatField> fields, Dictionary<string, string> snapshots)
        {
            if (fields == null) return;
            for (int i = 0; i < fields.Count; i++)
            {
                StatField field = fields[i];
                if (field != null) snapshots[field.name] = FormatValue(field);
            }
        }

        private void SampleForUnannouncedChanges()
        {
            if (_observedCharacterStats != null)
                SampleFields("BASE (sampled)", _observedCharacterStats.fields, _characterSnapshots);

            if (_observedServer != null && _observedServer.clients != null)
            {
                for (int i = 0; i < _observedServer.clients.Count; i++)
                {
                    GameplayStatClient client = _observedServer.clients[i];
                    if (client != null && client.output != null)
                        SampleField("DERIVED (sampled)", client.output, _gameplaySnapshots);
                }
            }
        }

        private void SampleFields(string label, List<StatField> fields, Dictionary<string, string> snapshots)
        {
            if (fields == null) return;
            for (int i = 0; i < fields.Count; i++) SampleField(label, fields[i], snapshots);
        }

        private void SampleField(string label, StatField field, Dictionary<string, string> snapshots)
        {
            if (field == null) return;
            string oldValue;
            string newValue = FormatValue(field);
            if (snapshots.TryGetValue(field.name, out oldValue) && oldValue != newValue)
            {
                snapshots[field.name] = newValue;
                AddHistory(label + " " + field.name + ": " + oldValue + " -> " + newValue);
            }
            else if (!snapshots.ContainsKey(field.name))
            {
                snapshots[field.name] = newValue;
            }
        }

        private void AddHistory(string message)
        {
            _history.Add(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + message);
            int limit = Mathf.Max(8, historyLimit);
            while (_history.Count > limit) _history.RemoveAt(0);
            _detailsDirty = true;
        }

        private void RefreshDetails()
        {
            if (_detailText == null) return;
            _detailsDirty = false;

            if (_selected == null)
            {
                _selectionTitle.text = "NO CHARACTER SELECTED";
                _detailText.text = _characters.Count == 0
                    ? "No active Character components are currently in the scene.\n\nSpawn a character through CharacterBuilderServer and it will appear in the roster."
                    : "Click a character in the roster. You can then switch between its Character Stats, Gameplay Stats, and Server Monitor views.";
            }
            else
            {
                _selectionTitle.text = "INSPECTING  " + DisplayName(_selected) + "  /  " + _selected.Kind;
                StringBuilder report = new StringBuilder(1024);
                switch (_view)
                {
                    case InspectView.GameplayStats:
                        AppendGameplayStats(report, false);
                        break;
                    case InspectView.ServerMonitor:
                        AppendServerMonitor(report);
                        break;
                    default:
                        AppendCharacterStats(report);
                        break;
                }
                _detailText.text = report.ToString();
            }

            Canvas.ForceUpdateCanvases();
            float preferredHeight = _detailText.preferredHeight;
            float viewHeight = _detailViewport != null ? _detailViewport.rect.height : 380f;
            float textHeight = Mathf.Max(viewHeight - 20f, preferredHeight + 8f);
            _detailContent.sizeDelta = new Vector2(0f, textHeight + 20f);
            _detailText.rectTransform.sizeDelta = new Vector2(-24f, textHeight);
        }

        private void AppendCharacterStats(StringBuilder report)
        {
            report.AppendLine("CHARACTER STATS  /  BASE VALUES");
            report.AppendLine("Updates from CharacterStats.SetFloat / SetInt / SetBool are captured as they happen.");
            report.AppendLine();

            if (_selected.characterStats == null || _selected.characterStats.fields == null ||
                _selected.characterStats.fields.Count == 0)
            {
                report.AppendLine("No character stat fields are initialized.");
                return;
            }

            List<StatField> fields = _selected.characterStats.fields;
            for (int i = 0; i < fields.Count; i++)
            {
                StatField field = fields[i];
                if (field == null) continue;
                report.Append(field.name).Append("   [").Append(field.type).Append("]  =  ")
                    .Append(FormatValue(field));
                StatSchemaEntry schemaEntry = _selected.characterStats.Schema != null
                    ? _selected.characterStats.Schema.Find(field.name) : null;
                if (schemaEntry != null && (schemaEntry.hasMin || schemaEntry.hasMax))
                {
                    report.Append("    range ");
                    report.Append(schemaEntry.hasMin ? FormatNumber(schemaEntry.minValue) : "-inf");
                    report.Append(" .. ");
                    report.Append(schemaEntry.hasMax ? FormatNumber(schemaEntry.maxValue) : "+inf");
                }
                report.AppendLine();
            }
        }

        private void AppendGameplayStats(StringBuilder report, bool includeMonitorFields)
        {
            GameplayStatsServer server = _selected.gameplayStats != null
                ? _selected.gameplayStats.server : null;
            report.AppendLine("GAMEPLAY STATS  /  DERIVED OUTPUTS");
            report.AppendLine("Compiled formula outputs for this character, in dependency order.");
            report.AppendLine();

            if (server == null || server.clients == null || server.clients.Count == 0)
            {
                report.AppendLine("No GameplayStatClient formulas are compiled for this character.");
                return;
            }

            for (int i = 0; i < server.clients.Count; i++)
            {
                GameplayStatClient client = server.clients[i];
                if (client == null) continue;
                report.Append((i + 1).ToString("00", CultureInfo.InvariantCulture)).Append(". ")
                    .Append(client.name).Append("   [").Append(client.returnType).Append("]  =  ")
                    .Append(client.output != null ? FormatValue(client.output) : "(no output field)");
                if (includeMonitorFields)
                {
                    int count;
                    _clientPassCounts.TryGetValue(client, out count);
                    report.Append("    evaluations: ").Append(count)
                        .Append("    callback: ").Append(client.callback != null ? "ready" : "MISSING");
                }
                report.AppendLine();
                if (client.dependencies != null && client.dependencies.Count > 0)
                    report.Append("      depends on: ").AppendLine(string.Join(", ", client.dependencies.ToArray()));
            }
        }

        private void AppendServerMonitor(StringBuilder report)
        {
            GameplayStatsServer server = _selected.gameplayStats != null
                ? _selected.gameplayStats.server : null;
            bool sameSource = server != null && ReferenceEquals(server.characterStats, _selected.characterStats);

            report.AppendLine("GAMEPLAY STATS SERVER  /  LIVE TRACE");
            report.AppendLine("This view mirrors the derived stats and adds execution order, callback health, and change history.");
            report.AppendLine("Bound CharacterStats reference: " + (sameSource ? "THIS CHARACTER'S INSTANCE" : "NOT BOUND TO THIS CHARACTER"));
            report.AppendLine("Completed recalculation passes observed: " + _recalculationPasses);
            report.AppendLine("Formula clients: " + (server != null && server.clients != null ? server.clients.Count : 0));
            report.AppendLine();

            AppendGameplayStats(report, true);
            report.AppendLine();
            report.AppendLine("LIVE CHANGE HISTORY  /  newest at bottom");
            if (_history.Count == 0)
            {
                report.AppendLine("Waiting for a stat change or recalculation...");
            }
            else
            {
                for (int i = 0; i < _history.Count; i++) report.AppendLine(_history[i]);
            }

            report.AppendLine();
            report.AppendLine("Note: every completed pass evaluates all formula clients in topological order.");
        }

        private void UpdateLauncherLabel()
        {
            if (_launcherLabel != null)
                _launcherLabel.text = "CHARACTERS  (" + _characters.Count + ")  [F2]";
        }

        private static string DisplayName(Character character)
        {
            if (character == null) return "(destroyed character)";
            if (!string.IsNullOrEmpty(character.characterName)) return character.characterName;
            if (character.gameObject != null && !string.IsNullOrEmpty(character.gameObject.name))
                return character.gameObject.name;
            return character.GetType().Name;
        }

        private static string FormatValue(StatField field)
        {
            if (field == null) return "(null)";
            switch (field.type)
            {
                case StatType.Int: return field.intValue.ToString(CultureInfo.InvariantCulture);
                case StatType.Bool: return field.boolValue ? "true" : "false";
                default: return FormatNumber(field.floatValue);
            }
        }

        private static string FormatNumber(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static void AddUnique(List<Character> characters, Character candidate)
        {
            if (candidate == null) return;
            for (int i = 0; i < characters.Count; i++)
                if (ReferenceEquals(characters[i], candidate)) return;
            characters.Add(candidate);
        }

        private static bool ContainsReference(List<Character> characters, Character target)
        {
            for (int i = 0; i < characters.Count; i++)
                if (ReferenceEquals(characters[i], target)) return true;
            return false;
        }

        private static RectTransform CreateRect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            return rect;
        }

        private static void Place(RectTransform rect, Vector2 anchor, Vector2 pivot,
            Vector2 position, Vector2 size)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static Text CreateText(Transform parent, string name, string content,
            int fontSize, Color color, TextAnchor alignment)
        {
            RectTransform rect = CreateRect(name, parent);
            Text text = rect.gameObject.AddComponent<Text>();
            if (_runtimeFont == null)
            {
                _runtimeFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (_runtimeFont == null)
                    _runtimeFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (_runtimeFont == null)
                    _runtimeFont = Font.CreateDynamicFontFromOSFont("Arial", 14);
            }
            text.font = _runtimeFont;
            text.text = content;
            text.fontSize = fontSize;
            text.color = color;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            return text;
        }

        private Button CreateButton(string name, string caption, Transform parent,
            Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size,
            Color background, Action clicked, out Text label)
        {
            RectTransform rect = CreateRect(name, parent);
            Place(rect, anchor, pivot, position, size);
            Image image = rect.gameObject.AddComponent<Image>();
            image.color = background;
            Button button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(delegate
            {
                if (clicked != null) clicked();
            });

            label = CreateText(rect, name + " Label", caption, 14, TextColor, TextAnchor.MiddleCenter);
            RectTransform labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.pivot = new Vector2(0.5f, 0.5f);
            labelRect.offsetMin = new Vector2(7f, 2f);
            labelRect.offsetMax = new Vector2(-7f, -2f);
            return button;
        }

        private ScrollRect CreateScrollView(string name, Transform parent,
            Vector2 topLeftPosition, Vector2 size, out RectTransform content,
            out RectTransform viewport)
        {
            RectTransform scrollRoot = CreateRect(name, parent);
            Place(scrollRoot, new Vector2(0f, 1f), new Vector2(0f, 1f), topLeftPosition, size);

            ScrollRect scroll = scrollRoot.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.inertia = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28f;

            viewport = CreateRect(name + " Viewport", scrollRoot);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.pivot = new Vector2(0.5f, 0.5f);
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = Vector2.zero;
            Image viewportImage = viewport.gameObject.AddComponent<Image>();
            viewportImage.color = new Color(0.045f, 0.065f, 0.09f, 0.62f);
            viewportImage.raycastTarget = true;
            viewport.gameObject.AddComponent<RectMask2D>();

            content = CreateRect(name + " Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 100f);

            scroll.viewport = viewport;
            scroll.content = content;
            return scroll;
        }
    }
#endif
}
