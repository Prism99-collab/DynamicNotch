using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Every box you can type in on the notch behaves like a normal text box:
///  • a click in it gives it the keyboard at once (the notch doesn't take focus by itself, so the keys used to go to
///    the app behind, or to the search); the caret shows and blinks;
///  • a right-click opens Cut · Copy · Paste · Select all, and Ctrl+X / C / V / A work as anywhere.
/// </summary>
public partial class NotchWindow
{
    void WireTextBoxes()
    {
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (FindParent<TextBox>(e.OriginalSource as DependencyObject) is not { } box || ReferenceEquals(box, _launchBox)) return;
            if (!IsActive) BeginWorkTyping(box); // the keyboard comes here (and goes back after)
        }), handledEventsToo: true);

        AddHandler(PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (FindParent<TextBoxBase>(e.OriginalSource as DependencyObject) is not { } box) return;
            e.Handled = true;
            if (box is TextBox tb && !ReferenceEquals(tb, _launchBox) && !IsActive) BeginWorkTyping(tb);
            box.Focus();
            EditMenu(box).IsOpen = true;
        }), handledEventsToo: true);
    }

    /// <summary>Cut · Copy · Paste · Select all, in the notch's dark style (each greyed out when it can't apply).</summary>
    ContextMenu EditMenu(TextBoxBase box)
    {
        var bg = Frozen(Color.FromRgb(0x1E, 0x1E, 0x22));
        var menu = new ContextMenu
        {
            PlacementTarget = box, Placement = PlacementMode.MousePoint, Background = bg, Foreground = Brushes.White,
            BorderBrush = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), Padding = new Thickness(2),
            FontSize = 12, HasDropShadow = true,
        };
        void Item(string name, ICommand cmd, string keys)
        {
            var item = new MenuItem
            {
                Header = Loc.T(name), Command = cmd, CommandTarget = box, InputGestureText = keys,
                Foreground = Brushes.White, Background = bg, Padding = new Thickness(6, 3, 6, 3),
            };
            menu.Items.Add(item);
        }
        Item("Cut", ApplicationCommands.Cut, "Ctrl+X");
        Item("Copy", ApplicationCommands.Copy, "Ctrl+C");
        Item("Paste", ApplicationCommands.Paste, "Ctrl+V");
        menu.Items.Add(new Separator { Background = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)) });
        Item("Select all", ApplicationCommands.SelectAll, "Ctrl+A");
        return menu;
    }
}
