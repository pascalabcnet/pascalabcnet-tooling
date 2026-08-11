using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PascalABCNet.LanguageServer;

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

var repositoryRoot = FindRepositoryRoot(Environment.CurrentDirectory);
var serverAssembly = Environment.GetEnvironmentVariable("PABC_TOOLING_SERVER_ASSEMBLY") ??
    Path.Combine(
        repositoryRoot,
        "PascalABCNet.LanguageServer",
        "bin",
        "Debug",
        "net10.0",
        "PascalABCNet.LanguageServer.dll");

var firstSyntheticUri = new Uri("untitled:VirtualOne.pas");
var secondSyntheticUri = new Uri("untitled:VirtualTwo.pas");
var firstSyntheticFileName = DocumentConversions.GetFileName(firstSyntheticUri);
var secondSyntheticFileName = DocumentConversions.GetFileName(secondSyntheticUri);
Check(
    firstSyntheticFileName.EndsWith(".pas", StringComparison.OrdinalIgnoreCase) &&
    secondSyntheticFileName.EndsWith(".pas", StringComparison.OrdinalIgnoreCase),
    "Synthetic document names preserve the Pascal extension");
Check(
    firstSyntheticFileName == DocumentConversions.GetFileName(firstSyntheticUri),
    "Synthetic document names are stable for the same URI");
Check(
    !string.Equals(firstSyntheticFileName, secondSyntheticFileName, StringComparison.OrdinalIgnoreCase),
    "Different non-file URIs receive different synthetic document names");
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
Check(initialize.RootElement.GetProperty("result").GetProperty("capabilities")
    .GetProperty("textDocumentSync").GetProperty("change").GetInt32() == 2,
    "initialize advertises incremental document synchronization");
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

const string firstVirtualSource = """
program VirtualOne;
type TFirstVirtual = class procedure FirstMember; end;
procedure TFirstVirtual.FirstMember;
begin
end;
var value: TFirstVirtual;
begin
  value.FirstMember;
end.
""";

const string secondVirtualSource = """
program VirtualTwo;
type TSecondVirtual = class procedure SecondMember; end;
procedure TSecondVirtual.SecondMember;
begin
end;
var value: TSecondVirtual;
begin
  value.SecondMember;
end.
""";

await WriteDidOpenAsync(
    input,
    firstSyntheticUri.AbsoluteUri,
    firstVirtualSource,
    version: 1,
    timeout.Token);
await WriteDidOpenAsync(
    input,
    secondSyntheticUri.AbsoluteUri,
    secondVirtualSource,
    version: 1,
    timeout.Token);
var firstVirtualOffset =
    firstVirtualSource.IndexOf("value.", StringComparison.Ordinal) + "value.".Length;
var secondVirtualOffset =
    secondVirtualSource.IndexOf("value.", StringComparison.Ordinal) + "value.".Length;
await WriteRequestAsync(
    input,
    18,
    "textDocument/completion",
    firstSyntheticUri.AbsoluteUri,
    GetPosition(firstVirtualSource, firstVirtualOffset),
    timeout.Token);
using var firstVirtualCompletion = await ReadResponseAsync(output, 18, timeout.Token);
await WriteRequestAsync(
    input,
    19,
    "textDocument/completion",
    secondSyntheticUri.AbsoluteUri,
    GetPosition(secondVirtualSource, secondVirtualOffset),
    timeout.Token);
using var secondVirtualCompletion = await ReadResponseAsync(output, 19, timeout.Token);
var firstVirtualLabels = GetCompletionLabels(firstVirtualCompletion);
var secondVirtualLabels = GetCompletionLabels(secondVirtualCompletion);
Check(
    firstVirtualLabels.Contains("FirstMember") && !firstVirtualLabels.Contains("SecondMember"),
    "First virtual document keeps its own semantic model");
Check(
    secondVirtualLabels.Contains("SecondMember") && !secondVirtualLabels.Contains("FirstMember"),
    "Second virtual document keeps its own semantic model");
Console.WriteLine("PASS simultaneous non-file documents over stdio");

