// TimberbotPanel.cs -- In-game UI for agent start/stop/status.
//
// Collapsed: one-line status badge in bottom-right strip with + button.
// Expanded: absolute-positioned panel with status + controls.
//
// Reads agent state directly from TimberbotAgent properties (no HTTP).
// Updates labels every ~0.5s in UpdateSingleton().

using Timberborn.CoreUI;
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

        // collapsed bar
        private VisualElement _collapsedWrapper;
        private Label _statusBarLabel;

        // expanded panel
        private VisualElement _expanded;
        private Label _statusLabel;
        private Label _goalLabel;
        private Label _cmdLabel;
        private TextField _binaryField;
        private TextField _modelField;
        private VisualElement _modelDropdown;
        private TextField _effortField;
        private VisualElement _effortDropdown;
        private TextField _goalField;
        private NineSliceButton _startBtn;
        private NineSliceButton _stopBtn;

        private float _lastUpdate;
        private bool _isExpanded;

        // sorted most expensive to cheapest; $$$$ = ~$15/$75, $$$ = ~$3/$15, $ = ~$0.80/$4 per M in/out tokens
        private static readonly string[][] ModelChoices = new[]
        {
            new[] { "opus", "opus (latest) - smartest, slow $$$$" },
            new[] { "claude-opus-4-6", "opus 4.6 - strongest model $$$$" },
            new[] { "claude-opus-4-5", "opus 4.5 $$$$" },
            new[] { "claude-opus-4-1", "opus 4.1 $$$$" },
            new[] { "sonnet", "sonnet (latest) - balanced $$$" },
            new[] { "claude-sonnet-4-6", "sonnet 4.6 - best value $$$" },
            new[] { "claude-sonnet-4-5", "sonnet 4.5 $$$" },
            new[] { "claude-sonnet-3-7", "sonnet 3.7 - older $$" },
            new[] { "haiku", "haiku (latest) - fast, light $" },
            new[] { "claude-haiku-4-5", "haiku 4.5 $" },
            new[] { "claude-haiku-3-5", "haiku 3.5 - older $" },
        };

        private static readonly string[][] EffortChoices = new[]
        {
            new[] { "high", "high - thorough (default)" },
            new[] { "medium", "medium - faster, routine" },
            new[] { "low", "low - fastest, simple tasks" },
            new[] { "max", "max - deep thinking, complex" },
        };

        public TimberbotPanel(UILayout layout, TimberbotService service, VisualElementInitializer veInit)
        {
            _layout = layout;
            _service = service;
            _veInit = veInit;
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

            _statusBarLabel.text = "Timberbot: " + statusText;
            _statusLabel.text = "Status: " + statusText;
            _goalLabel.text = "Goal: " + Truncate(agent.CurrentGoal, 40);
            _cmdLabel.text = "Cmd: " + Truncate(agent.CurrentCommand, 40);

            bool running = status == AgentStatus.GatheringState ||
                           status == AgentStatus.Interactive;
            _startBtn.SetEnabled(!running);
            _stopBtn.SetEnabled(running);
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
            _statusBarLabel.text = "Timberbot: Idle";
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

            // header row
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 4;

            var title = new NineSliceLabel { text = "Timberbot Agent" };
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

            // status labels
            _statusLabel = MakeLabel("Status: Idle");
            _expanded.Add(_statusLabel);
            _goalLabel = MakeLabel("Goal: (none)");
            _expanded.Add(_goalLabel);
            _cmdLabel = MakeLabel("Cmd: (none)");
            _expanded.Add(_cmdLabel);

            // separator
            _expanded.Add(MakeSeparator());

            // binary field
            _binaryField = MakeTextField("claude");
            _expanded.Add(MakeFieldRow("Binary:", _binaryField));

            // model dropdown
            _modelField = MakeTextField("sonnet");
            _modelDropdown = MakeDropdownPopup(ModelChoices, _modelField);
            var modelRow = MakeDropdownRow("Model:", _modelField, _modelDropdown);
            _expanded.Add(modelRow);
            _expanded.Add(_modelDropdown);

            // effort dropdown
            _effortField = MakeTextField("high");
            _effortDropdown = MakeDropdownPopup(EffortChoices, _effortField);
            var effortRow = MakeDropdownRow("Effort:", _effortField, _effortDropdown);
            _expanded.Add(effortRow);
            _expanded.Add(_effortDropdown);

            // goal field
            _goalField = MakeTextField("reach 50 beavers with 77 well-being");
            _goalField.multiline = true;
            _goalField.style.height = 36;
            _expanded.Add(MakeFieldRow("Goal:", _goalField));

            // button row
            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.justifyContent = Justify.Center;
            btnRow.style.marginTop = 6;

            _startBtn = MakeGameButton("Start", OnStartClicked);
            _startBtn.style.marginRight = 6;
            btnRow.Add(_startBtn);

            _stopBtn = MakeGameButton("Stop", OnStopClicked);
            _stopBtn.SetEnabled(false);
            btnRow.Add(_stopBtn);

            _expanded.Add(btnRow);
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

            string model = _modelField.value;
            if (string.IsNullOrWhiteSpace(model)) model = null;

            string effort = _effortField.value;
            if (string.IsNullOrWhiteSpace(effort)) effort = null;

            string goal = _goalField.value;

            agent.Start(binary, model, effort, 120, goal);
            TimberbotLog.Info($"panel: started agent binary={binary} model={model ?? "default"} effort={effort ?? "default"}");
        }

        private void OnStopClicked()
        {
            _service.Agent?.Stop();
            TimberbotLog.Info("panel: stopped agent");
        }

        // --- dropdown helper ---

        private static VisualElement MakeDropdownRow(string label, TextField field, VisualElement dropdown)
        {
            var row = MakeFieldRow(label, field);

            var btn = new NineSliceButton { text = "v" };
            btn.AddToClassList("button-game");
            btn.style.width = 22;
            btn.style.height = 22;
            btn.style.paddingLeft = 0;
            btn.style.paddingRight = 0;
            btn.style.paddingTop = 0;
            btn.style.paddingBottom = 0;
            btn.style.marginLeft = 2;
            btn.clicked += () =>
            {
                bool show = dropdown.resolvedStyle.display == DisplayStyle.None;
                dropdown.ToggleDisplayStyle(show);
            };
            row.Add(btn);

            return row;
        }

        private static VisualElement MakeDropdownPopup(string[][] choices, TextField target)
        {
            var popup = new NineSliceVisualElement();
            popup.AddToClassList("bg-sub-box--green");
            popup.style.paddingTop = 4;
            popup.style.paddingBottom = 4;
            popup.style.paddingLeft = 4;
            popup.style.paddingRight = 4;
            popup.ToggleDisplayStyle(false);

            foreach (var choice in choices)
            {
                var value = choice[0];
                var label = choice[1];
                var item = new NineSliceButton { text = label };
                item.AddToClassList("button-game");
                item.AddToClassList("game-text-normal");
                item.style.height = 22;
                item.style.marginBottom = 1;
                item.style.paddingLeft = 6;
                item.clicked += () => { target.value = value; popup.ToggleDisplayStyle(false); };
                popup.Add(item);
            }

            return popup;
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
                case AgentStatus.Interactive: return "Interactive";
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
