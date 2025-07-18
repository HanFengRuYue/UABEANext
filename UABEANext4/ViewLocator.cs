using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Core;
using System;

namespace UABEANext4;

public class ViewLocator : IDataTemplate
{
    public Control Build(object? data)
    {
        if (data == null)
        {
            return new TextBlock { Text = "空视图模型" };
        }

        var dataType = data.GetType();
        var name = dataType.FullName!.Replace("ViewModel", "View");
        var type = dataType.Assembly.GetType(name);

        if (type != null)
        {
            var instance = (Control)Activator.CreateInstance(type)!;
            if (instance != null)
            {
                return instance;
            }
            else
            {
                return new TextBlock { Text = "创建实例失败: " + type.FullName };
            }
        }
        else
        {
            return new TextBlock { Text = "未找到: " + name };
        }
    }

    public bool Match(object? data)
    {
        return data is ObservableObject or IDockable;
    }
}