var dependencyDirectory = Path.Combine(Path.GetTempPath(), "PascalABCNet.Tooling.LspDependencyRegression");
Directory.CreateDirectory(dependencyDirectory);
var dependencyFileName = Path.Combine(dependencyDirectory, "LspDependencyA.pas");
var consumerFileName = Path.Combine(dependencyDirectory, "LspDependencyB.pas");
var dependencyUri = new Uri(dependencyFileName).AbsoluteUri;
var consumerUri = new Uri(consumerFileName).AbsoluteUri;

const string dependencySourceV1 = """
unit LspDependencyA;
interface
type
  TLspDependency = class
    procedure Foo;
  end;
implementation
procedure TLspDependency.Foo;
begin
end;
end.
""";

const string dependencyBrokenSource = """
unit LspDependencyA;
interface
type
  TLspDependency = class
    procedure
  end;
implementation
end.
""";

const string dependencySourceV3 = """
unit LspDependencyA;
interface
type
  TLspDependency = class
    procedure Bar;
  end;
implementation
procedure TLspDependency.Bar;
begin
end;
end.
""";

const string dependencySourceV4 = """
unit LspDependencyA;
interface
type
  TLspDependency = class
    procedure Baz;
  end;
implementation
procedure TLspDependency.Baz;
begin
end;
end.
""";

const string dependencySourceV5 = """
unit LspDependencyA;
interface
type
  TLspDependency = class
    procedure LongMember;
  end;
implementation
procedure TLspDependency.LongMember;
begin
end;
end.
""";

const string dependencySourceV6 = """
unit LspDependencyA;
interface
type
  TLspDependency = class
    procedure FinalMember;
  end;
implementation
procedure TLspDependency.FinalMember;
begin
end;
end.
""";

const string consumerSource = """
program LspDependencyB;
uses LspDependencyA;
var value: TLspDependency;
begin
  value.Foo;
end.
""";

File.WriteAllText(dependencyFileName, dependencySourceV1);
File.WriteAllText(consumerFileName, consumerSource);
await WriteDidOpenAsync(input, dependencyUri, dependencySourceV1, version: 1, timeout.Token);
await WriteDidOpenAsync(input, consumerUri, consumerSource, version: 1, timeout.Token);

var dependencyCaretOffset = consumerSource.IndexOf("value.", StringComparison.Ordinal) + "value.".Length;
var dependencyPosition = GetPosition(consumerSource, dependencyCaretOffset);
await WriteRequestAsync(input, 5, "textDocument/completion", consumerUri, dependencyPosition, timeout.Token);
using var initialDependencyCompletion = await ReadResponseAsync(output, 5, timeout.Token);
Check(
    GetCompletionLabels(initialDependencyCompletion).Contains("Foo"),
    "LSP consumer initially sees Foo from dependency A");

File.WriteAllText(dependencyFileName, dependencyBrokenSource);
await WriteDidChangeAsync(input, dependencyUri, dependencyBrokenSource, version: 2, timeout.Token);
File.WriteAllText(dependencyFileName, dependencySourceV3);
await WriteDidChangeAsync(input, dependencyUri, dependencySourceV3, version: 3, timeout.Token);

await WriteRequestAsync(input, 6, "textDocument/completion", consumerUri, dependencyPosition, timeout.Token);
using var refreshedDependencyCompletion = await ReadResponseAsync(output, 6, timeout.Token);
var refreshedLabels = GetCompletionLabels(refreshedDependencyCompletion);
Check(refreshedLabels.Contains("Bar"), "LSP consumer sees Bar after burst update of A");
Check(!refreshedLabels.Contains("Foo"), "LSP consumer drops stale Foo after burst update of A");

await WriteDidChangeAsync(input, dependencyUri, dependencyBrokenSource, version: 2, timeout.Token);
await WriteRequestAsync(input, 7, "textDocument/completion", consumerUri, dependencyPosition, timeout.Token);
using var completionAfterStaleVersion = await ReadResponseAsync(output, 7, timeout.Token);
var labelsAfterStaleVersion = GetCompletionLabels(completionAfterStaleVersion);
Check(labelsAfterStaleVersion.Contains("Bar") && !labelsAfterStaleVersion.Contains("Foo"),
    "Stale LSP document version cannot roll semantic state back");

