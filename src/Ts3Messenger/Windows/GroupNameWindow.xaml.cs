using System.Windows;
using System.Windows.Controls;

namespace Ts3Messenger.Windows;

public partial class GroupNameWindow : Window
{
    public string GroupName { get; private set; } = "";

    public GroupNameWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e) =>
        NextButton.IsEnabled = NameBox.Text.Trim().Length > 0;

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        GroupName = NameBox.Text.Trim();
        if (GroupName.Length == 0)
            return;
        DialogResult = true;
        Close();
    }
}
