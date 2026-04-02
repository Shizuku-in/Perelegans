using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Perelegans.ViewModels;

public class ProcessItem
{
    public string ProcessName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public int ProcessId { get; set; }
}

public partial class AddFromProcessViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<ProcessItem> _processes = new();

    [ObservableProperty]
    private ProcessItem? _selectedProcess;

    public AddFromProcessViewModel()
    {
        RefreshProcesses();
    }

    [RelayCommand]
    public void RefreshProcesses()
    {
        Processes.Clear();
        var allProcs = Process.GetProcesses();
        foreach (var p in allProcs)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(p.MainWindowTitle))
                {
                    var item = new ProcessItem
                    {
                        ProcessName = p.ProcessName,
                        WindowTitle = p.MainWindowTitle,
                        ProcessId = p.Id,
                        ExecutablePath = p.MainModule?.FileName ?? ""
                    };
                    if (!string.IsNullOrWhiteSpace(item.ExecutablePath))
                    {
                        Processes.Add(item);
                    }
                }
            }
            catch
            {
                // Ignore processes we can't access
            }
        }
        
        var sorted = Processes.OrderBy(x => x.WindowTitle).ToList();
        Processes.Clear();
        foreach (var item in sorted) Processes.Add(item);
    }
}
