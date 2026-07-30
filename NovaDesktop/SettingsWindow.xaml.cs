using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NovaDesktop;

public partial class SettingsWindow : Window
{
    private readonly bool _openAiConfigured;
    private readonly bool _deepSeekConfigured;
    private readonly bool _kimiConfigured;
    private readonly bool _openAiPersisted;
    private readonly bool _deepSeekPersisted;
    private readonly bool _kimiPersisted;
    private readonly string _initialModel;

    public SettingsWindow(
        string selectedProvider,
        string selectedModel,
        bool openAiConfigured,
        bool deepSeekConfigured,
        bool kimiConfigured,
        bool openAiPersisted,
        bool deepSeekPersisted,
        bool kimiPersisted)
    {
        InitializeComponent();
        _openAiConfigured = openAiConfigured;
        _deepSeekConfigured = deepSeekConfigured;
        _kimiConfigured = kimiConfigured;
        _openAiPersisted = openAiPersisted;
        _deepSeekPersisted = deepSeekPersisted;
        _kimiPersisted = kimiPersisted;
        _initialModel = selectedModel;

        foreach (var item in ProviderBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), selectedProvider, StringComparison.OrdinalIgnoreCase))
            {
                ProviderBox.SelectedItem = item;
                break;
            }
        }
        ProviderBox.SelectedIndex = ProviderBox.SelectedIndex < 0 ? 0 : ProviderBox.SelectedIndex;
        RefreshModels(_initialModel);
        UpdateKeyHint();
        UpdateRememberState();
    }

    public string? ApiKey { get; private set; }
    public string SelectedProvider { get; private set; } = "openai";
    public string SelectedModel { get; private set; } = "gpt-5.6";
    public bool ClearRequested { get; private set; }
    public bool RememberKey { get; private set; }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var enteredKey = ApiKeyBox.Password.Trim();
        if (!HasExistingKeyForSelection() && string.IsNullOrWhiteSpace(enteredKey))
        {
            ValidationText.Text = "请输入 API 密钥。未连接模型时 NOVA 不会启动任务。";
            ApiKeyBox.Focus();
            return;
        }

        if (SelectedProvider == "openai"
            && !string.IsNullOrWhiteSpace(enteredKey)
            && !enteredKey.StartsWith("sk-", StringComparison.Ordinal))
        {
            ValidationText.Text = "OpenAI 密钥格式应以 sk- 开头。";
            ApiKeyBox.Focus();
            return;
        }

        ApiKey = string.IsNullOrWhiteSpace(enteredKey) ? null : enteredKey;
        SelectedModel = (ModelBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gpt-5.6";
        RememberKey = RememberKeyBox.IsChecked == true;
        DialogResult = true;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var enteredKey = ApiKeyBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(enteredKey))
        {
            ValidationText.Text = HasExistingKeyForSelection()
                ? "出于安全原因，测试连接需要重新输入一次密钥；不会保存。"
                : "请先输入 API 密钥。";
            ApiKeyBox.Focus();
            return;
        }

        TestConnectionButton.IsEnabled = false;
        TestConnectionButton.Content = "正在验证…";
        ValidationText.Foreground = new SolidColorBrush(Color.FromRgb(126, 143, 169));
        ValidationText.Text = "正在验证提供商凭据与模型访问权限…";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", enteredKey);
            var model = (ModelBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()
                        ?? SelectedModel;
            var endpoint = SelectedProvider switch
            {
                "deepseek" => "https://api.deepseek.com/models",
                "kimi" => "https://api.moonshot.cn/v1/models",
                _ => $"https://api.openai.com/v1/models/{Uri.EscapeDataString(model)}"
            };
            using var response = await client.GetAsync(endpoint);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync();
                ValidationText.Foreground = new SolidColorBrush(Color.FromRgb(255, 112, 143));
                ValidationText.Text =
                    $"连接失败（HTTP {(int)response.StatusCode}）：{Limit(detail, 150)}";
                return;
            }

            ValidationText.Foreground = new SolidColorBrush(Color.FromRgb(107, 229, 169));
            ValidationText.Text = $"连接已验证 · {SelectedProvider} · {model}";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            ValidationText.Foreground = new SolidColorBrush(Color.FromRgb(255, 112, 143));
            ValidationText.Text = $"连接测试失败：{exception.Message}";
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
            TestConnectionButton.Content = "测试连接";
        }
    }

    private void PreviewMode_Click(object sender, RoutedEventArgs e)
    {
        ClearRequested = true;
        ApiKey = string.Empty;
        RememberKey = false;
        DialogResult = true;
    }

    private void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedModel = (ModelBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gpt-5.6";
    }

    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedProvider = (ProviderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "openai";
        if (!IsLoaded)
        {
            return;
        }

        ApiKeyBox.Clear();
        ValidationText.Text = string.Empty;
        RefreshModels(null);
        UpdateKeyHint();
        UpdateRememberState();
    }

    private void RefreshModels(string? preferredModel)
    {
        ModelBox.Items.Clear();
        var models = SelectedProvider switch
        {
            "deepseek" => new[]
            {
                ("deepseek-v4-flash", "DeepSeek V4 Flash · 高速经济"),
                ("deepseek-v4-pro", "DeepSeek V4 Pro · 复杂任务")
            },
            "kimi" => new[]
            {
                ("kimi-k3", "Kimi K3 · 旗舰 Agent 与视觉理解"),
                ("kimi-k2.6", "Kimi K2.6 · 多模态与深度思考")
            },
            _ => new[]
            {
                ("gpt-5.6", "GPT-5.6 Sol · 旗舰能力"),
                ("gpt-5.6-terra", "GPT-5.6 Terra · 平衡质量与成本"),
                ("gpt-5.6-luna", "GPT-5.6 Luna · 高速经济")
            }
        };

        foreach (var (id, label) in models)
        {
            ModelBox.Items.Add(new ComboBoxItem { Tag = id, Content = label });
        }

        var selected = ModelBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), preferredModel, StringComparison.OrdinalIgnoreCase));
        ModelBox.SelectedItem = selected ?? ModelBox.Items[0];
    }

    private void UpdateKeyHint()
    {
        var providerLabel = SelectedProvider switch
        {
            "deepseek" => "DeepSeek",
            "kimi" => "Kimi",
            _ => "OpenAI"
        };
        if (HasExistingKeyForSelection())
        {
            KeyHint.Text = IsPersistedForSelection()
                ? $"{providerLabel} 密钥已由 Windows 凭据管理器保护。留空可继续使用。"
                : $"{providerLabel} 密钥已在当前进程中连接。留空可继续使用。";
            KeyHint.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        else
        {
            KeyHint.Text = $"输入 {providerLabel} API Key；不会写入设置、日志或任务记录。";
            KeyHint.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(102, 113, 136));
        }
    }

    private bool HasExistingKeyForSelection()
        => SelectedProvider switch
        {
            "deepseek" => _deepSeekConfigured,
            "kimi" => _kimiConfigured,
            _ => _openAiConfigured
        };

    private bool IsPersistedForSelection()
        => SelectedProvider switch
        {
            "deepseek" => _deepSeekPersisted,
            "kimi" => _kimiPersisted,
            _ => _openAiPersisted
        };

    private void UpdateRememberState()
        => RememberKeyBox.IsChecked = IsPersistedForSelection();

    private static string Limit(string value, int maximum)
    {
        var compact = string.Join(
            " ",
            value.Split(
                ['\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return compact.Length <= maximum ? compact : compact[..maximum] + "…";
    }
}
