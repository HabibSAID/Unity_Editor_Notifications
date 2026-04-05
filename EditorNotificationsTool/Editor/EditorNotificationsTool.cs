#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace EditorNotificationsTool
{
    // =========================================================
    // DATA
    // =========================================================
    [Serializable]
    public class EditorNotificationItem
    {
        public string id = Guid.NewGuid().ToString("N");
        public string title = "Reminder";
        [TextArea(3, 8)] public string description = "Description...";

        // ✅ No Active toggle anymore
        // ✅ Only fires when user presses "Add" (armed = true)
        public bool armed = false;
        public bool firedOnce = false;

        // Start point for delay (set when "Add" is pressed)
        public long createdUtcTicks = DateTime.UtcNow.Ticks;

        // Delay parts ALWAYS shown
        public int delayDays = 0;
        public int delayHours = 0;
        public int delayMinutes = 20;

        public bool enableReferences = false;
        public List<UnityEngine.Object> references = new List<UnityEngine.Object>();

        public TimeSpan GetDelaySafe()
        {
            int d = Mathf.Max(0, delayDays);
            int h = Mathf.Max(0, delayHours);
            int m = Mathf.Max(0, delayMinutes);

            if (m >= 60) { h += m / 60; m %= 60; }
            if (h >= 24) { d += h / 24; h %= 24; }

            return new TimeSpan(d, h, m, 0);
        }

        public DateTime GetLocalDateTimeSafe()
        {
            long ticks = Math.Max(1, createdUtcTicks);
            var createdUtc = new DateTime(ticks, DateTimeKind.Utc);
            return createdUtc.Add(GetDelaySafe()).ToLocalTime();
        }

        public string PrettyDelayShort()
        {
            var delay = GetDelaySafe();
            return $"{delay.Days}d {delay.Hours}h {delay.Minutes}m";
        }

        public string PrettyTime()
        {
            string when = GetLocalDateTimeSafe().ToString("yyyy-MM-dd HH:mm");
            return $"In {PrettyDelayShort()} (at {when})";
        }

        public bool IsDue(DateTime nowLocal) => nowLocal >= GetLocalDateTimeSafe();

        // ✅ Called when user presses "Add"
        public void ArmFromNow()
        {
            createdUtcTicks = DateTime.UtcNow.Ticks;
            firedOnce = false;
            armed = true;
        }
    }

    // =========================================================
    // STORE
    // =========================================================
    [FilePath("ProjectSettings/EditorNotificationsTool.asset", FilePathAttribute.Location.ProjectFolder)]
    public class EditorNotificationsStore : ScriptableSingleton<EditorNotificationsStore>
    {
        public List<EditorNotificationItem> items = new List<EditorNotificationItem>();

        [Header("Sound (Global)")]
        public bool beepOnPopup = true; // UI label: "Notification Sound Effect"
        public AudioClip popupClip;

        public void SaveNow() => Save(true);

        public EditorNotificationItem GetById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return items.FirstOrDefault(x => x != null && x.id == id);
        }

        public void RemoveById(string id)
        {
            items.RemoveAll(x => x == null || x.id == id);
        }
    }

    // =========================================================
    // AUDIO
    // =========================================================
    internal static class EditorAudioUtil
    {
        private static MethodInfo _playPreviewClip;

        static EditorAudioUtil()
        {
            var t = Type.GetType("UnityEditor.AudioUtil, UnityEditor");
            if (t == null) return;

            _playPreviewClip = t.GetMethod(
                "PlayPreviewClip",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(AudioClip), typeof(int), typeof(bool) },
                null);

            if (_playPreviewClip == null)
            {
                _playPreviewClip = t.GetMethod(
                    "PlayPreviewClip",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(AudioClip), typeof(int), typeof(bool), typeof(bool) },
                    null);
            }
        }

        public static bool TryPlay(AudioClip clip)
        {
            if (clip == null || _playPreviewClip == null) return false;
            try
            {
                var p = _playPreviewClip.GetParameters();
                if (p.Length == 3) _playPreviewClip.Invoke(null, new object[] { clip, 0, false });
                else _playPreviewClip.Invoke(null, new object[] { clip, 0, false, false });
                return true;
            }
            catch { return false; }
        }
    }

    // =========================================================
    // SCHEDULER
    // =========================================================
    [InitializeOnLoad]
    public static class EditorNotificationsScheduler
    {
        private static double _next;
        private const double Interval = 1.0;

        static EditorNotificationsScheduler()
        {
            _next = EditorApplication.timeSinceStartup + Interval;
            EditorApplication.update += Update;
        }

        private static void Update()
        {
            if (EditorApplication.isCompiling) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            double now = EditorApplication.timeSinceStartup;
            if (now < _next) return;
            _next = now + Interval;

            var store = EditorNotificationsStore.instance;
            if (store == null || store.items == null || store.items.Count == 0) return;

            var nowLocal = DateTime.Now;
            bool changed = false;

            for (int i = 0; i < store.items.Count; i++)
            {
                var it = store.items[i];
                if (it == null) continue;

                // ✅ Only fire if user pressed "Add"
                if (!it.armed) continue;
                if (it.firedOnce) continue;

                if (it.IsDue(nowLocal))
                {
                    it.firedOnce = true;
                    changed = true;

                    EditorNotificationsPopup.Show(it.id);
                    PlaySound(store);
                }
            }

            if (changed) store.SaveNow();
        }

        internal static void PlaySound(EditorNotificationsStore store)
        {
            bool played = false;
            if (store.popupClip != null) played = EditorAudioUtil.TryPlay(store.popupClip);
            if (!played && store.beepOnPopup) EditorApplication.Beep();
        }
    }

    // =========================================================
    // POPUP
    // =========================================================
    public class EditorNotificationsPopup : EditorWindow
    {
        private string _id;
        private EditorNotificationsStore _store;

        private bool _temporary;
        private string _tmpTitle, _tmpTime, _tmpDesc;
        private List<UnityEngine.Object> _tmpRefs;

        public static void Show(string id)
        {
            var w = CreateInstance<EditorNotificationsPopup>();
            w._id = id;
            w._temporary = false;
            w.titleContent = new GUIContent("Notification");
            w.minSize = new Vector2(480, 260);
            w.maxSize = new Vector2(980, 740);
            w.ShowUtility();
            w.Focus();
        }

        public static void ShowTemporary(string title, string timeText, string desc = null, List<UnityEngine.Object> refs = null)
        {
            var w = CreateInstance<EditorNotificationsPopup>();
            w._temporary = true;
            w._tmpTitle = title;
            w._tmpTime = timeText;
            w._tmpDesc = desc;
            w._tmpRefs = refs;
            w.titleContent = new GUIContent("Notification");
            w.minSize = new Vector2(480, 260);
            w.maxSize = new Vector2(980, 740);
            w.ShowUtility();
            w.Focus();
        }

        private void OnEnable()
        {
            _store = EditorNotificationsStore.instance;

            rootVisualElement.style.paddingLeft = 14;
            rootVisualElement.style.paddingRight = 14;
            rootVisualElement.style.paddingTop = 12;
            rootVisualElement.style.paddingBottom = 12;
            rootVisualElement.style.flexGrow = 1;
            rootVisualElement.style.flexDirection = FlexDirection.Column;

            rootVisualElement.RegisterCallback<GeometryChangedEvent>(_ => BuildUI());
            BuildUI();
        }

        private void BuildUI()
        {
            rootVisualElement.Clear();

            if (_temporary)
            {
                BuildPopupContent(_tmpTitle, _tmpTime, _tmpDesc, _tmpRefs);
                return;
            }

            if (_store == null)
            {
                rootVisualElement.Add(new HelpBox("Store not available.", HelpBoxMessageType.Warning));
                return;
            }

            var item = _store.GetById(_id);
            if (item == null)
            {
                BuildPopupContent("Missing Notification", DateTime.Now.ToString("yyyy-MM-dd HH:mm"), null, null);
                return;
            }

            var refs = (item.enableReferences && item.references != null && item.references.Count > 0)
                ? item.references
                : null;

            BuildPopupContent(item.title, item.PrettyTime(), item.description, refs, item.id);
        }

        private void BuildPopupContent(string titleText, string timeText, string descText, List<UnityEngine.Object> refs, string realId = null)
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.style.minHeight = 0;

            var card = UI.Card();
            card.style.flexGrow = 0;

            var title = new Label(string.IsNullOrEmpty(titleText) ? "(Untitled)" : titleText);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 16;
            title.style.marginBottom = 6;

            var time = new Label("Scheduled: " + (string.IsNullOrEmpty(timeText) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm") : timeText));
            time.style.opacity = 0.78f;
            time.style.marginBottom = 6;

            card.Add(title);
            card.Add(time);

            // Description hidden by default
            bool hasDesc = !string.IsNullOrWhiteSpace(descText);
            var descFold = new Foldout { text = "Description", value = false };
            descFold.style.marginTop = 4;
            descFold.style.marginBottom = 2;

            if (hasDesc)
            {
                var desc = new Label(descText);
                desc.style.whiteSpace = WhiteSpace.Normal;
                desc.style.opacity = 0.95f;
                desc.style.marginTop = 4;
                descFold.Add(desc);
            }
            else
            {
                var empty = new Label("(no description)");
                empty.style.opacity = 0.7f;
                empty.style.marginTop = 4;
                descFold.Add(empty);
            }

            card.Add(descFold);

            bool hasRefs = refs != null && refs.Count > 0;
            if (hasRefs)
            {
                var refsFold = new Foldout { text = $"References ({refs.Count})", value = false };
                refsFold.style.marginTop = 2;

                for (int i = 0; i < refs.Count; i++)
                {
                    var obj = refs[i];

                    var row = new VisualElement();
                    row.style.flexDirection = FlexDirection.Row;
                    row.style.alignItems = Align.Center;
                    row.style.marginTop = 4;

                    var of = new ObjectField { objectType = typeof(UnityEngine.Object), value = obj };
                    of.SetEnabled(false);
                    of.style.flexGrow = 1;

                    var ping = new Button(() => { if (obj) EditorGUIUtility.PingObject(obj); }) { text = "Ping" };
                    UI.StyleNeutral(ping);
                    ping.style.minWidth = 70;
                    ping.style.height = 24;
                    ping.style.marginLeft = 8;

                    row.Add(of);
                    row.Add(ping);
                    refsFold.Add(row);
                }

                card.Add(refsFold);
            }

            scroll.Add(card);
            rootVisualElement.Add(scroll);

            var rowBtns = new VisualElement();
            rowBtns.style.flexDirection = FlexDirection.Row;
            rowBtns.style.justifyContent = Justify.FlexEnd;
            rowBtns.style.marginTop = 10;
            rowBtns.style.flexWrap = Wrap.Wrap;

            if (!string.IsNullOrEmpty(realId))
            {
                var goBtn = new Button(() => { EditorNotificationsBoard.OpenAndSelect(realId); Close(); }) { text = "Go To Notification" };
                UI.StylePrimary(goBtn);
                goBtn.style.marginRight = 8;
                rowBtns.Add(goBtn);

                var deleteBtn = new Button(() =>
                {
                    _store.RemoveById(realId);
                    _store.SaveNow();
                    Close();
                })
                { text = "Delete Notification" };
                UI.StyleDanger(deleteBtn);
                rowBtns.Add(deleteBtn);
            }
            else
            {
                var closeBtn = new Button(Close) { text = "Close" };
                UI.StyleNeutral(closeBtn);
                rowBtns.Add(closeBtn);
            }

            rootVisualElement.Add(rowBtns);
        }
    }

    // =========================================================
    // BOARD
    // =========================================================
    public class EditorNotificationsBoard : EditorWindow
    {
        [MenuItem("Tools/Editor Notifications Board")]
        public static void Open()
        {
            var w = GetWindow<EditorNotificationsBoard>();
            w.titleContent = new GUIContent("Editor Notifications");
            w.minSize = new Vector2(820, 520);
            w.Show();
        }

        public static void OpenAndSelect(string id)
        {
            var w = GetWindow<EditorNotificationsBoard>();
            w.titleContent = new GUIContent("Editor Notifications");
            w.minSize = new Vector2(820, 520);
            w.Show();
            w.Focus();
            w._pendingSelectId = id;
            w.Repaint();
        }

        private EditorNotificationsStore _store;
        private TwoPaneSplitView _splitView;

        private ListView _list;
        private TextField _search;
        private Label _countLabel;

        private Label _scheduledSelectedTitleValue;

        private VisualElement _detailsRoot;
        private VisualElement _placeholderRoot;

        private TextField _titleField;
        private TextField _descField;

        private IntegerField _delayDaysField;
        private IntegerField _delayHoursField;
        private IntegerField _delayMinutesField;

        private Label _prettyTime;

        private Toggle _enableRefsToggle;
        private VisualElement _refsBlock;
        private ListView _refsList;
        private ObjectField _refAddField;

        private Toggle _soundToggle;
        private ObjectField _clipField;

        private Button _deleteSelectedBtn;
        private Button _newBtnBottom;

        // Actions
        private Button _addBtn;
        private Button _set10Btn;
        private Button _testPopupBtn;

        private EditorNotificationItem _selected;
        private string _pendingSelectId;

        private void OnEnable()
        {
            _store = EditorNotificationsStore.instance;
            BuildUI();
            RefreshList();
            ShowPlaceholder(true);
        }

        private void OnFocus()
        {
            if (!string.IsNullOrEmpty(_pendingSelectId))
            {
                var it = _store?.GetById(_pendingSelectId);
                _pendingSelectId = null;
                if (it != null)
                {
                    RefreshList();
                    SelectItem(it);
                }
            }
        }

        private void BuildUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.flexDirection = FlexDirection.Column;
            rootVisualElement.style.flexGrow = 1;

            rootVisualElement.Add(BuildToolbar());

            var host = new VisualElement();
            host.style.flexGrow = 1;
            host.style.minHeight = 0;
            host.style.paddingLeft = 12;
            host.style.paddingRight = 12;
            host.style.paddingTop = 10;
            host.style.paddingBottom = 12;

            _splitView = new TwoPaneSplitView(0, 420, TwoPaneSplitViewOrientation.Horizontal);
            _splitView.style.flexGrow = 1;
            _splitView.style.minHeight = 0;

            _splitView.Add(BuildLeftPanel());
            _splitView.Add(BuildRightPanel());

            host.Add(_splitView);
            rootVisualElement.Add(host);

            rootVisualElement.RegisterCallback<GeometryChangedEvent>(_ => StyleSplitter());
            StyleSplitter();
        }

        private void StyleSplitter()
        {
            if (_splitView == null) return;

            var splitter = _splitView.Q(className: "unity-two-pane-split-view__splitter");
            if (splitter == null) return;

            splitter.style.width = 8;
            splitter.style.marginLeft = 14;
            splitter.style.marginRight = 14;

            splitter.style.backgroundColor = new Color(1, 1, 1, 0.06f);

            splitter.style.borderLeftWidth = 1;
            splitter.style.borderRightWidth = 1;
            splitter.style.borderLeftColor = new Color(1, 1, 1, 0.10f);
            splitter.style.borderRightColor = new Color(0, 0, 0, 0.25f);
        }

        private VisualElement BuildToolbar()
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.paddingLeft = 12;
            bar.style.paddingRight = 12;
            bar.style.paddingTop = 10;
            bar.style.paddingBottom = 10;
            bar.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f, 1f);

            var title = new Label("Editor Notifications");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 14;

            _countLabel = new Label("");
            _countLabel.style.opacity = 0.8f;
            _countLabel.style.marginLeft = 10;

            _search = new TextField();
            _search.style.flexGrow = 1;
            _search.style.marginLeft = 14;
            _search.style.marginRight = 8;
            _search.style.height = 28;
            _search.style.borderTopLeftRadius = 10;
            _search.style.borderTopRightRadius = 10;
            _search.style.borderBottomLeftRadius = 10;
            _search.style.borderBottomRightRadius = 10;
            _search.style.paddingLeft = 10;
            _search.style.paddingRight = 10;
            _search.RegisterValueChangedCallback(_ => RefreshList());

            var saveBtn = new Button(Save) { text = "Save" };
            UI.StyleNeutral(saveBtn);

            var refreshBtn = new Button(() =>
            {
                RefreshList();
                BindDetails(_selected);
            })
            { text = "Refresh" };
            UI.StyleNeutral(refreshBtn);
            refreshBtn.style.marginLeft = 8;

            bar.Add(title);
            bar.Add(_countLabel);
            bar.Add(_search);
            bar.Add(saveBtn);
            bar.Add(refreshBtn);

            return bar;
        }

        private VisualElement BuildLeftPanel()
        {
            var panel = UI.Card(null);
            panel.style.flexGrow = 1;
            panel.style.minHeight = 0;

            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Column;
            headerRow.style.marginBottom = 8;

            var h1 = new Label("Scheduled");
            h1.style.unityFontStyleAndWeight = FontStyle.Bold;
            h1.style.opacity = 0.95f;

            var selectedRow = new VisualElement();
            selectedRow.style.flexDirection = FlexDirection.Row;
            selectedRow.style.alignItems = Align.Center;

            var prefix = new Label("Selected: ");
            prefix.style.opacity = 0.75f;

            _scheduledSelectedTitleValue = new Label("(none)");
            _scheduledSelectedTitleValue.style.opacity = 0.85f;
            _scheduledSelectedTitleValue.style.unityFontStyleAndWeight = FontStyle.Bold;
            _scheduledSelectedTitleValue.style.whiteSpace = WhiteSpace.NoWrap;
            _scheduledSelectedTitleValue.style.overflow = Overflow.Hidden;
            _scheduledSelectedTitleValue.style.flexGrow = 1;

            selectedRow.Add(prefix);
            selectedRow.Add(_scheduledSelectedTitleValue);

            headerRow.Add(h1);
            headerRow.Add(selectedRow);
            panel.Add(headerRow);

            _list = new ListView();
            _list.style.flexGrow = 1;
            _list.style.minHeight = 0;
            _list.selectionType = SelectionType.Single;
            _list.showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly;

            _list.makeItem = () =>
            {
                var root = new VisualElement();
                root.style.flexDirection = FlexDirection.Column;
                root.style.paddingLeft = 10;
                root.style.paddingRight = 10;
                root.style.paddingTop = 8;
                root.style.paddingBottom = 8;

                var line1 = new VisualElement();
                line1.style.flexDirection = FlexDirection.Row;
                line1.style.alignItems = Align.Center;

                var t = new Label { name = "t" };
                t.style.unityFontStyleAndWeight = FontStyle.Bold;
                t.style.flexGrow = 1;
                t.style.flexShrink = 1;
                t.style.whiteSpace = WhiteSpace.NoWrap;
                t.style.overflow = Overflow.Hidden;

                var time = new Label { name = "time" };
                time.style.opacity = 0.8f;
                time.style.flexShrink = 0;
                time.style.marginLeft = 10;
                time.style.whiteSpace = WhiteSpace.NoWrap;

                line1.Add(t);
                line1.Add(time);

                var sub = new Label { name = "sub" };
                sub.style.opacity = 0.75f;
                sub.style.marginTop = 4;
                sub.style.whiteSpace = WhiteSpace.NoWrap;

                root.Add(line1);
                root.Add(sub);
                return root;
            };

            _list.bindItem = (el, i) =>
            {
                var view = (EditorNotificationItem)_list.itemsSource[i];

                el.Q<Label>("t").text = string.IsNullOrEmpty(view.title) ? "(Untitled)" : view.title;
                el.Q<Label>("time").text = view.armed ? view.PrettyTime() : "Not added yet";

                string state =
                    view.firedOnce ? "Read" :
                    view.armed ? "Scheduled" :
                    "Draft";

                el.Q<Label>("sub").text = state;
                el.style.opacity = view.firedOnce ? 0.75f : 1f;
            };

            _list.onSelectionChange += sel =>
            {
                _selected = sel?.FirstOrDefault() as EditorNotificationItem;
                BindDetails(_selected);
                ShowPlaceholder(_selected == null);

                _scheduledSelectedTitleValue.text = _selected == null
                    ? "(none)"
                    : (_selected.title ?? "(Untitled)");
            };

            panel.Add(_list);

            panel.Add(UI.CenteredSeparatorCompact());

            var footer = new VisualElement();
            footer.style.flexDirection = FlexDirection.Row;
            footer.style.justifyContent = Justify.FlexEnd;
            footer.style.marginTop = 6;

            _newBtnBottom = new Button(AddNew) { text = "New" };
            UI.StyleSuccess(_newBtnBottom);
            _newBtnBottom.style.marginRight = 8;
            footer.Add(_newBtnBottom);

            _deleteSelectedBtn = new Button(DeleteSelected) { text = "Delete" };
            UI.StyleDanger(_deleteSelectedBtn);
            footer.Add(_deleteSelectedBtn);

            panel.Add(footer);
            return panel;
        }

        private VisualElement BuildRightPanel()
        {
            var container = new VisualElement();
            container.style.flexGrow = 1;
            container.style.minHeight = 0;

            _placeholderRoot = UI.Card("Details");
            var ph = new Label("Select a notification from the list to edit it.");
            ph.style.opacity = 0.75f;
            ph.style.marginTop = 6;
            _placeholderRoot.Add(ph);

            _detailsRoot = new ScrollView(ScrollViewMode.Vertical);
            _detailsRoot.style.flexGrow = 1;
            _detailsRoot.style.minHeight = 0;

            var details = UI.Card("Details");
            details.style.paddingTop = 10;
            details.style.paddingBottom = 10;

            _titleField = new TextField("Title");

            _descField = new TextField("Description") { multiline = true };
            _descField.style.flexGrow = 0;
            _descField.style.flexShrink = 0;
            _descField.style.height = 70;
            _descField.style.minHeight = 70;
            _descField.style.maxHeight = 70;
            _descField.style.marginTop = 2;
            _descField.style.marginBottom = 4;
            _descField.style.paddingTop = 6;
            _descField.style.paddingBottom = 6;

            _descField.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                var label = _descField.Q<Label>(className: "unity-label");
                if (label != null)
                {
                    label.style.marginBottom = 2;
                    label.style.paddingBottom = 0;
                }
                var input = _descField.Q(className: "unity-text-input");
                if (input != null) input.style.marginTop = 0;
            });

            UI.MakeTextFieldTypingBigger(_descField, 15);

            details.Add(_titleField);
            details.Add(_descField);

            _enableRefsToggle = new Toggle("Enable References");
            _enableRefsToggle.style.marginTop = 0;
            _enableRefsToggle.style.marginBottom = 0;
            _enableRefsToggle.style.paddingTop = 0;
            _enableRefsToggle.style.paddingBottom = 0;
            details.Add(_enableRefsToggle);

            _refsBlock = new VisualElement();
            _refsBlock.style.flexDirection = FlexDirection.Column;
            _refsBlock.style.marginTop = 2;

            var addRow = new VisualElement();
            addRow.style.flexDirection = FlexDirection.Row;
            addRow.style.alignItems = Align.Center;

            _refAddField = new ObjectField("Add") { objectType = typeof(UnityEngine.Object) };
            _refAddField.style.flexGrow = 1;

            var addBtn = new Button(() =>
            {
                if (_selected == null) return;
                var obj = _refAddField.value;
                if (obj == null) return;

                _selected.references ??= new List<UnityEngine.Object>();
                if (!_selected.references.Contains(obj))
                    _selected.references.Add(obj);

                _refAddField.value = null;
                Save();
                RebuildRefs();
                RefreshList();
            })
            { text = "Add" };
            UI.StylePrimary(addBtn);
            addBtn.style.minWidth = 90;
            addBtn.style.marginLeft = 8;

            addRow.Add(_refAddField);
            addRow.Add(addBtn);

            _refsList = new ListView();
            _refsList.style.marginTop = 6;
            _refsList.style.minHeight = 110;
            _refsList.selectionType = SelectionType.Single;

            _refsList.makeItem = () =>
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.paddingTop = 5;
                row.style.paddingBottom = 5;

                var name = new Label { name = "n" };
                name.style.flexGrow = 1;
                name.style.whiteSpace = WhiteSpace.NoWrap;
                name.style.overflow = Overflow.Hidden;

                var ping = new Button { name = "p", text = "Ping" };
                UI.StyleNeutral(ping);
                ping.style.minWidth = 70;
                ping.style.height = 24;
                ping.style.marginLeft = 8;

                var remove = new Button { name = "r", text = "Remove" };
                UI.StyleDanger(remove);
                remove.style.minWidth = 90;
                remove.style.height = 24;
                remove.style.marginLeft = 8;

                row.Add(name);
                row.Add(ping);
                row.Add(remove);
                return row;
            };

            _refsList.bindItem = (el, i) =>
            {
                if (_selected == null || _selected.references == null) return;
                if (i < 0 || i >= _selected.references.Count) return;

                var obj = _selected.references[i];
                el.Q<Label>("n").text = obj ? obj.name : "(Missing)";

                el.Q<Button>("p").clickable = new Clickable(() =>
                {
                    if (obj) EditorGUIUtility.PingObject(obj);
                });

                el.Q<Button>("r").clickable = new Clickable(() =>
                {
                    if (_selected == null || _selected.references == null) return;
                    _selected.references.Remove(obj);
                    Save();
                    RebuildRefs();
                    RefreshList();
                });
            };

            _refsBlock.Add(addRow);
            _refsBlock.Add(_refsList);
            details.Add(_refsBlock);

            // ---------------- SCHEDULE CARD ----------------
            var schedule = UI.Card("Schedule");
            schedule.style.marginTop = 10;

            var delayRow = new VisualElement();
            delayRow.style.flexDirection = FlexDirection.Row;
            delayRow.style.alignItems = Align.Center;
            delayRow.style.flexWrap = Wrap.Wrap;

            VisualElement DelayPair(string label, IntegerField field, int labelWidth)
            {
                var pair = new VisualElement();
                pair.style.flexDirection = FlexDirection.Row;
                pair.style.alignItems = Align.Center;
                pair.style.marginRight = 14;
                pair.style.marginBottom = 4;

                var l = new Label(label);
                l.style.minWidth = labelWidth;
                l.style.opacity = 0.85f;

                field.style.width = 90;
                field.style.height = 24;

                pair.Add(l);
                pair.Add(field);
                return pair;
            }

            _delayDaysField = new IntegerField();
            _delayHoursField = new IntegerField();
            _delayMinutesField = new IntegerField();

            delayRow.Add(DelayPair("Days", _delayDaysField, 42));
            delayRow.Add(DelayPair("Hours", _delayHoursField, 48));
            delayRow.Add(DelayPair("Minutes", _delayMinutesField, 60));

            schedule.Add(delayRow);

            _prettyTime = new Label("");
            _prettyTime.style.opacity = 0.75f;
            _prettyTime.style.marginTop = 6;
            schedule.Add(_prettyTime);

            // ---------------- ACTIONS CARD ----------------
            var actions = UI.Card("Actions");
            actions.style.marginTop = 10;

            var actionsRow = new VisualElement();
            actionsRow.style.flexDirection = FlexDirection.Row;
            actionsRow.style.justifyContent = Justify.FlexEnd;
            actionsRow.style.flexWrap = Wrap.Wrap;

            // ✅ NEW: "Add" button replaces Active toggle behavior
            _addBtn = new Button(AddOrArmSelected) { text = "Add" };
            UI.StyleSuccess(_addBtn);
            _addBtn.style.marginRight = 8;

            _set10Btn = new Button(SetPlus10) { text = "+10 min" };
            UI.StylePrimary(_set10Btn);
            _set10Btn.style.marginRight = 8;

            _testPopupBtn = new Button(TestPopupAlwaysWorks) { text = "Test Popup" };
            UI.StylePrimary(_testPopupBtn);

            actionsRow.Add(_addBtn);
            actionsRow.Add(_set10Btn);
            actionsRow.Add(_testPopupBtn);
            actions.Add(actionsRow);

            // ---------------- SOUND CARD ----------------
            var sound = UI.Card("Sound (Global)");
            sound.style.marginTop = 10;

            _soundToggle = new Toggle("Notification Sound Effect");
            _clipField = new ObjectField("Popup Clip") { objectType = typeof(AudioClip) };

            sound.Add(_soundToggle);
            sound.Add(_clipField);

            var sv = (ScrollView)_detailsRoot;
            sv.Add(details);
            sv.Add(schedule);
            sv.Add(actions);
            sv.Add(sound);

            container.Add(_placeholderRoot);
            container.Add(_detailsRoot);

            HookDetails();
            ShowPlaceholder(true);

            return container;
        }

        private void AddOrArmSelected()
        {
            if (_selected == null) return;

            // ✅ Arm & schedule from NOW (this is what makes it fire)
            _selected.ArmFromNow();

            Save();
            BindDetails(_selected);
            RefreshList();
        }

        private void RebuildRefs()
        {
            if (_refsList == null) return;

            if (_selected == null)
            {
                _refsList.itemsSource = null;
                _refsList.Rebuild();
                return;
            }

            _selected.references ??= new List<UnityEngine.Object>();
            _refsList.itemsSource = _selected.references;
            _refsList.Rebuild();
        }

        private void ShowPlaceholder(bool showPlaceholder)
        {
            if (_placeholderRoot != null) _placeholderRoot.style.display = showPlaceholder ? DisplayStyle.Flex : DisplayStyle.None;
            if (_detailsRoot != null) _detailsRoot.style.display = showPlaceholder ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void HookDetails()
        {
            void HookText(TextField f, Action apply)
            {
                f.RegisterValueChangedCallback(_ =>
                {
                    if (_selected == null) return;
                    apply();
                    UpdatePretty();
                    RefreshList();
                    Save();
                });
            }

            void HookInt(IntegerField f, Action apply)
            {
                f.RegisterValueChangedCallback(_ =>
                {
                    if (_selected == null) return;
                    apply();
                    // If user edits delay after adding, we keep it scheduled (armed)
                    // but reset "firedOnce" so it can fire again with new schedule AFTER pressing Add again.
                    _selected.firedOnce = false;
                    UpdatePretty();
                    RefreshList();
                    Save();
                });
            }

            HookText(_titleField, () => _selected.title = _titleField.value);
            HookText(_descField, () => _selected.description = _descField.value);

            HookInt(_delayDaysField, () => _selected.delayDays = _delayDaysField.value);
            HookInt(_delayHoursField, () => _selected.delayHours = _delayHoursField.value);
            HookInt(_delayMinutesField, () => _selected.delayMinutes = _delayMinutesField.value);

            _enableRefsToggle.RegisterValueChangedCallback(_ =>
            {
                if (_selected == null) return;

                _selected.enableReferences = _enableRefsToggle.value;
                if (_refsBlock != null)
                    _refsBlock.style.display = _selected.enableReferences ? DisplayStyle.Flex : DisplayStyle.None;

                RebuildRefs();
                Save();
            });

            _soundToggle.RegisterValueChangedCallback(_ =>
            {
                _store.beepOnPopup = _soundToggle.value;
                Save();
            });

            _clipField.RegisterValueChangedCallback(_ =>
            {
                _store.popupClip = _clipField.value as AudioClip;
                Save();
            });
        }

        // New notification starts as Draft (armed = false)
        private void AddNew()
        {
            var it = new EditorNotificationItem
            {
                createdUtcTicks = DateTime.UtcNow.Ticks,
                delayDays = 0,
                delayHours = 0,
                delayMinutes = 20,

                armed = false,        // ✅ not scheduled until user presses "Add"
                firedOnce = false,

                enableReferences = false,
                references = new List<UnityEngine.Object>()
            };

            _store.items.Add(it);
            _store.SaveNow();

            RefreshList();
            SelectItem(it);
        }

        private void DeleteSelected()
        {
            if (_selected == null) return;

            // ✅ Deleted means scheduler will never find it -> no popup
            _store.RemoveById(_selected.id);

            _selected = null;
            Save();
            RefreshList();
            BindDetails(null);
            ShowPlaceholder(true);
            _scheduledSelectedTitleValue.text = "(none)";
        }

        private void SetPlus10()
        {
            if (_selected == null) return;

            // Just adjust delay values, doesn't auto-arm.
            int m = Mathf.Max(0, _selected.delayMinutes) + 10;
            int h = Mathf.Max(0, _selected.delayHours);
            int d = Mathf.Max(0, _selected.delayDays);

            if (m >= 60) { h += m / 60; m %= 60; }
            if (h >= 24) { d += h / 24; h %= 24; }

            _selected.delayDays = d;
            _selected.delayHours = h;
            _selected.delayMinutes = m;

            _selected.firedOnce = false;

            Save();
            BindDetails(_selected);
            UpdatePretty();
            RefreshList();
        }

        private void TestPopupAlwaysWorks()
        {
            if (_selected == null)
            {
                EditorNotificationsPopup.ShowTemporary(
                    "Test Notification",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    "No selection. This debug popup always works.",
                    null
                );
                EditorNotificationsScheduler.PlaySound(_store);
                return;
            }

            Save();

            var storeItem = _store.GetById(_selected.id);
            if (storeItem == null)
            {
                EditorNotificationsPopup.ShowTemporary(
                    string.IsNullOrEmpty(_selected.title) ? "Test Notification" : _selected.title,
                    _selected.PrettyTime(),
                    _selected.description,
                    (_selected.enableReferences && _selected.references != null && _selected.references.Count > 0) ? _selected.references : null
                );
                EditorNotificationsScheduler.PlaySound(_store);
                return;
            }

            EditorNotificationsPopup.Show(storeItem.id);
            EditorNotificationsScheduler.PlaySound(_store);
        }

        private void Save() => _store.SaveNow();

        private void RefreshList()
        {
            var search = (_search?.value ?? "").Trim().ToLowerInvariant();
            IEnumerable<EditorNotificationItem> src = _store.items.Where(x => x != null);

            if (!string.IsNullOrEmpty(search))
            {
                src = src.Where(x =>
                    (x.title ?? "").ToLowerInvariant().Contains(search) ||
                    (x.description ?? "").ToLowerInvariant().Contains(search) ||
                    (x.PrettyTime() ?? "").ToLowerInvariant().Contains(search));
            }

            // Show armed (scheduled) first, then drafts; order by due time for armed, by created for drafts
            var list = src
                .OrderByDescending(x => x.armed) // armed first
                .ThenBy(x => x.armed ? x.GetLocalDateTimeSafe() : new DateTime(Math.Max(1, x.createdUtcTicks), DateTimeKind.Utc).ToLocalTime())
                .ToList();

            _list.itemsSource = list;
            _list.Rebuild();

            int total = _store.items.Count(x => x != null);
            int shown = list.Count;
            _countLabel.text = $"({shown}/{total})";

            if (_selected != null)
            {
                var keep = list.FirstOrDefault(x => x.id == _selected.id);
                if (keep != null) SelectItem(keep);
            }
        }

        private void SelectItem(EditorNotificationItem it)
        {
            _selected = it;
            BindDetails(it);
            ShowPlaceholder(false);

            _scheduledSelectedTitleValue.text = it == null
                ? "(none)"
                : (it.title ?? "(Untitled)");

            if (_list.itemsSource is List<EditorNotificationItem> src)
            {
                int idx = src.FindIndex(x => x.id == it.id);
                if (idx >= 0) _list.selectedIndex = idx;
            }
        }

        private void BindDetails(EditorNotificationItem it)
        {
            _selected = it;
            bool has = it != null;

            _deleteSelectedBtn?.SetEnabled(has);
            _addBtn?.SetEnabled(has);
            _set10Btn?.SetEnabled(has);
            _testPopupBtn?.SetEnabled(true);

            if (!has)
            {
                _soundToggle?.SetValueWithoutNotify(_store.beepOnPopup);
                _clipField?.SetValueWithoutNotify(_store.popupClip);
                return;
            }

            _titleField.SetValueWithoutNotify(it.title);
            _descField.SetValueWithoutNotify(it.description);

            _delayDaysField.SetValueWithoutNotify(it.delayDays);
            _delayHoursField.SetValueWithoutNotify(it.delayHours);
            _delayMinutesField.SetValueWithoutNotify(it.delayMinutes);

            _enableRefsToggle.SetValueWithoutNotify(it.enableReferences);
            if (_refsBlock != null)
                _refsBlock.style.display = it.enableReferences ? DisplayStyle.Flex : DisplayStyle.None;

            RebuildRefs();
            UpdatePretty();

            // Button text shows state
            if (_addBtn != null)
                _addBtn.text = it.armed ? "Re-Add (Reschedule)" : "Add";

            _soundToggle.SetValueWithoutNotify(_store.beepOnPopup);
            _clipField.SetValueWithoutNotify(_store.popupClip);
        }

        private void UpdatePretty()
        {
            if (_selected == null) return;

            if (!_selected.armed)
                _prettyTime.text = "Not added yet. Press Add to schedule.";
            else
                _prettyTime.text = "Will fire at: " + _selected.PrettyTime();
        }
    }

    // =========================================================
    // UI HELPERS
    // =========================================================
    internal static class UI
    {
        public static VisualElement Card(string header = null)
        {
            var card = new VisualElement();
            card.style.backgroundColor = new Color(0.12f, 0.12f, 0.12f, 0.85f);
            card.style.borderTopLeftRadius = 14;
            card.style.borderTopRightRadius = 14;
            card.style.borderBottomLeftRadius = 14;
            card.style.borderBottomRightRadius = 14;
            card.style.paddingLeft = 12;
            card.style.paddingRight = 12;
            card.style.paddingTop = 12;
            card.style.paddingBottom = 12;

            card.style.borderLeftWidth = 1;
            card.style.borderRightWidth = 1;
            card.style.borderTopWidth = 1;
            card.style.borderBottomWidth = 1;
            card.style.borderLeftColor = new Color(1, 1, 1, 0.08f);
            card.style.borderRightColor = new Color(1, 1, 1, 0.08f);
            card.style.borderTopColor = new Color(1, 1, 1, 0.08f);
            card.style.borderBottomColor = new Color(1, 1, 1, 0.08f);

            if (!string.IsNullOrEmpty(header))
            {
                var h = new Label(header);
                h.style.unityFontStyleAndWeight = FontStyle.Bold;
                h.style.opacity = 0.95f;
                h.style.marginBottom = 8;
                card.Add(h);
            }

            return card;
        }

        public static VisualElement SeparatorCompact()
        {
            var s = new VisualElement();
            s.style.height = 1;
            s.style.marginTop = 6;
            s.style.marginBottom = 6;
            s.style.backgroundColor = new Color(1, 1, 1, 0.08f);
            return s;
        }

        public static VisualElement CenteredSeparatorCompact()
        {
            var wrap = new VisualElement();
            wrap.style.flexDirection = FlexDirection.Row;
            wrap.style.justifyContent = Justify.Center;
            wrap.style.marginTop = 6;
            wrap.style.marginBottom = 6;

            var s = new VisualElement();
            s.style.height = 1;
            s.style.width = Length.Percent(92);
            s.style.backgroundColor = new Color(1, 1, 1, 0.08f);

            wrap.Add(s);
            return wrap;
        }

        public static void MakeTextFieldTypingBigger(TextField tf, int fontSize)
        {
            if (tf == null) return;

            void Apply()
            {
                var input = tf.Q(className: "unity-text-input");
                if (input != null)
                {
                    input.style.fontSize = fontSize;
                    input.style.unityTextAlign = TextAnchor.UpperLeft;
                }
            }

            Apply();
            tf.RegisterCallback<GeometryChangedEvent>(_ => Apply());
        }

        public static void StylePrimary(Button b)
        {
            b.style.height = 30;
            b.style.minWidth = 130;
            b.style.borderTopLeftRadius = 10;
            b.style.borderTopRightRadius = 10;
            b.style.borderBottomLeftRadius = 10;
            b.style.borderBottomRightRadius = 10;
            b.style.backgroundColor = new Color(0.22f, 0.55f, 0.95f, 1f);
            b.style.color = Color.white;
        }

        public static void StyleSuccess(Button b)
        {
            b.style.height = 30;
            b.style.minWidth = 110;
            b.style.borderTopLeftRadius = 10;
            b.style.borderTopRightRadius = 10;
            b.style.borderBottomLeftRadius = 10;
            b.style.borderBottomRightRadius = 10;
            b.style.backgroundColor = new Color(0.20f, 0.70f, 0.35f, 1f);
            b.style.color = Color.white;
        }

        public static void StyleDanger(Button b)
        {
            b.style.height = 30;
            b.style.minWidth = 160;
            b.style.borderTopLeftRadius = 10;
            b.style.borderTopRightRadius = 10;
            b.style.borderBottomLeftRadius = 10;
            b.style.borderBottomRightRadius = 10;
            b.style.backgroundColor = new Color(0.85f, 0.22f, 0.22f, 1f);
            b.style.color = Color.white;
        }

        public static void StyleNeutral(Button b)
        {
            b.style.height = 30;
            b.style.minWidth = 110;
            b.style.borderTopLeftRadius = 10;
            b.style.borderTopRightRadius = 10;
            b.style.borderBottomLeftRadius = 10;
            b.style.borderBottomRightRadius = 10;
            b.style.backgroundColor = new Color(0.26f, 0.26f, 0.26f, 1f);
            b.style.color = Color.white;
        }
    }
}
#endif
