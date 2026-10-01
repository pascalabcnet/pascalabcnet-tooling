using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NetMQ;
using NetMQ.Sockets;
#if NETFRAMEWORK
using System.Web.Script.Serialization;
#else
using System.Text.Json;
#endif

namespace PascalABCNet.CompilerController;

internal static class Program
{
    private sealed class ControllerRequest
    {
        public int id { get; set; }
        public string? command { get; set; }
        public string? fileName { get; set; }
        public string? outputDirectory { get; set; }
    }

    private sealed class WorkerCompileRequest
    {
        public string? fileName { get; set; }
        public string? outputDirectory { get; set; }
    }

    private static RequestSocket CreateClient(string address)
    {
        var client = new RequestSocket();
        client.Options.Linger = TimeSpan.Zero;
        client.Connect(address);
        return client;
    }

    private static void DisposeClient(ref RequestSocket? client)
    {
        client?.Dispose();
        client = null;
    }

    private static void Log(string message)
    {
        Console.Error.WriteLine(message);
        Console.Error.Flush();
    }

    private static int GetFreeLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void WriteJson(Dictionary<string, object?> response)
    {
        Console.WriteLine(SerializeJson(response));
        Console.Out.Flush();
    }

    private static string SerializeJson(object value)
    {
#if NETFRAMEWORK
        return new JavaScriptSerializer().Serialize(value);
#else
        return JsonSerializer.Serialize(value);
#endif
    }

    private static ControllerRequest DeserializeRequest(string json)
    {
#if NETFRAMEWORK
        return new JavaScriptSerializer().Deserialize<ControllerRequest>(json)
               ?? throw new InvalidDataException("JSON-запрос не содержит объекта");
#else
        return JsonSerializer.Deserialize<ControllerRequest>(json)
               ?? throw new InvalidDataException("JSON-запрос не содержит объекта");
#endif
    }

