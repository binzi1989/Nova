using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NovaDesktop.ViewModels;

namespace NovaDesktop.Controls;

public partial class ConversationStage : UserControl
{
    private INotifyCollectionChanged? _observedConversation;
    private ScrollViewer? _conversationScroller;
    private bool _scrollPending;
    private bool _followTail = true;
    private readonly DispatcherTimer _scrollAnimationTimer;
    private long _scrollAnimationStarted;
    private double _scrollAnimationFrom;
    private double _scrollAnimationTarget;
    private double _scrollAnimationDurationMilliseconds;

    public ConversationStage()
    {
        _scrollAnimationTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            ScrollAnimation_Tick,
            Dispatcher);
        _scrollAnimationTimer.Stop();
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _conversationScroller = FindVisualChild<ScrollViewer>(ConversationList);
            ObserveConversation();
            ScrollToLatest();
        };
        DataContextChanged += (_, _) => ObserveConversation();
        ConversationList.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(Conversation_ScrollChanged));
        ConversationList.PreviewMouseLeftButtonDown += (_, _) =>
            _scrollAnimationTimer.Stop();
        Unloaded += (_, _) =>
        {
            _scrollAnimationTimer.Stop();
            StopObservingConversation();
        };
    }

    private void ObserveConversation()
    {
        StopObservingConversation();
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        _observedConversation = viewModel.ConversationTurns;
        _observedConversation.CollectionChanged += Conversation_CollectionChanged;
    }

    private void StopObservingConversation()
    {
        if (_observedConversation is not null)
        {
            _observedConversation.CollectionChanged -= Conversation_CollectionChanged;
            _observedConversation = null;
        }
    }

    private void Conversation_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_followTail)
        {
            NewMessagesButton.Visibility = Visibility.Visible;
            return;
        }
        ScrollToLatest();
    }

    private void ScrollToLatest(bool force = false)
    {
        if ((!_followTail && !force) || _scrollPending)
        {
            return;
        }
        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollPending = false;
            if (!IsLoaded)
            {
                return;
            }
            if (ConversationList.Items.Count > 0)
            {
                var scroller = EnsureConversationScroller();
                if (scroller is not null)
                {
                    SmoothScrollTo(scroller.ScrollableHeight, 230);
                }
                NewMessagesButton.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void Conversation_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_conversationScroller is not null
            && !ReferenceEquals(e.OriginalSource, _conversationScroller))
        {
            return;
        }
        if (e.ExtentHeightChange != 0)
        {
            return;
        }
        _followTail = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 80;
        if (_followTail)
        {
            NewMessagesButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ConversationList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scroller = EnsureConversationScroller();
        if (scroller is null || scroller.ScrollableHeight <= 0)
        {
            return;
        }

        var distance = Math.Max(72, Math.Abs(e.Delta) * 0.9);
        var origin = _scrollAnimationTimer.IsEnabled
            ? _scrollAnimationTarget
            : scroller.VerticalOffset;
        var target = e.Delta > 0
            ? origin - distance
            : origin + distance;
        SmoothScrollTo(target, 170);
        e.Handled = true;
    }

    private void ConversationList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var scroller = EnsureConversationScroller();
        if (scroller is null)
        {
            return;
        }

        var handled = true;
        switch (e.Key)
        {
            case Key.PageUp:
                SmoothScrollTo(
                    scroller.VerticalOffset - Math.Max(120, scroller.ViewportHeight * .82),
                    210);
                break;
            case Key.PageDown:
                SmoothScrollTo(
                    scroller.VerticalOffset + Math.Max(120, scroller.ViewportHeight * .82),
                    210);
                break;
            case Key.Home:
                SmoothScrollTo(0, 240);
                break;
            case Key.End:
                SmoothScrollTo(scroller.ScrollableHeight, 240);
                break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
    }

    private void ScrollTop_Click(object sender, RoutedEventArgs e)
    {
        _followTail = false;
        SmoothScrollTo(0, 240);
    }

    private void ScrollLatest_Click(object sender, RoutedEventArgs e)
    {
        _followTail = true;
        ScrollToLatest(force: true);
    }

    private void NewMessages_Click(object sender, RoutedEventArgs e)
    {
        _followTail = true;
        ScrollToLatest(force: true);
    }

    private ScrollViewer? EnsureConversationScroller()
        => _conversationScroller ??= FindVisualChild<ScrollViewer>(ConversationList);

    private void SmoothScrollTo(double target, double durationMilliseconds)
    {
        var scroller = EnsureConversationScroller();
        if (scroller is null)
        {
            return;
        }

        _scrollAnimationFrom = scroller.VerticalOffset;
        _scrollAnimationTarget = Math.Clamp(target, 0, scroller.ScrollableHeight);
        _scrollAnimationDurationMilliseconds = Math.Max(80, durationMilliseconds);
        _scrollAnimationStarted = Stopwatch.GetTimestamp();
        if (Math.Abs(_scrollAnimationTarget - _scrollAnimationFrom) < .5)
        {
            scroller.ScrollToVerticalOffset(_scrollAnimationTarget);
            _scrollAnimationTimer.Stop();
            return;
        }
        _scrollAnimationTimer.Start();
    }

    private void ScrollAnimation_Tick(object? sender, EventArgs e)
    {
        var scroller = EnsureConversationScroller();
        if (scroller is null || !IsLoaded)
        {
            _scrollAnimationTimer.Stop();
            return;
        }

        var elapsedMilliseconds =
            Stopwatch.GetElapsedTime(_scrollAnimationStarted).TotalMilliseconds;
        var progress = Math.Clamp(
            elapsedMilliseconds / _scrollAnimationDurationMilliseconds,
            0,
            1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        scroller.ScrollToVerticalOffset(
            _scrollAnimationFrom
            + ((_scrollAnimationTarget - _scrollAnimationFrom) * eased));
        if (progress >= 1)
        {
            scroller.ScrollToVerticalOffset(_scrollAnimationTarget);
            _scrollAnimationTimer.Stop();
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }
            var nested = FindVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }
}
