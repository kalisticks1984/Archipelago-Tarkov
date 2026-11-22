using System;
using System.Threading.Tasks;
using BepInEx.Configuration;
using HarmonyLib;
using SPT.Common.Http;
using UnityEngine;

namespace ArchipelagoTarkov.Client
{
    /// <summary>
    /// Archipelago tracker: the checks still to do on each unlocked map, and during a raid where that raid's
    /// Archipelago Items are. Opens with a hotkey (F8 by default) and by itself while the Tasks screen is open.
    /// Data comes from the server mod (GET /archipelago/client/tracker).
    /// </summary>
    internal class TrackerWindow : MonoBehaviour
    {
        private const int WindowId = 0x41505452; // "APTR"

        internal static ConfigEntry<KeyboardShortcut> Hotkey;
        internal static ConfigEntry<bool> ShowOnTasks;

        private Rect _rect = new Rect(40, 80, 460, 560);
        private Vector2 _scroll;
        private bool _shownByHotkey;
        private bool _onTasks;
        private bool _closedOnTasks;
        private TrackerResponse _data;
        private string _status = "Loading...";
        private Task<string> _pending;
        private float _nextFetch;
        private float _nextTasksCheck;
        private Type _tasksScreenType;
        private bool _tasksTypeLooked;
        private GUIStyle _header, _current, _entry, _muted;

        private bool Visible => _shownByHotkey || (_onTasks && !_closedOnTasks);

        private void Update()
        {
            if (Hotkey.Value.IsDown())
            {
                if (Visible)
                {
                    _shownByHotkey = false;
                    _closedOnTasks = _onTasks;
                }
                else
                {
                    _shownByHotkey = true;
                    _nextFetch = 0;
                }
            }

            if (ShowOnTasks.Value && Time.realtimeSinceStartup >= _nextTasksCheck)
            {
                _nextTasksCheck = Time.realtimeSinceStartup + 0.5f;
                var onTasks = TasksScreenOpen();
                if (onTasks && !_onTasks)
                {
                    _closedOnTasks = false;
                    _nextFetch = 0;
                }

                _onTasks = onTasks;
            }

            if (_pending != null && _pending.IsCompleted)
            {
                var task = _pending;
                _pending = null;
                if (task.Status == TaskStatus.RanToCompletion && !string.IsNullOrEmpty(task.Result))
                {
                    try
                    {
                        _data = JsonUtility.FromJson<TrackerResponse>(task.Result);
                        _status = _data == null ? "No answer from the server mod." : null;
                    }
                    catch (Exception ex)
                    {
                        _status = $"Unexpected answer from the server mod ({ex.Message}).";
                    }
                }
                else
                {
                    _status = "Cannot reach the SPT server mod.";
                }
            }

            if (Visible && _pending == null && Time.realtimeSinceStartup >= _nextFetch)
            {
                _nextFetch = Time.realtimeSinceStartup + 3f;
                _pending = Task.Run(() => RequestHandler.GetJson("/archipelago/client/tracker"));
            }
        }

        /// <summary>True while the game's Tasks screen (EFT.UI.TasksScreen) is open. Found by name; false if renamed.</summary>
        private bool TasksScreenOpen()
        {
            if (!_tasksTypeLooked)
            {
                _tasksTypeLooked = true;
                _tasksScreenType = AccessTools.TypeByName("EFT.UI.TasksScreen");
            }

            if (_tasksScreenType == null)
            {
                return false;
            }

            var screen = FindObjectOfType(_tasksScreenType) as Component;
            return screen != null && screen.gameObject.activeInHierarchy;
        }

        private void OnGUI()
        {
            if (!Visible)
            {
                return;
            }

            EnsureStyles();
            _rect = GUILayout.Window(WindowId, _rect, DrawWindow, "Archipelago tracker");
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{Hotkey.Value} to close", _muted);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(60)))
            {
                _shownByHotkey = false;
                _closedOnTasks = _onTasks;
            }

            GUILayout.EndHorizontal();
            _scroll = GUILayout.BeginScrollView(_scroll);
            if (_status != null && _data == null)
            {
                GUILayout.Label(_status, _muted);
            }
            else if (_data != null && !_data.linked)
            {
                GUILayout.Label("This profile is not linked to an Archipelago game yet. Message the Archipelago contact: connect <server:port> <slot>.", _muted);
            }
            else if (_data != null)
            {
                foreach (var map in _data.maps ?? Array.Empty<TrackerMap>())
                {
                    var title = map.current ? $"> {map.name} (this raid): {map.total} checks" : $"{map.name}: {map.total} checks";
                    GUILayout.Label(title, map.current ? _current : _header);
                    DrawGroups(map.groups);
                }

                if (_data.anywhere != null && _data.anywhere.Length > 0)
                {
                    GUILayout.Label("Any map", _header);
                    DrawGroups(_data.anywhere);
                }
            }

            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        private void DrawGroups(TrackerGroup[] groups)
        {
            if (groups == null || groups.Length == 0)
            {
                GUILayout.Label("    nothing left here", _muted);
                return;
            }

            foreach (var group in groups)
            {
                GUILayout.Label($"    {group.title}: {group.count}", _entry);
                foreach (var entry in group.entries ?? Array.Empty<string>())
                {
                    GUILayout.Label($"        - {entry}", _muted);
                }
            }
        }

        private void EnsureStyles()
        {
            if (_header != null)
            {
                return;
            }

            _header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 14 };
            _current = new GUIStyle(_header) { normal = { textColor = new Color(0.79f, 0.6f, 0.95f) } };
            _entry = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            _muted = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, normal = { textColor = new Color(0.8f, 0.8f, 0.8f) } };
        }
    }

    // JsonUtility needs plain serializable classes with public fields named like the JSON (it fills them).
#pragma warning disable 0649
    [Serializable]
    internal class TrackerResponse
    {
        public bool linked;
        public string raidMap;
        public TrackerMap[] maps;
        public TrackerGroup[] anywhere;
    }

    [Serializable]
    internal class TrackerMap
    {
        public string key;
        public string name;
        public bool current;
        public int total;
        public TrackerGroup[] groups;
    }

    [Serializable]
    internal class TrackerGroup
    {
        public string title;
        public int count;
        public string[] entries;
    }
#pragma warning restore 0649
}
