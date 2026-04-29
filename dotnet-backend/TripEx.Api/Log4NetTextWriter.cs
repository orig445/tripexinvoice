using System.Text;
using log4net;

namespace TripEx.Api;

/// <summary>
/// Bridges Console.WriteLine / Console.Error.WriteLine output into log4net,
/// so all existing [OCI] / [OCR] / [OCR-LOG] / [OCR-VALIDATE] / [OCR-DATE]
/// log lines are written to the daily-rolling file under /logs/.
/// </summary>
public sealed class Log4NetTextWriter : TextWriter
{
    private static readonly ILog _log = LogManager.GetLogger("Console");
    private readonly bool _isError;
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();

    public Log4NetTextWriter(bool isError)
    {
        _isError = isError;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_lock)
        {
            if (value == '\n') Flush();
            else if (value != '\r') _buffer.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (_lock)
        {
            foreach (var ch in value)
            {
                if (ch == '\n') Flush();
                else if (ch != '\r') _buffer.Append(ch);
            }
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(value)) _buffer.Append(value);
            Flush();
        }
    }

    public override void Flush()
    {
        var line = _buffer.ToString();
        _buffer.Clear();
        if (string.IsNullOrWhiteSpace(line)) return;

        if (_isError) _log.Error(line);
        else _log.Info(line);
    }
}
