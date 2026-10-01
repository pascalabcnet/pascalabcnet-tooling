using System.Diagnostics;
using System.Text;
using System.Text.Json;

var options = ParseArguments(args);
var runtimeRoot = Path.GetFullPath(options.RuntimeRoot);
var isNet10 = options.Target == "net10";
var controllerPath = Path.Combine(
    runtimeRoot,
    isNet10 ? "PABCCompilerController.dll" : "PABCCompilerController.exe");
var workerPath = Path.Combine(
    runtimeRoot,
    isNet10 ? "ZMQServerPas.dll" : "ZMQServerPas.exe");

Check(File.Exists(controllerPath), $"Controller exists: {controllerPath}");
Check(File.Exists(workerPath), $"Worker exists: {workerPath}");

var testRoot = Path.Combine(
    Path.GetTempPath(),
    "pabc-tooling-controller-" + Guid.NewGuid().ToString("N"));
var sourceRoot = Path.Combine(testRoot, "папка с пробелами");
var outputRoot = Path.Combine(testRoot, "output");
Directory.CreateDirectory(sourceRoot);
Directory.CreateDirectory(outputRoot);
var sourcePath = Path.Combine(sourceRoot, "Проверка.pas");

var startInfo = new ProcessStartInfo
{
    FileName = isNet10 ? ResolveDotnet() : controllerPath,
    WorkingDirectory = runtimeRoot,
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    StandardInputEncoding = new UTF8Encoding(false),
    StandardOutputEncoding = new UTF8Encoding(false),
    StandardErrorEncoding = new UTF8Encoding(false)
};
if (isNet10)
    startInfo.ArgumentList.Add(controllerPath);
startInfo.ArgumentList.Add(workerPath);
startInfo.ArgumentList.Add("2");
startInfo.ArgumentList.Add("0");

using var process = new Process { StartInfo = startInfo };
var stderr = new StringBuilder();
var processStarted = false;
process.ErrorDataReceived += (_, eventArgs) =>
{
    if (eventArgs.Data is not null)
        stderr.AppendLine(eventArgs.Data);
};

try
{
    processStarted = process.Start();
    Check(processStarted, "Controller process started");
    process.BeginErrorReadLine();

    var ping = await SendAsync(process, new { id = 1, command = "ping" });
    CheckSuccess(ping, "ping");
    Check(ping.GetProperty("result").GetString() == "PONG", "Worker answered PONG");
    var firstWorkerPid = ping.GetProperty("workerPid").GetInt32();

    await File.WriteAllTextAsync(
        sourcePath,
        "begin\n  Println('first compilation');\nend.\n",
        new UTF8Encoding(false));
    var firstCompile = await CompileAsync(process, 2, sourcePath, outputRoot);
    CheckSuccess(firstCompile, "first compilation");
    Check(firstCompile.GetProperty("diagnostics").GetArrayLength() == 0,
        "Successful compilation has no diagnostics");
    var firstOutput = firstCompile.GetProperty("outputFile").GetString();
    Check(!string.IsNullOrWhiteSpace(firstOutput) && File.Exists(firstOutput),
        "First compilation produced an output file");

    await File.WriteAllTextAsync(
        sourcePath,
        "begin\n  Println('second compilation');\nend.\n",
        new UTF8Encoding(false));
    var secondCompile = await CompileAsync(process, 3, sourcePath, outputRoot);
    CheckSuccess(secondCompile, "second compilation in the same controller");

    var pingAfterRestart = await SendAsync(process, new { id = 4, command = "ping" });
    CheckSuccess(pingAfterRestart, "ping after automatic restart");
    Check(pingAfterRestart.GetProperty("workerPid").GetInt32() != firstWorkerPid,
        "Worker restarted after the configured compilation limit");

    await File.WriteAllTextAsync(
        sourcePath,
        "begin\n  this is not valid Pascal\nend.\n",
        new UTF8Encoding(false));
    var invalidCompile = await CompileAsync(process, 5, sourcePath, outputRoot);
    Check(!invalidCompile.GetProperty("success").GetBoolean(),
        "Invalid source was rejected");
    Check(invalidCompile.GetProperty("diagnostics").GetArrayLength() > 0,
        "Invalid source returned diagnostics");

    await File.WriteAllTextAsync(
        sourcePath,
        "begin\n  Println('recovered');\nend.\n",
        new UTF8Encoding(false));
    var recoveredCompile = await CompileAsync(process, 6, sourcePath, outputRoot);
    CheckSuccess(recoveredCompile, "compilation after an error");

    var shutdown = await SendAsync(process, new { id = 7, command = "shutdown" });
    CheckSuccess(shutdown, "shutdown");
    Check(shutdown.GetProperty("result").GetString() == "shutdown",
        "Controller acknowledged shutdown");
    Check(process.WaitForExit(10_000), "Controller exited after shutdown");
    Check(process.ExitCode == 0, "Controller exit code is zero");

    Console.WriteLine($"All {options.Target} compiler-controller smoke checks passed.");
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    if (stderr.Length > 0)
    {
        Console.Error.WriteLine("Controller stderr:");
        Console.Error.WriteLine(stderr);
    }
    Environment.ExitCode = 1;
}
finally
{
    if (processStarted && !process.HasExited)
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
    }

    try
    {
        Directory.Delete(testRoot, recursive: true);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

static async Task<JsonElement> CompileAsync(
    Process process,
    int id,
    string sourcePath,
    string outputRoot) =>
    await SendAsync(process, new
    {
        id,
        command = "compile",
        fileName = sourcePath,
        outputDirectory = outputRoot
    });

static async Task<JsonElement> SendAsync(Process process, object request)
{
    var json = JsonSerializer.Serialize(request);
    await process.StandardInput.WriteLineAsync(json);
    await process.StandardInput.FlushAsync();

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
    if (line is null)
        throw new EndOfStreamException("Controller closed stdout before returning JSON.");

    using var document = JsonDocument.Parse(line);
    return document.RootElement.Clone();
}

static void CheckSuccess(JsonElement response, string operation)
{
    Check(response.GetProperty("success").GetBoolean(),
        $"{operation} succeeded: {response}");
}

static string ResolveDotnet()
{
    var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
    return string.IsNullOrWhiteSpace(hostPath) ? "dotnet" : hostPath;
}

static (string RuntimeRoot, string Target) ParseArguments(string[] arguments)
{
    string? runtimeRoot = null;
    string? target = null;
    for (var index = 0; index < arguments.Length; index++)
    {
        switch (arguments[index])
        {
            case "--runtime" when index + 1 < arguments.Length:
                runtimeRoot = arguments[++index];
                break;
            case "--target" when index + 1 < arguments.Length:
                target = arguments[++index];
                break;
            default:
                throw new ArgumentException($"Unknown or incomplete argument: {arguments[index]}");
        }
    }

    if (string.IsNullOrWhiteSpace(runtimeRoot))
        throw new ArgumentException("--runtime is required.");
    if (target is not ("net10" or "net-framework"))
        throw new ArgumentException("--target must be net10 or net-framework.");
    return (runtimeRoot, target);
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException("Smoke check failed: " + message);
    Console.WriteLine("PASS " + message);
}
