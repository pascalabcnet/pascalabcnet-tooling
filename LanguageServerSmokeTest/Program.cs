using System.Diagnostics;
using System.Text;
using System.Text.Json;

const string source = """
program HeadlessSmoke;

var
  text: string;

begin
  text := 'PascalABC.NET';
  var result := text.Substring(1);
  Writeln(result);
end.
""";

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var serverAssembly = Path.Combine(
    repositoryRoot,
    "PascalABCNet.LanguageServer",
    "bin",
    "Debug",
    "net10.0",
    "PascalABCNet.LanguageServer.dll");
Check(File.Exists(serverAssembly), $"Language server assembly exists: {serverAssembly}");

using var process = new Process
{
    StartInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = repositoryRoot
    }
};
process.StartInfo.ArgumentList.Add(serverAssembly);
process.StartInfo.ArgumentList.Add("--stdio");
process.StartInfo.ArgumentList.Add("--documentation-language");
process.StartInfo.ArgumentList.Add("en");
Check(process.Start(), "Language server process started");

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var input = process.StandardInput.BaseStream;
var output = process.StandardOutput.BaseStream;
var documentUri = "file:///C:/HeadlessSmoke.pas";

await WriteMessageAsync(input, new
{
    jsonrpc = "2.0",
    id = 1,
    method = "initialize",
    @params = new
    {
        processId = Environment.ProcessId,
        rootUri = "file:///C:/",
        capabilities = new { }
    }
}, timeout.Token);
using var initialize = await ReadResponseAsync(output, 1, timeout.Token);
Check(initialize.RootElement.GetProperty("result").GetProperty("capabilities")
    .GetProperty("completionProvider").GetProperty("triggerCharacters")[0].GetString() == ".",
    "initialize advertises completion after dot");
Console.WriteLine("PASS initialize over stdio");

await WriteMessageAsync(input, new
{
    jsonrpc = "2.0",
    method = "initialized",
    @params = new { }
}, timeout.Token);

await WriteMessageAsync(input, new
{
    jsonrpc = "2.0",
    method = "textDocument/didOpen",
    @params = new
    {
        textDocument = new
        {
            uri = documentUri,
            languageId = "pascal",
            version = 1,
            text = source
        }
    }
}, timeout.Token);

const string memberAccess = "text.Substring";
var memberStart = source.IndexOf(memberAccess, StringComparison.Ordinal);
Check(memberStart >= 0, "Completion marker exists");
var dotCaretOffset = memberStart + "text.".Length;
var dotPosition = GetPosition(source, dotCaretOffset);

await WriteRequestAsync(input, 2, "textDocument/completion", documentUri, dotPosition, timeout.Token);
using var completion = await ReadResponseAsync(output, 2, timeout.Token);
Check(completion.RootElement.GetProperty("result").GetProperty("items")
    .EnumerateArray().Any(item => item.GetProperty("label").GetString() == "Substring"),
    "completion contains Substring");
Console.WriteLine("PASS completion over stdio");

var hoverOffset = memberStart + "text.".Length + 1;
var hoverPosition = GetPosition(source, hoverOffset);
await WriteRequestAsync(input, 3, "textDocument/hover", documentUri, hoverPosition, timeout.Token);
using var hover = await ReadResponseAsync(output, 3, timeout.Token);
Check(hover.RootElement.GetProperty("result").GetProperty("contents").GetProperty("value")
    .GetString()?.Contains("Substring", StringComparison.OrdinalIgnoreCase) == true,
    "hover describes Substring");
Console.WriteLine("PASS hover over stdio");

var signatureOffset = memberStart + memberAccess.Length + 1;
var signaturePosition = GetPosition(source, signatureOffset);
await WriteRequestAsync(input, 4, "textDocument/signatureHelp", documentUri, signaturePosition, timeout.Token);
using var signature = await ReadResponseAsync(output, 4, timeout.Token);
Check(signature.RootElement.GetProperty("result").GetProperty("signatures")
    .EnumerateArray().Any(item => item.GetProperty("label").GetString()
        ?.Contains("Substring", StringComparison.OrdinalIgnoreCase) == true),
    "signature help describes Substring");