    private static Dictionary<string, object?> CreateResponse(int requestId, bool success)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = requestId,
            ["success"] = success
        };
    }

    private static string QuoteArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";

    private static Process StartWorker(string workerFileName, int port)
    {
        if (!File.Exists(workerFileName))
            throw new FileNotFoundException(
                "Не найден ZMQServerPas: " + workerFileName,
                workerFileName);

        var workerIsDotNetAssembly = string.Equals(
            Path.GetExtension(workerFileName),
            ".dll",
            StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = workerIsDotNetAssembly ? "dotnet" : workerFileName,
            Arguments = workerIsDotNetAssembly
                ? QuoteArgument(workerFileName) + " " + port
                : port.ToString(),
            WorkingDirectory = Path.GetDirectoryName(workerFileName) ??
                               AppDomain.CurrentDomain.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            // stdout контроллера используется только для JSON Lines.
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var worker = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        worker.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
                Log("[worker] " + eventArgs.Data);
        };
        worker.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
                Log("[worker error] " + eventArgs.Data);
        };

        if (!worker.Start())
        {
            worker.Dispose();
            throw new InvalidOperationException("Не удалось запустить ZMQServerPas");
        }

        worker.BeginOutputReadLine();
        worker.BeginErrorReadLine();
        return worker;
    }

    private static void WaitUntilReady(
        Process worker,
        string address,
        ref RequestSocket? client)
    {
        var finishTime = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < finishTime)
        {
            if (worker.HasExited)
                throw new InvalidOperationException(
                    "ZMQServerPas завершился с кодом " + worker.ExitCode);

            DisposeClient(ref client);
            client = CreateClient(address);

            try
            {
                client.SendFrame("#PING");
                if (client.TryReceiveFrameString(
                        TimeSpan.FromMilliseconds(500),
                        out var response) && response == "PONG")
                    return;
            }
            catch
            {
                // Worker ещё может не успеть открыть сокет.
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("ZMQServerPas не ответил на #PING за 10 секунд");
    }

    private static void StartWorkerAndConnect(
        string workerFileName,
        int port,
        string address,
        ref Process? worker,
        ref RequestSocket? client)
    {
        worker = StartWorker(workerFileName, port);
        try
        {
            WaitUntilReady(worker, address, ref client);
        }
        catch
        {
            if (!worker.HasExited)
                worker.Kill();
            worker.Dispose();
            worker = null;
            DisposeClient(ref client);
            throw;
        }

        Log("ZMQServerPas запущен, PID = " + worker.Id);
    }

    private static void StopWorker(
        string address,
        ref Process? worker,
        ref RequestSocket? client)
    {
        DisposeClient(ref client);
        if (worker == null)
            return;

        if (!worker.HasExited)
        {
            RequestSocket? shutdownClient = null;
            try
            {
                shutdownClient = CreateClient(address);
                shutdownClient.SendFrame("#SHUTDOWN");
                shutdownClient.TryReceiveFrameString(
                    TimeSpan.FromMilliseconds(500), out _);
            }
            catch
            {
                // При невозможности штатного завершения worker будет остановлен принудительно.
            }
            finally
            {
                DisposeClient(ref shutdownClient);
            }

            if (!worker.WaitForExit(1500))
                worker.Kill();
        }

        worker.Dispose();
        worker = null;
    }

    private static void RestartWorker(
        string workerFileName,
        int port,
        string address,
        ref Process? worker,
        ref RequestSocket? client)
    {
        Log("Перезапуск ZMQServerPas");
        StopWorker(address, ref worker, ref client);
        Thread.Sleep(100);
        StartWorkerAndConnect(
            workerFileName, port, address, ref worker, ref client);
    }

    private static bool TrySendRequest(
        RequestSocket client,
        string request,
        TimeSpan timeout,
        out string response)
    {
        try
        {
            client.SendFrame(request);
            if (client.TryReceiveFrameString(timeout, out var received) &&
                received != null)
            {
                response = received;
                return true;
            }

            response = "";
            return false;
        }
        catch
        {
            response = "";
            return false;
        }
    }

    private static string SendRequest(
        string request,
        string workerFileName,
        int port,
        string address,
        ref Process? worker,
        ref RequestSocket? client)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (client != null && TrySendRequest(
                    client, request, TimeSpan.FromMinutes(5), out var response))
                return response;

            if (attempt == 1)
            {
                Log("ZMQServerPas не ответил. Выполняется перезапуск");
                RestartWorker(
                    workerFileName, port, address, ref worker, ref client);
            }
        }

        throw new InvalidOperationException("Не удалось получить ответ от ZMQServerPas");
    }

    private static long GetWorkingSetMb(Process? worker)
    {
        if (worker == null || worker.HasExited)
            return 0;
        worker.Refresh();
        return worker.WorkingSet64 / (1024 * 1024);
    }

    private static void ParseCompileResponse(
        string workerResponse,
        string sourceFileName,
        Dictionary<string, object?> response)
    {
        var normalized = workerResponse.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalized.Split('\n');
        var diagnostics = new List<Dictionary<string, object?>>();
        response["diagnostics"] = diagnostics;

        if (lines.Length > 0 && lines[0] == "OK")
        {
            response["success"] = true;
            response["outputFile"] = lines.Length > 1 ? lines[1].Trim() : "";
            response["message"] = "";
            return;
        }

        response["success"] = false;
        response["outputFile"] = "";
        var firstLine = lines.Length > 0 &&
                        (lines[0] == "ERROR" || lines[0] == "FATAL") ? 1 : 0;
        var message = new StringBuilder();
        var errorPattern = new Regex(
            @"^\s*\((\d+),\s*(\d+)\):\s*(.*)$",
            RegexOptions.CultureInvariant);

        for (var index = firstLine; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0)
                continue;
            if (message.Length > 0)
                message.AppendLine();
            message.Append(line);

            var match = errorPattern.Match(line);
            var diagnostic = new Dictionary<string, object?>
            {
                ["fileName"] = sourceFileName,
                ["severity"] = "error"
            };
            if (match.Success)
            {
                diagnostic["line"] = int.Parse(match.Groups[1].Value);
                diagnostic["column"] = int.Parse(match.Groups[2].Value);
                diagnostic["message"] = match.Groups[3].Value;
            }
            else
            {
                diagnostic["line"] = 1;
                diagnostic["column"] = 1;
                diagnostic["message"] = line;
            }
            diagnostics.Add(diagnostic);
        }

        response["message"] = message.Length == 0
            ? workerResponse
            : message.ToString();
    }

    private static string GetDefaultWorkerFileName()
    {
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
#if NET10_0
        var dotNetWorker = Path.Combine(baseDirectory, "ZMQServerPas.dll");
        if (File.Exists(dotNetWorker))
            return dotNetWorker;
#endif
        return Path.Combine(baseDirectory, "ZMQServerPas.exe");
    }

    private static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);

        var workerFileName = Path.GetFullPath(
            args.Length >= 1 ? args[0] : GetDefaultWorkerFileName());
        var port = GetFreeLocalPort();
        var maxCompilations = args.Length >= 2 ? int.Parse(args[1]) : 0;
        var maxWorkingSetMb = args.Length >= 3 ? long.Parse(args[2]) : 0;
        var address = "tcp://127.0.0.1:" + port;

        Log("Адрес ZMQServerPas: " + address);

        Process? worker = null;
        RequestSocket? client = null;
        var compilationCount = 0;
        var running = true;

        try
        {
            StartWorkerAndConnect(
                workerFileName, port, address, ref worker, ref client);
            Log("PABCCompilerController готов");

            while (running)
            {
                var inputLine = Console.ReadLine();
                if (inputLine == null)
                    break;
                if (string.IsNullOrWhiteSpace(inputLine))
                    continue;

                inputLine = inputLine.TrimStart('\uFEFF');

                var requestId = 0;
                try
                {
                    var request = DeserializeRequest(inputLine);
                    requestId = request.id;
                    var command = (request.command ?? "").ToLowerInvariant();

                    switch (command)
                    {
                        case "ping":
                        {
                            var workerResponse = SendRequest(
                                "#PING", workerFileName, port, address,
                                ref worker, ref client);
                            var response = CreateResponse(
                                requestId, workerResponse == "PONG");
                            response["result"] = workerResponse;
                            response["workerPid"] = worker!.Id;
                            response["workingSetMB"] = GetWorkingSetMb(worker);
                            WriteJson(response);
                            break;
                        }
                        case "compile":
                        {
                            var fileName = request.fileName ?? "";
                            if (fileName.Length == 0)
                            {
                                var missingFileResponse = CreateResponse(requestId, false);
                                missingFileResponse["outputFile"] = "";
                                missingFileResponse["message"] = "Не задано поле fileName";
                                WriteJson(missingFileResponse);
                                continue;
                            }

                            fileName = Path.GetFullPath(fileName);
                            var outputDirectory = request.outputDirectory;
                            if (!string.IsNullOrWhiteSpace(outputDirectory))
                            {
                                outputDirectory = Path.GetFullPath(outputDirectory);
                                Directory.CreateDirectory(outputDirectory);
                            }

                            var workerRequest = SerializeJson(
                                new WorkerCompileRequest
                                {
                                    fileName = fileName,
                                    outputDirectory = outputDirectory
                                });
                            var workerResponse = SendRequest(
                                workerRequest, workerFileName, port, address,
                                ref worker, ref client);
                            compilationCount++;
                            var workingSetMb = GetWorkingSetMb(worker);
                            var response = CreateResponse(requestId, false);
                            ParseCompileResponse(workerResponse, fileName, response);
                            response["fileName"] = fileName;
                            response["compilationCount"] = compilationCount;
                            response["workerPid"] = worker!.Id;
                            response["workingSetMB"] = workingSetMb;
                            WriteJson(response);

                            Log("Компиляций: " + compilationCount +
                                ", память: " + workingSetMb + " MB");

                            var restartByCount = maxCompilations > 0 &&
                                                 compilationCount >= maxCompilations;
                            var restartByMemory = maxWorkingSetMb > 0 &&
                                                  workingSetMb >= maxWorkingSetMb;
                            if (restartByCount || restartByMemory)
                            {
                                RestartWorker(
                                    workerFileName, port, address,
                                    ref worker, ref client);
                                compilationCount = 0;
                            }
                            break;
                        }
                        case "restart":
                        {
                            RestartWorker(
                                workerFileName, port, address,
                                ref worker, ref client);
                            compilationCount = 0;
                            var response = CreateResponse(requestId, true);
                            response["result"] = "restarted";
                            response["workerPid"] = worker!.Id;
                            WriteJson(response);
                            break;
                        }
                        case "shutdown":
                        {
                            var response = CreateResponse(requestId, true);
                            response["result"] = "shutdown";
                            WriteJson(response);
                            running = false;
                            break;
                        }
                        default:
                        {
                            var response = CreateResponse(requestId, false);
                            response["message"] = "Неизвестная команда: " + command;
                            WriteJson(response);
                            break;
                        }
                    }
                }
                catch (Exception exception)
                {
                    var response = CreateResponse(requestId, false);
                    response["message"] = exception.Message;
                    response["errorType"] = exception.GetType().FullName;
                    WriteJson(response);
                    Log(exception.ToString());
                }
            }
        }
        catch (Exception exception)
        {
            Log("Критическая ошибка контроллера:");
            Log(exception.ToString());
        }
        finally
        {
            StopWorker(address, ref worker, ref client);
            NetMQConfig.Cleanup();
        }

        return 0;
    }
}
