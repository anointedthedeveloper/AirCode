using System.Windows.Controls;
using AirCode.ViewModels;

namespace AirCode.Views;

public partial class TransfersPage : UserControl
{
    public TransfersPage() => InitializeComponent();

    public void Initialize(MainViewModel vm)
        => TransferList.ItemsSource = vm.Transfers;
}
