using Aibell.Desktop.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Aibell.Domain.Playlists;

namespace Aibell.Desktop;

public sealed record SmartPlaylistEdit(string Name, SmartPlaylistDefinition Definition);

public sealed class SmartPlaylistDialog : Window
{
    private readonly TextBox nameInput = new()
    {
        Name = "SmartPlaylistName",
        PlaceholderText = Loc.Get("Dialog.PlaylistName"),
        Height = 34,
        Padding = new Thickness(10, 0),
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly ComboBox matchInput = new()
    {
        Name = "SmartPlaylistMatch",
        MinWidth = 80,
        Height = 32,
        Padding = new Thickness(10, 0),
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly StackPanel rowsPanel = new() { Spacing = 6 };
    private readonly List<RuleEditor> rows = [];
    private readonly TextBlock validation = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button accept = new() { Name = "SaveSmartPlaylist", Content = Loc.Get("Dialog.Create"), MinWidth = 92, Height = 30, FontWeight = FontWeight.SemiBold, IsEnabled = false, Classes = { "dialogAccept" } };

    public SmartPlaylistDialog(string name = "", SmartPlaylistDefinition? definition = null)
    {
        Title = definition is null ? Loc.Get("Smart.CreateTitle") : Loc.Get("Smart.EditTitle");
        Width = 680;
        Height = 460;
        MinWidth = 600;
        MinHeight = 380;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("DialogBrush");
        AutomationProperties.SetName(nameInput, Loc.Get("Dialog.PlaylistName"));
        AutomationProperties.SetName(matchInput, Loc.Get("Smart.MatchAutomation"));
        nameInput.Text = name;
        nameInput.IsReadOnly = definition is not null;
        matchInput.ItemsSource = new[]
        {
            new Choice<SmartPlaylistMatch>(SmartPlaylistMatch.All, Loc.Get("Smart.All")),
            new Choice<SmartPlaylistMatch>(SmartPlaylistMatch.Any, Loc.Get("Smart.Any"))
        };
        matchInput.SelectedIndex = definition?.Match == SmartPlaylistMatch.Any ? 1 : 0;
        accept.Content = definition is null ? Loc.Get("Dialog.Create") : Loc.Get("Dialog.Save");
        Button cancel = new() { Content = Loc.Get("Dialog.Cancel"), MinWidth = 92, Height = 30 };
        Button close = new()
        {
            Content = new PathIcon
            {
                Width = 14,
                Height = 14,
                Foreground = new SolidColorBrush(Color.Parse("#E6E8EA")),
                Data = Geometry.Parse("M5,3 L12,10 L19,3 L21,5 L14,12 L21,19 L19,21 L12,14 L5,21 L3,19 L10,12 L3,5 Z")
            },
            Width = 40,
            Height = 40,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Classes = { "toolbar" }
        };
        AutomationProperties.SetName(close, Loc.Get("Smart.CloseAutomation"));
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,40") };
        header.Children.Add(new TextBlock { Text = Title, Margin = new Thickness(16, 0), FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White });
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        Border titleBar = new()
        {
            Background = new SolidColorBrush(Color.Parse("#303438")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D52268")),
            BorderThickness = new Thickness(0, 0, 0, 3),
            Child = header
        };
        titleBar.PointerPressed += (_, args) =>
        {
            if (args.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed) BeginMoveDrag(args);
        };
        StackPanel fields = new() { Spacing = 8, Margin = new Thickness(20, 15, 20, 10) };
        fields.Children.Add(new TextBlock
        {
            Text = Loc.Get("Smart.NameLabel"),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            [!ForegroundProperty] = new DynamicResourceExtension("SecondaryTextBrush")
        });
        fields.Children.Add(nameInput);
        StackPanel match = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        match.Children.Add(new TextBlock { Text = Loc.Get("Smart.Match"), VerticalAlignment = VerticalAlignment.Center });
        match.Children.Add(matchInput);
        match.Children.Add(new TextBlock { Text = Loc.Get("Smart.Conditions"), VerticalAlignment = VerticalAlignment.Center });
        ScrollViewer rulesScroll = new()
        {
            Content = rowsPanel,
            Margin = new Thickness(0, 8, 0, 8),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid conditions = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        conditions.Children.Add(match);
        Grid.SetRow(rulesScroll, 1); conditions.Children.Add(rulesScroll);
        TextBlock hint = new()
        {
            Text = Loc.Get("Smart.Hint"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            [!ForegroundProperty] = new DynamicResourceExtension("SecondaryTextBrush")
        };
        Grid.SetRow(hint, 2); conditions.Children.Add(hint);
        Border conditionsPanel = new()
        {
            Child = conditions,
            Margin = new Thickness(18, 0, 18, 0),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            [!BorderBrushProperty] = new DynamicResourceExtension("PanelBorderBrush")
        };
        StackPanel footer = new() { Spacing = 7, Margin = new Thickness(18, 10, 18, 10) };
        footer.Children.Add(validation);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(accept);
        Border buttonFooter = new()
        {
            Child = buttons,
            Padding = new Thickness(14, 10),
            BorderThickness = new Thickness(0, 1, 0, 0),
            [!BackgroundProperty] = new DynamicResourceExtension("DialogFooterBrush"),
            [!BorderBrushProperty] = new DynamicResourceExtension("PanelBorderBrush")
        };
        Grid layout = new() { RowDefinitions = new RowDefinitions("44,Auto,*,Auto,51") };
        layout.Children.Add(titleBar);
        Grid.SetRow(fields, 1); layout.Children.Add(fields);
        Grid.SetRow(conditionsPanel, 2); layout.Children.Add(conditionsPanel);
        Grid.SetRow(footer, 3); layout.Children.Add(footer);
        Grid.SetRow(buttonFooter, 4); layout.Children.Add(buttonFooter);
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#17191B")),
            BorderThickness = new Thickness(1),
            Child = layout
        };

        nameInput.TextChanged += (_, _) => ValidateInput();
        matchInput.SelectionChanged += (_, _) => ValidateInput();
        accept.Click += (_, _) => Close(new SmartPlaylistEdit(nameInput.Text!.Trim(), Definition()));
        cancel.Click += (_, _) => Close(null);
        close.Click += (_, _) => Close(null);
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { Close(null); args.Handled = true; }
            else if (args.Key == Key.Enter && accept.IsEnabled)
            { Close(new SmartPlaylistEdit(nameInput.Text!.Trim(), Definition())); args.Handled = true; }
        };
        foreach (SmartPlaylistRule rule in definition?.Rules ?? []) AddRule(rule);
        if (rows.Count == 0) AddRule(new(SmartPlaylistField.Rating, SmartPlaylistOperator.GreaterOrEqual, "4"));
        ValidateInput();
        Opened += (_, _) => { nameInput.Focus(); nameInput.SelectAll(); };
    }

    private void AddRule(SmartPlaylistRule rule, RuleEditor? after = null)
    {
        if (rows.Count >= 20) return;
        RuleEditor editor = new(rule, ValidateInput);
        editor.Add.Click += (_, _) => AddRule(new(SmartPlaylistField.Title, SmartPlaylistOperator.Contains, ""), editor);
        editor.Remove.Click += (_, _) =>
        {
            if (rows.Count <= 1) return;
            rows.Remove(editor);
            rowsPanel.Children.Remove(editor.View);
            ValidateInput();
        };
        int position = after is null ? rows.Count : rows.IndexOf(after) + 1;
        rows.Insert(position, editor);
        rowsPanel.Children.Insert(position, editor.View);
        ValidateInput();
        if (after is not null) editor.FocusValue();
    }

    private SmartPlaylistDefinition Definition() => new(
        ((Choice<SmartPlaylistMatch>)matchInput.SelectedItem!).Value,
        rows.Select(row => row.Rule()).ToArray());

    private void ValidateInput()
    {
        foreach (RuleEditor row in rows)
        {
            row.Add.IsEnabled = rows.Count < 20;
            row.Remove.IsEnabled = rows.Count > 1;
        }
        try
        {
            if (string.IsNullOrWhiteSpace(nameInput.Text)) throw new ArgumentException("Enter a playlist name.");
            Definition().Validate();
            validation.Text = "";
            accept.IsEnabled = true;
        }
        catch (ArgumentException exception)
        {
            validation.Text = exception.Message;
            accept.IsEnabled = false;
        }
    }

    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class RuleEditor
    {
        private readonly ComboBox field = new()
        {
            MinWidth = 130,
            Height = 28,
            Padding = new Thickness(8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        private readonly ComboBox comparison = new()
        {
            MinWidth = 130,
            Height = 28,
            Padding = new Thickness(8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        private readonly TextBox value = new()
        {
            Height = 28,
            Padding = new Thickness(8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        public Grid View { get; } = new() { ColumnDefinitions = new ColumnDefinitions("145,145,*,28,28"), ColumnSpacing = 6 };
        public Button Remove { get; } = CreateRuleButton("M0,4 L10,4 L10,6 L0,6 Z");
        public Button Add { get; } = CreateRuleButton("M0,4 L4,4 L4,0 L6,0 L6,4 L10,4 L10,6 L6,6 L6,10 L4,10 L4,6 L0,6 Z");

        private static Button CreateRuleButton(string geometry) => new()
        {
            Content = new PathIcon
            {
                Width = 10,
                Height = 10,
                Data = Geometry.Parse(geometry),
                [!ForegroundProperty] = new DynamicResourceExtension("TextBrush")
            },
            Padding = new Thickness(0),
            MinWidth = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        public RuleEditor(SmartPlaylistRule rule, Action changed)
        {
            AutomationProperties.SetName(field, Loc.Get("Smart.RuleField"));
            AutomationProperties.SetName(comparison, Loc.Get("Smart.RuleComparison"));
            AutomationProperties.SetName(value, Loc.Get("Smart.RuleValue"));
            AutomationProperties.SetName(Remove, Loc.Get("Smart.RemoveRule"));
            AutomationProperties.SetName(Add, Loc.Get("Smart.AddRuleAutomation"));
            ToolTip.SetTip(Remove, Loc.Get("Smart.RemoveTip"));
            ToolTip.SetTip(Add, Loc.Get("Smart.AddTip"));
            field.ItemsSource = Enum.GetValues<SmartPlaylistField>().Select(item =>
                new Choice<SmartPlaylistField>(item, item switch
                {
                    SmartPlaylistField.Title => Loc.Get("Column.Title"),
                    SmartPlaylistField.Artist => Loc.Get("Column.Artist"),
                    SmartPlaylistField.Album => Loc.Get("Column.Album"),
                    SmartPlaylistField.Genre => Loc.Get("Column.Genre"),
                    SmartPlaylistField.Rating => Loc.Get("Column.Rating"),
                    SmartPlaylistField.Year => Loc.Get("Column.Year"),
                    SmartPlaylistField.PlayCount => Loc.Get("Column.PlayCount"),
                    SmartPlaylistField.SkipCount => Loc.Get("Smart.SkipCount"),
                    _ => item.ToString()
                })).ToArray();
            field.SelectedIndex = (int)rule.Field;
            SetComparisons(rule.Operator);
            value.Text = rule.Value;
            field.SelectionChanged += (_, _) => { SetComparisons(); changed(); };
            comparison.SelectionChanged += (_, _) => changed();
            value.TextChanged += (_, _) => changed();
            View.Children.Add(field);
            Grid.SetColumn(comparison, 1); View.Children.Add(comparison);
            Grid.SetColumn(value, 2); View.Children.Add(value);
            Grid.SetColumn(Remove, 3); View.Children.Add(Remove);
            Grid.SetColumn(Add, 4); View.Children.Add(Add);
        }

        public void FocusValue() => Dispatcher.UIThread.Post(() => value.Focus());

        private void SetComparisons(SmartPlaylistOperator? selected = null)
        {
            SmartPlaylistField selectedField = ((Choice<SmartPlaylistField>)field.SelectedItem!).Value;
            Choice<SmartPlaylistOperator>[] options = SmartPlaylistRule.OperatorsFor(selectedField)
                .Select(item => new Choice<SmartPlaylistOperator>(item, item switch
                {
                    SmartPlaylistOperator.Contains => Loc.Get("Smart.Contains"),
                    SmartPlaylistOperator.Equals => Loc.Get("Smart.Equals"),
                    SmartPlaylistOperator.NotEquals => Loc.Get("Smart.NotEquals"),
                    SmartPlaylistOperator.GreaterOrEqual => Loc.Get("Smart.AtLeast"),
                    SmartPlaylistOperator.LessOrEqual => Loc.Get("Smart.AtMost"),
                    _ => item.ToString()
                })).ToArray();
            comparison.ItemsSource = options;
            comparison.SelectedItem = options.FirstOrDefault(item => item.Value == selected) ?? options[0];
        }

        public SmartPlaylistRule Rule()
        {
            if (field.SelectedItem is not Choice<SmartPlaylistField> selectedField ||
                comparison.SelectedItem is not Choice<SmartPlaylistOperator> selectedComparison)
                throw new ArgumentException("Choose a field and comparison for every rule.");
            return new(selectedField.Value, selectedComparison.Value, value.Text?.Trim() ?? "");
        }
    }
}
