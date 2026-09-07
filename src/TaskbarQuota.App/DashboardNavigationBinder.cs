using System;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaskbarQuota.Usage;
using TaskbarQuota.ViewModels;

namespace TaskbarQuota
{
    internal sealed class DashboardNavigationBinder : IDisposable
    {
        private readonly NavigationView _nav;
        private readonly NavigationViewItem _providerGroup;
        private readonly DashboardViewModel _viewModel;
        private readonly DispatcherQueueTimer _rebuildTimer;
        private bool _rebuildPending;
        private bool _disposed;
        private bool _providerPageActive;
        private ProviderId? _requestedProviderId;

        public bool IsSyncing { get; private set; }

        public DashboardNavigationBinder(
            NavigationView nav,
            NavigationViewItem providerGroup,
            DashboardViewModel viewModel)
        {
            _nav = nav;
            _providerGroup = providerGroup;
            _viewModel = viewModel;
            _rebuildTimer = nav.DispatcherQueue.CreateTimer();
            _rebuildTimer.Interval = TimeSpan.FromMilliseconds(50);
            _rebuildTimer.Tick += (_, _) =>
            {
                _rebuildTimer.Stop();
                _rebuildPending = false;
                Rebuild();
            };
            _viewModel.Cards.CollectionChanged += Cards_CollectionChanged;
            _viewModel.AvailableCards.CollectionChanged += Cards_CollectionChanged;
            _viewModel.SelectedCardChanged += ViewModel_SelectedCardChanged;
            WidgetSettingsService.Changed += OnWidgetSettingsChanged;
            Rebuild();
        }

        /// <summary>
        /// Detaches from the view model and from the static settings event.
        ///
        /// The window that owns this binder is destroyed and rebuilt every time the user closes and
        /// reopens it from the tray. <see cref="WidgetSettingsService.Changed"/> is static, so a binder
        /// that never unsubscribes is rooted for the life of the process — along with its NavigationView
        /// and everything reachable from it — and each reopen adds another one that then refreshes badges
        /// on a dead window's dispatcher.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            WidgetSettingsService.Changed -= OnWidgetSettingsChanged;
            _viewModel.Cards.CollectionChanged -= Cards_CollectionChanged;
            _viewModel.AvailableCards.CollectionChanged -= Cards_CollectionChanged;
            _viewModel.SelectedCardChanged -= ViewModel_SelectedCardChanged;
            _rebuildTimer.Stop();
        }

        /// <summary>
        /// Re-applies the selected item and its icon brush. Call once the NavigationView is loaded —
        /// selection set during construction (before the control realizes its containers) doesn't
        /// paint the active item's icon on first show.
        /// </summary>
        public void ReapplySelection()
        {
            IsSyncing = true;
            SyncSelection();
            IsSyncing = false;
        }

        public void SetProviderPageActive(bool active)
        {
            _providerPageActive = active;
            if (!active)
                _requestedProviderId = null;
            IsSyncing = true;
            SyncSelection();
            IsSyncing = false;
        }

        public bool SelectFromNavigation(NavigationViewSelectionChangedEventArgs args)
        {
            if (IsSyncing)
                return true;

            if (args.SelectedItemContainer is not NavigationViewItem { Tag: ProviderId id })
                return false;

            if (WidgetSettingsService.IsProviderUserHidden(id))
            {
                // Hidden-group entries are restored via right-click, never opened.
                // Bounce selection back to the currently shown card.
                ReselectCurrentCard();
                return true;
            }

            _requestedProviderId = id;
            bool providerIsKnown = _viewModel.Cards.Any(card => card.ProviderId == id)
                || _viewModel.AvailableCards.Any(card => card.ProviderId == id);
            _viewModel.SelectProvider(id);
            if (!providerIsKnown)
                _viewModel.EnableAvailableProvider(id);
            return true;
        }

        // Pins can change from the dashboard card, from Settings, or from the budget auto-unpinning one.
        private void OnWidgetSettingsChanged(object? sender, EventArgs e)
        {
            if (_disposed)
                return;

            _nav.DispatcherQueue.TryEnqueue(RefreshPinBadges);
        }

        private void Cards_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Quota refreshes replace card VMs in place; rebuilding nav recreates PathIcons and crashes WinUI.
            if (e.Action == NotifyCollectionChangedAction.Replace)
                return;

