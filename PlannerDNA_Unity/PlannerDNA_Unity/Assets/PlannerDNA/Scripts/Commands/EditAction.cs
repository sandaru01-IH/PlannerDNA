using System;

namespace PlannerDNA.Commands
{
    public enum EditAction
    {
        SetPark,
        SetResidential,
        SetCommercial,
        Demolish,
        RaiseFloors,
        LowerFloors,
        RestoreBaseline,
        LoadDesign,
    }

    public enum InputSource
    {
        Mouse,
        Keyboard,
        GestureOptional,
        AssistantGenerated,
        System,
    }

    public static class EditActionNames
    {
        public static string ToKey(this EditAction action)
        {
            switch (action)
            {
                case EditAction.SetPark: return "set_park";
                case EditAction.SetResidential: return "set_residential";
                case EditAction.SetCommercial: return "set_commercial";
                case EditAction.Demolish: return "demolish";
                case EditAction.RaiseFloors: return "raise_floors";
                case EditAction.LowerFloors: return "lower_floors";
                case EditAction.RestoreBaseline: return "restore_baseline";
                case EditAction.LoadDesign: return "load_design";
                default: throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
        }

        public static string DisplayName(this EditAction action)
        {
            switch (action)
            {
                case EditAction.SetPark: return "Add park";
                case EditAction.SetResidential: return "Set residential";
                case EditAction.SetCommercial: return "Set commercial";
                case EditAction.Demolish: return "Demolish";
                case EditAction.RaiseFloors: return "Height +";
                case EditAction.LowerFloors: return "Height -";
                case EditAction.RestoreBaseline: return "Reset to baseline";
                case EditAction.LoadDesign: return "Load saved design";
                default: return action.ToString();
            }
        }

        public static string ToKey(this InputSource source)
        {
            switch (source)
            {
                case InputSource.Mouse: return "mouse";
                case InputSource.Keyboard: return "keyboard";
                case InputSource.GestureOptional: return "gesture_optional";
                case InputSource.AssistantGenerated: return "assistant_generated";
                case InputSource.System: return "system";
                default: throw new ArgumentOutOfRangeException(nameof(source), source, null);
            }
        }
    }
}