var barOffsets = FindAllOffsets(dependencySourceV3, "Bar");
Check(barOffsets.Count == 2, "Dependency source contains two Bar ranges to update");
File.WriteAllText(dependencyFileName, dependencySourceV4);
await WriteDidChangeRangesAsync(
    input,
    dependencyUri,
    version: 4,
    barOffsets.Select(offset =>
    {
        var start = GetPosition(dependencySourceV3, offset);
        var end = GetPosition(dependencySourceV3, offset + "Bar".Length);
        return (start, end, "Baz");
    }).ToArray(),
    timeout.Token);
await WriteRequestAsync(input, 10, "textDocument/completion", consumerUri, dependencyPosition, timeout.Token);
using var completionAfterIncrementalChanges = await ReadResponseAsync(output, 10, timeout.Token);
var labelsAfterIncrementalChanges = GetCompletionLabels(completionAfterIncrementalChanges);
Check(labelsAfterIncrementalChanges.Contains("Baz"),
    "LSP consumer sees Baz after incremental range changes in A");
Check(!labelsAfterIncrementalChanges.Contains("Foo") && !labelsAfterIncrementalChanges.Contains("Bar"),
    "Incremental range changes remove superseded dependency members");

var firstBazOffset = dependencySourceV4.IndexOf("Baz", StringComparison.Ordinal);
var sourceAfterFirstVariableLengthChange = string.Concat(
    dependencySourceV4.AsSpan(0, firstBazOffset),
    "LongMember",
    dependencySourceV4.AsSpan(firstBazOffset + "Baz".Length));
var secondBazOffset = sourceAfterFirstVariableLengthChange.IndexOf("Baz", StringComparison.Ordinal);
File.WriteAllText(dependencyFileName, dependencySourceV5);
await WriteDidChangeRangesAsync(
    input,
    dependencyUri,
    version: 5,
    new[]
    {
        (
            GetPosition(dependencySourceV4, firstBazOffset),
            GetPosition(dependencySourceV4, firstBazOffset + "Baz".Length),
            "LongMember"),
        (
            GetPosition(sourceAfterFirstVariableLengthChange, secondBazOffset),
            GetPosition(sourceAfterFirstVariableLengthChange, secondBazOffset + "Baz".Length),
            "LongMember")
    },
    timeout.Token);
await WriteRequestAsync(input, 11, "textDocument/completion", consumerUri, dependencyPosition, timeout.Token);
using var completionAfterVariableLengthChanges = await ReadResponseAsync(output, 11, timeout.Token);
var labelsAfterVariableLengthChanges = GetCompletionLabels(completionAfterVariableLengthChanges);
Check(labelsAfterVariableLengthChanges.Contains("LongMember"),
    "Sequential incremental ranges are based on the text produced by the previous change");
Check(!labelsAfterVariableLengthChanges.Contains("Baz"),
    "Variable-length incremental ranges remove the previous member");

await WriteDidChangeRangesAsync(
    input,
    dependencyUri,
    version: 6,
    new[] { ((Line: 999, Character: 0), (Line: 999, Character: 1), "corrupt") },
    timeout.Token);
await WriteRequestAsync(input, 12, "textDocument/completion", consumerUri, dependencyPosition, timeout.Token);
using var completionAfterInvalidRange = await ReadResponseAsync(output, 12, timeout.Token);
Check(GetCompletionLabels(completionAfterInvalidRange).Contains("LongMember"),
    "An invalid incremental range does not corrupt the current document snapshot");

File.WriteAllText(dependencyFileName, dependencySourceV6);
await WriteDidChangeAsync(input, dependencyUri, dependencySourceV6, version: 6, timeout.Token);
await WriteRequestAsync(input, 13, "textDocument/completion", consumerUri, dependencyPosition, timeout.Token);
using var completionAfterInvalidRangeRecovery = await ReadResponseAsync(output, 13, timeout.Token);
var labelsAfterInvalidRangeRecovery = GetCompletionLabels(completionAfterInvalidRangeRecovery);
Check(labelsAfterInvalidRangeRecovery.Contains("FinalMember") &&
      !labelsAfterInvalidRangeRecovery.Contains("LongMember"),
    "A valid update is accepted after an invalid range with the same version was rejected");
