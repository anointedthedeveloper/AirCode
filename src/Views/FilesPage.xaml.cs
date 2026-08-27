using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AirCode.Models;
using AirCode.ViewModels;

namespace AirCode.Views;

public class FileItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string SizeText { get; set; } = "";
}

public partial class FilesPage : UserControl
{
    private MainViewModel? _vm;
    private readonly ObservableCollection<FileItem> _selectedFiles = new();

    public FilesPage() => InitializeComponent();

    public void Initialize(MainViewModel vm)
    {
        _vm = vm;
        SelectedFilesList.ItemsSource = _selectedFiles;
        ActiveTransferList.ItemsSource = vm.Transfers;
        vm.Members.CollectionChanged += (s, e) => Dispatcher.InvokeAsync(RebuildRecipients);
        RebuildRecipients();
    }

    private void RebuildRecipients()
    {
        if (_vm == null) return;
        RecipientCombo.Items.Clear();
        foreach (var m in _vm.Members.Where(x => x.Id != _vm.MyId))
            RecipientCombo.Items.Add(m);
        if (RecipientCombo.Items.Count > 0) RecipientCombo.SelectedIndex = 0;
    }

    private void ChooseFiles_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
        if (dlg.ShowDialog() == true)
            foreach (var f in dlg.FileNames) AddFile(f);
    }

    private void DropZone_DragOver(object s, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files) AddFile(f);
    }

    private void AddFile(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return;
        if (_selectedFiles.Any(x => x.Path == path)) return;
        _selectedFiles.Add(new FileItem
        {
            Name = fi.Name,
            Path = path,
            SizeText = FileTransfer.FormatSize(fi.Length)
        });
    }

    private void RemoveFile_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is FileItem fi)
            _selectedFiles.Remove(fi);
    }

    private async void Send_Click(object s, RoutedEventArgs e)
    {
        if (_vm == null || RecipientCombo.SelectedItem is not Member target) return;
        if (_selectedFiles.Count == 0) { MessageBox.Show("No files selected."); return; }

        foreach (var fi in _selectedFiles.ToList())
            await _vm.OfferFileAsync(target, fi.Path);

        _selectedFiles.Clear();
    }
}
