# Compiler controller protocol

`PABCCompilerController` is an editor-neutral process that reads one JSON request per line from standard input and writes exactly one JSON response per line to standard output. Operational logs and worker output are written only to standard error.

The controller starts `ZMQServerPas` on a free loopback TCP port. NetMQ is an internal controller-to-worker transport and is not exposed to editor integrations.

## Commands

Ping:

```json
{"id":1,"command":"ping"}
```

Compile:

```json
{"id":2,"command":"compile","fileName":"C:\\work\\Program.pas","outputDirectory":"C:\\work\\out"}
```

Restart the worker:

```json
{"id":3,"command":"restart"}
```

Shut down the worker and controller:

```json
{"id":4,"command":"shutdown"}
```

Every response repeats `id` and contains `success`. Compile responses also contain `diagnostics`, `outputFile`, `message`, `fileName`, `compilationCount`, `workerPid`, and `workingSetMB` where applicable.

## Lifecycle

The controller serializes requests and owns one compiler worker. It restarts that worker after the configured compilation-count or memory threshold and recovers it after failures. The optional command-line arguments are worker path, maximum compilation count, and maximum working-set size in MB.

Both projects target .NET Framework 4.7.2 and .NET 10. Build them with `scripts/build-compiler-host.ps1`; validate both runtimes with `scripts/test-compiler-host.ps1`.
