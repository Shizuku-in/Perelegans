using CommunityToolkit.Mvvm.ComponentModel;

namespace Perelegans.ViewModels;

public partial class WorkflowStepViewModel(string name, string statusText) : ObservableObject
{
    [ObservableProperty]
    private string _name = name;

    [ObservableProperty]
    private string _statusText = statusText;

    [ObservableProperty]
    private string _accentBrushKey = "Perelegans.SubtleForegroundBrush";
}
