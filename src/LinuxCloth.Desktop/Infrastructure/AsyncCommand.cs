using System.Windows.Input;

namespace LinuxCloth.Desktop.Infrastructure;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<bool> _canExecute;
    private readonly Action<Exception> _errorHandler;
    private readonly Func<Task> _execute;
    private bool _isExecuting;

    public AsyncCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? errorHandler = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute ?? (() => true);
        _errorHandler = errorHandler ?? (_ => { });
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && _canExecute();

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _errorHandler(exception);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
