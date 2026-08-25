using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Nova;

// The maximized activity feed shared by all 3 skins - a scrollable,
// append-only, terminal-style log of tool-call steps (see
// NovaAssistant.RecordActivity/ActivityEntry and ActivityEntry's own doc
// comment). Same shared-plumbing/per-skin-look split as TranscriptPanel:
// only scroll/visibility/rebuild plumbing lives here, each skin's own row
// look comes from its own rowFactory. A genuinely separate component
// rather than reusing TranscriptPanel - different content shape (a
// growing list of short status lines vs. conversational You/Nova turns)
// and a different animation need: the single most recent entry's "..."
// has to keep cycling on its own, independent of whether Update() has
// anything new to rebuild.
internal sealed class ActivityLogPanel
{
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _list;
    private readonly double _fullHeight;
    private readonly DispatcherTimer _dotsTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    private Func<ActivityEntry, (Control Row, TextBlock? Dots)> _rowFactory;
    private TextBlock? _activeDots;
    private int _dotsFrame;
    private int _lastCount = -1;
    private bool _lastEntryWasInProgress;
    private bool _isVisible;

    public Control Root => _scroll;

    // width/height match whatever TranscriptPanel instance sits alongside
    // this one - see each skin's own construction site. Same reasoning as
    // TranscriptPanel's own width parameter: nothing here otherwise
    // constrains layout width for wrapping/measurement to work against.
    public ActivityLogPanel(Func<ActivityEntry, (Control Row, TextBlock? Dots)> rowFactory, double width, Color thumbColor, double height)
    {
        _rowFactory = rowFactory;
        _fullHeight = height;

        _list = new StackPanel { Margin = new Thickness(2, 4, 2, 4) };
        _scroll = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Width = width,
            Height = 0,
            IsVisible = false,
            ClipToBounds = true,
            // Same slide-open shape as TranscriptPanel, so the two panels
            // sitting side by side in the maximized split animate in sync
            // instead of one sliding open while the other just snaps.
            Transitions =
            [
                new DoubleTransition
                {
                    Property = Layoutable.HeightProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new QuadraticEaseOut(),
                },
            ],
        };
        TranscriptScrollBarStyle.Apply(_scroll, thumbColor);

        // One shared timer drives whichever row is currently the
        // in-progress one, rather than each row owning its own timer -
        // matches the DispatcherTimer-driven animation convention the
        // ArcFace/WebFace/AuraFace avatars already use elsewhere in this
        // project instead of Avalonia's declarative Animation/KeyFrame
        // system, and there's only ever one active row to animate at a
        // time regardless of how many entries exist.
        _dotsTimer.Tick += (_, _) =>
        {
            if (_activeDots is null)
            {
                return;
            }

            _dotsFrame = (_dotsFrame + 1) % 4;
            _activeDots.Text = new string('.', _dotsFrame);
        };
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            AnimateTo(value);
        }
    }

    private async void AnimateTo(bool open)
    {
        if (open)
        {
            _scroll.IsVisible = true;
            _scroll.Height = _fullHeight;
            _dotsTimer.Start();
        }
        else
        {
            _scroll.Height = 0;
            _dotsTimer.Stop();
            await Task.Delay(180);
            if (!_isVisible)
            {
                _scroll.IsVisible = false;
            }
        }
    }

    // ARC's scroll thumb tracks its gold/cyan theme toggle, same as
    // TranscriptPanel's own SetThumbColor.
    public void SetThumbColor(Color thumbColor) => TranscriptScrollBarStyle.Apply(_scroll, thumbColor);

    // Called when a skin's own row colors depend on its current theme
    // (AURA) - swaps the factory and forces the next Update to rebuild
    // every row, same as TranscriptPanel's own SetRowFactory.
    public void SetRowFactory(Func<ActivityEntry, (Control Row, TextBlock? Dots)> rowFactory)
    {
        _rowFactory = rowFactory;
        _lastCount = -1;
    }

    public void Update(IReadOnlyList<ActivityEntry> entries)
    {
        // Confirmed live as a real gap: NovaAssistant settles the last
        // entry's InProgress flag in place (RecordActivity's own next-entry
        // path, or FinishLastActivity when a task's activity-driven work
        // stops without a next entry ever starting) - neither changes the
        // *count* this was only ever diffing against, so that settling
        // never actually reached a rebuild here, and the dots this entry's
        // row keeps animating (see BuildActivityRow implementations - only
        // an InProgress entry gets a Dots block at all) never stopped, even
        // once the underlying data correctly showed the task was done.
        bool lastEntryInProgress = entries.Count > 0 && entries[^1].InProgress;
        if (!IsVisible || (entries.Count == _lastCount && lastEntryInProgress == _lastEntryWasInProgress))
        {
            return;
        }

        _lastCount = entries.Count;
        _lastEntryWasInProgress = lastEntryInProgress;
        _list.Children.Clear();
        _activeDots = null;
        _dotsFrame = 0;
        foreach (ActivityEntry entry in entries)
        {
            (Control row, TextBlock? dots) = _rowFactory(entry);
            _list.Children.Add(row);
            // The last entry with a non-null Dots block wins - only the
            // single most recent InProgress entry (see RecordActivity)
            // should ever be the one still animating.
            if (dots is not null)
            {
                _activeDots = dots;
            }
        }

        _scroll.Offset = new Vector(0, double.MaxValue);
    }
}