Console.WriteLine("PASS dependency refresh and burst updates over stdio");
Console.WriteLine("PASS incremental range synchronization over stdio");

var chainAFileName = Path.Combine(dependencyDirectory, "LspChainA.pas");
var chainBFileName = Path.Combine(dependencyDirectory, "LspChainB.pas");
var chainCFileName = Path.Combine(dependencyDirectory, "LspChainC.pas");
var chainAUri = new Uri(chainAFileName).AbsoluteUri;
var chainBUri = new Uri(chainBFileName).AbsoluteUri;
var chainCUri = new Uri(chainCFileName).AbsoluteUri;

const string chainASourceV1 = """
unit LspChainA;
interface
type TBase = class procedure OldMember; end;
implementation
procedure TBase.OldMember;
begin
end;
end.
""";

const string chainASourceV2 = """
unit LspChainA;
interface
type TBase = class procedure NewMember; end;
implementation
procedure TBase.NewMember;
begin
end;
end.
""";

const string chainBSource = """
unit LspChainB;
interface
uses LspChainA;
type TDerived = class(TBase) end;
implementation
end.
""";

const string chainCSource = """
program LspChainC;
uses LspChainB;
var value: TDerived;
begin
  value.OldMember;
end.
""";

File.WriteAllText(chainAFileName, chainASourceV1);
File.WriteAllText(chainBFileName, chainBSource);
File.WriteAllText(chainCFileName, chainCSource);
await WriteDidOpenAsync(input, chainAUri, chainASourceV1, version: 1, timeout.Token);
await WriteDidOpenAsync(input, chainBUri, chainBSource, version: 1, timeout.Token);
await WriteDidOpenAsync(input, chainCUri, chainCSource, version: 1, timeout.Token);

var chainCaretOffset = chainCSource.IndexOf("value.", StringComparison.Ordinal) + "value.".Length;
var chainPosition = GetPosition(chainCSource, chainCaretOffset);
await WriteRequestAsync(input, 8, "textDocument/completion", chainCUri, chainPosition, timeout.Token);
using var initialChainCompletion = await ReadResponseAsync(output, 8, timeout.Token);
Check(GetCompletionLabels(initialChainCompletion).Contains("OldMember"),
    "Transitive LSP consumer initially sees OldMember");

File.WriteAllText(chainAFileName, chainASourceV2);
await WriteDidChangeAsync(input, chainAUri, chainASourceV2, version: 2, timeout.Token);
await WriteRequestAsync(input, 9, "textDocument/completion", chainCUri, chainPosition, timeout.Token);
using var refreshedChainCompletion = await ReadResponseAsync(output, 9, timeout.Token);
var refreshedChainLabels = GetCompletionLabels(refreshedChainCompletion);
Check(refreshedChainLabels.Contains("NewMember"),
    "Transitive LSP consumer sees NewMember after A changes");
Check(!refreshedChainLabels.Contains("OldMember"),
    "Transitive LSP consumer drops stale OldMember after A changes");
Console.WriteLine("PASS transitive dependency refresh over stdio");

var implementationAFileName = Path.Combine(dependencyDirectory, "LspImplementationA.pas");
var implementationBFileName = Path.Combine(dependencyDirectory, "LspImplementationB.pas");
var implementationAUri = new Uri(implementationAFileName).AbsoluteUri;
var implementationBUri = new Uri(implementationBFileName).AbsoluteUri;

const string implementationASourceV1 = """
unit LspImplementationA;
interface
type TImplementationDependency = class procedure OldMember; end;
implementation
procedure TImplementationDependency.OldMember;
begin
end;
end.
""";

