using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using NovaDesktop.Services;
using NovaDesktop.ViewModels;

namespace NovaDesktop;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private CrashRecoveryService? _recovery;
    private bool _fatalException;
    private bool _automatedSmoke;
    private bool _presentationRecoveryNoticeShown;
    private string? _attachmentSmokePath;

    public static bool HadUncleanShutdown { get; private set; }
    public static string CrashDirectory { get; private set; } = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        var startupSmoke = e.Args.Contains(
            "--startup-smoke",
            StringComparer.OrdinalIgnoreCase);
        var extensionCenterSmoke = e.Args.Contains(
            "--extension-center-smoke",
            StringComparer.OrdinalIgnoreCase);
        var attachmentRenderSmoke = e.Args.Contains(
            "--attachment-render-smoke",
            StringComparer.OrdinalIgnoreCase);
        _automatedSmoke = startupSmoke || extensionCenterSmoke || attachmentRenderSmoke;
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\NOVA.Desktop.Singleton",
            createdNew: out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "NOVA 已经在运行。请切换到现有窗口。",
                "NOVA",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(2);
            return;
        }

        _recovery = new CrashRecoveryService();
        HadUncleanShutdown = _recovery.HadUncleanShutdown;
        CrashDirectory = _recovery.CrashDirectory;
        _recovery.StartSession();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        if (attachmentRenderSmoke)
        {
            _attachmentSmokePath = Path.Combine(
                Path.GetTempPath(),
                "nova-attachment-render-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(
                _attachmentSmokePath,
                Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z9t8AAAAASUVORK5CYII="));
            if (mainWindow.DataContext is MainViewModel viewModel)
            {
                viewModel.AddInputAttachments([_attachmentSmokePath]);
            }
            mainWindow.ContentRendered += (_, _) =>
                Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    () => mainWindow.Close());
        }
        else if (startupSmoke)
        {
            mainWindow.ContentRendered += (_, _) =>
                Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    () => mainWindow.Close());
        }
        else if (extensionCenterSmoke)
        {
            mainWindow.ContentRendered += (_, _) =>
            {
                var smokeRoot = Path.Combine(
                    Path.GetTempPath(),
                    "nova-extension-smoke-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(smokeRoot);
                var extensionCenter = new ExtensionCenterWindow(
                    new McpRegistryService(Path.Combine(smokeRoot, "mcp.json")),
                    new SkillRegistryService(Path.Combine(smokeRoot, "skills")),
                    Environment.CurrentDirectory,
                    "检查当前工程并在后台调研公开资料");
                extensionCenter.Owner = mainWindow;
                extensionCenter.ContentRendered += (_, _) =>
                    Dispatcher.BeginInvoke(
                        DispatcherPriority.ApplicationIdle,
                        () =>
                        {
                            extensionCenter.Close();
                            mainWindow.Close();
                            try
                            {
                                Directory.Delete(smokeRoot, recursive: true);
                            }
                            catch
                            {
                                // Smoke cleanup is best-effort and never affects user data.
                            }
                        });
                extensionCenter.Show();
            };
        }
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_fatalException)
        {
            _recovery?.MarkCleanExit();
        }
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // A non-owning second instance never acquired the mutex.
            }
            _singleInstanceMutex.Dispose();
        }
        if (!string.IsNullOrWhiteSpace(_attachmentSmokePath))
        {
            try
            {
                File.Delete(_attachmentSmokePath);
            }
            catch
            {
                // The isolated smoke attachment is best-effort cleanup only.
            }
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        if (IsRecoverablePresentationBindingException(e.Exception))
        {
            var recoveryReport = _recovery?.WriteCrashReport(
                e.Exception,
                "WPF presentation binding",
                fatal: false);
            e.Handled = true;
            if (!_automatedSmoke && !_presentationRecoveryNoticeShown)
            {
                _presentationRecoveryNoticeShown = true;
                Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    () => MessageBox.Show(
                        $"一个界面组件没有正确显示，但任务和会话仍然安全，NOVA 已继续运行。\n\n诊断报告：{recoveryReport ?? CrashDirectory}",
                        "NOVA 已恢复界面",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning));
            }
            return;
        }

        _fatalException = true;
        var reportPath = _recovery?.WriteCrashReport(e.Exception, "WPF Dispatcher", fatal: true);
        e.Handled = true;
        if (_automatedSmoke)
        {
            Shutdown(-1);
            return;
        }
        try
        {
            MessageBox.Show(
                $"NOVA 遇到无法安全恢复的错误，诊断报告已保存。\n\n{reportPath ?? CrashDirectory}",
                "NOVA 已安全停止",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Shutdown(-1);
        }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _fatalException |= e.IsTerminating;
            _recovery?.WriteCrashReport(exception, "AppDomain", e.IsTerminating);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _recovery?.WriteCrashReport(e.Exception, "TaskScheduler", fatal: false);
        e.SetObserved();
    }

    private static bool IsRecoverablePresentationBindingException(Exception exception)
    {
        if (exception is not XamlParseException)
        {
            return false;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException
                && current.Message.Contains("只读属性", StringComparison.Ordinal)
                && current.Message.Contains("绑定", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
