using System;
using System.Collections.Generic;
using PlannerDNA.App;
using PlannerDNA.Commands;
using PlannerDNA.Rendering;
using UnityEngine;
using UnityEngine.UIElements;

namespace PlannerDNA.UI
{
    /// <summary>
    /// Left tool palette. Edit tools act on the selected parcel. Tools that would be refused are
    /// shown muted; clicking them still explains why (the reason is never hidden).
    /// </summary>
    public sealed class ToolbarView
    {
        private readonly SandboxUi _ui;
        private readonly PlannerApp _app;
        private readonly Dictionary<EditAction, Button> _editButtons = new Dictionary<EditAction, Button>();
        private readonly Button _undo, _redo, _reset, _focus;
        private readonly Toggle _overlayToggle;

        public ToolbarView(SandboxUi ui, PlannerApp app)
        {
            _ui = ui;
            _app = app;
            VisualElement tools = ui.Q<VisualElement>("tool-buttons");
            tools.Clear();
            AddEdit(tools, EditAction.SetPark, "Make it a park", "P", PaletteColors.Park);
            AddEdit(tools, EditAction.SetResidential, "Build homes", "R", PaletteColors.Residential);
            AddEdit(tools, EditAction.SetCommercial, "Build shops", "C", PaletteColors.Commercial);
            AddEdit(tools, EditAction.Demolish, "Clear the land", "X", PaletteColors.Vacant);
            AddEdit(tools, EditAction.RaiseFloors, "Add a floor", "]", null);
            AddEdit(tools, EditAction.LowerFloors, "Remove a floor", "[", null);

            VisualElement history = ui.Q<VisualElement>("history-buttons");
            history.Clear();
            _undo = Add(history, "Undo", "Ctrl+Z", null, () => app.Undo(InputSource.Mouse));
            _redo = Add(history, "Redo", "Ctrl+Y", null, () => app.Redo(InputSource.Mouse));
            _reset = Add(history, "Start over", "", null, () => app.ResetToBaseline(InputSource.Mouse));
            _focus = Add(history, "Zoom to selected", "F", null, app.FocusSelection);
            _reset.name = "tool-reset";
            _focus.name = "tool-focus";
            _undo.name = "tool-undo";
            _redo.name = "tool-redo";

            _overlayToggle = ui.Q<Toggle>("toggle-access");
            _overlayToggle.RegisterValueChangedCallback(e => app.SetAccessOverlay(e.newValue));
        }

        public void SyncAccessToggle()
        {
            _overlayToggle.SetValueWithoutNotify(_app.AccessOverlay);
            _ui.SyncAccessKey();
        }

        public Button ButtonFor(EditAction action) => _editButtons[action];

        private void AddEdit(VisualElement container, EditAction action, string label, string shortcut, Color? swatch)
        {
            Button b = Add(container, label, shortcut, swatch, () => _app.ApplyTool(action, InputSource.Mouse));
            b.name = "tool-" + action.ToKey().Replace('_', '-');
            _editButtons[action] = b;
        }

        private static Button Add(VisualElement container, string label, string shortcut, Color? swatch, Action onClick)
        {
            var button = new Button(onClick) { text = string.Empty };
            button.AddToClassList("tool-button");
            if (swatch.HasValue)
            {
                var chip = new VisualElement();
                chip.AddToClassList("tool-swatch");
                chip.style.backgroundColor = swatch.Value;
                button.Add(chip);
            }
            button.Add(UiFormat.Text(label, "tool-label"));
            if (!string.IsNullOrEmpty(shortcut)) button.Add(UiFormat.Text(shortcut, "tool-shortcut"));
            container.Add(button);
            return button;
        }

        public void Refresh()
        {
            bool hasSession = _app.Session != null;
            bool previewing = _app.PreviewSlot.HasValue;
            foreach (KeyValuePair<EditAction, Button> pair in _editButtons)
            {
                ValidationResult v = _app.PreviewTool(pair.Key);
                pair.Value.EnableInClassList("tool--invalid", !v.Ok);
                pair.Value.tooltip = v.Ok ? "Apply to the selected parcel" : v.Reason;
                pair.Value.SetEnabled(hasSession);
            }
            _undo.SetEnabled(hasSession && !previewing && _app.Session.History.CanUndo);
            _redo.SetEnabled(hasSession && !previewing && _app.Session.History.CanRedo);
            _reset.SetEnabled(hasSession && !previewing && !_app.Session.IsAtBaseline);
            _focus.SetEnabled(_app.HasSelection);
        }
    }
}
