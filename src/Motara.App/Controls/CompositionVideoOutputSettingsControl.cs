using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Layout;
using Motara.App.Localization;
using Motara.App.ViewModels;

namespace Motara.App.Controls;

internal sealed class CompositionVideoOutputSettingsControl : UserControl
{
    private CompositionVideoOutputSettingsViewModel? viewModel;
    private LocalizationManager? localization;
    private readonly TextBox name = new();
    private readonly TextBox width = new();
    private readonly TextBox height = new();
    private readonly TextBox fps = new();
    private readonly TextBlock validation = new();

    internal Control InitialFocus => name;

    internal void Attach(CompositionVideoOutputSettingsViewModel value, LocalizationManager manager)
    {
        viewModel = value;
        localization = manager;
        value.PropertyChanged += OnChanged;
        name.TextChanged += (_, _) => { if (viewModel is not null) viewModel.Name = name.Text ?? ""; };
        width.TextChanged += (_, _) => { if (viewModel is not null) viewModel.Width = width.Text ?? ""; };
        height.TextChanged += (_, _) => { if (viewModel is not null) viewModel.Height = height.Text ?? ""; };
        fps.TextChanged += (_, _) => { if (viewModel is not null) viewModel.FramesPerSecond = fps.Text ?? ""; };
        var apply = new Button { Content = manager.GetString("Command.Confirm") };
        var cancel = new Button { Content = manager.GetString("Command.Cancel") };
        apply.Classes.Add("workspace-action");
        cancel.Classes.Add("workspace-action");
        apply.Click += async (_, _) => await value.ApplyAsync(CancellationToken.None);
        cancel.Click += (_, _) => value.Cancel();
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"),
            RowSpacing = 12,
            Children =
            {
                Field(manager.GetString("Workspace.VideoOutput.Name"), name, 0),
                Field(manager.GetString("Workspace.VideoOutput.Width"), width, 1),
                Field(manager.GetString("Workspace.VideoOutput.Height"), height, 2),
                Field(manager.GetString("Workspace.VideoOutput.Fps"), fps, 3),
                validation,
                new StackPanel { [Grid.RowProperty] = 5, Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Children = { cancel, apply } },
            },
        };
        Grid.SetRow(validation, 4);
        Refresh();
    }

    internal void Detach()
    {
        if (viewModel is not null) viewModel.PropertyChanged -= OnChanged;
        viewModel = null;
        localization = null;
    }

    private static Grid Field(string label, Control input, int row)
    {
        Grid.SetColumn(input, 1);
        return new Grid
        {
            [Grid.RowProperty] = row,
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 12,
            Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }, input },
        };
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs args) => Refresh();

    private void Refresh()
    {
        if (viewModel is null) return;
        name.Text = viewModel.Name;
        width.Text = viewModel.Width;
        height.Text = viewModel.Height;
        fps.Text = viewModel.FramesPerSecond;
        validation.Text = viewModel.Validation is { } key && localization is not null ? localization.GetString(key) : "";
    }
}
