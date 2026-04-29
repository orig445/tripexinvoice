using System.Text;
using Serilog;

namespace TripEx.Api;

/// <summary>
/// Bridges Console.WriteLine / Console.Error.WriteLine output into Serilog,
/// so that all existing [OCI] / [OCR] / [OCR-LOG] / [OCR-VALIDATE] / [OCR-DATE]
/// log lines are written to the daily-rolling file under /logs/.
/// Buffers partial writes until a newline is seen, then flushes one log entry per line.
/// </summary>
public sealed class SerilogTextWriter : TextWriter
{
    private readonly bool _isError;
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();

    public SerilogTextWriter(bool isError)
    {
        _isError = isError;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_lock)
        {
            if (value == '\n')
            {
                Flush();
            }
            else if (value != '\r')
            {
                _buffer.Append(value);
            }
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
        // Caller already holds _lock when invoked from Write/WriteLine above.
        // External Flush() calls are also safe because StringBuilder ops are short.
        var line = _buffer.ToString();
        _buffer.Clear();
        if (string.IsNullOrWhiteSpace(line)) return;

        if (_isError) Log.Error("{ConsoleLine}", line);
        else Log.Information("{ConsoleLine}", line);
    }
}
