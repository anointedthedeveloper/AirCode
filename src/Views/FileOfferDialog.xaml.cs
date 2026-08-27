using System.Windows;
using AirCode.Models;

namespace AirCode.Views;

public partial class FileOfferDialog : Window
{
    public FileOfferDialog(FileTransfer transfer)
    {
        InitializeComponent();
        SubtitleText.Text = $"{transfer.PeerName} wants to send you:";
        FileNameText.Text = transfer.FileName;
        FileSizeText.Text = FileTransfer.FormatSize(transfer.FileSize);
    }

    private void Accept_Click(object s, RoutedEventArgs e) => DialogResult = true;
    private void Decline_Click(object s, RoutedEventArgs e) => DialogResult = false;
}
