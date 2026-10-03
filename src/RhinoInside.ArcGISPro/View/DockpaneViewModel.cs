using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace RhinoInside.ArcGISPro
{
    public class DockpaneViewModel : DockPane
    {
        public const string _dockPaneID = "RhinoInside_ArcGISPro_Dockpane";
        const string BulkSummaryPrompt = "Preview or apply all links to refresh their status.";

        public DockpaneViewModel()
        {
            LaunchCommand = new SimpleCommand(Launch, () => !IsRunning);
            RefreshCommand = new SimpleCommand(Refresh);
            PullCommand = new SimpleCommand(async () => await PullAsync(),
                                            () => IsRunning && !IsBusy && !string.IsNullOrWhiteSpace(SelectedArcGisLayer));
            ShowRhinoCommand = new SimpleCommand(ShowRhino, () => IsRunning);
            PreviewSyncCommand = new SimpleCommand(async () => await RunSyncAsync(false),
                                                   () => IsRunning && !IsBusy && !string.IsNullOrWhiteSpace(SelectedArcGisLayer));
            ApplySyncCommand = new SimpleCommand(async () => await RunSyncAsync(true),
                                                 () => IsRunning && !IsBusy && !string.IsNullOrWhiteSpace(SelectedArcGisLayer));
            SetAnchorFromMapCommand = new SimpleCommand(async () => await SetAnchorFromMapAsync(),
                                                         () => IsRunning && !IsBusy);
            ClearAnchorCommand = new SimpleCommand(ClearAnchor, () => IsRunning && !IsBusy);
            CreateArcGisLayerCommand = new SimpleCommand(async () => await CreateArcGisLayerForCommandAsync(),
                () => IsRunning && !IsBusy && !string.IsNullOrWhiteSpace(NewArcGisRhinoLayer));

            AddLinkCommand = new SimpleCommand(AddLink);
            RemoveLinkCommand = new SimpleCommand(RemoveLink, () => ActiveLink != null);
            PreviewAllCommand = new SimpleCommand(async () => await RunAllAsync(false), () => IsRunning && !IsBusy && Links.Count > 0);
            ApplyAllCommand = new SimpleCommand(async () => await RunAllAsync(true), () => IsRunning && !IsBusy && Links.Count > 0);
            SaveProfileCommand = new SimpleCommand(async () => await SaveProfileAsync(),
                () => IsRunning && !IsBusy && ActiveLink?.IsComplete == true && _profileDraft != null);
            RestoreProfileCommand = new SimpleCommand(async () => await RestoreProfileAsync(),
                () => IsRunning && !IsBusy && ActiveLink?.IsComplete == true && ActiveLink.HasCustomProfile);
            SaveRhinoDocumentCommand = new SimpleCommand(SaveRhinoDocument, () => IsRunning && !IsBusy);
            ShowSubTabCommand = new SimpleCommand<string>(OpenSubTab);
            ShowHelpCommand = new SimpleCommand<string>(ShowHelp);
            CloseHelpCommand = new SimpleCommand(CloseHelp);

            Links.CollectionChanged += (s, e) =>
            {
                NotifyPropertyChanged(() => LinkCount);
                NotifyPropertyChanged(() => HasLinks);
                BulkSummary = BulkSummaryPrompt;
                SaveLinks();
            };

            // Rhino can be started by something other than this pane's button (the test bridge,
            // for one), and a new or opened .3dm carries its own links and tracked layers.
            RhinoHost.Started += (s, e) => OnUiThread(Refresh);
            RhinoHost.DocumentChanging += (s, e) => OnUiThread(OnRhinoDocumentChanging);
            RhinoHost.DocumentChanged += (s, e) => OnUiThread(OnRhinoDocumentChanged);
            // Both fire in bursts (a select-all, a pull); one deferred refresh per burst is enough.
            RhinoHost.SelectionChanged += (s, e) => QueueSelectionRefresh();
            RhinoHost.ObjectsChanged += (s, e) => { MarkPreviewStale(); QueueDocumentRefresh(); };
            // Progress from a running pull or sync arrives off the UI thread; posted, never waited
            // for, so the run is not held up by the pane.
            SyncCoordinator.ProgressChanged += message => Post(() => BusyPhase = message);
            SyncCoordinator.NoticeChanged += notice => Post(() => BusyNotice = notice);
            ArcGIS.Desktop.Editing.Events.EditCompletedEvent.Subscribe(_ =>
            {
                OnUiThread(MarkPreviewStale);
                return Task.CompletedTask;
            });

            HookLayerWatchers();
            Refresh();
            _ = RefreshArcGisLayersAsync();
        }

        /// <summary>The pane's view model, created on demand; for the test bridge.</summary>
        internal static DockpaneViewModel Instance =>
            FrameworkApplication.DockPaneManager.Find(_dockPaneID) as DockpaneViewModel;

        protected override void OnShow(bool isVisible)
        {
            base.OnShow(isVisible);
            if (isVisible) Refresh();
        }

        /// <summary>
        /// The table belongs to the outgoing document; stop writing it anywhere until the incoming
        /// document's own table is loaded. Without this, a picker refreshing mid-open saved the old
        /// links into the new file.
        /// </summary>
        void OnRhinoDocumentChanging()
        {
            _linksDocSerial = 0;
            _linksLoaded = false;
        }

        void OnRhinoDocumentChanged()
        {
            _linksDocSerial = 0;
            _linksLoaded = false;
            RhinoLayers.Clear();
            Refresh();
            RefreshRhinoLayers();
        }

        bool _selectionRefreshQueued, _documentRefreshQueued;

        // ------------------------------------------------------------------ stale results

        DateTime _ignoreEditsUntil;

        /// <summary>
        /// A preview is a snapshot. Once either side is edited after it, the rows on screen no
        /// longer describe what Apply would do -- which is exactly the back-and-forth moment where
        /// trusting them is risky. Edits made by a run itself do not count.
        /// </summary>
        void MarkPreviewStale()
        {
            if (IsBusy || DateTime.UtcNow < _ignoreEditsUntil || _allRows.Count == 0 || IsPreviewStale) return;
            IsPreviewStale = true;
        }

        bool _isPreviewStale;
        public bool IsPreviewStale
        {
            get => _isPreviewStale;
            private set => SetProperty(ref _isPreviewStale, value);
        }

        public string StaleNotice => "Rhino or ArcGIS changed since this preview. Preview again before applying — Apply always recomputes, but these rows are out of date.";

        // ------------------------------------------------------------------ document safety

        public ICommand SaveRhinoDocumentCommand { get; }

        bool _showDocumentWarning;
        public bool ShowDocumentWarning
        {
            get => _showDocumentWarning;
            private set => SetProperty(ref _showDocumentWarning, value);
        }

        string _documentWarning;
        public string DocumentWarning
        {
            get => _documentWarning;
            private set => SetProperty(ref _documentWarning, value);
        }

        void UpdateDocumentWarning(RhinoDocumentInfo doc)
        {
            var path = RhinoHost.GetActiveDocumentPath();
            if (!IsRunning || doc == null || Links.Count == 0)
            {
                ShowDocumentWarning = false;
                return;
            }
            if (path == null)
            {
                DocumentWarning = "This Rhino document has never been saved. Its layer links and sync baselines live in it — save it to keep them next session.";
                ShowDocumentWarning = true;
            }
            else if (doc.IsModified)
            {
                DocumentWarning = $"Unsaved changes in {System.IO.Path.GetFileName(path)}. Save before closing, or the next session compares against the last saved state.";
                ShowDocumentWarning = true;
            }
            else ShowDocumentWarning = false;
        }

        void SaveRhinoDocument()
        {
            try
            {
                var saved = RhinoHost.SaveActiveDocument();
                if (saved != null) SyncSummary = $"Saved {System.IO.Path.GetFileName(saved)}.";
                Refresh();
            }
            catch (Exception ex)
            {
                ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show($"Could not save the Rhino document.\n\n{ex.Message}", "Rhino.Inside");
            }
        }

        // ------------------------------------------------------------------ show a review row

        /// <summary>
        /// Selects a review row's Rhino object and ArcGIS feature, so a person can see what the row
        /// is about in both apps before deciding.
        /// </summary>
        internal async Task ShowRowAsync(SyncRow row)
        {
            if (row == null || !IsRunning) return;
            try
            {
                if (row.RhinoGuid.HasValue) RhinoHost.SelectAndZoom(row.RhinoGuid.Value);
                var layer = ActiveLink?.ArcGisLayer;
                if (row.ObjectId.HasValue && !string.IsNullOrWhiteSpace(layer))
                    await GisUtil.SelectFeatureAsync(layer, row.ObjectId.Value);
            }
            catch (Exception ex)
            {
                SyncSummary = "Could not show that row: " + ex.Message;
            }
        }

        /// <summary>
        /// One refresh per burst of Rhino events: the first event queues it, the rest see the flag,
        /// and the refresh itself clears the flag. It runs once the dispatcher is free, which is
        /// also outside Rhino's own event handler -- the safe moment to read the document.
        /// </summary>
        void QueueSelectionRefresh()
        {
            if (_selectionRefreshQueued) return;
            _selectionRefreshQueued = true;
            Defer(RefreshSelectedObject);
        }

        void QueueDocumentRefresh()
        {
            if (_documentRefreshQueued) return;
            _documentRefreshQueued = true;
            Defer(RefreshWhenIdle);
        }

        /// <summary>
        /// Refreshes once the sync gate is free. A pull or apply raises an object event per object,
        /// and each refresh walks the whole document: refreshing between its batches made a large
        /// pull quadratic. The run ends with a refresh of its own, and this one follows it.
        /// </summary>
        void RefreshWhenIdle()
        {
            if (!SyncCoordinator.IsBusy) { Refresh(); return; }
            _ = Task.Delay(500).ContinueWith(_ => Defer(RefreshWhenIdle));
        }

        static void Defer(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) action();
            else dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Background);
        }

        // ------------------------------------------------------------------ selected object

        /// <summary>Key / value rows of the selected Rhino object's user text.</summary>
        public System.Collections.ObjectModel.ObservableCollection<UserTextRow> SelectedObjectRows { get; }
            = new System.Collections.ObjectModel.ObservableCollection<UserTextRow>();

        string _selectedObjectTitle = "-";
        public string SelectedObjectTitle
        {
            get => _selectedObjectTitle;
            private set => SetProperty(ref _selectedObjectTitle, value);
        }

        public bool HasSelectedObject => SelectedObjectRows.Count > 0;

        void RefreshSelectedObject()
        {
            _selectionRefreshQueued = false;
            SelectedObjectRows.Clear();

            var info = IsRunning ? RhinoHost.GetSelectedObject() : null;
            if (info == null)
            {
                SelectedObjectTitle = IsRunning ? "Select an object in Rhino to see its user text here." : "-";
            }
            else
            {
                SelectedObjectTitle = info.Count == 1
                    ? $"{info.Kind} on '{info.Layer}'"
                    : $"{info.Count} selected — showing the first: {info.Kind} on '{info.Layer}'";

                // Data first, then the sync bookkeeping; the per-field hashes are noise here.
                foreach (var kv in info.UserStrings)
                    if (!kv.Key.StartsWith(RhinoArcGIS.Core.Identity.GisKeys.Namespace, StringComparison.Ordinal))
                        SelectedObjectRows.Add(new UserTextRow { Key = kv.Key, Value = kv.Value });
                foreach (var kv in info.UserStrings)
                    if (kv.Key.StartsWith(RhinoArcGIS.Core.Identity.GisKeys.Namespace, StringComparison.Ordinal) &&
                        !kv.Key.StartsWith(RhinoArcGIS.Core.Identity.GisKeys.FieldHashPrefix, StringComparison.Ordinal))
                        SelectedObjectRows.Add(new UserTextRow { Key = kv.Key, Value = kv.Value });

                if (SelectedObjectRows.Count == 0) SelectedObjectTitle += " — no user text.";
            }
            NotifyPropertyChanged(() => HasSelectedObject);
        }

        // ------------------------------------------------------------------ sub-tabs

        // Link is the first step, so it is where the pane opens until a pair exists.
        string _subTab = "Link";
        /// <summary>Which of the Sync tab's areas is showing: "Link", "Pull" or "Sync".</summary>
        public string SubTab
        {
            get => _subTab;
            set
            {
                if (!SetProperty(ref _subTab, value)) return;
                NotifyPropertyChanged(() => IsSyncSubTab);
                NotifyPropertyChanged(() => IsPullSubTab);
                NotifyPropertyChanged(() => IsLinkSubTab);
                NotifyPropertyChanged(() => IsProfileSubTab);
            }
        }
        public bool IsSyncSubTab => SubTab == "Sync";
        public bool IsPullSubTab => SubTab == "Pull";
        public bool IsLinkSubTab => SubTab == "Link";
        public bool IsProfileSubTab => SubTab == "Profile";
        public ICommand ShowSubTabCommand { get; }

        internal void OpenSubTab(string name)
        {
            SubTab = name;
            if (name == "Profile") _ = LoadProfileAsync();
        }

        // ------------------------------------------------------------------ contextual help

        string _helpTitle = "About Rhino.Inside";
        public string HelpTitle
        {
            get => _helpTitle;
            private set => SetProperty(ref _helpTitle, value);
        }

        string _helpText = string.Empty;
        public string HelpText
        {
            get => _helpText;
            private set => SetProperty(ref _helpText, value);
        }

        bool _isHelpVisible;
        public bool IsHelpVisible
        {
            get => _isHelpVisible;
            private set => SetProperty(ref _isHelpVisible, value);
        }

        public ICommand ShowHelpCommand { get; }
        public ICommand CloseHelpCommand { get; }

        void ShowHelp(string topic)
        {
            switch (topic)
            {
                case "rhino-host":
                    HelpTitle = "Rhino host";
                    HelpText = "Launch starts Rhino 8 inside this ArcGIS Pro process. Show brings the Rhino window forward if it is hidden. Refresh rereads the active Rhino document, its layers, links, and selected-object details; it does not change either model.";
                    break;
                case "active-pair":
                    HelpTitle = "Active layer pair";
                    HelpText = "Pull, Preview, and Apply act on the pair selected here. A saved pair stays bound to its ArcGIS data source across renames; a reused label never changes its target. To replace the source, remove and recreate the link after making the intended layer unique. Its direction controls what Apply may write; Preview compares both sides without writing.";
                    break;
                case "preview-apply":
                    HelpTitle = "Preview and Apply";
                    HelpText = "Preview is read-only: it compares both sides with the last synchronized baseline. Apply recomputes the comparison, writes only changes allowed by the pair direction, and then updates the baseline. Manual conflicts remain held for review.";
                    break;
                case "change-review":
                    HelpTitle = "Change review";
                    HelpText = "Each row shows the detected state, which side changed, and a short explanation. Point to a row for its full detail. Keep Needs action only selected for a compact work list, or clear it to include clean objects.";
                    break;
                case "pull":
                    HelpTitle = "First pull into Rhino";
                    HelpText = "Pull creates tracked Rhino objects from every feature in the active ArcGIS layer. Use it to establish a pair for the first time. Pull skips features the Rhino layer already holds, so running it again only restores objects deleted in Rhino. After the first pull, use Preview and Apply to bring across additions and edits.";
                    break;
                case "new-layer":
                    HelpTitle = "Create an ArcGIS layer from Rhino";
                    HelpText = "Choose one Rhino layer and review the inferred Z-aware Point, Polyline, Polygon, or Multipatch plan. Planar regions create polygons; meshes and three-dimensional Breps, surfaces, and extrusions create Multipatch feature classes. Create and push makes a uniquely named feature class in the project's default geodatabase, adds it to the map, creates a two-way link with shared authored fields, pushes matching objects and stores durable identities. Mixed or unsupported geometry is skipped and reported.";
                    break;
                case "layer-links":
                    HelpTitle = "Layer links and directions";
                    HelpText = "The selected row is the active pair, and links are saved in the Rhino document. Two-way allows changes in either direction. Pull only prevents Rhino changes from writing to ArcGIS. Push only prevents ArcGIS changes from writing to Rhino. Preview all is read-only; Apply all processes every complete link.";
                    break;
                case "earth-anchor":
                    HelpTitle = "Earth anchor";
                    HelpText = "The anchor maps a real-world location to a Rhino model point so pull and push use the same coordinate frame. Set from map centre uses the current ArcGIS view. Leave the model base at 0,0 to map that location to the Rhino origin. Clear only when intentionally resetting georeferencing.";
                    break;
                case "attributes":
                    HelpTitle = "Attribute ownership";
                    HelpText = "This switch sets the fallback for links that do not have a custom profile. Off is safest: ArcGIS owns attributes and Rhino receives read-only copies. On makes ordinary design fields shared. Use the Profile workspace for per-field include, key, and ownership choices. Identity and Shape_* fields always remain ArcGIS-managed.";
                    break;
                case "profile":
                    HelpTitle = "Per-link synchronization profile";
                    HelpText = "A profile travels with the selected link in the Rhino document. Include only fields this workflow needs, choose the Rhino user-text key for each ArcGIS field, and set which side owns the value. Rhino-owned pushes from Rhino, ArcGIS-owned pulls from ArcGIS, Shared allows either side and flags simultaneous edits, and local-only values never cross to the other side. Save before Preview or Apply. ArcGIS system fields stay locked.";
                    break;
                case "conflicts":
                    HelpTitle = "Conflict policy";
                    HelpText = "A conflict means both sides changed since the last baseline. Manual is safest and writes neither version. Prefer Rhino or Prefer ArcGIS resolves each conflict during Apply by overwriting the other side. Preview before changing this setting so the affected rows are visible.";
                    break;
                default:
                    HelpTitle = "About Rhino.Inside";
                    HelpText = "Use the information buttons beside a control or section for focused guidance.";
                    break;
            }

            IsHelpVisible = true;
        }

        void CloseHelp() => IsHelpVisible = false;

        // ------------------------------------------------------------------ links

        public ICommand AddLinkCommand { get; }
        public ICommand RemoveLinkCommand { get; }
        public ICommand PreviewAllCommand { get; }
        public ICommand ApplyAllCommand { get; }
        public ICommand CreateArcGisLayerCommand { get; }
        public ICommand SaveProfileCommand { get; }
        public ICommand RestoreProfileCommand { get; }

        /// <summary>The link table: one row per ArcGIS layer / Rhino layer pair.</summary>
        public System.Collections.ObjectModel.ObservableCollection<LayerLink> Links { get; }
            = new System.Collections.ObjectModel.ObservableCollection<LayerLink>();

        public int LinkCount => Links.Count;
        public bool HasLinks => Links.Count > 0;

        public Array Directions => Enum.GetValues(typeof(RhinoArcGIS.Core.Sync.SyncDirectionMode));

        /// <summary>User-facing labels for the direction picker; enum member names are implementation detail.</summary>
        public IReadOnlyList<SyncDirectionChoice> DirectionChoices { get; } = new[]
        {
            new SyncDirectionChoice(RhinoArcGIS.Core.Sync.SyncDirectionMode.TwoWay, "Two-way"),
            new SyncDirectionChoice(RhinoArcGIS.Core.Sync.SyncDirectionMode.PullOnly, "Pull only"),
            new SyncDirectionChoice(RhinoArcGIS.Core.Sync.SyncDirectionMode.PushOnly, "Push only")
        };

        LayerLink _activeLink;
        /// <summary>
        /// The selected row. It is what Pull, Preview and Apply act on: the layer pickers on those
        /// areas are views of this row, so choosing a row and choosing a layer are the same act.
        /// </summary>
        public LayerLink ActiveLink
        {
            get => _activeLink;
            set
            {
                // Assigning the active row again is common after a run. Do not unsubscribe its
                // change handler and then return from SetProperty, or that row stops persisting.
                if (ReferenceEquals(_activeLink, value)) return;
                if (_activeLink != null) _activeLink.PropertyChanged -= OnActiveLinkChanged;
                if (!SetProperty(ref _activeLink, value)) return;
                if (_activeLink != null) _activeLink.PropertyChanged += OnActiveLinkChanged;
                NotifyPropertyChanged(() => SelectedArcGisLayer);
                NotifyPropertyChanged(() => SelectedRhinoLayer);
                NotifyPropertyChanged(() => ActiveLinkTitle);
                NotifyPropertyChanged(() => ActiveArcGisLayer);
                NotifyPropertyChanged(() => ActiveRhinoLayer);
                NotifyPropertyChanged(() => ActiveDirection);
                (PreviewSyncCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (ApplySyncCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (PullCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (SaveProfileCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (RestoreProfileCommand as SimpleCommand)?.RaiseCanExecuteChanged();

                // The Sync area shows the selected pair's own last result, not the last run's.
                SetRows(_activeLink?.LastRows ?? Array.Empty<SyncRow>());
                SyncSummary = _activeLink == null ? "No layer pair selected."
                    : _activeLink.LastRows == null ? "No preview run for this pair yet."
                    : $"Last run: {_activeLink.LastResult}.";

                ClearProfileEditor(_activeLink == null
                    ? "Select a complete layer pair to author its profile."
                    : "Open Profile to load this pair's current schema and mapping.");
                if (IsProfileSubTab && _activeLink?.IsComplete == true) _ = LoadProfileAsync();
            }
        }

        public string ActiveArcGisLayer
        {
            get => ActiveLink?.ArcGisLayer;
            set
            {
                if (ActiveLink != null && value != null) SetLayerFromExplicitChoice(ActiveLink, value);
            }
        }

        public string ActiveRhinoLayer
        {
            get => ActiveLink?.RhinoLayer;
            set { if (ActiveLink != null) ActiveLink.RhinoLayer = value; }
        }

        void OnActiveLinkChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LayerLink.ArcGisLayer) ||
                e.PropertyName == nameof(LayerLink.RhinoLayer) ||
                e.PropertyName == nameof(LayerLink.Direction) ||
                e.PropertyName == nameof(LayerLink.ProfileJson))
            {
                NotifyPropertyChanged(() => ActiveArcGisLayer);
                NotifyPropertyChanged(() => ActiveRhinoLayer);
                NotifyPropertyChanged(() => ActiveDirection);
                // A result belongs to the exact layer pair and direction that produced it. Keeping
                // it after the row changes makes the overview claim evidence that is no longer true.
                var link = (LayerLink)sender;
                link.LastRows = null;
                link.LastResult = null;
                BulkSummary = BulkSummaryPrompt;
                SetRows(Array.Empty<SyncRow>());
                SyncSummary = "Layer pair changed — preview again to review its current state.";
            }

            NotifyPropertyChanged(() => SelectedArcGisLayer);
            NotifyPropertyChanged(() => SelectedRhinoLayer);
            NotifyPropertyChanged(() => ActiveDirection);
            NotifyPropertyChanged(() => ActiveLinkTitle);
            (PullCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            (PreviewSyncCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            (ApplySyncCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            (SaveProfileCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            (RestoreProfileCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            SaveLinks();
        }

        public string ActiveLinkTitle => ActiveLink == null || !ActiveLink.IsComplete
            ? "No layer pair selected — add one on the Link tab."
            : $"{ActiveLink.ArcGisLayer}  ⇄  {ActiveLink.EffectiveRhinoLayer}  ({Describe(ActiveLink.Direction)})";

        static string Describe(RhinoArcGIS.Core.Sync.SyncDirectionMode d)
        {
            switch (d)
            {
                case RhinoArcGIS.Core.Sync.SyncDirectionMode.PullOnly: return "pull only";
                case RhinoArcGIS.Core.Sync.SyncDirectionMode.PushOnly: return "push only";
                default: return "two-way";
            }
        }

        public RhinoArcGIS.Core.Sync.SyncDirectionMode ActiveDirection
        {
            get => ActiveLink?.Direction ?? RhinoArcGIS.Core.Sync.SyncDirectionMode.TwoWay;
            set { if (ActiveLink != null) ActiveLink.Direction = value; }
        }

        void AddLink()
        {
            var link = new LayerLink();
            // A sensible starting point: the first ArcGIS layer not already linked.
            foreach (var name in ArcGisLayers)
            {
                var taken = false;
                foreach (var l in Links) if (l.ArcGisLayer == name) { taken = true; break; }
                if (!taken) { link.ArcGisLayer = name; break; }
            }
            Links.Add(link);
            ActiveLink = link;
        }

        void RemoveLink()
        {
            if (ActiveLink == null) return;
            var index = Links.IndexOf(ActiveLink);
            Links.Remove(ActiveLink);
            ActiveLink = Links.Count == 0 ? null : Links[Math.Min(index, Links.Count - 1)];
        }

        /// <summary>Adds or updates the row for an ArcGIS layer and makes it active; for the test bridge.</summary>
        internal LayerLink SetLink(string arcgisLayer, string rhinoLayer, RhinoArcGIS.Core.Sync.SyncDirectionMode direction)
        {
            if (string.IsNullOrWhiteSpace(arcgisLayer)) throw new ArgumentException("arcgisLayer is required.");
            var link = Links.FirstOrDefault(l => l.ArcGisLayer == arcgisLayer);
            if (link == null) { link = new LayerLink { ArcGisLayer = arcgisLayer }; Links.Add(link); }
            if (rhinoLayer != null) link.RhinoLayer = rhinoLayer;
            link.Direction = direction;
            ActiveLink = link;
            SaveLinks();
            return link;
        }

        /// <summary>Removes the row for an ArcGIS layer; for the test bridge.</summary>
        internal bool RemoveLink(string arcgisLayer)
        {
            var link = Links.FirstOrDefault(l => l.ArcGisLayer == arcgisLayer);
            if (link == null) return false;
            if (ActiveLink == link) ActiveLink = null;
            Links.Remove(link);
            return true;
        }

        /// <summary>The one-line result a row shows after a run.</summary>
        static string DescribeRun(bool apply, IReadOnlyList<SyncRow> rows)
        {
            var changed = rows.Count(r => r.IsChanged);
            var held = rows.Count(r => r.State == "Held");
            if (apply)
                return held > 0
                    ? $"applied: {changed - held} of {rows.Count} changed, {held} held"
                    : $"applied: {changed} of {rows.Count} changed";
            return changed == 0 ? $"in sync ({rows.Count})" : $"{changed} of {rows.Count} need attention";
        }

        bool _loadingLinks;

        /// <summary>The document the table was loaded from; saves go to that document only.</summary>
        uint _linksDocSerial;

        /// <summary>Loads the table from the Rhino document; called once Rhino is up.</summary>
        void LoadLinks()
        {
            _loadingLinks = true;
            try
            {
                Links.Clear();
                foreach (var link in LayerLinkStore.Load()) Links.Add(link);
                ActiveLink = Links.Count > 0 ? Links[0] : null;
                SubTab = Links.Count > 0 ? "Sync" : "Link";
                BulkSummary = BulkSummaryPrompt;
                _linksDocSerial = RhinoHost.GetActiveDocumentSerial();
                LoadDocumentSettings();
            }
            finally { _loadingLinks = false; }
            ReconcileLinkLayers();
        }

        void SaveLinks()
        {
            if (_loadingLinks || !IsRunning) return;
            // Never write one document's table into another (see OnRhinoDocumentChanging).
            if (_linksDocSerial == 0 || _linksDocSerial != RhinoHost.GetActiveDocumentSerial()) return;
            try { LayerLinkStore.Save(Links); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Rhino.Inside could not save links: {ex}"); }
        }

        /// <summary>Map layer name -> data source, from the last read of the map.</summary>
        IReadOnlyList<KeyValuePair<string, string>> _layerSources = Array.Empty<KeyValuePair<string, string>>();

        /// <summary>
        /// Points every link at its layer in the current map. A link whose name is gone but whose
        /// unique data source is in the map under another name (renamed, or re-added as "Name (2)")
        /// follows the data. Missing or ambiguous layer objects are flagged and never rebound by name.
        /// </summary>
        void ReconcileLinkLayers()
        {
            if (_layerSources.Count == 0 && ArcGisLayers.Count == 0) return; // map not read yet
            var changed = false;
            foreach (var link in Links)
            {
                if (!link.IsComplete) continue;
                var binding = RhinoArcGIS.Core.Identity.LayerSourceBinding.Resolve(
                    link.ArcGisLayer, link.ArcGisSource, _layerSources);
                if (binding.Status == RhinoArcGIS.Core.Identity.LayerSourceBinding.Status.Resolved)
                {
                    if (!string.Equals(link.ArcGisLayer, binding.Name, StringComparison.Ordinal))
                    {
                        // Keep the Rhino layer where it was; it was usually named after the
                        // original ArcGIS layer label.
                        if (string.IsNullOrWhiteSpace(link.RhinoLayer)) link.RhinoLayer = link.ArcGisLayer;
                        link.ArcGisLayer = binding.Name;
                        changed = true;
                    }
                    if (!string.Equals(link.ArcGisSource, binding.Source, StringComparison.OrdinalIgnoreCase))
                    {
                        link.ArcGisSource = binding.Source;
                        changed = true;
                    }
                    link.IsArcGisLayerMissing = false;
                    continue;
                }
                link.IsArcGisLayerMissing = true;
            }
            if (changed) SaveLinks();
            NotifyPropertyChanged(() => MissingLinkCount);
        }

        public int MissingLinkCount => Links.Count(l => l.IsArcGisLayerMissing);

        async Task CreateArcGisLayerForCommandAsync()
        {
            try
            {
                await NewArcGisLayerAsync(NewArcGisRhinoLayer, NewArcGisLayerName);
            }
            catch (Exception ex)
            {
                NewLayerSummary = "Create and push failed: " + ex.Message;
                System.Diagnostics.Debug.WriteLine($"Rhino.Inside new-layer creation failed: {ex}");
                ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(
                    $"Creating the ArcGIS layer failed.\n\n{ex}", "Rhino.Inside");
            }
        }

        /// <summary>
        /// Creates, links, and initially pushes one Rhino layer. Kept internal so the live bridge
        /// exercises the exact pane workflow instead of a second test-only implementation.
        /// </summary>
        /// <summary>What to do when the objects to publish are already linked to another ArcGIS layer.</summary>
        internal enum LinkedObjectsChoice { Ask, Copy, Move }

        internal async Task<NewLayerPushResult> NewArcGisLayerAsync(string rhinoLayer, string requestedName,
            LinkedObjectsChoice linked = LinkedObjectsChoice.Ask)
        {
            IsBusy = true;
            NewLayerSummary = $"Inspecting '{rhinoLayer}' and creating its ArcGIS schema…";
            string createdLayer = null;
            string linkNote = null;
            try
            {
                // One Rhino object carries one link. Objects already linked to another ArcGIS layer
                // are either copied (the original link stays) or moved (their link ends and starts
                // on the new layer); publishing them as they are would do neither correctly.
                var linkedTo = RhinoHost.GetLinkedLayers(rhinoLayer);
                if (linkedTo.Count > 0)
                {
                    var names = string.Join(", ", linkedTo.Keys.Select(k => $"'{k}'"));
                    var count = linkedTo.Values.Sum();
                    var copyLayer = RhinoArcGIS.Core.Sync.NewLayerInference.SanitizeName(
                        string.IsNullOrWhiteSpace(requestedName) ? rhinoLayer + " copy" : requestedName, "RhinoLayer", 60);
                    if (linked == LinkedObjectsChoice.Ask)
                    {
                        var answer = ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(
                            $"{count} object(s) on '{rhinoLayer}' are linked to ArcGIS layer {names}.\n\n" +
                            $"Yes: publish a copy on a new Rhino layer '{copyLayer}'. The existing link is untouched.\n" +
                            $"No: move these objects' link to the new ArcGIS layer. The link to {names} ends.\n" +
                            "Cancel: do nothing.",
                            "Rhino.Inside", System.Windows.MessageBoxButton.YesNoCancel,
                            System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.Yes);
                        if (answer == System.Windows.MessageBoxResult.Cancel)
                        {
                            NewLayerSummary = "Cancelled: nothing was created.";
                            return null;
                        }
                        linked = answer == System.Windows.MessageBoxResult.Yes ? LinkedObjectsChoice.Copy : LinkedObjectsChoice.Move;
                    }

                    if (linked == LinkedObjectsChoice.Copy)
                    {
                        var copied = RhinoHost.CopyLayerUnlinked(rhinoLayer, copyLayer);
                        linkNote = $"Copied {copied} object(s) to Rhino layer '{copyLayer}'; '{rhinoLayer}' keeps its link to {names}.";
                        rhinoLayer = copyLayer;
                    }
                    else
                    {
                        RhinoHost.StripLinks(rhinoLayer);
                        foreach (var old in Links.Where(l => string.Equals(l.EffectiveRhinoLayer, rhinoLayer, StringComparison.OrdinalIgnoreCase)).ToList())
                            Links.Remove(old);
                        linkNote = $"Moved {count} object(s) off {names}; their link now points at the new layer.";
                    }
                    RefreshRhinoLayers();
                }

                var creation = await SyncCoordinator.CreateLayerFromRhinoAsync(rhinoLayer, requestedName);
                createdLayer = creation.LayerName;
                await RefreshArcGisLayersAsync();

                var link = SetLink(createdLayer, rhinoLayer, RhinoArcGIS.Core.Sync.SyncDirectionMode.TwoWay);
                link.ArcGisSource = _layerSources.FirstOrDefault(l =>
                    string.Equals(l.Key, createdLayer, StringComparison.OrdinalIgnoreCase)).Value;
                var previousAttributePolicy = SyncCoordinator.AttributesEditableInRhino;
                RhinoArcGIS.Core.Reporting.SyncReport report;
                try
                {
                    // The very first push must carry inferred user-text fields even when the
                    // project's normal safety toggle keeps Rhino attributes read-only. Persist
                    // those shared field rules on the new link too, otherwise the first push would
                    // work and the next Rhino attribute edit would appear to do nothing.
                    SyncCoordinator.AttributesEditableInRhino = true;
                    var profileDraft = await SyncCoordinator.GetProfileDraftAsync(
                        createdLayer, rhinoLayer, profileJson: null,
                        expectedArcGisSource: link.ArcGisSource);
                    link.ProfileJson = RhinoArcGIS.Core.Profiles.ProfileJson.Serialize(profileDraft.Profile);
                    report = await SyncCoordinator.ApplyAsync(
                        createdLayer, rhinoLayer, RhinoArcGIS.Core.Sync.ConflictResolution.Manual,
                        RhinoArcGIS.Core.Sync.SyncDirectionMode.TwoWay, link.ProfileJson,
                        link.ArcGisSource);
                }
                finally
                {
                    SyncCoordinator.AttributesEditableInRhino = previousAttributePolicy;
                    NotifyPropertyChanged(() => AttributesEditableInRhino);
                }

                var rows = report.Entries.Select(SyncRow.From).ToList();
                link.LastResult = DescribeRun(true, rows);
                link.LastRows = rows;
                ActiveLink = link;
                SetRows(rows);

                var created = report.CountOf(RhinoArcGIS.Core.Reporting.SyncOutcome.Created);
                var skipped = report.CountOf(RhinoArcGIS.Core.Reporting.SyncOutcome.Skipped);
                var failed = report.CountOf(RhinoArcGIS.Core.Reporting.SyncOutcome.Failed);
                NewLayerSummary = $"Created '{createdLayer}' and pushed {created} object(s)." +
                    (linkNote == null ? string.Empty : " " + linkNote);
                if (skipped > 0) NewLayerSummary += $" Skipped {skipped}.";
                if (failed > 0) NewLayerSummary += $" Failed {failed}; review the Sync tab.";
                SyncSummary = failed > 0
                    ? $"Initial push completed with {failed} failure(s)."
                    : $"Initial push complete — {created} object(s), ready to preview.";
                Refresh();
                SaveLinks();
                return new NewLayerPushResult(creation, report, rhinoLayer);
            }
            catch (Exception original)
            {
                if (!string.IsNullOrWhiteSpace(createdLayer))
                {
                    RemoveLink(createdLayer);
                    try
                    {
                        await Task.Run(() =>
                            new RhinoArcGIS.ArcGIS.ArcGISAdapter().DeleteCreatedFeatureClass(createdLayer));
                        await RefreshArcGisLayersAsync();
                    }
                    catch (Exception cleanup)
                    {
                        throw new AggregateException(
                            $"Creating '{createdLayer}' failed and its partial feature class could not be removed.",
                            original, cleanup);
                    }
                }
                throw;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>Runs every row in the table in turn, and writes each row's one-line result.</summary>
        internal async Task RunAllAsync(bool apply)
        {
            IsBusy = true;
            BulkSummary = apply ? "Applying all complete links…" : "Previewing all complete links…";
            try
            {
                int completed = 0, needsAttention = 0, clean = 0, failed = 0;
                foreach (var link in Links)
                {
                    if (!link.IsComplete) continue;
                    try
                    {
                        var report = apply
                            ? await SyncCoordinator.ApplyAsync(link.ArcGisLayer, link.EffectiveRhinoLayer,
                                ConflictPolicy, link.Direction, link.ProfileJson, link.ArcGisSource)
                            : await SyncCoordinator.PreviewAsync(link.ArcGisLayer, link.EffectiveRhinoLayer,
                                link.Direction, link.ProfileJson, link.ArcGisSource);
                        var rows = report.Entries.Select(SyncRow.From).ToList();
                        link.LastResult = DescribeRun(apply, rows);
                        link.LastRows = rows;
                        if (link == ActiveLink) SetRows(rows);
                        completed++;
                        if (rows.Any(r => r.IsChanged)) needsAttention++;
                        else clean++;
                    }
                    catch (Exception ex)
                    {
                        link.LastResult = "failed: " + ex.Message;
                        link.LastRows = Array.Empty<SyncRow>();
                        failed++;
                        if (link == ActiveLink) SetRows(Array.Empty<SyncRow>());
                    }
                }
                BulkSummary = $"{(apply ? "Applied" : "Previewed")} {completed + failed} link(s) — " +
                              $"{needsAttention} need review, {clean} in sync" +
                              (failed > 0 ? $", {failed} failed." : ".");
                SyncSummary = ActiveLink == null ? "No layer pair selected."
                    : ActiveLink.LastResult == null ? "No preview run for this pair yet."
                    : $"Selected pair — {ActiveLink.LastResult}.";
                Refresh();
            }
            finally { IsBusy = false; }
        }

        string _bulkSummary = BulkSummaryPrompt;
        public string BulkSummary
        {
            get => _bulkSummary;
            private set => SetProperty(ref _bulkSummary, value);
        }

        // ------------------------------------------------------------------ profile authoring

        ProfileDraft _profileDraft;
        bool _loadingProfile;
        int _profileLoadVersion;

        public System.Collections.ObjectModel.ObservableCollection<ProfileFieldRow> ProfileFields { get; }
            = new System.Collections.ObjectModel.ObservableCollection<ProfileFieldRow>();

        public IReadOnlyList<FieldOwnershipChoice> FieldOwnershipChoices { get; } = new[]
        {
            new FieldOwnershipChoice(RhinoArcGIS.Core.Attributes.FieldOwnership.RhinoOwned, "Rhino owns"),
            new FieldOwnershipChoice(RhinoArcGIS.Core.Attributes.FieldOwnership.ArcGisOwned, "ArcGIS owns"),
            new FieldOwnershipChoice(RhinoArcGIS.Core.Attributes.FieldOwnership.Shared, "Shared"),
            new FieldOwnershipChoice(RhinoArcGIS.Core.Attributes.FieldOwnership.LocalOnlyRhino, "Rhino only"),
            new FieldOwnershipChoice(RhinoArcGIS.Core.Attributes.FieldOwnership.LocalOnlyArcGis, "ArcGIS only"),
            new FieldOwnershipChoice(RhinoArcGIS.Core.Attributes.FieldOwnership.Derived, "Derived"),
            new FieldOwnershipChoice(RhinoArcGIS.Core.Attributes.FieldOwnership.Locked, "Locked")
        };

        public Array GeometryOwners => Enum.GetValues(typeof(RhinoArcGIS.Core.Profiles.GeometryOwner));
        public Array PullModes => Enum.GetValues(typeof(RhinoArcGIS.Core.Profiles.PullMode));
        public Array PushModes => Enum.GetValues(typeof(RhinoArcGIS.Core.Profiles.PushMode));
        public Array ElevationModes => Enum.GetValues(typeof(RhinoArcGIS.Core.Spatial.ElevationMode));
        public Array ExtrusionModes => Enum.GetValues(typeof(RhinoArcGIS.Core.Profiles.ExtrusionMode));

        string _profileProjectName;
        public string ProfileProjectName
        {
            get => _profileProjectName;
            set { if (SetProperty(ref _profileProjectName, value)) MarkProfileDirty(); }
        }

        string _profileCrs = "-";
        public string ProfileCrs
        {
            get => _profileCrs;
            private set => SetProperty(ref _profileCrs, value);
        }

        string _profileGeometryTarget = "-";
        public string ProfileGeometryTarget
        {
            get => _profileGeometryTarget;
            private set => SetProperty(ref _profileGeometryTarget, value);
        }

        RhinoArcGIS.Core.Profiles.GeometryOwner _profileGeometryOwner;
        public RhinoArcGIS.Core.Profiles.GeometryOwner ProfileGeometryOwner
        {
            get => _profileGeometryOwner;
            set { if (SetProperty(ref _profileGeometryOwner, value)) MarkProfileDirty(); }
        }

        RhinoArcGIS.Core.Profiles.PullMode _profileDefaultPullMode;
        public RhinoArcGIS.Core.Profiles.PullMode ProfileDefaultPullMode
        {
            get => _profileDefaultPullMode;
            set { if (SetProperty(ref _profileDefaultPullMode, value)) MarkProfileDirty(); }
        }

        RhinoArcGIS.Core.Profiles.PushMode _profileDefaultPushMode;
        public RhinoArcGIS.Core.Profiles.PushMode ProfileDefaultPushMode
        {
            get => _profileDefaultPushMode;
            set { if (SetProperty(ref _profileDefaultPushMode, value)) MarkProfileDirty(); }
        }

        RhinoArcGIS.Core.Spatial.ElevationMode _profileElevationMode;
        public RhinoArcGIS.Core.Spatial.ElevationMode ProfileElevationMode
        {
            get => _profileElevationMode;
            set { if (SetProperty(ref _profileElevationMode, value)) MarkProfileDirty(); }
        }

        RhinoArcGIS.Core.Profiles.ExtrusionMode _profileExtrusionMode;
        public RhinoArcGIS.Core.Profiles.ExtrusionMode ProfileExtrusionMode
        {
            get => _profileExtrusionMode;
            set { if (SetProperty(ref _profileExtrusionMode, value)) MarkProfileDirty(); }
        }

        string _profileBaseElevationField;
        public string ProfileBaseElevationField
        {
            get => _profileBaseElevationField;
            set { if (SetProperty(ref _profileBaseElevationField, value)) MarkProfileDirty(); }
        }

        string _profileHeightField;
        public string ProfileHeightField
        {
            get => _profileHeightField;
            set { if (SetProperty(ref _profileHeightField, value)) MarkProfileDirty(); }
        }

        bool _profileHasUnsavedChanges;
        public bool ProfileHasUnsavedChanges
        {
            get => _profileHasUnsavedChanges;
            private set => SetProperty(ref _profileHasUnsavedChanges, value);
        }

        string _profileStatus = "Select a complete layer pair to author its profile.";
        public string ProfileStatus
        {
            get => _profileStatus;
            private set => SetProperty(ref _profileStatus, value);
        }

        public int IncludedProfileFieldCount => ProfileFields.Count(row => row.Included);

        ProfileFieldRow _selectedProfileField;
        public ProfileFieldRow SelectedProfileField
        {
            get => _selectedProfileField;
            set => SetProperty(ref _selectedProfileField, value);
        }

        void MarkProfileDirty()
        {
            if (_loadingProfile || _profileDraft == null) return;
            ProfileHasUnsavedChanges = true;
            ProfileStatus = "Unsaved profile changes — Save profile before Preview or Apply.";
            NotifyPropertyChanged(() => IncludedProfileFieldCount);
        }

        void OnProfileFieldChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) => MarkProfileDirty();

        void ClearProfileEditor(string status)
        {
            _profileLoadVersion++;
            _profileDraft = null;
            foreach (var row in ProfileFields) row.PropertyChanged -= OnProfileFieldChanged;
            ProfileFields.Clear();
            SelectedProfileField = null;
            _loadingProfile = true;
            try
            {
                ProfileProjectName = string.Empty;
                ProfileCrs = "-";
                ProfileGeometryTarget = "-";
                ProfileBaseElevationField = string.Empty;
                ProfileHeightField = string.Empty;
                ProfileHasUnsavedChanges = false;
                ProfileStatus = status;
            }
            finally { _loadingProfile = false; }
            NotifyPropertyChanged(() => IncludedProfileFieldCount);
            (SaveProfileCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            (RestoreProfileCommand as SimpleCommand)?.RaiseCanExecuteChanged();
        }

        async Task LoadProfileAsync(bool useDefaults = false)
        {
            var link = ActiveLink;
            if (!IsRunning || link?.IsComplete != true)
            {
                ClearProfileEditor("Start Rhino and select a complete layer pair to author its profile.");
                return;
            }

            var version = ++_profileLoadVersion;
            IsBusy = true;
            ProfileStatus = useDefaults ? "Loading safe defaults…" : "Loading the current schema and profile…";
            try
            {
                var draft = await SyncCoordinator.GetProfileDraftAsync(
                    link.ArcGisLayer, link.EffectiveRhinoLayer, useDefaults ? null : link.ProfileJson,
                    link.ArcGisSource);
                if (version != _profileLoadVersion || link != ActiveLink) return;

                _loadingProfile = true;
                _profileDraft = draft;
                try
                {
                    var profile = draft.Profile;
                    var layer = profile.Layers[0];
                    ProfileProjectName = profile.ProjectName;
                    ProfileCrs = profile.ArcGisCrs ?? "(unknown)";
                    ProfileGeometryTarget = layer.GeometryTarget.ToString();
                    ProfileGeometryOwner = layer.Sync.GeometryOwner;
                    ProfileDefaultPullMode = layer.Sync.DefaultPullMode;
                    ProfileDefaultPushMode = layer.Sync.DefaultPushMode;
                    ProfileElevationMode = layer.Geometry.ElevationMode;
                    ProfileExtrusionMode = layer.Geometry.ExtrusionMode;
                    ProfileBaseElevationField = layer.Geometry.BaseElevationField ?? string.Empty;
                    ProfileHeightField = layer.Geometry.HeightField ?? string.Empty;

                    foreach (var row in ProfileFields) row.PropertyChanged -= OnProfileFieldChanged;
                    ProfileFields.Clear();
                    foreach (var definition in draft.Schema.Fields)
                    {
                        var mapping = layer.Attributes.FirstOrDefault(item => item != null &&
                            string.Equals(item.ArcGisField, definition.Name, StringComparison.OrdinalIgnoreCase));
                        var row = ProfileFieldRow.From(definition, mapping);
                        row.PropertyChanged += OnProfileFieldChanged;
                        ProfileFields.Add(row);
                    }
                    SelectedProfileField = ProfileFields.FirstOrDefault(row => !row.IsManaged)
                                           ?? ProfileFields.FirstOrDefault();

                    ProfileHasUnsavedChanges = false;
                    ProfileStatus = link.HasCustomProfile && !useDefaults
                        ? $"Custom profile loaded — {IncludedProfileFieldCount} of {ProfileFields.Count} fields included."
                        : $"Generated defaults loaded — {IncludedProfileFieldCount} of {ProfileFields.Count} fields included.";
                }
                finally { _loadingProfile = false; }

                NotifyPropertyChanged(() => IncludedProfileFieldCount);
                (SaveProfileCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (RestoreProfileCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            }
            catch (Exception ex)
            {
                if (version == _profileLoadVersion)
                {
                    _profileDraft = null;
                    ProfileStatus = "Could not load the profile: " + ex.Message;
                }
            }
            finally { IsBusy = false; }
        }

        async Task SaveProfileAsync()
        {
            var link = ActiveLink;
            if (_profileDraft == null || link?.IsComplete != true) return;

            IsBusy = true;
            try
            {
                var profile = _profileDraft.Profile;
                var layer = profile.Layers[0];
                profile.ProjectName = ProfileProjectName;
                layer.Sync.GeometryOwner = ProfileGeometryOwner;
                layer.Sync.DefaultPullMode = ProfileDefaultPullMode;
                layer.Sync.DefaultPushMode = ProfileDefaultPushMode;
                layer.Geometry.ElevationMode = ProfileElevationMode;
                layer.Geometry.ExtrusionMode = ProfileExtrusionMode;
                layer.Geometry.BaseElevationField = NullIfBlank(ProfileBaseElevationField);
                layer.Geometry.HeightField = NullIfBlank(ProfileHeightField);
                layer.Attributes.Clear();

                foreach (var row in ProfileFields.Where(item => item.Included))
                {
                    if (string.IsNullOrWhiteSpace(row.RhinoKey))
                        throw new InvalidOperationException($"Choose a Rhino key for ArcGIS field '{row.ArcGisField}'.");
                    var mapping = row.ToMapping();
                    RhinoArcGIS.Core.Profiles.ProfileAuthoring.ApplyOwnershipPolicy(
                        mapping, _profileDraft.Schema.FindField(row.ArcGisField), false, false);
                    layer.Attributes.Add(mapping);
                }

                var validation = RhinoArcGIS.Core.Profiles.ProfileValidation.Validate(profile);
                if (validation.IsBlocking)
                    throw new InvalidOperationException(string.Join("; ", validation.Issues.Select(issue => issue.Message)));

                link.ProfileJson = RhinoArcGIS.Core.Profiles.ProfileJson.Serialize(profile);
                ProfileHasUnsavedChanges = false;
                ProfileStatus = $"Profile saved with {layer.Attributes.Count} field mapping(s). Preview to verify the new rules.";
                SaveLinks();
            }
            catch (Exception ex)
            {
                ProfileStatus = "Profile was not saved: " + ex.Message;
            }
            finally { IsBusy = false; }
        }

        async Task RestoreProfileAsync()
        {
            var link = ActiveLink;
            if (link?.IsComplete != true) return;
            link.ProfileJson = null;
            SaveLinks();
            await LoadProfileAsync(useDefaults: true);
            if (link == ActiveLink)
                ProfileStatus = "Custom profile removed. This link now uses the generated defaults.";
        }

        static string NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>Exercises the same profile editor save path from the opt-in live bridge.</summary>
        internal async Task<object> ConfigureProfileFieldForTestAsync(string arcGisField, string rhinoKey,
                                                                       string owner, bool included)
        {
            await LoadProfileAsync();
            if (_profileDraft == null) throw new InvalidOperationException(ProfileStatus);
            var row = ProfileFields.FirstOrDefault(item =>
                string.Equals(item.ArcGisField, arcGisField, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Field '{arcGisField}' is not in the active layer schema.");
            row.Included = included;
            if (rhinoKey != null) row.RhinoKey = rhinoKey;
            if (!row.IsManaged && Enum.TryParse(owner, true, out RhinoArcGIS.Core.Attributes.FieldOwnership parsed))
                row.Owner = parsed;
            await SaveProfileAsync();
            if (ProfileHasUnsavedChanges) throw new InvalidOperationException(ProfileStatus);
            return new
            {
                field = row.ArcGisField,
                row.RhinoKey,
                owner = row.Owner.ToString(),
                row.Included,
                custom = ActiveLink?.HasCustomProfile == true,
                status = ProfileStatus
            };
        }

        // ------------------------------------------------------------------ tracked layers

        public System.Collections.ObjectModel.ObservableCollection<TrackedLayerRow> TrackedLayers { get; }
            = new System.Collections.ObjectModel.ObservableCollection<TrackedLayerRow>();

        public int TrackedLayerCount => TrackedLayers.Count;

        void RefreshTrackedLayers()
        {
            TrackedLayers.Clear();
            foreach (var t in RhinoHost.GetTrackedLayers())
                TrackedLayers.Add(new TrackedLayerRow
                {
                    Layer = t.Layer,
                    ArcGisLayer = t.ArcGisLayer ?? "-",
                    Tracked = t.Tracked,
                    Parts = t.Parts,
                    Total = t.Total
                });
            NotifyPropertyChanged(() => TrackedLayerCount);
        }

        /// <summary>
        /// Keeps both layer pickers current on their own, so there is nothing to press to refresh.
        /// </summary>
        void HookLayerWatchers()
        {
            ArcGIS.Desktop.Mapping.Events.LayersAddedEvent.Subscribe(_ => OnArcGisLayersChanged());
            ArcGIS.Desktop.Mapping.Events.LayersRemovedEvent.Subscribe(_ => OnArcGisLayersChanged());
            ArcGIS.Desktop.Mapping.Events.ActiveMapViewChangedEvent.Subscribe(_ => OnArcGisLayersChanged());
            // A rename in the Contents pane: links follow the data to the new name.
            ArcGIS.Desktop.Mapping.Events.MapMemberPropertiesChangedEvent.Subscribe(e =>
            {
                if (e.EventHints.Any(h => h == ArcGIS.Desktop.Mapping.Events.MapMemberEventHint.Name))
                    OnArcGisLayersChanged();
            });

            // Raised by the host once Rhino is running and its layer table changes.
            RhinoHost.LayersChanged += (s, e) => OnUiThread(RefreshRhinoLayers);
        }

        void OnArcGisLayersChanged() => OnUiThread(() => _ = RefreshArcGisLayersAsync());

        /// <summary>These events can arrive off the UI thread, and the pickers are bound collections.</summary>
        static void OnUiThread(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }

        /// <summary>Rhino layer names, refreshed from the running Rhino document.</summary>
        void RefreshRhinoLayers()
        {
            var previous = SelectedRhinoLayer;
            var previousNewLayer = NewArcGisRhinoLayer;

            RhinoLayers.Clear();
            foreach (var name in RhinoHost.GetLayerNames()) RhinoLayers.Add(name);

            if (previous != null && RhinoLayers.Contains(previous)) SelectedRhinoLayer = previous;
            NewArcGisRhinoLayer = previousNewLayer != null && RhinoLayers.Contains(previousNewLayer)
                ? previousNewLayer
                : RhinoLayers.FirstOrDefault();
        }

        /// <summary>
        /// Show the DockPane.
        /// </summary>
        public static void Show()
        {
            DockPane pane = FrameworkApplication.DockPaneManager.Find(_dockPaneID);
            if (pane == null)
                return;

            pane.Activate();
        }

        /// <summary>
        /// Text shown near the top of the DockPane.
        /// </summary>
        public string _heading = "Rhino.Inside";
        public string Heading
        {
            get => _heading;
            set => SetProperty(ref _heading, value);
        }

        public ICommand LaunchCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand PullCommand { get; }
        public ICommand SetAnchorFromMapCommand { get; }
        public ICommand ClearAnchorCommand { get; }
        public ICommand ShowRhinoCommand { get; }
        public ICommand PreviewSyncCommand { get; }
        public ICommand ApplySyncCommand { get; }

        string _newArcGisRhinoLayer;
        /// <summary>The Rhino layer used to infer and populate a new ArcGIS feature class.</summary>
        public string NewArcGisRhinoLayer
        {
            get => _newArcGisRhinoLayer;
            set
            {
                if (!SetProperty(ref _newArcGisRhinoLayer, value)) return;
                NewArcGisLayerName = RhinoArcGIS.Core.Sync.NewLayerInference.SanitizeName(
                    value, "RhinoLayer", 60);
                RefreshNewLayerPlan();
                (CreateArcGisLayerCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            }
        }

        string _newArcGisLayerName = "RhinoLayer";
        public string NewArcGisLayerName
        {
            get => _newArcGisLayerName;
            set => SetProperty(ref _newArcGisLayerName, value);
        }

        string _newLayerSummary = "Choose a Rhino layer to infer its ArcGIS geometry and fields.";
        public string NewLayerSummary
        {
            get => _newLayerSummary;
            private set => SetProperty(ref _newLayerSummary, value);
        }

        string _newLayerPlanSummary = "No Rhino layer selected.";
        public string NewLayerPlanSummary
        {
            get => _newLayerPlanSummary;
            private set => SetProperty(ref _newLayerPlanSummary, value);
        }

        void RefreshNewLayerPlan()
        {
            if (!IsRunning || string.IsNullOrWhiteSpace(NewArcGisRhinoLayer))
            {
                NewLayerPlanSummary = "No Rhino layer selected.";
                return;
            }

            try
            {
                var plan = SyncCoordinator.PlanLayerFromRhino(NewArcGisRhinoLayer, NewArcGisLayerName);
                if (plan.Target == RhinoArcGIS.Core.Geometry.GeometryTarget.Unsupported || plan.MatchingCount == 0)
                {
                    NewLayerPlanSummary = $"No publishable point, curve, polygon, mesh, or Brep geometry on this layer ({plan.ObjectCount} object(s)).";
                    return;
                }

                var geometry = plan.Target == RhinoArcGIS.Core.Geometry.GeometryTarget.PointZ ? "Point"
                    : plan.Target == RhinoArcGIS.Core.Geometry.GeometryTarget.PolylineZ ? "Polyline"
                    : plan.Target == RhinoArcGIS.Core.Geometry.GeometryTarget.Multipatch ? "Multipatch"
                    : "Polygon";
                var fields = plan.Fields.Count == 0
                    ? "no user-text fields"
                    : string.Join(", ", plan.Fields.Take(4).Select(field => $"{field.Name} ({field.Type})")) +
                      (plan.Fields.Count > 4 ? $", +{plan.Fields.Count - 4} more" : string.Empty);
                var skipped = plan.ObjectCount - plan.MatchingCount;
                NewLayerPlanSummary = $"Will create a Z-aware {geometry} layer from {plan.MatchingCount} object(s), with {fields}." +
                    (skipped > 0 ? $" {skipped} other object(s) will be skipped." : string.Empty);
            }
            catch (Exception ex)
            {
                NewLayerPlanSummary = "Could not infer the layer: " + ex.Message;
            }
        }

        public System.Collections.ObjectModel.ObservableCollection<string> ArcGisLayers { get; }
            = new System.Collections.ObjectModel.ObservableCollection<string>();

        public System.Collections.ObjectModel.ObservableCollection<string> RhinoLayers { get; }
            = new System.Collections.ObjectModel.ObservableCollection<string>();

        /// <summary>
        /// Rhino layer of the active row; null means use the ArcGIS layer's own name. Setting it
        /// with no row selected creates one, so the pickers work before the table has been visited.
        /// </summary>
        public string SelectedRhinoLayer
        {
            get => ActiveLink?.RhinoLayer;
            set
            {
                if (ActiveLink == null) { if (value == null) return; Links.Add(new LayerLink()); ActiveLink = Links[Links.Count - 1]; }
                ActiveLink.RhinoLayer = value;
            }
        }

        /// <summary>Every row from the last run, before filtering.</summary>
        readonly List<SyncRow> _allRows = new List<SyncRow>();

        public System.Collections.ObjectModel.ObservableCollection<SyncRow> SyncRows { get; }
            = new System.Collections.ObjectModel.ObservableCollection<SyncRow>();

        public System.Collections.ObjectModel.ObservableCollection<SyncRollupItem> Rollup { get; }
            = new System.Collections.ObjectModel.ObservableCollection<SyncRollupItem>();

        bool _showOnlyChanged = true;
        /// <summary>
        /// Defaults to on: a sync over a whole layer is mostly rows that need nothing done, and
        /// listing them buries the handful that matter.
        /// </summary>
        public bool ShowOnlyChanged
        {
            get => _showOnlyChanged;
            set { SetProperty(ref _showOnlyChanged, value); ApplyRowFilter(); }
        }

        int _changedCount;
        public int ChangedCount
        {
            get => _changedCount;
            private set => SetProperty(ref _changedCount, value);
        }

        void ApplyRowFilter()
        {
            SyncRows.Clear();

            var shown = 0;
            foreach (var row in _allRows)
            {
                if (ShowOnlyChanged && !row.IsChanged) continue;
                if (shown++ >= 500) break;   // The roll-up carries the totals; the list is for reading.
                SyncRows.Add(row);
            }
        }

        void SetRows(IEnumerable<SyncRow> rows)
        {
            IsPreviewStale = false;
            _ignoreEditsUntil = DateTime.UtcNow.AddSeconds(3);
            _allRows.Clear();
            _allRows.AddRange(rows);

            ChangedCount = _allRows.Count(r => r.IsChanged);

            Rollup.Clear();
            foreach (var item in SyncRollupItem.From(_allRows)) Rollup.Add(item);

            ApplyRowFilter();
        }

        public Array ConflictPolicies => Enum.GetValues(typeof(RhinoArcGIS.Core.Sync.ConflictResolution));

        RhinoArcGIS.Core.Sync.ConflictResolution _conflictPolicy = RhinoArcGIS.Core.Sync.ConflictResolution.Manual;
        public RhinoArcGIS.Core.Sync.ConflictResolution ConflictPolicy
        {
            get => _conflictPolicy;
            set => SetProperty(ref _conflictPolicy, value);
        }

        /// <summary>ArcGIS layer of the active row.</summary>
        public string SelectedArcGisLayer
        {
            get => ActiveLink?.ArcGisLayer;
            set
            {
                if (value == null) return;
                if (ActiveLink == null) { Links.Add(new LayerLink()); ActiveLink = Links[Links.Count - 1]; }
                SetLayerFromExplicitChoice(ActiveLink, value);
                (PreviewSyncCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (ApplySyncCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (PullCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            }
        }

        void SetLayerFromExplicitChoice(LayerLink link, string layerName)
        {
            var sourceLess = string.IsNullOrWhiteSpace(link.ArcGisSource);
            var labelChanged = !string.Equals(link.ArcGisLayer, layerName, StringComparison.OrdinalIgnoreCase);
            if (!labelChanged && !sourceLess) return; // picker re-announcement must preserve saved identity

            var binding = RhinoArcGIS.Core.Identity.LayerSourceBinding.Resolve(layerName, null, _layerSources);
            if (binding.Status == RhinoArcGIS.Core.Identity.LayerSourceBinding.Status.Resolved)
            {
                link.ArcGisLayer = binding.Name;
                link.ArcGisSource = binding.Source;
                link.IsArcGisLayerMissing = false;
                link.LastResult = null;
                SaveLinks();
            }
            else
            {
                // A new, not-yet-bound row may retain its label while the source list loads.
                // Existing saved links keep their previous name/source if the new choice is not
                // uniquely identifiable.
                if (sourceLess)
                {
                    link.ArcGisLayer = layerName;
                    link.IsArcGisLayerMissing = true;
                }
                link.LastResult = binding.Status == RhinoArcGIS.Core.Identity.LayerSourceBinding.Status.Ambiguous
                    ? $"Cannot rebind '{layerName}': more than one layer object matches that name. Make the intended layer unique, then create or repair the link."
                    : $"Cannot bind '{layerName}': its data source is unavailable.";
                NotifyPropertyChanged(() => MissingLinkCount);
            }
        }

        /// <summary>
        /// Off by default: attributes stay ArcGIS-owned and read-only in Rhino. Turn it on to author
        /// attributes in Rhino and push them out.
        /// </summary>
        /// <remarks>
        /// Saved in the Rhino document next to the links. As a session-only switch it came back off
        /// after every restart, and Rhino-side attribute edits were then silently not pushed.
        /// </remarks>
        public bool AttributesEditableInRhino
        {
            get => SyncCoordinator.AttributesEditableInRhino;
            set
            {
                SyncCoordinator.AttributesEditableInRhino = value;
                NotifyPropertyChanged(() => AttributesEditableInRhino);
                if (IsRunning && _linksDocSerial != 0 && _linksDocSerial == RhinoHost.GetActiveDocumentSerial())
                {
                    var stored = RhinoHost.GetDocumentStrings(AttributesEditableKey);
                    stored.TryGetValue(AttributesEditableKey, out var current);
                    var text = value ? "true" : "false";
                    if (current != text)
                        RhinoHost.SetDocumentStrings(new Dictionary<string, string> { { AttributesEditableKey, text } });
                }
            }
        }

        const string AttributesEditableKey = "gis.settings.attributes_editable";

        /// <summary>Takes the document's saved choice when a document's links are loaded.</summary>
        void LoadDocumentSettings()
        {
            var stored = RhinoHost.GetDocumentStrings(AttributesEditableKey);
            SyncCoordinator.AttributesEditableInRhino =
                stored.TryGetValue(AttributesEditableKey, out var text) && text == "true";
            NotifyPropertyChanged(() => AttributesEditableInRhino);
        }

        string _syncSummary = "No sync run yet.";
        public string SyncSummary
        {
            get => _syncSummary;
            private set => SetProperty(ref _syncSummary, value);
        }

        /// <summary>
        /// Reads the map's feature layers. Runs off the UI thread because it touches only ArcGIS,
        /// which marshals to its own main CIM thread internally.
        /// </summary>
        async Task RefreshArcGisLayersAsync()
        {
            try
            {
                var sources = await Task.Run(() => SyncCoordinator.GetArcGisLayerSources());

                ArcGisLayers.Clear();
                foreach (var layer in sources) ArcGisLayers.Add(layer.Key);
                _layerSources = sources;

                // Clearing the list blanked every picker bound to it; the rows kept their values,
                // so tell the pickers to re-select.
                foreach (var link in Links) link.Reannounce();
                ReconcileLinkLayers();
                NotifyPropertyChanged(() => SelectedArcGisLayer);
            }
            catch (Exception ex)
            {
                SyncSummary = "Could not read map layers: " + ex.Message;
            }
        }

        /// <summary>
        /// Runs a preview or an apply. Deliberately on the UI thread -- the Rhino side of the sync
        /// is affine to the thread Rhino was started on.
        /// </summary>
        async Task RunSyncAsync(bool apply)
        {
            IsBusy = true;
            SyncSummary = apply ? "Applying…" : "Computing the plan…";

            try
            {
                // Awaited, not blocked on: the run happens off the UI thread and marshals its Rhino
                // half back. Blocking here is what hung Pro when applying after a Rhino edit.
                var report = apply
                    ? await SyncCoordinator.ApplyAsync(SelectedArcGisLayer, SelectedRhinoLayer,
                        ConflictPolicy, ActiveDirection, ActiveLink?.ProfileJson, ActiveLink?.ArcGisSource)
                    : await SyncCoordinator.PreviewAsync(SelectedArcGisLayer, SelectedRhinoLayer,
                        ActiveDirection, ActiveLink?.ProfileJson, ActiveLink?.ArcGisSource);

                var rows = report.Entries.Select(SyncRow.From).ToList();
                SetRows(rows);
                if (ActiveLink != null)
                {
                    ActiveLink.LastResult = DescribeRun(apply, rows);
                    ActiveLink.LastRows = rows;
                }

                SyncSummary = apply
                    ? $"Applied — {rows.Count} object(s) processed, {ChangedCount} changed."
                    : ChangedCount == 0
                        ? $"In sync — nothing to do across {rows.Count} object(s)."
                        : $"{ChangedCount} of {rows.Count} object(s) need attention.";

                // Layer-level warnings (a changed data source, for one) are worth reading before
                // the counts, and they are the rows the list would otherwise bury.
                foreach (var entry in report.Entries)
                    if (entry.Outcome == RhinoArcGIS.Core.Reporting.SyncOutcome.Warning && entry.SyncGuid == Guid.Empty)
                        SyncSummary = entry.Message + "\n" + SyncSummary;

                Refresh();
            }
            catch (Exception ex)
            {
                SyncSummary = "Sync failed: " + ex.Message;
                System.Diagnostics.Debug.WriteLine($"Rhino.Inside sync failed: {ex}");
                ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show($"Sync failed.\n\n{ex}", "Rhino.Inside");
            }
            finally
            {
                IsBusy = false;
            }
        }

        void ShowRhino()
        {
            try { RhinoHost.ShowRhinoWindow(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Show Rhino failed: {ex}"); }
        }

        string _anchorStatus = "-";
        public string AnchorStatus
        {
            get => _anchorStatus;
            private set => SetProperty(ref _anchorStatus, value);
        }

        string _modelBaseX = "0";
        /// <summary>Model X the anchor location maps to; 0 puts it on the Rhino origin.</summary>
        public string ModelBaseX
        {
            get => _modelBaseX;
            set => SetProperty(ref _modelBaseX, value);
        }

        string _modelBaseY = "0";
        public string ModelBaseY
        {
            get => _modelBaseY;
            set => SetProperty(ref _modelBaseY, value);
        }

        /// <summary>
        /// Anchors the Rhino document at the centre of the current Pro map view.
        /// </summary>
        async Task SetAnchorFromMapAsync()
        {
            IsBusy = true;
            try
            {
                var centre = await GisUtil.GetMapCentreAsync();
                if (centre == null)
                {
                    AnchorStatus = "No active map view to take a location from.";
                    return;
                }

                double.TryParse(ModelBaseX, out var baseX);
                double.TryParse(ModelBaseY, out var baseY);

                RhinoHost.SetEarthAnchor(centre.Latitude, centre.Longitude, baseX, baseY);
                SyncCoordinator.EnsureGeoreferenceMode();
                Refresh();
            }
            catch (Exception ex)
            {
                AnchorStatus = "Could not set the anchor: " + ex.Message;
                System.Diagnostics.Debug.WriteLine($"Rhino.Inside set anchor failed: {ex}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        void ClearAnchor()
        {
            try
            {
                RhinoHost.ClearEarthAnchor();
                Refresh();
            }
            catch (Exception ex)
            {
                AnchorStatus = "Could not clear the anchor: " + ex.Message;
            }
        }

        // ------------------------------------------------------------------ busy strip

        string _busyPhase;
        /// <summary>What the running pull or sync is doing now.</summary>
        public string BusyPhase
        {
            get => _busyPhase;
            private set => SetProperty(ref _busyPhase, value);
        }

        string _busyNotice;
        /// <summary>A heads-up for the whole run, e.g. a large layer and roughly how long it takes.</summary>
        public string BusyNotice
        {
            get => _busyNotice;
            private set { SetProperty(ref _busyNotice, value); NotifyPropertyChanged(() => HasBusyNotice); }
        }

        public bool HasBusyNotice => IsBusy && !string.IsNullOrEmpty(BusyNotice);

        string _busyElapsed;
        /// <summary>"0:42" since the run started.</summary>
        public string BusyElapsed
        {
            get => _busyElapsed;
            private set => SetProperty(ref _busyElapsed, value);
        }

        System.Windows.Threading.DispatcherTimer _busyTimer;
        DateTime _busySince;

        void StartBusyClock()
        {
            _busySince = DateTime.UtcNow;
            BusyPhase = "Starting…";
            BusyNotice = null;
            BusyElapsed = "0:00";
            _busyTimer = _busyTimer ?? new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Background,
                (s, e) => BusyElapsed = (DateTime.UtcNow - _busySince).ToString(@"m\:ss"),
                System.Windows.Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher);
            _busyTimer.Start();
        }

        void StopBusyClock()
        {
            _busyTimer?.Stop();
            BusyNotice = null;
        }

        static void Post(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) action();
            else dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Normal);
        }

        bool _isBusy;
        public new bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (value && !_isBusy) StartBusyClock();
                else if (!value && _isBusy) StopBusyClock();
                SetProperty(ref _isBusy, value);
                NotifyPropertyChanged(() => HasBusyNotice);
                (PullCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (PreviewSyncCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (ApplySyncCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (PreviewAllCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (ApplyAllCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (SetAnchorFromMapCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (ClearAnchorCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (CreateArcGisLayerCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (SaveProfileCommand as SimpleCommand)?.RaiseCanExecuteChanged();
                (RestoreProfileCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            }
        }

        string _pullSummary = "Nothing pulled yet.";
        public string PullSummary
        {
            get => _pullSummary;
            private set => SetProperty(ref _pullSummary, value);
        }

        /// <summary>
        /// Reads features from the active map and creates the matching Rhino geometry.
        /// </summary>
        async Task PullAsync()
        {
            if (string.IsNullOrWhiteSpace(SelectedArcGisLayer))
            {
                PullSummary = "No layer pair selected — add one on the Link tab, then pull.";
                return;
            }

            IsBusy = true;
            try
            {
                // Through PullService, so every created object carries its gis.* sync identity and
                // baseline hashes. A pull that skips those looks like brand new Rhino work to the
                // next sync, which then tries to push the whole layer straight back to ArcGIS.
                var report = await SyncCoordinator.PullAsync(SelectedArcGisLayer, SelectedRhinoLayer,
                    selectedOnly: false, profileJson: ActiveLink?.ProfileJson,
                    expectedArcGisSource: ActiveLink?.ArcGisSource);
                if (ActiveLink != null) ActiveLink.LastResult = $"pulled {report.CountOf(RhinoArcGIS.Core.Reporting.SyncOutcome.Created)}";

                var created = report.CountOf(RhinoArcGIS.Core.Reporting.SyncOutcome.Created);
                var skipped = report.CountOf(RhinoArcGIS.Core.Reporting.SyncOutcome.Skipped);
                var failed = report.CountOf(RhinoArcGIS.Core.Reporting.SyncOutcome.Failed);

                var summary = $"Pulled {created} object(s) from '{SelectedArcGisLayer}'.";
                if (skipped > 0) summary += $" Skipped {skipped}.";
                if (failed > 0) summary += $" Failed {failed}.";

                foreach (var entry in report.Entries)
                {
                    if (entry.Outcome == RhinoArcGIS.Core.Reporting.SyncOutcome.Failed ||
                        entry.Outcome == RhinoArcGIS.Core.Reporting.SyncOutcome.Skipped)
                    {
                        summary += "\n" + entry.Message;
                        break;   // One example is enough; the sync list carries the detail.
                    }
                }

                PullSummary = summary;
                Refresh();
                RefreshRhinoLayers();
            }
            catch (Exception ex)
            {
                PullSummary = "Pull failed: " + ex.Message;
                System.Diagnostics.Debug.WriteLine($"Rhino.Inside pull failed: {ex}");
                ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(
                    $"Pulling features failed.\n\n{ex}", "Rhino.Inside");
            }
            finally
            {
                IsBusy = false;
            }
        }

        bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            private set => SetProperty(ref _isRunning, value);
        }

        string _statusText = "Not started";
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        string _statusColor = "#A0A0A0";
        /// <summary>Bound to the status indicator; grey idle, green running, red failed.</summary>
        public string StatusColor
        {
            get => _statusColor;
            private set => SetProperty(ref _statusColor, value);
        }

        string _rhinoVersion = "-";
        public string RhinoVersion
        {
            get => _rhinoVersion;
            private set => SetProperty(ref _rhinoVersion, value);
        }

        string _rhinoCommonPath = "-";
        public string RhinoCommonPath
        {
            get => _rhinoCommonPath;
            private set => SetProperty(ref _rhinoCommonPath, value);
        }

        string _documentName = "-";
        public string DocumentName
        {
            get => _documentName;
            private set => SetProperty(ref _documentName, value);
        }

        string _documentPath = "-";
        public string DocumentPath
        {
            get => _documentPath;
            private set => SetProperty(ref _documentPath, value);
        }

        string _objectCount = "-";
        public string ObjectCount
        {
            get => _objectCount;
            private set => SetProperty(ref _objectCount, value);
        }

        string _layerCount = "-";
        public string LayerCount
        {
            get => _layerCount;
            private set => SetProperty(ref _layerCount, value);
        }

        string _modelUnits = "-";
        public string ModelUnits
        {
            get => _modelUnits;
            private set => SetProperty(ref _modelUnits, value);
        }

        void Launch()
        {
            try
            {
                RhinoHost.Start();
            }
            catch (Exception ex)
            {
                IsRunning = false;
                StatusText = "Failed to start";
                StatusColor = "#D64550";
                System.Diagnostics.Debug.WriteLine($"Rhino.Inside launch failed: {ex}");
                ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(
                    $"Rhino.Inside failed to start.\n\n{ex}", "Rhino.Inside");
                return;
            }
            finally
            {
                (LaunchCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            }

            Refresh();
        }

        bool _linksLoaded;

        void Refresh()
        {
            _documentRefreshQueued = false;
            IsRunning = RhinoHost.IsStarted;
            (LaunchCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            (PullCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            (SetAnchorFromMapCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            (ClearAnchorCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            (ShowRhinoCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            (PreviewSyncCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            (ApplySyncCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            (CreateArcGisLayerCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            (SaveProfileCommand as SimpleCommand)?.RaiseCanExecuteChanged();
            (RestoreProfileCommand as SimpleCommand)?.RaiseCanExecuteChanged();

            if (!IsRunning)
            {
                StatusText = "Not started";
                StatusColor = "#A0A0A0";
                RhinoVersion = RhinoCommonPath = "-";
                DocumentName = DocumentPath = ObjectCount = LayerCount = ModelUnits = "-";
                AnchorStatus = "-";
                NewLayerPlanSummary = "Start Rhino to inspect a layer.";
                return;
            }

            // Populate the Rhino layer list as soon as Rhino is up. It used to fill only from the
            // layer-table event, so it stayed empty until the first pull happened to change it.
            if (RhinoLayers.Count == 0) RefreshRhinoLayers();
            if (!_linksLoaded) { LoadLinks(); _linksLoaded = true; }
            RefreshTrackedLayers();
            RefreshSelectedObject();

            var anchor = RhinoHost.GetEarthAnchor();
            AnchorStatus = anchor == null || !anchor.IsSet
                ? "Not set — the next pull will anchor at the centre of the data."
                : $"{anchor.Latitude:F6}, {anchor.Longitude:F6} at model ({anchor.ModelBaseX:N1}, {anchor.ModelBaseY:N1})";

            StatusText = "Rhino running";
            StatusColor = "#3FA45B";
            RhinoVersion = RhinoHost.RhinoVersion ?? "-";
            RhinoCommonPath = RhinoHost.LoadedRhinoCommon ?? "-";

            var doc = RhinoHost.GetActiveDocument();
            if (doc == null)
            {
                DocumentName = "(no active document)";
                DocumentPath = ObjectCount = LayerCount = ModelUnits = "-";
                return;
            }

            DocumentName = doc.IsModified ? doc.Name + " *" : doc.Name;
            DocumentPath = doc.Path;
            UpdateDocumentWarning(doc);
            ObjectCount = doc.ObjectCount.ToString();
            LayerCount = doc.LayerCount.ToString();
            ModelUnits = doc.ModelUnits;
            RefreshNewLayerPlan();
        }
    }

    internal sealed class NewLayerPushResult
    {
        internal NewLayerPushResult(NewLayerCreation creation, RhinoArcGIS.Core.Reporting.SyncReport report, string rhinoLayer)
        {
            Creation = creation;
            Report = report;
            RhinoLayer = rhinoLayer;
        }

        /// <summary>The Rhino layer that was published: the requested one, or its unlinked copy.</summary>
        internal string RhinoLayer { get; }

        internal NewLayerCreation Creation { get; }
        internal RhinoArcGIS.Core.Reporting.SyncReport Report { get; }
    }

    public sealed class SyncDirectionChoice
    {
        public SyncDirectionChoice(RhinoArcGIS.Core.Sync.SyncDirectionMode value, string label)
        {
            Value = value;
            Label = label;
        }

        public RhinoArcGIS.Core.Sync.SyncDirectionMode Value { get; }
        public string Label { get; }
    }

    /// <summary>
    /// Minimal ICommand so the dockpane does not depend on a particular framework command type.
    /// </summary>
    internal sealed class SimpleCommand : ICommand
    {
        readonly Action _execute;
        readonly Func<bool> _canExecute;

        internal SimpleCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute == null || _canExecute();

        public void Execute(object parameter) => _execute();

        /// <summary>
        /// Routed through WPF's CommandManager rather than a hand-raised event. Raising it manually
        /// from every property setter made buttons flicker between enabled and disabled, because
        /// each setter fired at a different point in a run; the CommandManager re-queries all
        /// commands together on its own cadence.
        /// </summary>
        public event EventHandler CanExecuteChanged
        {
            add { System.Windows.Input.CommandManager.RequerySuggested += value; }
            remove { System.Windows.Input.CommandManager.RequerySuggested -= value; }
        }

        internal void RaiseCanExecuteChanged() =>
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>A parameterised command, for the sub-tab buttons.</summary>
    internal sealed class SimpleCommand<T> : ICommand
    {
        readonly Action<T> _execute;
        internal SimpleCommand(Action<T> execute) { _execute = execute ?? throw new ArgumentNullException(nameof(execute)); }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => _execute(parameter is T t ? t : default);
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }

    /// <summary>One user-text pair of the selected Rhino object.</summary>
    public sealed class UserTextRow
    {
        public string Key { get; set; }
        public string Value { get; set; }
    }

    /// <summary>A Rhino layer holding synced objects, as shown on the Rhino tab.</summary>
    public sealed class TrackedLayerRow
    {
        public string Layer { get; set; }
        public string ArcGisLayer { get; set; }
        public int Tracked { get; set; }
        public int Parts { get; set; }
        public int Total { get; set; }

        public string Pair => string.Equals(Layer, ArcGisLayer, StringComparison.OrdinalIgnoreCase)
            ? Layer
            : $"{ArcGisLayer}  →  {Layer}";
    }

    /// <summary>
    /// Button implementation to show the DockPane.
    /// </summary>
    public class DockpaneView_ShowButton : Button
    {
        protected override void OnClick()
        {
            DockpaneViewModel.Show();
        }
    }
}
