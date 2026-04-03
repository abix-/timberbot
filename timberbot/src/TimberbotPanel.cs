// TimberbotPanel.cs -- In-game UI for agent start/stop/status.
//
// VM console model: Start, Stop, Edit buttons.
// Settings hidden until Edit clicked. Disabled while running.

using System.Collections.Generic;
using Timberborn.BlockSystem;
using Timberborn.CoreUI;
using Timberborn.DropdownSystem;
using Timberborn.SelectionSystem;
using Timberborn.SingletonSystem;
using Timberborn.UILayoutSystem;
using UnityEngine;
using UnityEngine.UIElements;

namespace Timberbot
{
    public class TimberbotPanel : ILoadableSingleton, IUpdatableSingleton
    {
        private readonly UILayout _layout;
        private readonly TimberbotService _service;
        private readonly VisualElementInitializer _veInit;
        private readonly DropdownItemsSetter _dropdownSetter;
        private readonly EntitySelectionService _selectionService;

        // collapsed bar
        private VisualElement _collapsedWrapper;
        private Label _statusBarLabel;

        // expanded panel
        private VisualElement _expanded;
        private Label _statusLabel;
        private Label _selectionLabel;

        // buttons
        private NineSliceButton _startBtn;
        private NineSliceButton _stopBtn;
        private NineSliceButton _editBtn;

        // settings (toggled by Edit)
        private VisualElement _settingsContainer;
        private TextField _binaryField;
        private Dropdown _modelDropdown;
        private Dropdown _effortDropdown;
        private TextField _goalField;
        private bool _settingsVisible;

        private float _lastUpdate;
        private bool _isExpanded;

        private readonly Dictionary<string, string> _modelDisplayToValue = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _effortDisplayToValue = new Dictionary<string, string>();

        private static readonly string[][] ModelChoices = new[]
        {
            new[] { "claude-opus-4-6", "opus 4.6 - strongest $$$$" },
            new[] { "claude-opus-4-5", "opus 4.5 $$$$" },
            new[] { "claude-opus-4-1", "opus 4.1 $$$$" },
            new[] { "claude-sonnet-4-6", "sonnet 4.6 - best value $$$" },
            new[] { "claude-sonnet-4-5", "sonnet 4.5 $$$" },
            new[] { "claude-sonnet-3-7", "sonnet 3.7 $$" },
            new[] { "claude-haiku-4-5", "haiku 4.5 $" },
            new[] { "claude-haiku-3-5", "haiku 3.5 $" },
        };

        private static readonly string[][] EffortChoices = new[]
        {
            new[] { "max", "max - deep thinking" },
            new[] { "high", "high - thorough (default)" },
            new[] { "medium", "medium - faster, routine" },
            new[] { "low", "low - fastest, simple" },
        };

        public TimberbotPanel(UILayout layout, TimberbotService service, VisualElementInitializer veInit, DropdownItemsSetter dropdownSetter, EntitySelectionService selectionService)
        {
            _layout = layout;
            _service = service;
            _veInit = veInit;
            _dropdownSetter = dropdownSetter;
            _selectionService = selectionService;
        }

        public void Load()
        {
            BuildCollapsedBar();
            BuildExpandedPanel();

            _veInit.InitializeVisualElement(_collapsedWrapper);
            _veInit.InitializeVisualElement(_expanded);

            _layout.AddBottomRight(_collapsedWrapper, 0);
            _layout.AddAbsoluteItem(_expanded);

            _collapsedWrapper.ToggleDisplayStyle(true);
            _expanded.ToggleDisplayStyle(false);
            _isExpanded = false;

            TimberbotLog.Info("panel: attached to game UI");
        }

