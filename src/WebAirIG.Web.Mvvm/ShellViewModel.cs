using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WebAirIG.Web.Mvvm;

/// <summary>Application shell state (sidebar), one per circuit.</summary>
public sealed class ShellViewModel : ObservableObject
{
    private bool _sidebarExpanded = true;

    public ShellViewModel() => ToggleSidebarCommand = new RelayCommand(() => SidebarExpanded = !SidebarExpanded);

    public bool SidebarExpanded { get => _sidebarExpanded; set => SetProperty(ref _sidebarExpanded, value); }
    public IRelayCommand ToggleSidebarCommand { get; }
}