Console.WriteLine("PASS signature help over stdio");

await WriteMessageAsync(input, new
{
    jsonrpc = "2.0",
    method = "textDocument/didClose",
    @params = new { textDocument = new { uri = documentUri } }
}, timeout.Token);

await WriteMessageAsync(input, new
{
    jsonrpc = "2.0",
    id = 5,
    method = "shutdown"
}, timeout.Token);
using var shutdown = await ReadResponseAsync(output, 5, timeout.Token);
Check(shutdown.RootElement.TryGetProperty("result", out var shutdownResult) &&
      shutdownResult.ValueKind == JsonValueKind.Null,
    "shutdown returned null result");

await WriteMessageAsync(input, new { jsonrpc = "2.0", method = "exit" }, timeout.Token);
await process.WaitForExitAsync(timeout.Token);
var errorOutput = await process.StandardError.ReadToEndAsync(timeout.Token);
Check(process.ExitCode == 0, $"Language server exited cleanly. stderr: {errorOutput}");
Console.WriteLine("PASS shutdown and exit over stdio");
Console.WriteLine("All LSP smoke checks passed.");

static async Task WriteRequestAsync(
    Stream stream,
    int id,
    string method,
    string documentUri,
    (int Line, int Character) position,
    CancellationToken cancellationToken)
{
    await WriteMessageAsync(stream, new
    {
        jsonrpc = "2.0",
        id,
        method,
        @params = new
        {
            textDocument = new { uri = documentUri },
            position = new { line = position.Line, character = position.Character }
        }
    }, cancellationToken);
}

static async Task WriteMessageAsync(Stream stream, object message, CancellationToken cancellationToken)
{
    var body = JsonSerializer.SerializeToUtf8Bytes(message);
    var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
    await stream.WriteAsync(header, cancellationToken);
    await stream.WriteAsync(body, cancellationToken);
    await stream.FlushAsync(cancellationToken);
}

static async Task<JsonDocument> ReadResponseAsync(
    Stream stream,
    int expectedId,
    CancellationToken cancellationToken)
{
    var header = new List<byte>();
    while (header.Count < 4 ||
           header[^4] != '\r' || header[^3] != '\n' ||
           header[^2] != '\r' || header[^1] != '\n')
    {
        var oneByte = new byte[1];
        var read = await stream.ReadAsync(oneByte, cancellationToken);
        if (read == 0)
            throw new EndOfStreamException("Language server closed stdout before sending a response.");
        header.Add(oneByte[0]);
    }

    var headerText = Encoding.ASCII.GetString(header.ToArray());
    var contentLengthLine = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
        .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
    var contentLength = int.Parse(contentLengthLine["Content-Length:".Length..].Trim());
    var body = new byte[contentLength];
    var offset = 0;
    while (offset < contentLength)
    {
        var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), cancellationToken);
        if (read == 0)
            throw new EndOfStreamException("Language server closed stdout inside a response.");
        offset += read;
    }

    var response = JsonDocument.Parse(body);
    if (response.RootElement.TryGetProperty("error", out var error))
        throw new InvalidOperationException($"LSP request {expectedId} failed: {error}");
    Check(response.RootElement.GetProperty("id").GetInt32() == expectedId,
        $"Received response for request {expectedId}");
    return response;
}

static (int Line, int Character) GetPosition(string text, int offset)
{
    var line = 0;
    var character = 0;
    for (var index = 0; index < offset; index++)
    {
        if (text[index] == '\n')
        {
            line++;
            character = 0;
        }
        else
        {
            character++;
        }
    }

    return (line, character);
}

static string FindRepositoryRoot(string startDirectory)
{
    for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
    {
        if (Directory.Exists(Path.Combine(directory.FullName, "PascalABCNet.LanguageServer")))
            return directory.FullName;
    }

    throw new DirectoryNotFoundException("Could not locate the tooling repository root.");
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException($"Smoke check failed: {message}");
}