        public void UpdateSingleton()
        {
            if (_collapsedWrapper == null) return;

            float now = Time.realtimeSinceStartup;
            if (now - _lastUpdate < 0.5f) return;
            _lastUpdate = now;

            var agent = _service.Agent;
            if (agent == null) return;

            var status = agent.CurrentStatus;
            string statusText = FormatStatus(agent);
            bool running = status == AgentStatus.GatheringState || status == AgentStatus.Interactive;

            _statusBarLabel.text = "Timberbot API: " + statusText;
            _statusLabel.text = "Status: " + statusText;

            // show selected entity coords
            try
            {
                var selected = _selectionService.SelectedObject;
                if (selected != null)
                {
                    // SelectableObject wraps an EntityComponent; get the GameObject via reflection
                    var goProp = selected.GetType().GetProperty("gameObject") ?? selected.GetType().GetProperty("GameObject");
                    var goField = goProp == null ? selected.GetType().GetField("_gameObject", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) : null;
                    var go = goProp != null ? goProp.GetValue(selected) as GameObject : goField?.GetValue(selected) as GameObject;
                    if (go != null)
                    {
                        var block = go.GetComponent<BlockObject>();
                        if (block != null)
                        {
                            var coords = block.Coordinates;
                            _selectionLabel.text = TimberbotEntityRegistry.CanonicalName(go.name) + " x:" + coords.x + " y:" + coords.y + " z:" + coords.z;
                        }
                        else
                        {
                            _selectionLabel.text = TimberbotEntityRegistry.CanonicalName(go.name);
                        }
                    }
                    else
                    {
                        _selectionLabel.text = selected.ToString();
                    }
                    _selectionLabel.ToggleDisplayStyle(true);
                }
                else
                {
                    _selectionLabel.ToggleDisplayStyle(false);
                }
            }
            catch { _selectionLabel.ToggleDisplayStyle(false); }

            _startBtn.SetEnabled(!running);
            _stopBtn.SetEnabled(running);
            _editBtn.SetEnabled(!running);
        }

        private void BuildCollapsedBar()
        {
            _collapsedWrapper = new VisualElement();
            _collapsedWrapper.AddToClassList("top-right-item__wrapper");

            var panel = new NineSliceVisualElement();
            panel.AddToClassList("top-right-item");
            panel.AddToClassList("square-large--green");
            panel.style.flexDirection = FlexDirection.Row;
            panel.style.justifyContent = Justify.Center;
            panel.style.alignItems = Align.Center;
            _collapsedWrapper.Add(panel);

            _statusBarLabel = new NineSliceLabel();
            _statusBarLabel.text = "Timberbot API: Idle";
            _statusBarLabel.AddToClassList("text--yellow");
            _statusBarLabel.AddToClassList("game-text-normal");
            _statusBarLabel.style.marginRight = 4;
            panel.Add(_statusBarLabel);

            var expandBtn = new NineSliceButton();
            expandBtn.AddToClassList("button-square");
            expandBtn.AddToClassList("button-square--small");
            expandBtn.AddToClassList("button-plus");
            expandBtn.clicked += ToggleExpanded;
            panel.Add(expandBtn);
        }

