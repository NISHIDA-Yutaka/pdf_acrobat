using System.Windows;

namespace PdfAcrobat.App.Views.Dialogs;

public partial class PasswordDialog
{
    public PasswordDialog(string fileName, bool retry)
    {
        InitializeComponent();
        MessageText.Text = $"「{fileName}」はパスワードで保護されています。文書を開くパスワードを入力してください。";
        ErrorText.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => Box.Focus();
    }

    public string Password => Box.Password;

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
