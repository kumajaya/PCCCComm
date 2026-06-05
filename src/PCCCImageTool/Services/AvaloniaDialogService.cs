using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PCCCImageTool.Models;

namespace PCCCImageTool.Services;

public class AvaloniaDialogService : IDialogService
{
    private static Window? GetMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow as Window;
        return null;
    }

    private static TopLevel? GetTopLevel() => GetMainWindow();

    private static Window BuildDialog(string title, string message, Control actionControl) =>
        new()
        {
            Title = title,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20, 16),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextAlignment = TextAlignment.Center
                    },
                    actionControl
                }
            }
        };

    public async Task ShowMessageAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource();
        var okButton = new Button { Content = "OK", Width = 80, HorizontalAlignment = HorizontalAlignment.Center };
        var dialog = BuildDialog(title, message, okButton);
        okButton.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => tcs.TrySetResult();
        var owner = GetMainWindow();
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
        await tcs.Task;
    }

    public async Task<bool> ShowConfirmAsync(string title, string message)
    {
        bool result = false;
        var yesButton = new Button { Content = "Yes", Width = 70 };
        var noButton = new Button { Content = "No", Width = 70 };
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 16,
            Children = { yesButton, noButton }
        };
        var dialog = BuildDialog(title, message, buttonRow);
        yesButton.Click += (_, _) => { result = true; dialog.Close(); };
        noButton.Click += (_, _) => { result = false; dialog.Close(); };
        var owner = GetMainWindow();
        if (owner != null)
            await dialog.ShowDialog(owner);
        else
            dialog.Show();
        return result;
    }

    public async Task<string?> OpenFilePickerAsync(string title)
    {
        var topLevel = GetTopLevel();
        if (topLevel == null) return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Binary file") { Patterns = new[] { "*.bin" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
                }
            });
        return files?.FirstOrDefault()?.Path.LocalPath;
    }

    public async Task<string?> SaveFilePickerAsync(string title, string suggestedFileName)
    {
        var topLevel = GetTopLevel();
        if (topLevel == null) return null;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedFileName,
                DefaultExtension = "bin",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Binary file") { Patterns = new[] { "*.bin" } },
                    new FilePickerFileType("All files") { Patterns = new[] { "*.*" } }
                }
            });
        return file?.Path.LocalPath;
    }

    public async Task ShowCompareResultsAsync<T>(List<T> results) where T : StructureCompareResult
    {
        bool allMatch = results.Count > 0 && results.All(r =>
            r.StructureMatch &&
            (r is not FullCompareResult fc || fc.DataMatches));

        if (allMatch)
        {
            await ShowMessageAsync("Compare Result", $"All {results.Count} file(s) match — no differences found.");
            return;
        }

        int mismatchCount = results.Count(r =>
            !r.StructureMatch || (r is FullCompareResult fc && !fc.DataMatches));

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = new Window
            {
                Title = (results.Any(x => x is FullCompareResult) ? "Full Compare Results" : "Structure Compare Results") +
                        $" — {mismatchCount} mismatch(es)",
                Width = 800,
                Height = 600,
                SizeToContent = SizeToContent.Manual,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var dataGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserResizeColumns = true,
                Margin = new Thickness(10, 10, 10, 0),
                ItemsSource = results,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            dataGrid.Columns.Add(new DataGridTextColumn { Header = "File", Binding = new Binding("FileTypeName"), Width = new DataGridLength(100) });
            dataGrid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding("FileNumber"), Width = new DataGridLength(50) });
            dataGrid.Columns.Add(new DataGridTextColumn { Header = "Backup (bytes)", Binding = new Binding("SizeDisplay"), Width = new DataGridLength(120) });
            dataGrid.Columns.Add(new DataGridTextColumn { Header = "PLC (bytes)", Binding = new Binding("PlcSizeDisplay"), Width = new DataGridLength(120) });
            dataGrid.Columns.Add(new DataGridTextColumn { Header = "Structure", Binding = new Binding("StructureStatus"), Width = new DataGridLength(100) });
            dataGrid.Columns.Add(new DataGridTextColumn { Header = "Data", Binding = new Binding("DataStatus"), Width = new DataGridLength(250) });

            dataGrid.LoadingRow += (_, e) =>
            {
                if (e.Row.DataContext is StructureCompareResult r)
                    e.Row.Background = r.StructureMatch ? new SolidColorBrush(Colors.LightGreen) : new SolidColorBrush(Colors.LightPink);
            };

            var closeButton = new Button { Content = "Close", Width = 80, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(10) };
            closeButton.Click += (_, _) => window.Close();

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(dataGrid, 0);
            Grid.SetRow(closeButton, 1);
            grid.Children.Add(dataGrid);
            grid.Children.Add(closeButton);

            window.Content = grid;
            await window.ShowDialog(GetMainWindow()!);
        });
    }
}
