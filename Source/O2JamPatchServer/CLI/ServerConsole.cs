using System.Runtime.InteropServices;
using System.Text;
using Encore.CLI;

namespace O2JamPatchServer.CLI;

public sealed class ServerConsole
{
    private static readonly string Prompt = IsLegacyConsole() ? "> " : "❯ ";

    private readonly Lock _lock = new();
    private readonly TextWriter _output;
    private readonly StringBuilder _buffer = new();
    private readonly List<string> _history = [];
    private readonly Timer? _resize;
    private int _historyIndex;
    private int _cursor;
    private int _offset;
    private int _width;
    private int _column;
    private bool _active;
    private bool _visible;
    private bool _closed;

    private ServerConsole(TextWriter output, bool interactive)
    {
        _output = output;
        IsInteractive = interactive;

        // Consoles don't signal a resize, the width is polled to keep the rules spanning the window
        if (interactive)
            _resize = new Timer(_ => Resize(), null, 250, 250);
    }

    public bool IsInteractive { get; }

    public string Placeholder { get; set; } = string.Empty;

    public static ServerConsole Attach()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected || !EnableVirtualTerminal())
            return new ServerConsole(Console.Out, interactive: false);

        // Writes through WriteConsoleW, the prompt characters are missing from most console code pages
        if (OperatingSystem.IsWindows())
            Console.OutputEncoding = Encoding.Unicode;

        var console = new ServerConsole(Console.Out, interactive: true);
        Console.SetOut(new PromptWriter(console, Console.Out));
        Console.SetError(new PromptWriter(console, Console.Error));

        return console;
    }

    public string? ReadLine()
    {
        if (!IsInteractive)
            return Console.In.ReadLine();

        lock (_lock)
        {
            if (_closed)
                return null;

            _active = true;
            Draw();
        }

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            lock (_lock)
            {
                if (_closed)
                    return null;

                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        string line = _buffer.ToString();
                        Clear();
                        _output.Write($"{Prompt}{line}{Environment.NewLine}");

                        if (!string.IsNullOrWhiteSpace(line) && (_history.Count == 0 || _history[^1] != line))
                            _history.Add(line);

                        _historyIndex = _history.Count;
                        SetBuffer(string.Empty);
                        Draw();
                        return line;
                    case ConsoleKey.Backspace when _cursor > 0:
                        _buffer.Remove(--_cursor, 1);
                        break;
                    case ConsoleKey.Delete when _cursor < _buffer.Length:
                        _buffer.Remove(_cursor, 1);
                        break;
                    case ConsoleKey.LeftArrow when _cursor > 0:
                        _cursor--;
                        break;
                    case ConsoleKey.RightArrow when _cursor < _buffer.Length:
                        _cursor++;
                        break;
                    case ConsoleKey.Home:
                        _cursor = 0;
                        break;
                    case ConsoleKey.End:
                        _cursor = _buffer.Length;
                        break;
                    case ConsoleKey.Escape:
                        SetBuffer(string.Empty);
                        break;
                    case ConsoleKey.UpArrow when _historyIndex > 0:
                        SetBuffer(_history[--_historyIndex]);
                        break;
                    case ConsoleKey.DownArrow when _historyIndex < _history.Count:
                        _historyIndex++;
                        SetBuffer(_historyIndex < _history.Count ? _history[_historyIndex] : string.Empty);
                        break;
                    default:
                        if (!char.IsControl(key.KeyChar))
                            _buffer.Insert(_cursor++, key.KeyChar);
                        break;
                }

                Draw();
            }
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            Clear();
            _active = false;
            _closed = true;
        }

        _resize?.Dispose();
    }

    private void Write(TextWriter writer, string text)
    {
        lock (_lock)
        {
            Clear();
            writer.Write(text);
            writer.Flush();

            if (_active)
                Draw();
        }
    }

    private void Resize()
    {
        lock (_lock)
        {
            if (_visible && Width != _width)
                Draw();
        }
    }

    private void SetBuffer(string text)
    {
        _buffer.Clear().Append(text);
        _cursor = text.Length;
        _offset = 0;
    }

    private void Draw()
    {
        Clear();

        int width = Width;
        int space = width - Prompt.Length - 1;
        if (_cursor < _offset)
            _offset = _cursor;
        else if (_cursor - _offset > space)
            _offset = _cursor - space;

        string text = _buffer.Length > 0
            ? _buffer.ToString(_offset, Math.Min(space, _buffer.Length - _offset))
            : Placeholder[..Math.Min(space, Placeholder.Length)].WithConsoleColor(ConsoleColor.DarkGray);
        string rule = new string('─', width).WithConsoleColor(ConsoleColor.DarkGray);

        _width = width;
        _column = Prompt.Length + _cursor - _offset;
        _visible = true;

        _output.Write($"{rule}\r\n{Prompt}{text}\r\n{rule}\u001b[1A\r\u001b[{_column}C");
        _output.Flush();
    }

    private static int Width => Math.Max(Console.WindowWidth, 16);

    private void Clear()
    {
        if (!_visible)
            return;

        // The terminal re-wraps the rules when the window shrinks
        int window = Math.Max(Console.WindowWidth, 1);
        int rows = (_width + window - 1) / window + _column / window;

        _output.Write($"\u001b[{rows}A\r\u001b[J");
        _output.Flush();
        _visible = false;
    }

    private static bool EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        nint handle = GetStdHandle(-11);
        return GetConsoleMode(handle, out uint mode) && SetConsoleMode(handle, mode | 0x0004);
    }

    // Legacy conhost draws its own window, its default fonts have no ❯
    private static bool IsLegacyConsole()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var name = new StringBuilder(64);
        return GetClassName(GetConsoleWindow(), name, name.Capacity) > 0 && name.ToString() == "ConsoleWindowClass";
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(nint handle, out uint mode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(nint handle, uint mode);

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int length);

    private sealed class PromptWriter(ServerConsole console, TextWriter inner) : TextWriter
    {
        private readonly StringBuilder _pending = new();

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            _pending.Append(value);
            if (value == '\n')
                Flush();
        }

        public override void Write(string? value)
        {
            _pending.Append(value);
            if (value != null && value.Contains('\n'))
                Flush();
        }

        public override void Write(char[] buffer, int index, int count)
            => Write(new string(buffer, index, count));

        public override void Flush()
        {
            string text = _pending.ToString();
            int end = text.LastIndexOf('\n') + 1;
            if (end == 0)
                return;

            _pending.Remove(0, end);
            console.Write(inner, text[..end]);
        }
    }
}
