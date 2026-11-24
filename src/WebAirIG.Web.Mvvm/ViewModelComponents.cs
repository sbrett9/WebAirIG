using System.ComponentModel;
using Microsoft.AspNetCore.Components;

namespace WebAirIG.Web.Mvvm;

/// <summary>
/// Page bound to a DI-resolved ViewModel. Re-renders when the ViewModel raises PropertyChanged and activates
/// polling ViewModels while the page is displayed. Views contain bindings and command invocations only.
/// </summary>
public abstract class ViewModelPage<TViewModel> : ComponentBase, IDisposable
    where TViewModel : class, INotifyPropertyChanged
{
    [Inject] protected TViewModel Vm { get; set; } = default!;

    protected override void OnInitialized()
    {
        Vm.PropertyChanged += OnViewModelChanged;
        (Vm as IActivatable)?.Activate();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Vm.PropertyChanged -= OnViewModelChanged;
        (Vm as IActivatable)?.Deactivate();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Layout bound to a DI-resolved ViewModel (same contract as <see cref="ViewModelPage{TViewModel}"/>).</summary>
public abstract class ViewModelLayout<TViewModel> : LayoutComponentBase, IDisposable
    where TViewModel : class, INotifyPropertyChanged
{
    [Inject] protected TViewModel Vm { get; set; } = default!;

    protected override void OnInitialized() => Vm.PropertyChanged += OnViewModelChanged;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Vm.PropertyChanged -= OnViewModelChanged;
        GC.SuppressFinalize(this);
    }
}

/// <summary>Child control bound to a ViewModel passed as a parameter.</summary>
public abstract class ViewModelControl<TViewModel> : ComponentBase, IDisposable
    where TViewModel : class, INotifyPropertyChanged
{
    private TViewModel? _subscribed;

    [Parameter, EditorRequired] public TViewModel Vm { get; set; } = default!;

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(_subscribed, Vm)) return;
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnViewModelChanged;
        _subscribed = Vm;
        Vm.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnViewModelChanged;
        GC.SuppressFinalize(this);
    }
}
