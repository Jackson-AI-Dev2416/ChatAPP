using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Ts3Messenger.Services;

namespace Ts3Messenger.Controls;

public partial class EmojiPicker : UserControl
{
    private readonly List<ToggleButton> _tabButtons = new();
    private int _activeIndex = -1;

    public event EventHandler<string>? EmojiSelected;

    public EmojiPicker()
    {
        InitializeComponent();
        Loaded += (_, _) => BuildCategories();
    }

    private void BuildCategories()
    {
        CategoryTabs.Children.Clear();
        _tabButtons.Clear();
        var categories = EmojiCatalog.All;
        for (var i = 0; i < categories.Count; i++)
        {
            var cat = categories[i];
            var tab = new ToggleButton
            {
                Content = new Emoji.Wpf.TextBlock
                {
                    Text = cat.TabIcon,
                    FontSize = 24,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                Style = (Style)FindResource("CategoryTabStyle"),
                ToolTip = cat.Title,
            };
            var index = i;
            tab.Click += (_, _) => SelectCategory(index);
            CategoryTabs.Children.Add(tab);
            _tabButtons.Add(tab);
        }
        SelectCategory(0);
    }

    private void SelectCategory(int index)
    {
        if (index < 0 || index >= EmojiCatalog.All.Count) return;
        _activeIndex = index;
        for (var i = 0; i < _tabButtons.Count; i++)
            _tabButtons[i].IsChecked = i == index;

        var cat = EmojiCatalog.All[index];
        CategoryTitle.Text = cat.Title;
        EmojiPanel.Children.Clear();
        foreach (var emoji in cat.Emojis)
        {
            var btn = new Button
            {
                Content = new Emoji.Wpf.TextBlock
                {
                    Text = emoji,
                    FontSize = 30,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
                Style = (Style)FindResource("EmojiButtonStyle"),
                ToolTip = emoji,
            };
            btn.Click += (_, _) => EmojiSelected?.Invoke(this, emoji);
            EmojiPanel.Children.Add(btn);
        }
    }
}
