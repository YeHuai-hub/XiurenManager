using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace XiurenManager;

public partial class TextInputWindow : FluentWindow
{
    public string ResultText { get; private set; } = "";

    public TextInputWindow(string title, string prompt, string value)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueEditor.Text = value;
        Loaded += (_, _) =>
        {
            ValueEditor.Focus();
            ValueEditor.SelectAll();
        };
    }

    private void Confirm_OnClick(object sender, RoutedEventArgs e)
    {
        var value = ValueEditor.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            ValidationText.Text = "名称不能为空";
            return;
        }
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ValidationText.Text = "名称包含 Windows 不允许的字符";
            return;
        }

        ResultText = value;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ValueEditor_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm_OnClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_OnClick(sender, e);
            e.Handled = true;
        }
    }
}
