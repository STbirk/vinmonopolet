using System.Windows;
using System.Windows.Controls;
using Vinliste.App.ViewModels;

namespace Vinliste.App.Views;

public partial class WineDetailView : UserControl
{
    public WineDetailView() => InitializeComponent();

    private void OnNoteLostFocus(object sender, RoutedEventArgs e) =>
        (DataContext as WineDetailViewModel)?.SaveNoteCommand.Execute(null);
}
