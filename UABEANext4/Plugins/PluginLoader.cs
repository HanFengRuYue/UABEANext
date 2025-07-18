using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UABEANext4.AssetWorkspace;
using UABEANext4.Util;

namespace UABEANext4.Plugins;
public class PluginLoader
{
    private readonly List<IUavPluginOption> _pluginOptions = [];
    private readonly List<IUavPluginPreviewer> _pluginPreviewers = [];

    public bool LoadPlugin(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var plugLoadCtx = new PluginLoadContext(fullPath);
            var asm = plugLoadCtx.LoadAssemblyByPath(fullPath);
            
            bool hasValidPlugin = false;
            
            foreach (Type type in asm.GetTypes())
            {
                if (typeof(IUavPluginOption).IsAssignableFrom(type))
                {
                    object? typeInst = Activator.CreateInstance(type);
                    if (typeInst == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to create instance of plugin type: {type.Name}");
                        continue;
                    }

                    if (typeInst is not IUavPluginOption plugInst)
                    {
                        System.Diagnostics.Debug.WriteLine($"Plugin type {type.Name} does not implement IUavPluginOption correctly");
                        continue;
                    }

                    _pluginOptions.Add(plugInst);
                    hasValidPlugin = true;
                }
                else if (typeof(IUavPluginPreviewer).IsAssignableFrom(type))
                {
                    object? typeInst = Activator.CreateInstance(type);
                    if (typeInst == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to create instance of previewer type: {type.Name}");
                        continue;
                    }

                    if (typeInst is not IUavPluginPreviewer plugInst)
                    {
                        System.Diagnostics.Debug.WriteLine($"Previewer type {type.Name} does not implement IUavPluginPreviewer correctly");
                        continue;
                    }

                    _pluginPreviewers.Add(plugInst);
                    hasValidPlugin = true;
                }
            }
            
            return hasValidPlugin;
        }
        catch (FileNotFoundException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Plugin file not found: {path}. Error: {ex.Message}");
            return false;
        }
        catch (BadImageFormatException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Invalid plugin format: {path}. Error: {ex.Message}");
            return false;
        }
        catch (ReflectionTypeLoadException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load types from plugin: {path}. Error: {ex.Message}");
            if (ex.LoaderExceptions != null)
            {
                foreach (var loaderEx in ex.LoaderExceptions)
                {
                    System.Diagnostics.Debug.WriteLine($"  Loader exception: {loaderEx?.Message}");
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unexpected error loading plugin: {path}. Error: {ex.Message}");
            return false;
        }
    }

    public void LoadPluginsInDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var loadedCount = 0;
            var totalCount = 0;
            
            foreach (string file in Directory.EnumerateFiles(directory, "*.dll"))
            {
                totalCount++;
                if (LoadPlugin(file))
                {
                    loadedCount++;
                    System.Diagnostics.Debug.WriteLine($"Successfully loaded plugin: {Path.GetFileName(file)}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load plugin: {Path.GetFileName(file)}");
                }
            }
            
            System.Diagnostics.Debug.WriteLine($"Plugin loading completed: {loadedCount}/{totalCount} plugins loaded successfully");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading plugins from directory {directory}: {ex.Message}");
        }
    }

    public List<PluginOptionModePair> GetOptionsThatSupport(Workspace workspace, List<AssetInst> assets, UavPluginMode mode)
    {
        var options = new List<PluginOptionModePair>();
        foreach (var option in _pluginOptions)
        {
            var bothOpt = mode & option.Options;
            foreach (var flag in bothOpt.GetUniqueFlags())
            {
                if (flag == UavPluginMode.All)
                    continue;

                var supported = option.SupportsSelection(workspace, flag, assets);
                if (supported)
                    options.Add(new PluginOptionModePair(option, flag));
            }
        }

        return options;
    }

    public List<PluginPreviewerTypePair> GetPreviewersThatSupport(Workspace workspace, AssetInst asset)
    {
        var previewers = new List<PluginPreviewerTypePair>();
        foreach (var previewer in _pluginPreviewers)
        {
            var previewType = previewer.SupportsPreview(workspace, asset);
            if (previewType != UavPluginPreviewerType.None)
                previewers.Add(new PluginPreviewerTypePair(previewer, previewType));
        }

        return previewers;
    }
}

