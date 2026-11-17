using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace OpenHW.App.Power;

/// <summary>
/// Drag-and-drop reordering of the cards in a <see cref="WrapPanel"/> (Fan Control's fan and curve cards): press on a
/// card's background or a label and move more than a few pixels; the card dims, an orange bar shows where it will land,
/// and releasing moves it there. Presses on the card's own controls (name boxes, dropdowns, buttons) don't start a drag.
/// Done with plain pointer events, so it works the same on X11, XWayland and headless.
/// </summary>
public sealed class CardDrag(WrapPanel panel, Action<string, int> move)
{
    private const double Threshold = 6;
    private static readonly IBrush Mark = new SolidColorBrush(Color.Parse("#F0A03C"));
    private readonly Dictionary<Control, string> _ids = [];
    private Control? _card;
    private Point _start;
    private bool _dragging;
    private (Border Card, Thickness Thickness, IBrush? Brush)? _marked;

    /// <summary>Makes a card draggable (call again after every rebuild).</summary>
    public void Attach(Border card, string id)
    {
        _ids[card] = id;
        card.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed || OnOwnControl(e.Source)) return;
            _card = card;
            _start = e.GetPosition(panel);
            _dragging = false;
        };
        card.PointerMoved += (_, e) =>
        {
            if (_card != card) return;
            var p = e.GetPosition(panel);
            if (!_dragging)
            {
                if (Math.Abs(p.X - _start.X) < Threshold && Math.Abs(p.Y - _start.Y) < Threshold) return;
                _dragging = true;
                card.Opacity = 0.45;
                e.Pointer.Capture(card);
            }
            ShowMark(Slot(p));
            e.Handled = true;
        };
        card.PointerReleased += (_, e) =>
        {
            if (_card != card) return;
            var dragging = _dragging;
            var slot = Slot(e.GetPosition(panel));
            End(e.Pointer);
            if (!dragging) return;
            var cards = Cards();
            var from = cards.IndexOf(card);
            if (from < 0 || slot.Index < 0) return;
            var insert = slot.Index + (slot.After ? 1 : 0);
            var to = insert > from ? insert - 1 : insert;
            if (to != from) move(_ids[card], to);
            e.Handled = true;
        };
        card.PointerCaptureLost += (_, _) => { if (_card == card && _dragging) End(null); };
    }

    public void Clear()
    {
        _ids.Clear();
        _card = null;
        _dragging = false;
        _marked = null;
    }

    private List<Control> Cards() => panel.Children.Where(_ids.ContainsKey).ToList();

    /// <summary>The card under (or nearest to) the pointer and which side of it.</summary>
    private (int Index, bool After) Slot(Point p)
    {
        var cards = Cards();
        var best = (-1, false);
        var bestDistance = double.MaxValue;
        for (var i = 0; i < cards.Count; i++)
        {
            var b = cards[i].Bounds;
            var dx = p.X < b.Left ? b.Left - p.X : p.X > b.Right ? p.X - b.Right : 0;
            var dy = p.Y < b.Top ? b.Top - p.Y : p.Y > b.Bottom ? p.Y - b.Bottom : 0;
            var d = dx * dx + dy * dy * 4; // prefer the card on the same row
            if (d < bestDistance) (bestDistance, best) = (d, (i, p.X > b.Center.X));
        }
        return best;
    }

    private void ShowMark((int Index, bool After) slot)
    {
        Unmark();
        var cards = Cards();
        if (slot.Index < 0 || cards[slot.Index] is not Border target || target == _card) return;
        _marked = (target, target.BorderThickness, target.BorderBrush);
        target.BorderBrush = Mark;
        target.BorderThickness = slot.After ? new Thickness(1, 1, 4, 1) : new Thickness(4, 1, 1, 1);
    }

    private void Unmark()
    {
        if (_marked is { } m) { m.Card.BorderThickness = m.Thickness; m.Card.BorderBrush = m.Brush; }
        _marked = null;
    }

    private void End(IPointer? pointer)
    {
        Unmark();
        if (_card is { } card) card.Opacity = 1;
        pointer?.Capture(null);
        _card = null;
        _dragging = false;
    }

    /// <summary>A press that belongs to one of the card's controls (they keep their own clicks and typing).</summary>
    private static bool OnOwnControl(object? source)
    {
        for (var c = source as Visual; c is not null; c = c.GetVisualParent())
        {
            if (c is TextBox or ComboBox or Button or CheckBox or Avalonia.Controls.Primitives.ScrollBar or Canvas) return true;
            if (c is Border { Child: StackPanel } && c.GetVisualParent() is WrapPanel) return false; // reached the card
        }
        return false;
    }
}
