using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Flow.Launcher.Plugin;
using Lookup.Models;
using Lookup.Services;

namespace Lookup;

/// <summary>
/// The "Links" section of the settings panel: add, edit and delete the links that
/// <c>go</c> opens. Built in code like the rest of the panel, so the SDK-free test
/// project never sees WPF types.
///
/// Edits mutate an in-memory list; "Save links" writes links.json and reloads the
/// index. Nothing is written per keystroke — a half-typed target should not become
/// a broken entry the moment the user pauses.
/// </summary>
internal static class LinksEditor
{
    private const string GlyphFont = IconResolver.FontFamily;

    /// <summary>A small curated set; the dropdown renders each glyph beside its label,
    /// so what the user sees is the truth regardless of the name.</summary>
    private static readonly (string Label, string Glyph)[] Glyphs =
    {
        ("Auto (detect)", ""),
        ("Link", ""),
        ("Globe", ""),
        ("Folder", ""),
        ("Document", ""),
        ("Mail", ""),
        ("Calendar", ""),
        ("Contact", ""),
        ("Settings", ""),
        ("Search", ""),
        ("Code", ""),
        ("Cloud", ""),
        ("Favorite", ""),
    };

    public static UIElement Build(
        PluginInitContext context,
        Func<List<LinkEntry>> links,
        Func<List<LinkError>> linkErrors,
        Action<List<LinkEntry>> saveLinks,
        Action reloadData)
    {
        var settingsDir = context.CurrentPluginMetadata.PluginSettingsDirectoryPath;
        var linksPath = LinkStore.FilePath(settingsDir);

        // Working copy: edits are discardable until Save is pressed.
        var working = links().Select(Clone).ToList();

        var section = new StackPanel();
        var rows = new StackPanel();
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 6, 0, 0),
        };

        section.Children.Add(rows);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Button("Add link", () =>
        {
            working.Add(new LinkEntry { Name = "New link", Target = "https://" });
            RenderRows();
        }));
        buttons.Children.Add(Button("Save links", () =>
        {
            var problems = Validate(working);
            if (problems.Count > 0)
            {
                status.Text = "Not saved — " + string.Join("  ·  ", problems);
                return;
            }

            saveLinks(working.Select(Clone).ToList());
            reloadData();
            working.Clear();
            working.AddRange(links().Select(Clone)); // adopt what the store actually kept
            RenderRows();
            status.Text = $"Saved {working.Count} link(s) to {linksPath}";
        }));
        buttons.Children.Add(Button("Edit links.json", () =>
        {
            if (!File.Exists(linksPath))
                saveLinks(working.Select(Clone).ToList()); // materialise the file first
            Process.Start(new ProcessStartInfo(linksPath) { UseShellExecute = true });
        }));
        buttons.Children.Add(Button("Reload from file", () =>
        {
            reloadData();
            working.Clear();
            working.AddRange(links().Select(Clone));
            RenderRows();
            status.Text = "Reloaded.";
        }));

        section.Children.Add(buttons);
        section.Children.Add(status);

        RenderRows();
        return section;

        void RenderRows()
        {
            rows.Children.Clear();

            if (working.Count == 0)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = "No links yet. Add one, then type  go  in Flow to see it.",
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            foreach (var link in working.ToList())
                rows.Children.Add(BuildRow(link));

            var errors = linkErrors();
            status.Text = errors.Count == 0
                ? status.Text
                : string.Join("\n", errors.Select(e => $"{e.Name}: {e.Message}"));
        }

        Border BuildRow(LinkEntry link)
        {
            var grid = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

            var nameBox = Field(link.Name, 150, v => link.Name = v);
            var aliasBox = Field(string.Join(", ", link.Aliases), 110,
                v => link.Aliases = v.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                     .Select(a => a.Trim())
                                     .Where(a => a.Length > 0)
                                     .ToList());
            var targetBox = Field(link.Target, 300, v => link.Target = v);

            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(Labeled("Name", nameBox));
            top.Children.Add(Labeled("Aliases", aliasBox));
            grid.Children.Add(top);

            var bottom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            bottom.Children.Add(Labeled("Target", targetBox));
            bottom.Children.Add(Button("Browse…", () =>
            {
                var picked = PickFile();
                if (picked is null) return;
                link.Target = picked;
                targetBox.Text = picked;
            }));
            grid.Children.Add(bottom);

            var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            meta.Children.Add(Labeled("Icon", GlyphPicker(link)));
            meta.Children.Add(Button("Delete", () =>
            {
                working.Remove(link);
                RenderRows();
            }));
            grid.Children.Add(meta);

            return new Border
            {
                Child = grid,
                Padding = new Thickness(10),
                Margin = new Thickness(0, 4, 0, 4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                CornerRadius = new CornerRadius(6),
            };
        }

        ComboBox GlyphPicker(LinkEntry link)
        {
            var picker = new ComboBox { Width = 170, VerticalAlignment = VerticalAlignment.Center };

            foreach (var (label, glyph) in Glyphs)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                if (glyph.Length > 0)
                    row.Children.Add(new TextBlock
                    {
                        Text = glyph,
                        FontFamily = new FontFamily(GlyphFont),
                        Margin = new Thickness(0, 0, 8, 0),
                        Width = 16,
                    });
                row.Children.Add(new TextBlock { Text = label });
                picker.Items.Add(new ComboBoxItem { Content = row, Tag = glyph });
            }

            var current = link.Icon.StartsWith("glyph:", StringComparison.OrdinalIgnoreCase)
                ? link.Icon["glyph:".Length..]
                : "";
            picker.SelectedIndex = Math.Max(0, Array.FindIndex(Glyphs, g => g.Glyph == current));

            picker.SelectionChanged += (_, _) =>
            {
                var glyph = (picker.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                link.Icon = glyph.Length == 0 ? "auto" : "glyph:" + glyph;
            };

            return picker;
        }
    }

    /// <summary>Blocks a save that would produce entries the store will reject anyway.</summary>
    private static List<string> Validate(List<LinkEntry> links)
    {
        var problems = new List<string>();

        if (links.Any(l => string.IsNullOrWhiteSpace(l.Name)))
            problems.Add("every link needs a name");

        if (links.Any(l => string.IsNullOrWhiteSpace(l.Target)))
            problems.Add("every link needs a target");

        var duplicates = links
            .SelectMany(l => l.Aliases)
            .GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            problems.Add("duplicate aliases: " + string.Join(", ", duplicates));

        return problems;
    }

    /// <summary>Uses Flow's own file dialog when available, so the picker matches the
    /// host's look; falls back to nothing rather than throwing.</summary>
    private static string? PickFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a file, application or shortcut",
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static LinkEntry Clone(LinkEntry link) => new()
    {
        Name = link.Name,
        Aliases = new List<string>(link.Aliases),
        Target = link.Target,
        Icon = link.Icon,
        Description = link.Description,
    };

    private static TextBox Field(string value, double width, Action<string> onChanged)
    {
        var box = new TextBox
        {
            Text = value,
            Width = width,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.TextChanged += (_, _) => onChanged(box.Text);
        return box;
    }

    private static StackPanel Labeled(string label, UIElement control)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Opacity = 0.7,
            MinWidth = 52,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(control);
        return panel;
    }

    private static Button Button(string label, Action onClick)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(0, 0, 8, 6),
            Padding = new Thickness(10, 5, 10, 5),
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