        private void BuildExpandedPanel()
        {
            _expanded = new NineSliceVisualElement();
            _expanded.AddToClassList("bg-sub-box--green");
            _expanded.style.position = Position.Absolute;
            _expanded.style.bottom = 60;
            _expanded.style.right = 10;
            _expanded.style.flexDirection = FlexDirection.Column;
            _expanded.style.width = 310;
            _expanded.style.paddingTop = 6;
            _expanded.style.paddingBottom = 8;
            _expanded.style.paddingLeft = 8;
            _expanded.style.paddingRight = 8;

            // header
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 4;

            var title = new NineSliceLabel { text = "Timberbot API" };
            title.AddToClassList("text--yellow");
            title.AddToClassList("game-text-normal");
            title.AddToClassList("text--bold");
            header.Add(title);

            var collapseBtn = new NineSliceButton();
            collapseBtn.AddToClassList("button-square");
            collapseBtn.AddToClassList("button-square--small");
            collapseBtn.AddToClassList("button-minus");
            collapseBtn.clicked += ToggleExpanded;
            header.Add(collapseBtn);
            _expanded.Add(header);

            // status
            _statusLabel = MakeLabel("Status: Idle");
            _expanded.Add(_statusLabel);

            // selected entity debug info
            _selectionLabel = MakeLabel("");
            _selectionLabel.AddToClassList("text--green");
            _selectionLabel.ToggleDisplayStyle(false);
            _expanded.Add(_selectionLabel);

            _expanded.Add(MakeSeparator());

            // button row: Start / Stop / Edit
            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.justifyContent = Justify.Center;
            btnRow.style.marginBottom = 4;

            _startBtn = MakeGameButton("Start", OnStartClicked);
            _startBtn.style.marginRight = 4;
            btnRow.Add(_startBtn);

            _stopBtn = MakeGameButton("Stop", OnStopClicked);
            _stopBtn.SetEnabled(false);
            _stopBtn.style.marginRight = 4;
            btnRow.Add(_stopBtn);

            _editBtn = MakeGameButton("Edit", OnEditClicked);
            btnRow.Add(_editBtn);

            _expanded.Add(btnRow);

            // settings container (hidden by default)
            _settingsContainer = new VisualElement();
            _settingsContainer.ToggleDisplayStyle(false);
            _settingsVisible = false;

            _settingsContainer.Add(MakeSeparator());

            // load saved values or use defaults
            var savedBinary = _service.GetUISetting("agentBinary") ?? "claude";
            var savedModel = _service.GetUISetting("agentModel");
            var savedEffort = _service.GetUISetting("agentEffort");
            var savedGoal = _service.GetUISetting("agentGoal") ?? "reach 50 beavers with 77 well-being";

            _binaryField = MakeTextField(savedBinary);
            _binaryField.RegisterValueChangedCallback(evt => _service.SaveUISetting("agentBinary", evt.newValue));
            _settingsContainer.Add(MakeFieldRow("Binary:", _binaryField));

            // resolve saved model value to display label
            string modelDefault = "sonnet 4.6 - best value $$$";
            if (savedModel != null)
                foreach (var c in ModelChoices)
                    if (c[0] == savedModel) { modelDefault = c[1]; break; }
            _modelDropdown = MakeNativeDropdown(ModelChoices, modelDefault, _modelDisplayToValue);
            _modelDropdown.ValueChanged += (s, e) => _service.SaveUISetting("agentModel", GetDropdownValue(_modelDropdown, _modelDisplayToValue));
            _settingsContainer.Add(MakeFieldRow("Model:", _modelDropdown));

            string effortDefault = "high - thorough (default)";
            if (savedEffort != null)
                foreach (var c in EffortChoices)
                    if (c[0] == savedEffort) { effortDefault = c[1]; break; }
            _effortDropdown = MakeNativeDropdown(EffortChoices, effortDefault, _effortDisplayToValue);
            _effortDropdown.ValueChanged += (s, e) => _service.SaveUISetting("agentEffort", GetDropdownValue(_effortDropdown, _effortDisplayToValue));
            _settingsContainer.Add(MakeFieldRow("Effort:", _effortDropdown));

            _goalField = MakeTextField(savedGoal);
            _goalField.multiline = true;
            _goalField.style.height = 36;
            _goalField.RegisterValueChangedCallback(evt => _service.SaveUISetting("agentGoal", evt.newValue));
            _settingsContainer.Add(MakeFieldRow("Goal:", _goalField));

            _expanded.Add(_settingsContainer);
        }

        private void ToggleExpanded()
        {
            _isExpanded = !_isExpanded;
            _collapsedWrapper.ToggleDisplayStyle(!_isExpanded);
            _expanded.ToggleDisplayStyle(_isExpanded);
        }

        private void OnStartClicked()
        {
            var agent = _service.Agent;
            if (agent == null) return;

            string binary = _binaryField.value;
            if (string.IsNullOrWhiteSpace(binary)) binary = "claude";

            string model = GetDropdownValue(_modelDropdown, _modelDisplayToValue);
            string effort = GetDropdownValue(_effortDropdown, _effortDisplayToValue);
            string goal = _goalField.value;

            agent.Start(binary, model, effort, 120, goal);
            TimberbotLog.Info($"panel: started agent binary={binary} model={model ?? "default"} effort={effort ?? "default"}");

            // hide settings when starting
            _settingsVisible = false;
            _settingsContainer.ToggleDisplayStyle(false);
        }

