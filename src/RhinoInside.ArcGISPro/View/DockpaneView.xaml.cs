﻿using System;
using System.Linq;
using System.Windows.Controls;


namespace RhinoInside.ArcGISPro
{
    /// <summary>
    /// Interaction logic for DockpaneView.xaml
    /// </summary>
    public partial class DockpaneView : UserControl
    {
        internal static DockpaneView Current { get; private set; }
        DockpaneViewModel _viewModel;

        public DockpaneView()
        {
            InitializeComponent();
            Current = this;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            foreach (var grid in new[] { LinkGrid, ProfileFieldGrid, ChangeReviewGrid, TrackedLayerGrid, SelectedObjectGrid })
                KeepStarColumnsSized(grid);
        }

        /// <summary>
        /// A DataGrid that is first measured while hidden -- every grid on an inactive sub-tab, or
        /// one collapsed until it has rows -- sizes its star columns against zero width and keeps
        /// them there once shown: the link table read "La Pr St". Re-applying the column widths
        /// whenever the grid becomes visible or gets real width makes it measure them again.
        /// </summary>
        static void KeepStarColumnsSized(System.Windows.Controls.DataGrid grid)
        {
            void Remeasure()
            {
                if (!grid.IsVisible || grid.ActualWidth < 1) return;
                grid.Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    foreach (var column in grid.Columns)
                    {
                        var width = column.Width;
                        column.Width = new System.Windows.Controls.DataGridLength(0);
                        column.Width = width;
                    }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }

            grid.IsVisibleChanged += (s, e) => { if ((bool)e.NewValue) Remeasure(); };
            grid.SizeChanged += (s, e) => { if (e.WidthChanged) Remeasure(); };
        }

        void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            // The ArcGIS content host can assign a second convention-created view model to the
            // UserControl. Always bind the visible view to the DockPaneManager instance used by
            // commands and document events, otherwise the header updates while grids stay empty.
            var pane = DockpaneViewModel.Instance;
            if (pane == null) return;
            if (!ReferenceEquals(DataContext, pane)) DataContext = pane;
            if (!ReferenceEquals(_viewModel, pane))
            {
                if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel = pane;
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }
            SubTabWorkspace.ClearValue(System.Windows.FrameworkElement.DataContextProperty);

            // ArcGIS detaches inactive TabItem content. Give each workspace a direct context when
            // it is attached and drive visibility from the same source as the sub-tab highlight.
            foreach (var area in new System.Windows.FrameworkElement[] { LinkArea, ProfileArea, PullArea, SyncArea })
                area.DataContext = pane;
            UpdateSubTabVisibility();

            // The three virtualized grids are created while their TabItems are detached. Give
            // them their live collections explicitly when the pane is attached; this avoids a
            // stale empty CollectionView in the ArcGIS content host.
            LinkGrid.ItemsSource = pane.Links;
            ProfileFieldGrid.ItemsSource = pane.ProfileFields;
            ChangeReviewGrid.ItemsSource = pane.SyncRows;
            ActiveArcGisLayerPicker.ItemsSource = pane.ArcGisLayers;
            ActiveDirectionPicker.ItemsSource = pane.DirectionChoices;
            ActiveRhinoLayerPicker.ItemsSource = pane.RhinoLayers;
            Bind(ActiveArcGisLayerPicker, ComboBox.TextProperty, pane,
                nameof(DockpaneViewModel.ActiveArcGisLayer), System.Windows.Data.BindingMode.TwoWay,
                System.Windows.Data.UpdateSourceTrigger.LostFocus);
            Bind(ActiveDirectionPicker, ComboBox.SelectedValueProperty, pane,
                nameof(DockpaneViewModel.ActiveDirection), System.Windows.Data.BindingMode.TwoWay,
                System.Windows.Data.UpdateSourceTrigger.PropertyChanged);
            Bind(ActiveRhinoLayerPicker, ComboBox.TextProperty, pane,
                nameof(DockpaneViewModel.ActiveRhinoLayer), System.Windows.Data.BindingMode.TwoWay,
                System.Windows.Data.UpdateSourceTrigger.LostFocus);
        }

