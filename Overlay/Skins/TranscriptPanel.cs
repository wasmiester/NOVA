using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Nova;

// The "maximized" chat log shared by all 3 skins - a scrollable list of
// You/Nova turns. Only the scroll/visibility/rebuild plumbing is shared;
// each skin's row look (ARC's bordered line, WEB's speech bubble, AURA's
// frosted card) comes from its own rowFactory, since those are genuinely
// different layouts, not just a color swap. Rebuilds skip when the entry
// count is unchanged, so the ~130ms state tick doesn't churn while idle.
internal sealed class TranscriptPanel
{
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _list;
    private readonly double _fullHeight;
    private Func<TranscriptEntry, Control> _rowFactory;
    private int _lastCount = -1;
    private bool _isVisible;

    public Control Root => _scroll;

    // `width` is required (not just Height) because nothing in the tree has
    // a hard width constraint otherwise pushed down to it - without this,
    // TextWrapping/TextWrap has nothing to wrap against and every row
    // instead renders as one long line, ballooning the window sideways.
    public TranscriptPanel(Func<TranscriptEntry, Control> rowFactory, double width, Color thumbColor, double height = 220)
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
            Margin = new Thickness(0, 10, 0, 0),
            IsVisible = false,
            ClipToBounds = true,
            // A property Transition (Avalonia's declarative "animate
            // whenever this value changes" mechanism) instead of WPF's
            // imperative BeginAnimation calls - just setting Height below
            // triggers the slide automatically.
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

    // Opens/closes with a slide (Height 0 <-> full) instead of an instant
    // IsVisible flip - matches the "sliding effect" the mockup's own
    // transcript expand/collapse had.
    private async void AnimateTo(bool open)
    {
        if (open)
        {
            _scroll.IsVisible = true;
            _scroll.Height = _fullHeight;
        }
        else
        {
            _scroll.Height = 0;
            await System.Threading.Tasks.Task.Delay(180);
            if (!_isVisible)
            {
                _scroll.IsVisible = false;
            }
        }
    }

    // ARC's scroll thumb tracks its gold/cyan theme toggle - re-applies the
    // scrollbar style with the new color.
    public void SetThumbColor(Color thumbColor) => TranscriptScrollBarStyle.Apply(_scroll, thumbColor);

    // Called when a skin's own row colors depend on its current theme (AURA)
    // - swaps the factory and forces the next Update to rebuild every row.
    public void SetRowFactory(Func<TranscriptEntry, Control> rowFactory)
    {
        _rowFactory = rowFactory;
        _lastCount = -1;
    }

    public void Update(IReadOnlyList<TranscriptEntry> entries)
    {
        if (!IsVisible || entries.Count == _lastCount)
        {
            return;
        }

        _lastCount = entries.Count;
        _list.Children.Clear();
        foreach (TranscriptEntry entry in entries)
        {
            _list.Children.Add(_rowFactory(entry));
        }

        _scroll.Offset = new Vector(0, double.MaxValue);
    }
}
