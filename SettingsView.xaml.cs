using System.Windows;
using System.Windows.Controls;

namespace GameRecommender
{
    public partial class SettingsView : UserControl
    {
        private readonly SettingsViewModel viewModel;

        public SettingsView(SettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            this.viewModel = viewModel;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            IgdbSecretBox.Password = viewModel.IgdbClientSecret;
            RawgKeyBox.Password = viewModel.RawgApiKey;
            AnthropicKeyBox.Password = viewModel.AnthropicApiKey;
            OpenAiKeyBox.Password = viewModel.OpenAiApiKey;
            ItadKeyBox.Password = viewModel.ItadApiKey;
            IgdbSecretBox.PasswordChanged += (s, args) => viewModel.IgdbClientSecret = IgdbSecretBox.Password;
            RawgKeyBox.PasswordChanged += (s, args) => viewModel.RawgApiKey = RawgKeyBox.Password;
            AnthropicKeyBox.PasswordChanged += (s, args) => viewModel.AnthropicApiKey = AnthropicKeyBox.Password;
            OpenAiKeyBox.PasswordChanged += (s, args) => viewModel.OpenAiApiKey = OpenAiKeyBox.Password;
            ItadKeyBox.PasswordChanged += (s, args) => viewModel.ItadApiKey = ItadKeyBox.Password;
        }
    }
}
