using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OpenSkiTime.Desktop.Dialogs;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        AvaloniaXamlLoader.Load(this);
        var titleBlock = this.FindControl<TextBlock>("TitleBlock")!;
        var messageBlock = this.FindControl<TextBlock>("MessageBlock")!;
        var confirmButton = this.FindControl<Button>("ConfirmButton")!;
        var cancelButton = this.FindControl<Button>("CancelButton")!;

        confirmButton.Click += (_, _) => Close(true);
        cancelButton.Click += (_, _) => Close(false);

        _titleBlock = titleBlock;
        _messageBlock = messageBlock;
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
