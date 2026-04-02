using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Perelegans.Services;

/// <summary>
/// Manages the user's Windows startup registration for Perelegans.
/// </summary>
public class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Perelegans";

    public void SetEnabled(bool enabled)
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new InvalidOperationException("Unable to open the Windows startup registry key.");

        if (enabled)
        {
            runKey.SetValue(ValueName, $"\"{GetExecutablePath()}\"", RegistryValueKind.String);
            return;
        }

        if (runKey.GetValue(ValueName) != null)
        {
            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string GetExecutablePath()
    {
        return Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Unable to determine the current executable path.");
    }
}
