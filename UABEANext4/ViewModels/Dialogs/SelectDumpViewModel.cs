using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using UABEANext4.Interfaces;

namespace UABEANext4.ViewModels.Dialogs;
public partial class SelectDumpViewModel : ViewModelBase, IDialogAware<SelectedDumpType?>
{
    [ObservableProperty]
    public SelectedDumpType _selectedItem;

    public List<string> DropdownItems { get; }

    public string Title => "批量导入";
    public int Width => 300;
    public int Height => 80;
    public event Action<SelectedDumpType?>? RequestClose;

    public SelectDumpViewModel(bool hideAnyOption)
    {
        SelectedItem = SelectedDumpType.JsonDump;

        DropdownItems = new List<string>
        {
            "UABEA JSON 转储",
            "UABE 文本转储",
            "原始转储",
            "任意"
        };

        if (hideAnyOption)
        {
            DropdownItems.RemoveAt(DropdownItems.Count - 1);
        }
    }

    public void BtnOk_Click()
    {
        RequestClose?.Invoke(SelectedItem);
    }

    public void BtnCancel_Click()
    {
        RequestClose?.Invoke(null);
    }
}

public enum SelectedDumpType
{
    JsonDump,
    TxtDump,
    RawDump,
    Any
}
