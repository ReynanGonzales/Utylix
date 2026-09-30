using System.Windows;

namespace IdmClone;

/// <summary>Asks for an archive's password.</summary>
public partial class PasswordWindow : Window
{
    public string Password => Box.Password;

    public PasswordWindow(string archiveName, bool wrong)
    {
        InitializeComponent();
        WindowTheme.DarkTitleBar(this);
        Message.Text = "This archive needs a password";
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
