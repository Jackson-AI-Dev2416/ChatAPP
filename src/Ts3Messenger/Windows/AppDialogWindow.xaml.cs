using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ts3Messenger.Services;

namespace Ts3Messenger.Windows;

public partial class AppDialogWindow : Window
{
    private MessageBoxResult _result;
    private readonly MessageBoxResult _escapeResult;

    public AppDialogWindow(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ApplyIcon(image);

        switch (buttons)
        {
            case MessageBoxButton.YesNo:
                AddButton("No", MessageBoxResult.No, primary: false);
                AddButton("Yes", MessageBoxResult.Yes, primary: true, danger: image == MessageBoxImage.Warning);
                _escapeResult = MessageBoxResult.No;
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("Cancel", MessageBoxResult.Cancel, primary: false);
                AddButton("No", MessageBoxResult.No, primary: false);
                AddButton("Yes", MessageBoxResult.Yes, primary: true);
                _escapeResult = MessageBoxResult.Cancel;
                break;
            case MessageBoxButton.OKCancel:
                AddButton("Cancel", MessageBoxResult.Cancel, primary: false);
                AddButton("OK", MessageBoxResult.OK, primary: true);
                _escapeResult = MessageBoxResult.Cancel;
                break;
            default:
                AddButton("OK", MessageBoxResult.OK, primary: true);
                _escapeResult = MessageBoxResult.OK;
                break;
        }

        _result = _escapeResult;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _result = _escapeResult;
            Close();
        };
    }

    public MessageBoxResult Result => _result;

    private void ApplyIcon(MessageBoxImage image)
    {
        switch (image)
        {
            case MessageBoxImage.Error:
                IconGlyph.Text = "\uE783";
                IconGlyph.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                IconHost.SetResourceReference(Border.BackgroundProperty, "DangerSoftBrush");
                break;
            case MessageBoxImage.Warning:
                IconGlyph.Text = "\uE7BA";
                IconGlyph.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                IconHost.SetResourceReference(Border.BackgroundProperty, "DangerSoftBrush");
                break;
            case MessageBoxImage.Question:
                IconGlyph.Text = "\uE9CE";
                break;
            case MessageBoxImage.Information:
                IconGlyph.Text = "\uE946";
                break;
            default:
                IconHost.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void AddButton(string text, MessageBoxResult result, bool primary, bool danger = false)
    {
        var styleKey = primary ? danger ? "DangerSolidButtonStyle" : "PrimaryButtonStyle" : "SecondaryButtonStyle";
        var button = new Button
        {
            Content = text,
            MinWidth = 84,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = primary,
        };
        button.SetResourceReference(StyleProperty, styleKey);
        button.Click += (_, _) =>
        {
            _result = result;
            Close();
        };
        ButtonPanel.Children.Add(button);
        if (primary)
            Loaded += (_, _) => button.Focus();
    }

    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }
}

/// <summary>Drop-in replacement for <see cref="MessageBox.Show(string)"/> that follows the app theme.</summary>
public static class AppDialog
{
    public static MessageBoxResult Show(
        string message,
        string title = "TS3 Messenger",
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None)
    {
        var app = Application.Current;
        if (app == null)
            return MessageBox.Show(message, title, buttons, image);
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Show(message, title, buttons, image));

        var dialog = new AppDialogWindow(message, title, buttons, image);
        var owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w != dialog)
                    ?? app.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w != dialog);
        if (owner != null)
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog.Result;
    }
}
