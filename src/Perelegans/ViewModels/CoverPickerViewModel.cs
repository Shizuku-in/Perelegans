using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Perelegans.Models;
using Perelegans.Services;

namespace Perelegans.ViewModels;

public partial class CoverPickerViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<CoverCandidate> _candidates = new();

    [ObservableProperty]
    private CoverCandidate? _selectedCandidate;

    public string HintText => TranslationService.Instance["Meta_CoverPickerHint"];

    public CoverPickerViewModel(IEnumerable<CoverCandidate> candidates)
    {
        Candidates = new ObservableCollection<CoverCandidate>(candidates);
        SelectedCandidate = Candidates.Count > 0 ? Candidates[0] : null;
    }
}
