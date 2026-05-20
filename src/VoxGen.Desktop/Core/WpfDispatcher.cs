using System;
using System.Windows.Threading;

namespace VoxGen.Desktop.Core;

/// <summary>WPF-backed <see cref="IUiDispatcher"/> — posts onto the application Dispatcher.</summary>
public sealed class WpfDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Post(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.BeginInvoke(action);
        }
    }
}
