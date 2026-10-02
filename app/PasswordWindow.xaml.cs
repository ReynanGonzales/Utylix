using System.Windows;

namespace IdmClone;

/// <summary>Asks for an archive's (or a PDF's) password.</summary>
public partial class PasswordWindow : Window
{
    public string Password => Box.Password;

    public PasswordWindow(string archiveName, bool wrong, string message = "This archive needs a password")
    {
        InitializeComponent();
        WindowTheme.DarkTitleBar(this);
        Message.Text = message;
        Detail.Text = archiveName;
        if (wrong) { ErrorText.Text = "That password is not right. Try again."; ErrorText.Visibility = Visibility.Visible; }
        Loaded += (_, _) => Box.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Box.Password.Length == 0) return;
        DialogResult = true;
    }
}
