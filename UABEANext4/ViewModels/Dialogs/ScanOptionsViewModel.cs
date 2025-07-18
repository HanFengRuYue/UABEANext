using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using UABEANext4.Interfaces;
using UABEANext4.Logic;

namespace UABEANext4.ViewModels.Dialogs;

public partial class ScanOptionsViewModel : ViewModelBase, IDialogAware<ScanOptions?>
{
    [ObservableProperty]
    public bool _includeSubdirectories = true;

    [ObservableProperty]
    public bool _scanCommonUnityDirectories = true;

    [ObservableProperty]
    public bool _validateFileTypes = true;

    [ObservableProperty]
    public bool _skipSmallFiles = true;

    public string Title => "扫描选项";
    public int Width => 400;
    public int Height => 300;
    public event Action<ScanOptions?>? RequestClose;

    [Obsolete("This constructor is for the designer only and should not be used directly.", true)]
    public ScanOptionsViewModel()
    {
    }

    public ScanOptionsViewModel(ScanOptions defaultOptions)
    {
        IncludeSubdirectories = defaultOptions.IncludeSubdirectories;
        ScanCommonUnityDirectories = defaultOptions.ScanCommonUnityDirectories;
        ValidateFileTypes = defaultOptions.ValidateFileTypes;
        SkipSmallFiles = defaultOptions.SkipSmallFiles;
    }

    [RelayCommand]
    public void Confirm()
    {
        var options = new ScanOptions
        {
            IncludeSubdirectories = IncludeSubdirectories,
            ScanCommonUnityDirectories = ScanCommonUnityDirectories,
            ValidateFileTypes = ValidateFileTypes,
            SkipSmallFiles = SkipSmallFiles
        };
        RequestClose?.Invoke(options);
    }

    [RelayCommand]
    public void Cancel()
    {
        RequestClose?.Invoke(null);
    }
} 