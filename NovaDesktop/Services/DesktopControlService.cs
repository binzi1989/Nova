using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NovaDesktop.Services;

public sealed class DesktopControlService
{
    private const int RestoreWindow = 9;
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "windowsterminal", "conhost",
        "securityhealthsystray", "securityhealthservice", "credentialui",
        "1password", "keepass", "keepassxc", "bitwarden",
        "novadesktop", "nova agentos", "nova.agentos", "chatgpt", "codex"
    };
    private static readonly IReadOnlyDictionary<string, ushort> AllowedKeys =
        new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["ENTER"] = 0x0D,
            ["TAB"] = 0x09,
            ["ESCAPE"] = 0x1B,
            ["BACKSPACE"] = 0x08,
            ["LEFT"] = 0x25,
            ["UP"] = 0x26,
            ["RIGHT"] = 0x27,
            ["DOWN"] = 0x28,
            ["HOME"] = 0x24,
            ["END"] = 0x23,
            ["PAGEUP"] = 0x21,
            ["PAGEDOWN"] = 0x22,
            ["DELETE"] = 0x2E
        };

    public string ListWindows()
    {
        var windows = EnumerateWindows()
            .OrderBy(window => window.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .Take(120)
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            count = windows.Length,
            windows
        }, JsonOptions);
    }

    public string ActivateWindow(JsonObject arguments)
    {
        var requested = arguments["window_id"]?.GetValue<string>()?.Trim() ?? string.Empty;
        var window = EnumerateWindows().FirstOrDefault(
            item => item.WindowId.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The requested desktop window is no longer available.");

        ShowWindowAsync(window.Handle, RestoreWindow);
        if (!SetForegroundWindow(window.Handle))
        {
            throw new InvalidOperationException(
                "Windows declined the foreground switch. The window may require direct user interaction.");
        }
        return JsonSerializer.Serialize(new
        {
            status = "activated",
            window.WindowId,
            window.Title,
            window.ProcessName
        }, JsonOptions);
    }

    public string OpenBrowserUrl(JsonObject arguments)
    {
        var rawUrl = arguments["url"]?.GetValue<string>()?.Trim() ?? string.Empty;
        if (rawUrl.Length is 0 or > 2048
            || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException("Only absolute HTTPS URLs without embedded credentials are allowed.");
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return JsonSerializer.Serialize(new
        {
            status = "opened",
            url = uri.AbsoluteUri,
            host = uri.IdnHost
        });
    }

    public async Task<string> TypeTextAsync(
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var text = arguments["text"]?.GetValue<string>() ?? string.Empty;
        ValidateText(text);
        var window = GetAllowedTarget(arguments);
        await ActivateForInputAsync(window, cancellationToken);

        var inputs = new List<KeyboardInput>(text.Length * 2);
        foreach (var character in text)
        {
            inputs.Add(CreateUnicodeInput(character, keyUp: false));
            inputs.Add(CreateUnicodeInput(character, keyUp: true));
        }
        SendKeyboardInputs(inputs);
        return JsonSerializer.Serialize(new
        {
            status = "typed",
            window.WindowId,
            window.Title,
            characters = text.Length
        }, JsonOptions);
    }

    public async Task<string> SendKeyAsync(
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var keyName = arguments["key"]?.GetValue<string>()?.Trim() ?? string.Empty;
        if (!AllowedKeys.TryGetValue(keyName, out var virtualKey))
        {
            throw new InvalidOperationException(
                $"Key '{keyName}' is not enabled. Allowed keys: {string.Join(", ", AllowedKeys.Keys)}.");
        }
        var window = GetAllowedTarget(arguments);
        await ActivateForInputAsync(window, cancellationToken);
        SendKeyboardInputs(
        [
            CreateVirtualKeyInput(virtualKey, keyUp: false),
            CreateVirtualKeyInput(virtualKey, keyUp: true)
        ]);
        return JsonSerializer.Serialize(new
        {
            status = "key_sent",
            window.WindowId,
            window.Title,
            key = keyName.ToUpperInvariant()
        }, JsonOptions);
    }

    public async Task<string> ClickWindowPointAsync(
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var xRatio = arguments["x_ratio"]?.GetValue<double>() ?? double.NaN;
        var yRatio = arguments["y_ratio"]?.GetValue<double>() ?? double.NaN;
        if (!double.IsFinite(xRatio)
            || !double.IsFinite(yRatio)
            || xRatio is < 0.01 or > 0.99
            || yRatio is < 0.01 or > 0.99)
        {
            throw new InvalidOperationException(
                "Desktop click ratios must be between 0.01 and 0.99.");
        }

        var window = GetAllowedTarget(arguments);
        if (window.Bounds.Width < 80 || window.Bounds.Height < 60)
        {
            throw new InvalidOperationException(
                "The target window is too small for a bounded desktop click.");
        }
        await ActivateForInputAsync(window, cancellationToken);
        var x = window.Bounds.Left + (int)Math.Round(window.Bounds.Width * xRatio);
        var y = window.Bounds.Top + (int)Math.Round(window.Bounds.Height * yRatio);
        if (!SetCursorPos(x, y))
        {
            throw new InvalidOperationException(
                "Windows declined the bounded pointer move; no click was sent.");
        }
        await Task.Delay(60, cancellationToken);
        SendMouseInputs(
        [
            CreateMouseInput(MouseEventLeftDown),
            CreateMouseInput(MouseEventLeftUp)
        ]);
        return JsonSerializer.Serialize(new
        {
            status = "clicked",
            window.WindowId,
            window.Title,
            xRatio,
            yRatio,
            screenPoint = new { x, y },
            foregroundVerified = GetForegroundWindow() == window.Handle
        }, JsonOptions);
    }

    private static DesktopWindow GetAllowedTarget(JsonObject arguments)
    {
        var requested = arguments["window_id"]?.GetValue<string>()?.Trim() ?? string.Empty;
        var window = EnumerateWindows().FirstOrDefault(
            item => item.WindowId.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The requested desktop window is no longer available.");
        if (IsProtectedWindow(window.ProcessName, window.Title))
        {
            throw new InvalidOperationException(
                $"Text and key injection are blocked for protected process '{window.ProcessName}'.");
        }
        return window;
    }

    private static bool IsProtectedWindow(string processName, string title)
        => BlockedProcesses.Contains(processName)
           || title.Contains("NOVA AgentOS", StringComparison.OrdinalIgnoreCase)
           || title.Contains("Windows Security", StringComparison.OrdinalIgnoreCase)
           || title.Contains("Credential", StringComparison.OrdinalIgnoreCase)
           || title.Contains("密码管理", StringComparison.OrdinalIgnoreCase);

    private static async Task ActivateForInputAsync(
        DesktopWindow window,
        CancellationToken cancellationToken)
    {
        ShowWindowAsync(window.Handle, RestoreWindow);
        if (!SetForegroundWindow(window.Handle))
        {
            throw new InvalidOperationException(
                "Windows declined the foreground switch; no keyboard input was sent.");
        }
        await Task.Delay(120, cancellationToken);
    }

    private static void ValidateText(string text)
    {
        if (text.Length is 0 or > 1000)
        {
            throw new InvalidOperationException("Desktop text input must contain 1 to 1,000 characters.");
        }
        if (text.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                "Desktop text input may not contain control characters. Use send_window_key for navigation keys.");
        }
    }

    private static KeyboardInput CreateUnicodeInput(char character, bool keyUp)
        => new()
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInputData
                {
                    VirtualKey = 0,
                    ScanCode = character,
                    Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0),
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };

    private static KeyboardInput CreateVirtualKeyInput(ushort virtualKey, bool keyUp)
        => new()
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInputData
                {
                    VirtualKey = virtualKey,
                    ScanCode = 0,
                    Flags = keyUp ? KeyEventKeyUp : 0,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };

    private static KeyboardInput CreateMouseInput(uint flags)
        => new()
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInputData
                {
                    Flags = flags,
                    ExtraInfo = IntPtr.Zero
                }
            }
        };

    private static void SendKeyboardInputs(IReadOnlyList<KeyboardInput> inputs)
    {
        var array = inputs.ToArray();
        var sent = SendInput(
            (uint)array.Length,
            array,
            Marshal.SizeOf<KeyboardInput>());
        if (sent != array.Length)
        {
            throw new InvalidOperationException(
                $"Windows accepted only {sent}/{array.Length} keyboard input events.");
        }
    }

    private static void SendMouseInputs(IReadOnlyList<KeyboardInput> inputs)
    {
        var array = inputs.ToArray();
        var sent = SendInput(
            (uint)array.Length,
            array,
            Marshal.SizeOf<KeyboardInput>());
        if (sent != array.Length)
        {
            throw new InvalidOperationException(
                $"Windows accepted only {sent}/{array.Length} pointer input events.");
        }
    }

    private static IReadOnlyList<DesktopWindow> EnumerateWindows()
    {
        var windows = new List<DesktopWindow>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
            {
                return true;
            }

            var titleLength = GetWindowTextLength(handle);
            if (titleLength <= 0)
            {
                return true;
            }

            var titleBuilder = new StringBuilder(Math.Min(titleLength + 1, 4096));
            if (GetWindowText(handle, titleBuilder, titleBuilder.Capacity) <= 0)
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out var processId);
            var processName = "unknown";
            try
            {
                processName = Process.GetProcessById((int)processId).ProcessName;
            }
            catch
            {
                // The process may exit while windows are being enumerated.
            }

            if (!GetWindowRect(handle, out var rectangle))
            {
                return true;
            }
            windows.Add(new DesktopWindow(
                handle,
                $"0x{handle.ToInt64():X}",
                titleBuilder.ToString(),
                processName,
                processId,
                new DesktopBounds(
                    rectangle.Left,
                    rectangle.Top,
                    Math.Max(0, rectangle.Right - rectangle.Left),
                    Math.Max(0, rectangle.Bottom - rectangle.Top)),
                IsProtectedWindow(processName, titleBuilder.ToString())));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private sealed record DesktopWindow(
        [property: JsonIgnore] IntPtr Handle,
        string WindowId,
        string Title,
        string ProcessName,
        uint ProcessId,
        DesktopBounds Bounds,
        bool InputProtected);

    private sealed record DesktopBounds(
        int Left,
        int Top,
        int Width,
        int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInputData Keyboard;

        [FieldOffset(0)]
        public MouseInputData Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsDelegate(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsDelegate callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out WindowRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint numberOfInputs,
        [In] KeyboardInput[] inputs,
        int sizeOfInputStructure);
}