        private void OnStopClicked()
        {
            _service.Agent?.Stop();
            TimberbotLog.Info("panel: stopped agent");
        }

        private void OnEditClicked()
        {
            _settingsVisible = !_settingsVisible;
            _settingsContainer.ToggleDisplayStyle(_settingsVisible);
        }

        // --- dropdown helpers ---

        private Dropdown MakeNativeDropdown(string[][] choices, string defaultDisplay, Dictionary<string, string> displayToValue)
        {
            var displayItems = new List<string>();
            foreach (var c in choices)
            {
                displayItems.Add(c[1]);
                displayToValue[c[1]] = c[0];
            }

            var dropdown = new Dropdown();
            dropdown.style.flexGrow = 1;
            dropdown.style.height = 26;
            _veInit.InitializeVisualElement(dropdown);

            var provider = new SimpleDropdownProvider(displayItems, defaultDisplay);
            _dropdownSetter.SetItems(dropdown, provider);

            return dropdown;
        }

        private string GetDropdownValue(Dropdown dropdown, Dictionary<string, string> displayToValue)
        {
            var provider = dropdown._dropdownProvider;
            if (provider == null) return null;
            var display = provider.GetValue();
            if (display != null && displayToValue.TryGetValue(display, out var val))
                return val;
            return display;
        }

        private class SimpleDropdownProvider : IDropdownProvider
        {
            public IReadOnlyList<string> Items { get; }
            private string _value;

            public SimpleDropdownProvider(IReadOnlyList<string> items, string defaultValue)
            {
                Items = items;
                _value = defaultValue ?? items[0];
            }

            public string GetValue() => _value;
            public void SetValue(string value) => _value = value;
        }

        // --- shared helpers ---

        private static string FormatStatus(TimberbotAgent agent)
        {
            switch (agent.CurrentStatus)
            {
                case AgentStatus.Idle: return "Idle";
                case AgentStatus.Done: return "Done";
                case AgentStatus.Error: return "Error";
                case AgentStatus.GatheringState: return "Loading...";
                case AgentStatus.Interactive: return "Running";
                default: return agent.CurrentStatus.ToString();
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "(none)";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        private static VisualElement MakeSeparator()
        {
            var sep = new VisualElement();
            sep.style.height = 1;
            sep.style.backgroundColor = new Color(0.4f, 0.36f, 0.28f);
            sep.style.marginTop = 4;
            sep.style.marginBottom = 4;
            return sep;
        }

        private static NineSliceLabel MakeLabel(string text)
        {
            var label = new NineSliceLabel { text = text };
            label.AddToClassList("text--yellow");
            label.AddToClassList("game-text-normal");
            label.style.overflow = Overflow.Hidden;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.marginBottom = 2;
            return label;
        }

        private static NineSliceTextField MakeTextField(string defaultValue)
        {
            var field = new NineSliceTextField();
            field.AddToClassList("text-field");
            field.value = defaultValue;
            field.style.height = 22;
            field.style.flexGrow = 1;
            return field;
        }

        private static NineSliceButton MakeGameButton(string text, System.Action onClick)
        {
            var btn = new NineSliceButton { text = text };
            btn.AddToClassList("button-game");
            btn.AddToClassList("game-text-normal");
            btn.style.width = 80;
            btn.style.height = 26;
            btn.style.paddingTop = 2;
            btn.style.paddingBottom = 2;
            btn.style.paddingLeft = 10;
            btn.style.paddingRight = 10;
            btn.clicked += onClick;
            return btn;
        }

        private static VisualElement MakeFieldRow(string labelText, VisualElement field)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 3;

            var lbl = new NineSliceLabel { text = labelText };
            lbl.AddToClassList("text--yellow");
            lbl.AddToClassList("game-text-normal");
            lbl.style.width = 48;
            row.Add(lbl);

            field.style.flexGrow = 1;
            row.Add(field);

            return row;
        }
    }
}
