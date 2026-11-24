using CommunityToolkit.Mvvm.ComponentModel;

namespace WebAirIG.Web.Mvvm;

/// <summary>ViewModels that poll services while at least one view is attached.</summary>
public interface IActivatable
{
    void Activate();
    void Deactivate();
}

/// <summary>Base for polling ViewModels: runs <see cref="Refresh"/> on a timer while views are attached.</summary>
public abstract class PollingViewModel : ObservableObject, IActivatable, IDisposable
{
    private readonly TimeSpan _period;
    private readonly object _gate = new();
    private int _views;
    private CancellationTokenSource? _cts;

    protected PollingViewModel(TimeSpan period) => _period = period;

    public void Activate()
    {
        lock (_gate)
        {
            if (_views++ > 0) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SafeRefresh();
            _ = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(_period);
                try
                {
                    while (await timer.WaitForNextTickAsync(token)) SafeRefresh();
                }
                catch (OperationCanceledException) { }
            }, token);
        }
    }

    public void Deactivate()
    {
        lock (_gate)
        {
            if (_views == 0 || --_views > 0) return;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void SafeRefresh()
    {
        try { Refresh(); }
        catch (Exception ex) { OnRefreshError(ex); }
    }

    /// <summary>Pulls fresh state from services and raises property changes. Runs on a thread-pool thread.</summary>
    protected abstract void Refresh();

    protected virtual void OnRefreshError(Exception ex) { }

    public virtual void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _views = 0;
        }
        GC.SuppressFinalize(this);
    }
}