const string implementationASourceV2 = """
unit LspImplementationA;
interface
type TImplementationDependency = class procedure NewMember; end;
implementation
procedure TImplementationDependency.NewMember;
begin
end;
end.
""";

const string implementationBSource = """
unit LspImplementationB;
interface
procedure Touch;
implementation
uses LspImplementationA;
procedure Touch;
var value: TImplementationDependency;
begin
  value.OldMember;
end;
end.
""";

File.WriteAllText(implementationAFileName, implementationASourceV1);
File.WriteAllText(implementationBFileName, implementationBSource);
await WriteDidOpenAsync(input, implementationAUri, implementationASourceV1, version: 1, timeout.Token);
await WriteDidOpenAsync(input, implementationBUri, implementationBSource, version: 1, timeout.Token);

var implementationCaretOffset =
    implementationBSource.IndexOf("value.", StringComparison.Ordinal) + "value.".Length;
var implementationPosition = GetPosition(implementationBSource, implementationCaretOffset);
await WriteRequestAsync(
    input,
    14,
    "textDocument/completion",
    implementationBUri,
    implementationPosition,
    timeout.Token);
using var initialImplementationCompletion = await ReadResponseAsync(output, 14, timeout.Token);
Check(
    GetCompletionLabels(initialImplementationCompletion).Contains("OldMember"),
    "Implementation-only LSP consumer initially sees OldMember");

File.WriteAllText(implementationAFileName, implementationASourceV2);
await WriteDidChangeAsync(input, implementationAUri, implementationASourceV2, version: 2, timeout.Token);
await WriteRequestAsync(
    input,
    15,
    "textDocument/completion",
    implementationBUri,
    implementationPosition,
    timeout.Token);
using var refreshedImplementationCompletion = await ReadResponseAsync(output, 15, timeout.Token);
var refreshedImplementationLabels = GetCompletionLabels(refreshedImplementationCompletion);
Check(
    refreshedImplementationLabels.Contains("NewMember"),
    "Implementation-only LSP consumer sees NewMember after A changes");
Check(
    !refreshedImplementationLabels.Contains("OldMember"),
    "Implementation-only LSP consumer drops stale OldMember after A changes");
Console.WriteLine("PASS implementation uses dependency refresh over stdio");

File.WriteAllText(dependencyFileName, dependencySourceV1);
await WriteDidChangeAsync(input, dependencyUri, dependencySourceV3, version: 7, timeout.Token);
await WriteRequestAsync(input, 16, "textDocument/completion", consumerUri, dependencyPosition, timeout.Token);
using var completionBeforeDependencyClose = await ReadResponseAsync(output, 16, timeout.Token);
var labelsBeforeDependencyClose = GetCompletionLabels(completionBeforeDependencyClose);
Check(
    labelsBeforeDependencyClose.Contains("Bar") && !labelsBeforeDependencyClose.Contains("Foo"),
    "LSP consumer uses the unsaved dependency model before close");

await WriteMessageAsync(input, new
{
    jsonrpc = "2.0",
    method = "textDocument/didClose",
    @params = new { textDocument = new { uri = dependencyUri } }
}, timeout.Token);
await WriteRequestAsync(input, 17, "textDocument/completion", consumerUri, dependencyPosition, timeout.Token);
using var completionAfterDependencyClose = await ReadResponseAsync(output, 17, timeout.Token);
var labelsAfterDependencyClose = GetCompletionLabels(completionAfterDependencyClose);
Check(
    labelsAfterDependencyClose.Contains("Foo"),
    "LSP consumer returns to the dependency model stored on disk after close");
Check(
    !labelsAfterDependencyClose.Contains("Bar"),
    "LSP consumer drops the unsaved dependency model after close");
Console.WriteLine("PASS closing an unsaved dependency restores its disk model over stdio");

foreach (var uri in new[] { implementationBUri, implementationAUri })
{
    await WriteMessageAsync(input, new
    {
        jsonrpc = "2.0",
        method = "textDocument/didClose",
        @params = new { textDocument = new { uri } }
    }, timeout.Token);
}

