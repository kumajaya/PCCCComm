using System;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using PCCCImageTool.ViewModels;

namespace PCCCImageTool.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    public MainWindow()
    {
        InitializeComponent();

        // ReactiveWindow sets DataContext = ViewModel before the window is shown,
        // so we can wire up the auto-scroll here directly.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                var logBox = this.FindControl<TextBox>("LogTextBox");
                if (logBox == null) return;

                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainWindowViewModel.LogText))
                    {
                        logBox.CaretIndex = logBox.Text?.Length ?? 0;
                        logBox.BringIntoView();
                    }
                        
                };
            }
        };
    }
}
