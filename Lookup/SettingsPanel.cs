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
/// The plugin's panel in Flow's Settings → Plugins → Lookup. Edits the config with
/// plain controls (no JSON knowledge needed): result cap, copy field, per-dataset
/// enable toggles, and a scope dropdown per action keyword. Changes save to
/// config.json immediately; toggling datasets also rebuilds the index.
/// Built in code (no XAML) so the SDK-free test project never sees WPF types.
/// </summary>
internal static class SettingsPanel
{
    private const string AllDatasets = "all datasets";

    /// <summary>Delegates rather than snapshots: config, load errors, and the dataset
    /// inventory are replaced by every reload, and the panel must show current state.</summary>
    public static Control Build(
        PluginInitContext context,
        Func<PluginConfig> config,
        SearchIndex index,
        Func<List<LoadError>> loadErrors,
        Func<IReadOnlyList<DatasetInfo>> availableDatasets,
        Action reloadData,
        Action saveConfig)
    {
        var pluginDir = context.CurrentPluginMetadata.PluginDirectory;
        var dataDir = Path.Combine(pluginDir, "data");
        var configPath = Path.Combine(pluginDir, "config.json");
        var suppress = false; // true while the panel itself is writing control values

        var root = new StackPanel
        {
            Margin = new Thickness(16),
            MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        // ---- Search settings -------------------------------------------------

        root.Children.Add(Header("Search"));

        var maxResultsValue = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 24,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var maxResultsSlider = new Slider
        {
            Minimum = 1,
            Maximum = 50,
            Width = 220,
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        maxResultsSlider.ValueChanged += (_, e) =>
        {
            maxResultsValue.Text = ((int)e.NewValue).ToString();
            if (suppress) return;
            config().MaxResults = (int)e.NewValue;
            saveConfig(); // query-time cap only; no index rebuild needed
        };
        root.Children.Add(LabeledRow("Max results", maxResultsSlider, maxResultsValue));

        var copyFieldBox = new ComboBox
        {
            ItemsSource = new[] { "code", "title" },
            Width = 120,
            VerticalAlignment = VerticalAlignment.Center,
        };
        copyFieldBox.SelectionChanged += (_, _) =>
        {
            if (suppress || copyFieldBox.SelectedItem is not string field) return;
            config().DefaultCopyField = field;
            saveConfig();
        };
        root.Children.Add(LabeledRow("Enter copies", copyFieldBox));

        // ---- Datasets: enable toggles + status --------------------------------

        root.Children.Add(Header("Datasets"));
        root.Children.Add(Hint("Unchecked datasets stay on disk but are not searched."));
        var datasetChecks = new StackPanel();
        root.Children.Add(datasetChecks);
        var errorsText = Body("");
        root.Children.Add(errorsText);

        // ---- Keywords: one scope dropdown per Flow action keyword -------------

        root.Children.Add(Header("Keywords"));
        root.Children.Add(Hint("What each action keyword searches. Add or rename the keywords themselves above, next to \"Action keyword\"."));
        var keywordRows = new StackPanel();
        root.Children.Add(keywordRows);

        // ---- Files + actions ---------------------------------------------------

        root.Children.Add(Header("Files"));
        root.Children.Add(Body($"Datasets:  {dataDir}\nConfig:      {configPath}"));

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(ActionButton("Open data folder", () =>
        {
            Directory.CreateDirectory(dataDir);
            context.API.OpenDirectory(dataDir);
        }));
        buttons.Children.Add(ActionButton("Edit config.json", () =>
        {
            if (!File.Exists(configPath)) saveConfig(); // materialize current settings first
            Process.Start(new ProcessStartInfo(configPath) { UseShellExecute = true });
        }));
        Button reloadButton = null!;
        reloadButton = ActionButton("Reload data", () => RunReload(reloadButton));
        buttons.Children.Add(reloadButton);
        buttons.Children.Add(ActionButton("Help / README", () =>
            context.API.OpenUrl(context.CurrentPluginMetadata.Website)));
        root.Children.Add(buttons);

        Refresh();
        return new UserControl { Content = root };

        // --- panel behaviour ---------------------------------------------------

        void RunReload(Button button)
        {
            button.IsEnabled = false;
            Task.Run(reloadData).ContinueWith(_ => button.Dispatcher.Invoke(() =>
            {
                Refresh();
                button.IsEnabled = true;
            }));
        }

        bool IsEnabled(string dataset)
        {
            var enabled = config().EnabledDatasets;
            return enabled is not { Count: > 0 } ||
                   enabled.Contains(dataset, StringComparer.OrdinalIgnoreCase);
        }

        void SaveEnabled()
        {
            var checkedNames = datasetChecks.Children.OfType<CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => (string)c.Tag)
                .ToList();
            // All (or none — unchecking everything is a mistake, not intent) → search all.
            config().EnabledDatasets =
                checkedNames.Count == 0 || checkedNames.Count == datasetChecks.Children.Count
                    ? null
                    : checkedNames;
            saveConfig();
            RunReload(reloadButton); // the filter is applied at index build time
        }

        void SaveKeyword(string keyword, string choice)
        {
            var map = config().KeywordDatasets;
            if (choice == AllDatasets) map.Remove(keyword);
            else map[keyword] = choice;
            saveConfig(); // applied per query; no rebuild needed
        }

        void Refresh()
        {
            suppress = true;
            var cfg = config();

            maxResultsSlider.Value = cfg.MaxResults;
            maxResultsValue.Text = cfg.MaxResults.ToString();
            copyFieldBox.SelectedItem =
                string.Equals(cfg.DefaultCopyField, "title", StringComparison.OrdinalIgnoreCase) ? "title" : "code";

            var datasets = availableDatasets();
            datasetChecks.Children.Clear();
            foreach (var d in datasets)
            {
                var check = new CheckBox
                {
                    Content = $"{d.Name}{(string.IsNullOrWhiteSpace(d.Version) ? "" : $" ({d.Version})")}  —  {d.Count:n0} item(s)",
                    Tag = d.Name,
                    IsChecked = IsEnabled(d.Name),
                    Margin = new Thickness(0, 2, 0, 2),
                };
                check.Checked += (_, _) => { if (!suppress) SaveEnabled(); };
                check.Unchecked += (_, _) => { if (!suppress) SaveEnabled(); };
                datasetChecks.Children.Add(check);
            }
            if (datasets.Count == 0)
                datasetChecks.Children.Add(Body("No datasets found — put dataset .json files in the data folder, then reload."));

            errorsText.Text = string.Join("\n", loadErrors().Select(e => $"⚠ {Path.GetFileName(e.File)}: {e.Message}"));
            errorsText.Visibility = errorsText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            var scopeChoices = new List<string> { AllDatasets };
            scopeChoices.AddRange(datasets.Select(d => d.Name));
            keywordRows.Children.Clear();
            foreach (var kw in context.CurrentPluginMetadata.ActionKeywords ?? new List<string>())
            {
                var box = new ComboBox
                {
                    ItemsSource = scopeChoices,
                    Width = 160,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                box.SelectedItem = cfg.KeywordDatasets.TryGetValue(kw, out var ds) &&
                                   scopeChoices.Contains(ds, StringComparer.OrdinalIgnoreCase)
                    ? scopeChoices.First(c => string.Equals(c, ds, StringComparison.OrdinalIgnoreCase))
                    : AllDatasets;
                var keyword = kw;
                box.SelectionChanged += (_, _) =>
                {
                    if (!suppress && box.SelectedItem is string choice) SaveKeyword(keyword, choice);
                };
                keywordRows.Children.Add(LabeledRow(keyword, box));
            }

            suppress = false;
        }
    }

    // --- small control builders ------------------------------------------------

    private static StackPanel LabeledRow(string label, params UIElement[] controls)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 4),
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            MinWidth = 110,
            VerticalAlignment = VerticalAlignment.Center,
        });
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 14,
        Margin = new Thickness(0, 14, 0, 4),
    };

    private static TextBlock Body(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.85,
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.6,
        FontSize = 11.5,
        Margin = new Thickness(0, 0, 0, 4),
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
}