foreach (var uri in new[] { chainCUri, chainBUri, chainAUri })
{
    await WriteMessageAsync(input, new
    {
        jsonrpc = "2.0",
        method = "textDocument/didClose",
        @params = new { textDocument = new { uri } }
    }, timeout.Token);
}

await WriteMessageAsync(input, new
{
    jsonrpc = "2.0",
    method = "textDocument/didClose",
    @params = new { textDocument = new { uri = consumerUri } }
}, timeout.Token);

await WriteMessageAsync(input, new
{
    jsonrpc = "2.0",
    method = "textDocument/didClose",
    @params = new { textDocument = new { uri = documentUri } }
}, timeout.Token);

foreach (var uri in new[] { firstSyntheticUri.AbsoluteUri, secondSyntheticUri.AbsoluteUri })
{
    await WriteMessageAsync(input, new
    {
        jsonrpc = "2.0",
        method = "textDocument/didClose",
        @params = new { textDocument = new { uri } }
    }, timeout.Token);
}

await WriteMessageAsync(input, new
{
    jsonrpc = "2.0",
    id = 20,
    method = "shutdown"
}, timeout.Token);
using var shutdown = await ReadResponseAsync(output, 20, timeout.Token);
Check(shutdown.RootElement.TryGetProperty("result", out var shutdownResult) &&
      shutdownResult.ValueKind == JsonValueKind.Null,
    "shutdown returned null result");

await WriteMessageAsync(input, new { jsonrpc = "2.0", method = "exit" }, timeout.Token);
await process.WaitForExitAsync(timeout.Token);
var errorOutput = await process.StandardError.ReadToEndAsync(timeout.Token);
Check(process.ExitCode == 0, $"Language server exited cleanly. stderr: {errorOutput}");
Console.WriteLine("PASS shutdown and exit over stdio");
Console.WriteLine("All LSP smoke checks passed.");

static async Task WriteDidOpenAsync(
    Stream stream,
    string documentUri,
    string text,
    int version,
    CancellationToken cancellationToken)
{
    await WriteMessageAsync(stream, new
    {
        jsonrpc = "2.0",
        method = "textDocument/didOpen",
        @params = new
        {
            textDocument = new { uri = documentUri, languageId = "pascal", version, text }
        }
    }, cancellationToken);
}

static async Task WriteDidChangeAsync(
    Stream stream,
    string documentUri,
    string text,
    int version,
    CancellationToken cancellationToken)
{
    await WriteMessageAsync(stream, new
    {
        jsonrpc = "2.0",
        method = "textDocument/didChange",
        @params = new
        {
            textDocument = new { uri = documentUri, version },
            contentChanges = new[] { new { text } }
        }
    }, cancellationToken);
}

static async Task WriteDidChangeRangesAsync(
    Stream stream,
    string documentUri,
    int version,
    IReadOnlyList<((int Line, int Character) Start, (int Line, int Character) End, string Text)> changes,
    CancellationToken cancellationToken)
{
    var contentChanges = changes.Select(change => new
    {
        range = new
        {
            start = new { line = change.Start.Line, character = change.Start.Character },
            end = new { line = change.End.Line, character = change.End.Character }
        },
        text = change.Text
    }).ToArray();

    await WriteMessageAsync(stream, new
    {
        jsonrpc = "2.0",
        method = "textDocument/didChange",
        @params = new
        {
            textDocument = new { uri = documentUri, version },
            contentChanges
        }
    }, cancellationToken);
}

static IReadOnlyList<int> FindAllOffsets(string text, string value)
{
    var offsets = new List<int>();
    for (var offset = text.IndexOf(value, StringComparison.Ordinal);
         offset >= 0;
         offset = text.IndexOf(value, offset + value.Length, StringComparison.Ordinal))
    {
        offsets.Add(offset);
    }

    return offsets;
}

static HashSet<string> GetCompletionLabels(JsonDocument response) =>
    response.RootElement.GetProperty("result").GetProperty("items")
        .EnumerateArray()
        .Select(item => item.GetProperty("label").GetString())
        .Where(label => label is not null)
        .Select(label => label!)
        .ToHashSet(StringComparer.Ordinal);

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
