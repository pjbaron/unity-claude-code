using System;
using UnityEditor;
using UnityEngine;

namespace ClaudeCodeBridge
{
    internal static class ClaudeCodeSettings
    {
        // Domain reload recovery
        internal const string kPrefInterrupted = "ClaudeCodeTerminal_WasInterrupted";
        internal const string kPrefInterruptedSession = "ClaudeCodeTerminal_InterruptedSessionId";
        internal const string kPrefAutoResume = "ClaudeCodeTerminal_AutoResume";

        internal const string kPrefTimeout = "ClaudeCodeTerminal_TimeoutSec";
        internal const int kDefaultTimeout = 600;

        internal const string kPrefShowCost = "ClaudeCodeTerminal_ShowCost";
        internal const string kPrefShowTurns = "ClaudeCodeTerminal_ShowTurns";
        internal const string kPrefShowContext = "ClaudeCodeTerminal_ShowContext";

        // Plan & Execute
        public const string kPrefPlanFirst = "CCT_PlanFirst";
    }

    public class TimeoutInputWindow : EditorWindow
    {
        private string _value;
        private Action<int> _callback;
        private bool _focusSet;

        public static void Show(int current, Action<int> callback)
        {
            var w = CreateInstance<TimeoutInputWindow>();
            w.titleContent = new GUIContent("Set Timeout");
            w._value = current.ToString();
            w._callback = callback;
            w._focusSet = false;
            w.minSize = new Vector2(260, 80);
            w.maxSize = new Vector2(260, 80);
            w.ShowUtility();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Seconds of inactivity (0 = disabled):");
            GUI.SetNextControlName("TimeoutField");
            _value = EditorGUILayout.TextField(_value);

            if (!_focusSet)
            {
                EditorGUI.FocusTextInControl("TimeoutField");
                _focusSet = true;
            }

            bool enterPressed = Event.current.type == EventType.KeyUp &&
                                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            bool ok = GUILayout.Button("OK", GUILayout.Width(60)) || enterPressed;
            bool cancel = GUILayout.Button("Cancel", GUILayout.Width(60));
            EditorGUILayout.EndHorizontal();

            if (ok)
            {
                int result;
                if (int.TryParse(_value, out result) && result >= 0)
                {
                    _callback?.Invoke(result);
                    Close();
                }
            }
            else if (cancel)
            {
                Close();
            }
        }
    }
}
