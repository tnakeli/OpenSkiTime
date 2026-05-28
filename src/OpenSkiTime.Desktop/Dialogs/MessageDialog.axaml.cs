using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenSkiTime.Desktop.Dialogs;

public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        AvaloniaXamlLoader.Load(this);
        _titleBlock = this.FindControl<TextBlock>("TitleBlock")!;
        _messageBlock = this.FindControl<TextBlock>("MessageBlock")!;
        var okButton = this.FindControl<Button>("OkButton")!;
        okButton.Click += (_, _) => Close();
    }

    private readonly TextBlock _titleBlock;
    private readonly TextBlock _messageBlock;

    public void Configure(string title, string message)
    {
        Title = title;
        _titleBlock.Text = title;
        _messageBlock.Text = message;
    }
}
