using System.Windows;
using Ts3Messenger.Services;

namespace Ts3Messenger.Windows;

public partial class FileSendConfirmWindow : Window
{
    public string? Caption { get; private set; }

    public FileSendConfirmWindow(OutgoingBundle bundle)
    {
        InitializeComponent();
        var count = bundle.Items.Count;
        Title = bundle.IsFolder ? "Send folder" : count > 1 ? "Send files" : "Send file";
        FileIconText.Text = bundle.IsFolder ? "📁" : "📄";
        FileNameText.Text = FileTransferLabels.TitleText(bundle.DisplayName, count, bundle.IsFolder);
        FileNameText.ToolTip = string.Join("\n", bundle.Items.Take(20).Select(i => i.RelativePath))
                               + (count > 20 ? $"\nand {count - 20} more" : "");
        FileSizeText.Text = FileSizeFormatter.Format(bundle.TotalSize);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSendClick(object sender, RoutedEventArgs e)
    {
        Caption = CaptionBox.Text.Trim();
        if (Caption.Length == 0)
            Caption = null;
        DialogResult = true;
        Close();
    }
}
