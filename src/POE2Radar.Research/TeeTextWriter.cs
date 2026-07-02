using System.Text;

namespace POE2Radar.Research;

/// <summary>Writes to two <see cref="TextWriter"/>s at once (console + report file) so the
/// <c>--atlas-diag</c> diagnostic captures everything it prints. Used only by that mode.</summary>
internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _a, _b;
    public TeeTextWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
    public override Encoding Encoding => _a.Encoding;
    public override void Write(char value) { _a.Write(value); _b.Write(value); }
    public override void Write(string? value) { _a.Write(value); _b.Write(value); }
    public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
    public override void Flush() { _a.Flush(); _b.Flush(); }
}
