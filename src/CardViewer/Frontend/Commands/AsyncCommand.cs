using System.Windows.Input;

namespace CardViewer;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool> canExecute;
    private readonly Action<Exception> reportError;
    private bool running;

    public AsyncCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> reportError)
    {
        this.execute = execute;
        this.canExecute = canExecute;
        this.reportError = reportError;
    }

    public Task ExecutionTask { get; private set; } = Task.CompletedTask;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && canExecute();
    public async void Execute(object? parameter) => await ExecuteAsync();

    public Task ExecuteAsync()
    {
        if (!CanExecute(null))
            return Task.CompletedTask;
        running = true;
        NotifyCanExecuteChanged();
        ExecutionTask = RunAsync();
        return ExecutionTask;
    }

    private async Task RunAsync()
    {
        try { await execute(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { reportError(ex); }
        finally
        {
            running = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
