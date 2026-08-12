using System.ComponentModel;
using System.Windows.Input;

namespace Motara.App.ViewModels;

internal sealed class SceneNamePromptViewModel : INotifyPropertyChanged
{
    private readonly Func<string, CancellationToken, Task<bool>> submit;
    private readonly Action close;
    private string name;

    internal SceneNamePromptViewModel(
        string initialName,
        bool isRename,
        Func<string, CancellationToken, Task<bool>> submit,
        Action close)
    {
        name = initialName;
        IsRename = isRename;
        this.submit = submit;
        this.close = close;
        SubmitCommand = new PromptAsyncCommand(SubmitAsync);
        CancelCommand = new DelegateCommand(_ => close());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool IsRename { get; }

    internal string Name
    {
        get => name;
        set
        {
            if (StringComparer.Ordinal.Equals(name, value))
            {
                return;
            }

            name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    internal IAsyncCommand SubmitCommand { get; }

    internal ICommand CancelCommand { get; }

    private async Task SubmitAsync(CancellationToken cancellationToken) =>
        _ = await submit(Name, cancellationToken);

    private sealed class DelegateCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute(parameter);
    }

    private sealed class PromptAsyncCommand(
        Func<CancellationToken, Task> execute) : IAsyncCommand
    {
        private int executing;

        public event EventHandler? CanExecuteChanged;

        public bool IsExecuting => Volatile.Read(ref executing) != 0;

        public bool CanExecute(object? parameter) => !IsExecuting;

        public void Execute(object? parameter) => _ = ExecuteAsync(parameter, CancellationToken.None);

        public async Task ExecuteAsync(object? parameter, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref executing, 1, 0) != 0)
            {
                return;
            }

            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                await execute(cancellationToken);
            }
            finally
            {
                Volatile.Write(ref executing, 0);
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

internal sealed class SceneDeleteConfirmationViewModel
{
    internal SceneDeleteConfirmationViewModel(
        string sceneName,
        Func<CancellationToken, Task<bool>> confirm,
        Action close)
    {
        SceneName = sceneName;
        ConfirmCommand = new ConfirmCommandImplementation(confirm);
        CancelCommand = new CancelCommandImplementation(close);
    }

    internal string SceneName { get; }

    internal IAsyncCommand ConfirmCommand { get; }

    internal ICommand CancelCommand { get; }

    private sealed class ConfirmCommandImplementation(
        Func<CancellationToken, Task<bool>> confirm) : IAsyncCommand
    {
        private int executing;

        public event EventHandler? CanExecuteChanged;

        public bool IsExecuting => Volatile.Read(ref executing) != 0;

        public bool CanExecute(object? parameter) => !IsExecuting;

        public void Execute(object? parameter) => _ = ExecuteAsync(parameter, CancellationToken.None);

        public async Task ExecuteAsync(object? parameter, CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref executing, 1, 0) != 0)
            {
                return;
            }

            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                await confirm(cancellationToken);
            }
            finally
            {
                Volatile.Write(ref executing, 0);
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private sealed class CancelCommandImplementation(Action close) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => close();
    }
}
