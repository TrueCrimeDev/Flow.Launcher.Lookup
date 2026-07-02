using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Flow.Launcher.Plugin;
using Lookup.Services;

namespace Lookup;

/// <summary>
/// The plugin's panel in Flow's Settings → Plugins → Lookup — quick paths to the
/// things the plugin actually reads: the data folder, config.json, the keyword →
/// dataset mappings, and a reload button with live dataset/error status.
/// Built in code (no XAML) so the SDK-free test project never sees WPF types.
/// </summary>
internal static class SettingsPanel
{
    /// <summary>Delegates rather than snapshots: config and load errors are replaced
    /// by every reload, and the panel must always show the current ones.</summary>
    public static Control Build(
        PluginInitContext context,
        Func<PluginConfig> config,
        SearchIndex index,
        Func<List<LoadError>> loadErrors,
        Action reloadData)
    {
        var pluginDir = context.CurrentPluginMetadata.PluginDirectory;
        var dataDir = Path.Combine(pluginDir, "data");
        var configPath = Path.Combine(pluginDir, "config.json");

        var root = new StackPanel
        {
            Margin = new Thickness(16),
            MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        root.Children.Add(Header("Datasets"));
        var datasetsText = Body("");
        root.Children.Add(datasetsText);

        root.Children.Add(Header("Keywords"));
        var keywordsText = Body("");
        root.Children.Add(keywordsText);

        root.Children.Add(Header("Files"));
        root.Children.Add(Body(
            $"Datasets:  {dataDir}\nConfig:      {configPath}"));

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ActionButton("Open data folder", () =>
        {
            Directory.CreateDirectory(dataDir);
            context.API.OpenDirectory(dataDir);
        }));
        buttons.Children.Add(ActionButton("Edit config.json", () =>
        {
            if (!File.Exists(configPath))
                File.WriteAllText(configPath, DefaultConfigTemplate, Encoding.UTF8);
            Process.Start(new ProcessStartInfo(configPath) { UseShellExecute = true });
        }));
        Button reloadButton = null!;
        reloadButton = ActionButton("Reload data", () =>
        {
            reloadButton.IsEnabled = false;
            Task.Run(reloadData).ContinueWith(_ => reloadButton.Dispatcher.Invoke(() =>
            {
                Refresh();
                reloadButton.IsEnabled = true;
            }));
        });
        buttons.Children.Add(reloadButton);
        buttons.Children.Add(ActionButton("Help / README", () =>
            context.API.OpenUrl(context.CurrentPluginMetadata.Website)));
        root.Children.Add(buttons);

        Refresh();
        return new UserControl { Content = root };

        void Refresh()
        {
            var lines = index.Datasets
                .Select(d => $"{d.Name}{(string.IsNullOrWhiteSpace(d.Version) ? "" : $" ({d.Version})")}  —  {d.Count:n0} item(s)")
                .ToList();
            if (lines.Count == 0)
                lines.Add("No datasets loaded — put dataset .json files in the data folder, then reload.");
            lines.AddRange(loadErrors().Select(e => $"⚠ {Path.GetFileName(e.File)}: {e.Message}"));
            datasetsText.Text = string.Join("\n", lines);

            var cfg = config();
            var kws = context.CurrentPluginMetadata.ActionKeywords ?? new List<string>();
            keywordsText.Text = string.Join("\n", kws.Select(k =>
                cfg.KeywordDatasets.TryGetValue(k, out var ds)
                    ? $"{k}  →  {ds} only"
                    : $"{k}  →  all datasets"));
        }
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 14,
        Margin = new Thickness(0, 12, 0, 4),
    };

    private static TextBlock Body(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.85,
    };

    private static Button ActionButton(string label, Action onClick)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(12, 6, 12, 6),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Written on first "Edit config.json" click; comments are legal in the
    /// plugin's JSON dialect (JsonDefaults skips them).</summary>
    private const string DefaultConfigTemplate =
        """
        {
          // Maximum number of results shown (1-50).
          "max_results": 15,

          // Only load these dataset names; delete the line to load all.
          // "enabled_datasets": ["naics", "zipcodes"],

          // Field copied on Enter: "code" or "title".
          "default_copy_field": "code",

          // Scoped action keywords -> the dataset each one searches.
          // Keywords not listed here (including "lu") search everything.
          "keyword_datasets": { "na": "naics", "zip": "zipcodes" }
        }
        """;
}
