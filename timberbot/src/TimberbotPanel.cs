// TimberbotPanel.cs -- In-game UI for agent start/stop/status.

using System.Collections.Generic;
using Timberborn.BlockSystem;
using Timberborn.CoreUI;
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
        private readonly EntitySelectionService _selectionService;

        private VisualElement _widget;
        private Label _statusBarLabel;
        private NineSliceButton _openBtn;

        private VisualElement _modalOverlay;
        private VisualElement _modalPanel;
        private Label _statusLabel;
        private Label _selectionLabel;
        private VisualElement _settingsContainer;

        private TextField _binaryField;
        private NineSliceButton _binaryPresetBtn;
        private TextField _modelField;
        private NineSliceButton _modelPresetBtn;
        private TextField _effortField;
        private NineSliceButton _effortPresetBtn;
        private TextField _goalField;

        private NineSliceButton _startBtn;
        private NineSliceButton _stopBtn;

        private VisualElement _presetPopup;
        private ScrollView _presetScroll;
        private VisualElement _presetPopupAnchor;

        private bool _isWidgetDragging;
        private int _dragPointerId;
        private Vector2 _dragStartPointer;
        private Vector2 _dragStartWidget;
        private bool _widgetPositionInitialized;

        private float _lastUpdate;

        private static readonly string[][] BinaryChoices = new[]
        {
            new[] { "claude", "claude" },
            new[] { "codex", "codex" },
        };

        private static readonly string[][] ClaudeModelChoices = new[]
        {
            new[] { "claude-opus-4-6", "claude-opus-4-6" },
            new[] { "claude-opus-4-5", "claude-opus-4-5" },
            new[] { "claude-opus-4-1", "claude-opus-4-1" },
            new[] { "claude-sonnet-4-6", "claude-sonnet-4-6" },
            new[] { "claude-sonnet-4-5", "claude-sonnet-4-5" },
            new[] { "claude-sonnet-3-7", "claude-sonnet-3-7" },
            new[] { "claude-haiku-4-5", "claude-haiku-4-5" },
            new[] { "claude-haiku-3-5", "claude-haiku-3-5" },
        };

        private static readonly string[][] CodexModelChoices = new[]
        {
            new[] { "gpt-5.4", "gpt-5.4" },
            new[] { "gpt-5.4-mini", "gpt-5.4-mini" },
            new[] { "gpt-5.3-codex", "gpt-5.3-codex" },
            new[] { "gpt-5.2-codex", "gpt-5.2-codex" },
            new[] { "gpt-5.2", "gpt-5.2" },
            new[] { "gpt-5.1-codex-max", "gpt-5.1-codex-max" },
            new[] { "gpt-5.1-codex-mini", "gpt-5.1-codex-mini" },
        };

        private static readonly string[][] ClaudeEffortChoices = new[]
        {
            new[] { "max", "max" },
            new[] { "high", "high" },
            new[] { "medium", "medium" },
            new[] { "low", "low" },
        };

        private static readonly string[][] CodexEffortChoices = new[]
        {
            new[] { "xhigh", "xhigh" },
            new[] { "high", "high" },
            new[] { "medium", "medium" },
            new[] { "low", "low" },
        };

        public TimberbotPanel(UILayout layout, TimberbotService service, VisualElementInitializer veInit, EntitySelectionService selectionService)
        {
            _layout = layout;
            _service = service;
            _veInit = veInit;
            _selectionService = selectionService;
        }

        public void Load()
        {
            BuildWidget();
            BuildModal();

            _veInit.InitializeVisualElement(_widget);
            _veInit.InitializeVisualElement(_modalOverlay);

            _layout.AddAbsoluteItem(_widget);
            _layout.AddAbsoluteItem(_modalOverlay);

            _widget.ToggleDisplayStyle(true);
            _modalOverlay.ToggleDisplayStyle(false);

            TimberbotLog.Info("panel: attached to game UI");
        }

        public void UpdateSingleton()
        {
            if (_widget == null)
                return;

            float now = Time.realtimeSinceStartup;
            if (now - _lastUpdate < 0.5f)
                return;
            _lastUpdate = now;

            var agent = _service.Agent;
            if (agent == null)
                return;

            var status = agent.CurrentStatus;
            var statusText = FormatStatus(agent);
            var running = status == AgentStatus.GatheringState || status == AgentStatus.Interactive;

            _statusBarLabel.text = "Timberbot API - " + statusText;
            _statusLabel.text = "Timberbot API - " + statusText;
            _startBtn.SetEnabled(!running);
            _stopBtn.SetEnabled(running);

            try
            {
                var selected = _selectionService.SelectedObject;
                if (selected != null)
                {
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
            catch
            {
                _selectionLabel.ToggleDisplayStyle(false);
            }
        }

        private void BuildWidget()
        {
            _widget = new NineSliceVisualElement();
            _widget.AddToClassList("top-right-item");
            _widget.AddToClassList("square-large--green");
            _widget.style.position = Position.Absolute;
            _widget.style.flexDirection = FlexDirection.Row;
            _widget.style.alignItems = Align.Center;
            _widget.style.paddingLeft = 6;
            _widget.style.paddingRight = 6;
            _widget.style.paddingTop = 2;
            _widget.style.paddingBottom = 2;
            ApplySavedWidgetPosition();

            _statusBarLabel = new NineSliceLabel { text = "Timberbot API - Idle" };
            _statusBarLabel.AddToClassList("text--yellow");
            _statusBarLabel.AddToClassList("game-text-normal");
            _statusBarLabel.style.marginRight = 4;
            _statusBarLabel.RegisterCallback<PointerDownEvent>(OnWidgetPointerDown);
            _statusBarLabel.RegisterCallback<PointerMoveEvent>(OnWidgetPointerMove);
            _statusBarLabel.RegisterCallback<PointerUpEvent>(OnWidgetPointerUp);
            _widget.Add(_statusBarLabel);

            _openBtn = new NineSliceButton();
            _openBtn.AddToClassList("button-square");
            _openBtn.AddToClassList("button-square--small");
            _openBtn.AddToClassList("button-plus");
            _openBtn.clicked += ShowModal;
            _widget.Add(_openBtn);
        }

        private void BuildModal()
        {
            _modalOverlay = new VisualElement();
            _modalOverlay.style.position = Position.Absolute;
            _modalOverlay.style.left = 0;
            _modalOverlay.style.top = 0;
            _modalOverlay.style.right = 0;
            _modalOverlay.style.bottom = 0;
            _modalOverlay.style.justifyContent = Justify.Center;
            _modalOverlay.style.alignItems = Align.Center;
            _modalOverlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.25f);
            _modalOverlay.RegisterCallback<PointerDownEvent>(OnOverlayPointerDown);

            _modalPanel = new NineSliceVisualElement();
            _modalPanel.AddToClassList("bg-sub-box--green");
            _modalPanel.style.width = 520;
            _modalPanel.style.maxHeight = 620;
            _modalPanel.style.paddingTop = 8;
            _modalPanel.style.paddingBottom = 8;
            _modalPanel.style.paddingLeft = 10;
            _modalPanel.style.paddingRight = 10;
            _modalPanel.style.flexDirection = FlexDirection.Column;
            _modalOverlay.Add(_modalPanel);

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 6;

            var title = new NineSliceLabel { text = "Timberbot API" };
            title.AddToClassList("text--yellow");
            title.AddToClassList("game-text-normal");
            title.AddToClassList("text--bold");
            header.Add(title);

            var closeBtn = new NineSliceButton();
            closeBtn.AddToClassList("button-square");
            closeBtn.AddToClassList("button-square--small");
            closeBtn.AddToClassList("button-minus");
            closeBtn.clicked += HideModal;
            header.Add(closeBtn);
            _modalPanel.Add(header);

            _statusLabel = MakeLabel("Timberbot API - Idle");
            _modalPanel.Add(_statusLabel);

            _selectionLabel = MakeLabel("");
            _selectionLabel.AddToClassList("text--green");
            _selectionLabel.ToggleDisplayStyle(false);
            _modalPanel.Add(_selectionLabel);

            _modalPanel.Add(MakeSeparator());

            _settingsContainer = new VisualElement();
            _settingsContainer.style.flexDirection = FlexDirection.Column;
            _settingsContainer.style.marginBottom = 6;
            _modalPanel.Add(_settingsContainer);

            var savedBinary = NormalizeValue(_service.GetUISetting("agentBinary"), "claude");
            var savedModel = _service.GetUISetting("agentModel");
            var savedEffort = _service.GetUISetting("agentEffort");
            var savedGoal = _service.GetUISetting("agentGoal") ?? "reach 50 beavers with 77 well-being";

            _binaryField = MakeTextField(savedBinary);
            _binaryField.RegisterValueChangedCallback(evt =>
            {
                var binary = NormalizeValue(evt.newValue, "claude");
                _service.SaveUISetting("agentBinary", binary);
                SyncFieldsForBinary(binary);
            });
            _binaryPresetBtn = MakePresetButton("v", () => TogglePresetMenu(_binaryPresetBtn, _binaryField, BinaryChoices));
            _settingsContainer.Add(MakePresetFieldRow("Binary:", _binaryField, _binaryPresetBtn));

            var modelChoices = GetModelChoices(savedBinary);
            _modelField = MakeTextField(GetInitialChoiceValue(modelChoices, savedModel));
            _modelField.RegisterValueChangedCallback(evt =>
                _service.SaveUISetting("agentModel", NormalizeValue(evt.newValue, modelChoices[0][0])));
            _modelPresetBtn = MakePresetButton("v", () => TogglePresetMenu(_modelPresetBtn, _modelField, GetModelChoices(CurrentBinary())));
            _settingsContainer.Add(MakePresetFieldRow("Model:", _modelField, _modelPresetBtn));

            var effortChoices = GetEffortChoices(savedBinary);
            _effortField = MakeTextField(GetInitialChoiceValue(effortChoices, savedEffort));
            _effortField.RegisterValueChangedCallback(evt =>
                _service.SaveUISetting("agentEffort", NormalizeValue(evt.newValue, effortChoices[0][0])));
            _effortPresetBtn = MakePresetButton("v", () => TogglePresetMenu(_effortPresetBtn, _effortField, GetEffortChoices(CurrentBinary())));
            _settingsContainer.Add(MakePresetFieldRow("Effort:", _effortField, _effortPresetBtn));

            _goalField = MakeTextField(savedGoal);
            _goalField.multiline = true;
            _goalField.style.height = 80;
            _goalField.RegisterValueChangedCallback(evt => _service.SaveUISetting("agentGoal", evt.newValue));
            _settingsContainer.Add(MakeFieldRow("Goal:", _goalField));

            _modalPanel.Add(MakeSeparator());

            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.justifyContent = Justify.Center;
            btnRow.style.marginTop = 4;

            _startBtn = MakeGameButton("Start", OnStartClicked);
            _startBtn.style.marginRight = 4;
            btnRow.Add(_startBtn);

            _stopBtn = MakeGameButton("Stop", OnStopClicked);
            _stopBtn.style.marginRight = 4;
            _stopBtn.SetEnabled(false);
            btnRow.Add(_stopBtn);

            var doneBtn = MakeGameButton("Close", HideModal);
            btnRow.Add(doneBtn);
            _modalPanel.Add(btnRow);

            _presetPopup = new NineSliceVisualElement();
            _presetPopup.AddToClassList("bg-sub-box--green");
            _presetPopup.style.position = Position.Absolute;
            _presetPopup.style.minWidth = 180;
            _presetPopup.style.paddingTop = 4;
            _presetPopup.style.paddingBottom = 4;
            _presetPopup.style.paddingLeft = 4;
            _presetPopup.style.paddingRight = 4;
            _presetPopup.ToggleDisplayStyle(false);
            _modalPanel.Add(_presetPopup);

            _presetScroll = new ScrollView(ScrollViewMode.Vertical);
            _presetScroll.style.maxHeight = 260;
            _presetScroll.style.minWidth = 172;
            _presetScroll.style.flexGrow = 1;
            _presetPopup.Add(_presetScroll);
        }

        private void ApplySavedWidgetPosition()
        {
            var savedLeft = _service.GetUISetting("widgetLeft");
            var savedTop = _service.GetUISetting("widgetTop");
            if (float.TryParse(savedLeft, out var left) && float.TryParse(savedTop, out var top))
            {
                _widget.style.left = left;
                _widget.style.top = top;
                _widget.style.right = StyleKeyword.Auto;
                _widget.style.bottom = StyleKeyword.Auto;
                _widgetPositionInitialized = true;
            }
            else
            {
                _widget.style.right = 0;
                _widget.style.bottom = 0;
                _widgetPositionInitialized = false;
            }
        }

        private void OnWidgetPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0)
                return;

            _isWidgetDragging = true;
            _dragPointerId = evt.pointerId;
            _dragStartPointer = new Vector2(evt.position.x, evt.position.y);

            var widgetBounds = _widget.worldBound;
            if (!_widgetPositionInitialized)
            {
                _widget.style.left = widgetBounds.xMin;
                _widget.style.top = widgetBounds.yMin;
                _widget.style.right = StyleKeyword.Auto;
                _widget.style.bottom = StyleKeyword.Auto;
                _widgetPositionInitialized = true;
            }

            _dragStartWidget = new Vector2(_widget.resolvedStyle.left, _widget.resolvedStyle.top);
            _statusBarLabel.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnWidgetPointerMove(PointerMoveEvent evt)
        {
            if (!_isWidgetDragging || evt.pointerId != _dragPointerId)
                return;

            var pointer = new Vector2(evt.position.x, evt.position.y);
            var delta = pointer - _dragStartPointer;
            var newLeft = _dragStartWidget.x + delta.x;
            var newTop = _dragStartWidget.y + delta.y;
            var root = _widget.parent;
            if (root != null)
            {
                newLeft = Mathf.Clamp(newLeft, 0, Mathf.Max(0, root.resolvedStyle.width - _widget.resolvedStyle.width));
                newTop = Mathf.Clamp(newTop, 0, Mathf.Max(0, root.resolvedStyle.height - _widget.resolvedStyle.height));
            }

            _widget.style.left = newLeft;
            _widget.style.top = newTop;
            _widget.style.right = StyleKeyword.Auto;
            _widget.style.bottom = StyleKeyword.Auto;
            evt.StopPropagation();
        }

        private void OnWidgetPointerUp(PointerUpEvent evt)
        {
            if (!_isWidgetDragging || evt.pointerId != _dragPointerId)
                return;

            _isWidgetDragging = false;
            _statusBarLabel.ReleasePointer(evt.pointerId);
            _service.SaveUISetting("widgetLeft", _widget.resolvedStyle.left.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _service.SaveUISetting("widgetTop", _widget.resolvedStyle.top.ToString(System.Globalization.CultureInfo.InvariantCulture));
            evt.StopPropagation();
        }

        private void OnOverlayPointerDown(PointerDownEvent evt)
        {
            if (evt.target == _modalOverlay)
            {
                HidePresetMenu();
                HideModal();
            }
        }

        private void ShowModal()
        {
            _modalOverlay.ToggleDisplayStyle(true);
        }

        private void HideModal()
        {
            HidePresetMenu();
            _modalOverlay.ToggleDisplayStyle(false);
        }

        private void OnStartClicked()
        {
            var agent = _service.Agent;
            if (agent == null)
                return;

            var binary = NormalizeValue(_binaryField.value, "claude");
            var model = NormalizeValue(_modelField.value, "");
            var effort = NormalizeValue(_effortField.value, "");
            var goal = _goalField.value;

            agent.Start(binary, model, effort, 120, goal);
            TimberbotLog.Info($"panel: started agent binary={binary} model={model ?? "default"} effort={effort ?? "default"}");
            HidePresetMenu();
        }

        private void OnStopClicked()
        {
            _service.Agent?.Stop();
            TimberbotLog.Info("panel: stopped agent");
        }

        private void TogglePresetMenu(VisualElement anchor, TextField targetField, string[][] choices)
        {
            if (_presetPopupAnchor == anchor && _presetPopup.resolvedStyle.display != DisplayStyle.None)
            {
                HidePresetMenu();
                return;
            }

            ShowPresetMenu(anchor, targetField, choices);
        }

        private void ShowPresetMenu(VisualElement anchor, TextField targetField, string[][] choices)
        {
            _presetScroll.Clear();
            _presetPopupAnchor = anchor;

            foreach (var choice in choices)
            {
                var value = choice[0];
                _presetScroll.Add(MakePresetOptionButton(value, () =>
                {
                    targetField.value = value;
                    HidePresetMenu();
                }));
            }

            const float optionHeight = 26f;
            const float popupPadding = 8f;
            const float popupWidth = 188f;
            const float panelMargin = 8f;

            var panelBounds = _modalPanel.worldBound;
            var anchorBounds = anchor.worldBound;
            var desiredHeight = choices.Length * optionHeight + popupPadding;
            var belowTop = anchorBounds.yMax - panelBounds.yMin + 2f;
            var spaceBelow = panelBounds.height - belowTop - panelMargin;
            var spaceAbove = anchorBounds.yMin - panelBounds.yMin - panelMargin;
            var useAbove = spaceBelow < desiredHeight && spaceAbove > spaceBelow;
            var popupHeight = Mathf.Min(desiredHeight, Mathf.Max(optionHeight + popupPadding, useAbove ? spaceAbove : spaceBelow));

            _presetPopup.style.width = popupWidth;
            _presetPopup.style.height = popupHeight;
            _presetPopup.style.left = Mathf.Clamp(anchorBounds.xMin - panelBounds.xMin - 156f, panelMargin, panelBounds.width - popupWidth - panelMargin);
            _presetPopup.style.top = useAbove
                ? Mathf.Max(panelMargin, anchorBounds.yMin - panelBounds.yMin - popupHeight - 2f)
                : Mathf.Max(panelMargin, belowTop);
            _presetPopup.ToggleDisplayStyle(true);
            _presetPopup.BringToFront();
        }

        private void HidePresetMenu()
        {
            if (_presetPopup == null)
                return;

            _presetScroll.Clear();
            _presetPopup.ToggleDisplayStyle(false);
            _presetPopupAnchor = null;
        }

        private static string[][] GetModelChoices(string binary)
        {
            return binary == "codex" ? CodexModelChoices : ClaudeModelChoices;
        }

        private static string[][] GetEffortChoices(string binary)
        {
            return binary == "codex" ? CodexEffortChoices : ClaudeEffortChoices;
        }

        private static string GetInitialChoiceValue(string[][] choices, string savedValue)
        {
            if (!string.IsNullOrWhiteSpace(savedValue))
            {
                foreach (var c in choices)
                    if (c[0] == savedValue)
                        return c[0];
            }

            return choices[0][0];
        }

        private string CurrentBinary()
        {
            return NormalizeValue(_binaryField?.value, "claude");
        }

        private void SyncFieldsForBinary(string binary)
        {
            var modelChoices = GetModelChoices(binary);
            if (!ChoiceContainsValue(modelChoices, _modelField?.value))
                _modelField.value = modelChoices[0][0];

            var effortChoices = GetEffortChoices(binary);
            if (!ChoiceContainsValue(effortChoices, _effortField?.value))
                _effortField.value = effortChoices[0][0];
        }

        private static bool ChoiceContainsValue(string[][] choices, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            foreach (var c in choices)
                if (c[0] == value.Trim())
                    return true;

            return false;
        }

        private static string NormalizeValue(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

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

        private static NineSliceButton MakePresetButton(string text, System.Action onClick)
        {
            var btn = new NineSliceButton { text = text };
            btn.AddToClassList("button-game");
            btn.AddToClassList("game-text-normal");
            btn.style.width = 22;
            btn.style.height = 22;
            btn.style.paddingLeft = 0;
            btn.style.paddingRight = 0;
            btn.clicked += onClick;
            return btn;
        }

        private static NineSliceButton MakePresetOptionButton(string text, System.Action onClick)
        {
            var btn = new NineSliceButton { text = text };
            btn.AddToClassList("button-game");
            btn.AddToClassList("game-text-normal");
            btn.style.height = 24;
            btn.style.marginBottom = 2;
            btn.clicked += onClick;
            return btn;
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
            row.style.marginBottom = 6;

            var lbl = new NineSliceLabel { text = labelText };
            lbl.AddToClassList("text--yellow");
            lbl.AddToClassList("game-text-normal");
            lbl.style.width = 52;
            row.Add(lbl);

            field.style.flexGrow = 1;
            row.Add(field);
            return row;
        }

        private static VisualElement MakePresetFieldRow(string labelText, TextField field, NineSliceButton button)
        {
            var row = MakeFieldRow(labelText, field);
            button.style.marginLeft = 4;
            row.Add(button);
            return row;
        }
    }
}