        void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_viewModel != null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }

        void OnViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DockpaneViewModel.SubTab)) UpdateSubTabVisibility();
            if (e.PropertyName == nameof(DockpaneViewModel.SelectedProfileField))
            {
                ProfileFieldGrid.SelectedItem = _viewModel?.SelectedProfileField;
                SetProfileFieldDetail(_viewModel?.SelectedProfileField);
            }
        }

        void ProfileFieldGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_viewModel != null && ProfileFieldGrid.SelectedItem is ProfileFieldRow row)
            {
                _viewModel.SelectedProfileField = row;
                SetProfileFieldDetail(row);
            }
        }

        void SetProfileFieldDetail(ProfileFieldRow row)
        {
            ProfileFieldDetail.DataContext = row;
            Bind(ProfileFieldDetailText, TextBlock.TextProperty, row, nameof(ProfileFieldRow.Detail));
            Bind(ProfileRhinoKeyText, TextBox.TextProperty, row, nameof(ProfileFieldRow.RhinoKey),
                System.Windows.Data.BindingMode.TwoWay, System.Windows.Data.UpdateSourceTrigger.PropertyChanged);
            Bind(ProfileUnitsText, TextBox.TextProperty, row, nameof(ProfileFieldRow.Units),
                System.Windows.Data.BindingMode.TwoWay, System.Windows.Data.UpdateSourceTrigger.PropertyChanged);
            Bind(ProfileValidatorsText, TextBox.TextProperty, row, nameof(ProfileFieldRow.ValidatorsText),
                System.Windows.Data.BindingMode.TwoWay, System.Windows.Data.UpdateSourceTrigger.PropertyChanged);
            Bind(ProfileDomainText, TextBlock.TextProperty, row, nameof(ProfileFieldRow.DomainSummary));
        }

        static void Bind(System.Windows.FrameworkElement target, System.Windows.DependencyProperty property,
                         object source, string path,
                         System.Windows.Data.BindingMode mode = System.Windows.Data.BindingMode.OneWay,
                         System.Windows.Data.UpdateSourceTrigger update = System.Windows.Data.UpdateSourceTrigger.Default)
        {
            if (source == null)
            {
                target.ClearValue(property);
                return;
            }
            target.SetBinding(property, new System.Windows.Data.Binding(path)
            {
                Source = source,
                Mode = mode,
                UpdateSourceTrigger = update
            });
        }

        void UpdateSubTabVisibility()
        {
            if (_viewModel == null) return;
            LinkArea.Visibility = _viewModel.IsLinkSubTab ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            ProfileArea.Visibility = _viewModel.IsProfileSubTab ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            PullArea.Visibility = _viewModel.IsPullSubTab ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            SyncArea.Visibility = _viewModel.IsSyncSubTab ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }

        void MainTabs_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Tab ||
                System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None ||
                !ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, SyncTopTab))
                return;

            var target = _viewModel?.SubTab switch
            {
                "Profile" => ProfileSubTabButton,
                "Pull" => PullSubTabButton,
                "Sync" => SyncSubTabButton,
                _ => LinkSubTabButton
            };
            if (target.Focus()) e.Handled = true;
        }

        internal object DescribeKeyboardFocusForTest()
        {
            var focused = System.Windows.Input.Keyboard.FocusedElement as System.Windows.FrameworkElement;
            return new
            {
                type = focused?.GetType().Name,
                name = focused == null ? null : System.Windows.Automation.AutomationProperties.GetName(focused),
                elementName = focused?.Name,
                help = focused == null ? null : System.Windows.Automation.AutomationProperties.GetHelpText(focused),
                tooltip = focused == null ? null : System.Windows.Controls.ToolTipService.GetToolTip(focused)?.ToString(),
                enabled = focused?.IsEnabled,
                visible = focused?.IsVisible
            };
        }

        internal void SelectMainTabForTest(int index) => MainTabs.SelectedIndex = index;

        /// <summary>Actual rendered widths of every grid and its columns, as laid out in the pane.</summary>
        internal object DescribeGridsForTest() => new
        {
            paneWidth = ActualWidth,
            grids = new[] { LinkGrid, ProfileFieldGrid, ChangeReviewGrid, TrackedLayerGrid, SelectedObjectGrid }
                .Select(g => new
                {
                    name = g.Name,
                    visible = g.IsVisible,
                    width = Math.Round(g.ActualWidth, 1),
                    columns = g.Columns.Select(c => new
                    {
                        header = c.Header?.ToString(),
                        width = Math.Round(c.ActualWidth, 1),
                        declared = c.Width.ToString()
                    }).ToList(),
                    total = Math.Round(g.Columns.Sum(c => c.ActualWidth), 1)
                }).ToList()
        };

        internal object DescribeForTest() => new
        {
            rootContext = DataContext?.GetType().FullName,
            hostContext = SyncWorkspaceHost.DataContext?.GetType().FullName,
            subTabContext = SubTabWorkspace.DataContext?.GetType().FullName,
            linkGridItems = LinkGrid.Items.Count,
            linkGridVisible = LinkGrid.IsVisible,
            linkGridDataContext = LinkGrid.DataContext?.GetType().FullName,
            contextLinks = (DataContext as DockpaneViewModel)?.Links.Count,
            managerLinks = DockpaneViewModel.Instance?.Links.Count,
            managerContext = ReferenceEquals(DataContext, DockpaneViewModel.Instance)
        };

        /// <summary>
        /// Renders the live pane to a PNG at a given size, for visual review of real states.
        /// The pane is laid out at that size for the capture and restored afterwards.
        /// </summary>
        internal object CaptureForTest(string path, int width, int height, int mainTab)
        {
            var oldWidth = Width;
            var oldHeight = Height;
            var oldTab = MainTabs.SelectedIndex;
            try
            {
                if (mainTab >= 0) MainTabs.SelectedIndex = mainTab;
                Width = width;
                Height = height;
                Measure(new System.Windows.Size(width, height));
                Arrange(new System.Windows.Rect(0, 0, width, height));
                UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                // Paint the host background first: the pane itself is transparent.
                var backdrop = new System.Windows.Media.DrawingVisual();
                using (var dc = backdrop.RenderOpen())
                    dc.DrawRectangle(TryFindResource("Esri_DialogFrameBackgroundBrush") as System.Windows.Media.Brush
                                     ?? System.Windows.Media.Brushes.White, null,
                        new System.Windows.Rect(0, 0, width, height));
                bitmap.Render(backdrop);
                bitmap.Render(this);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                using (var stream = System.IO.File.Create(path)) encoder.Save(stream);
                return new { path, width, height };
            }
            finally
            {
                Width = oldWidth;
                Height = oldHeight;
                if (mainTab >= 0) MainTabs.SelectedIndex = oldTab;
                UpdateLayout();
            }
        }

        async void ChangeReviewGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (DataContext is DockpaneViewModel viewModel && ChangeReviewGrid.SelectedItem is SyncRow row)
                await viewModel.ShowRowAsync(row);
        }

        // ArcGIS Pro's dockpane host can consume a RadioButton command while changing focus.
        // Keep the normal command binding, but also route the actual click (including Space/Enter)
        // to the view model so the highlighted sub-tab and visible workspace cannot diverge.
        void SubTab_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is DockpaneViewModel viewModel &&
                sender is System.Windows.Controls.Primitives.ButtonBase button &&
                button.CommandParameter is string name)
                viewModel.OpenSubTab(name);
        }
    }
}