            ScheduleRebuild();
        }

        private void ScheduleRebuild()
        {
            if (_rebuildPending)
                return;

            _rebuildPending = true;
            _rebuildTimer.Stop();
            _rebuildTimer.Start();
        }

        private void Rebuild()
        {
            IsSyncing = true;
            _providerGroup.MenuItems.Clear();
            foreach (var card in _viewModel.Cards)
            {
                var item = new NavigationViewItem
                {
                    Content = card.DisplayName,
                    Tag = card.ProviderId,
                    Icon = CreateProviderIcon(card.ProviderId),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ContextFlyout = BuildHideFlyout(card.ProviderId),
                };
                ApplyPinBadge(item, card.ProviderId, card.DisplayName);
                _providerGroup.MenuItems.Add(item);
            }

            AddHiddenGroup();

            if (_requestedProviderId is ProviderId requested
                && !_viewModel.Cards.Any(card => card.ProviderId == requested))
            {
                _requestedProviderId = null;
            }

            SyncSelection();
            IsSyncing = false;
        }

        private void ViewModel_SelectedCardChanged(ProviderCardViewModel? card)
        {
            if (card is null || IsSyncing)
                return;

            IsSyncing = true;
            SyncSelection();
            IsSyncing = false;
        }

        private void SyncSelection()
        {
            var selected = _requestedProviderId ?? _viewModel.SelectedCard?.ProviderId;
            foreach (var item in _providerGroup.MenuItems)
            {
                if (item is not NavigationViewItem navItem)
                    continue;

                bool isSelected = _providerPageActive
                    && selected is ProviderId id
                    && navItem.Tag is ProviderId tag
                    && id == tag;

                if (isSelected)
                    _nav.SelectedItem = navItem;

                SetActiveVisual(navItem, isSelected);
            }
        }

        /// <summary>
        /// Collapsible group with providers the user hid from this list.
        /// Hidden when empty. Right-click a child to show it back.
        /// </summary>
        private void AddHiddenGroup()
        {
            var hidden = WidgetSettingsService.UserHiddenDashboardProviderIds();
            if (hidden.Count == 0)
                return;

            var group = new NavigationViewItem
            {
                Content = $"Hidden ({hidden.Count})",
                IsExpanded = false,
                SelectsOnInvoked = false,
            };
            ToolTipService.SetToolTip(group, "Providers you hid from this list. Right-click one to show it back.");
            var service = UsageCoordinator.Instance.Service;
            foreach (var id in hidden)
            {
                var child = new NavigationViewItem
                {
                    Content = service.Get(id)?.DisplayName ?? id.ToString(),
                    Tag = id,
                    Icon = CreateProviderIcon(id),
                    ContextFlyout = BuildUnhideFlyout(id),
                };
                ToolTipService.SetToolTip(child, "Right-click to show back in the list");
                group.MenuItems.Add(child);
            }
            _providerGroup.MenuItems.Add(group);
        }

        private static MenuFlyout BuildHideFlyout(ProviderId id)
        {
            var hide = new MenuFlyoutItem { Text = "Hide from sidebar" };
            hide.Click += (_, _) => WidgetSettingsService.SetProviderUserHidden(id, true);
            return new MenuFlyout { Items = { hide } };
        }

        private static MenuFlyout BuildUnhideFlyout(ProviderId id)
        {
            var show = new MenuFlyoutItem { Text = "Show in sidebar" };
            show.Click += (_, _) => WidgetSettingsService.SetProviderUserHidden(id, false);
            return new MenuFlyout { Items = { show } };
        }

        private void ReselectCurrentCard()
        {
            var current = _requestedProviderId ?? _viewModel.SelectedCard?.ProviderId;
            IsSyncing = true;
            foreach (var item in _providerGroup.MenuItems)
            {
                if (item is NavigationViewItem navItem
                    && navItem.Tag is ProviderId tag
                    && current is ProviderId currentId
                    && tag == currentId)
                {
                    _nav.SelectedItem = navItem;
                    break;
                }
            }
            IsSyncing = false;
        }

        /// <summary>
        /// Marks the providers that are pinned to the taskbar. In icon-only mode the strip is nothing but
        /// glyphs, so without a marker there is no way to tell which of them are pinned short of opening
        /// each one.
        /// </summary>
        private static void ApplyPinBadge(NavigationViewItem item, ProviderId id, string displayName)
        {
            bool pinned = WidgetSettingsService.IsProviderPinned(id);
            item.InfoBadge = pinned
                ? new InfoBadge
                {
                    IconSource = new FontIconSource { Glyph = PinGlyph, FontSize = 10 },
                    Style = (Style)Application.Current.Resources["AttentionIconInfoBadgeStyle"],
                }
                : null;

            ToolTipService.SetToolTip(item, pinned ? $"{displayName} — pinned in the usage widget" : displayName);
        }

        /// <summary>
        /// Refreshes the pin markers in place. A full rebuild would recreate every PathIcon, which is what
        /// the Replace guard above exists to avoid.
        /// </summary>
        private void RefreshPinBadges()
        {
            foreach (var menuItem in _providerGroup.MenuItems)
            {
                if (menuItem is not NavigationViewItem { Tag: ProviderId id } item)
                    continue;

                var card = _viewModel.Cards.Concat(_viewModel.AvailableCards)
                    .FirstOrDefault(candidate => candidate.ProviderId == id);
                var provider = UsageCoordinator.Instance.Service.Get(id);
                ApplyPinBadge(item, id, card?.DisplayName ?? provider?.DisplayName ?? id.ToString());
            }
        }

        // Segoe Fluent "Pin" glyph.
        private const string PinGlyph = "";

        private static IconElement CreateProviderIcon(ProviderId id)
        {
            var brush = GetSelectionBrush(isSelected: false);
            if (ProviderGlyphs.Data.TryGetValue(id, out var pathData)
                && Ui.ParseFreshGeometry(pathData) is { } geometry)
            {
                return new PathIcon
                {
                    Data = geometry,
                    Foreground = brush,
                };
            }

            return new FontIcon { Glyph = "\uE8A5", FontSize = 16, Foreground = brush };
        }

        private static void SetActiveVisual(NavigationViewItem item, bool isSelected)
        {
            var brush = GetSelectionBrush(isSelected);
            item.Foreground = brush;
            ApplyIconBrush(item.Icon, isSelected);
        }

        private static void ApplyIconBrush(IconElement? icon, bool isSelected)
        {
            if (icon is null)
                return;

            var brush = GetSelectionBrush(isSelected);
            if (icon is FontIcon fontIcon)
                fontIcon.Foreground = brush;
            else if (icon is PathIcon pathIcon)
                pathIcon.Foreground = brush;
        }

        private static Brush GetSelectionBrush(bool isSelected) => isSelected
            ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }
}
