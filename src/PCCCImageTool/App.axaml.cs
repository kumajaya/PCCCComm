using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PCCCImageTool.Services;
using PCCCImageTool.ViewModels;
using PCCCImageTool.Views;

namespace PCCCImageTool;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dialogService = new AvaloniaDialogService();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(dialogService)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
