using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

return await RunFixtureAsync(args);

static async Task<int> RunFixtureAsync(string[] arguments)
{
    if (arguments.Length == 0)
    {
        return 2;
    }

    if (arguments.Length == 4
        && arguments[0] == "--health-pipe"
        && arguments[2] == "--health-nonce")
    {
        return await RunHealthFixtureAsync(arguments[1], arguments[3]);
    }

    switch (arguments[0])
    {
        case "p4-login-fixture":
            if (arguments.Length < 4)
            {
                return 2;
            }

            var standardInput = await Console.In.ReadToEndAsync();
            var loginCapture = new
            {
                Arguments = arguments.Skip(4).ToArray(),
                StandardInput = standardInput,
                ProcessId = Environment.ProcessId
            };
            await File.WriteAllTextAsync(arguments[1], JsonSerializer.Serialize(loginCapture));
            Console.Out.WriteLine("fixture stdout");
            Console.Error.WriteLine("fixture stderr");
            await Task.Delay(int.Parse(arguments[3], CultureInfo.InvariantCulture));
            return int.Parse(arguments[2], CultureInfo.InvariantCulture);

        case "capture":
            if (arguments.Length < 4)
            {
                return 2;
            }

            var capture = new
            {
                Arguments = arguments,
                WorkingDirectory = Directory.GetCurrentDirectory()
            };
            await File.WriteAllTextAsync(arguments[1], JsonSerializer.Serialize(capture));
            await Task.Delay(int.Parse(arguments[2], CultureInfo.InvariantCulture));
            return 0;

        case "spawn-child":
            if (arguments.Length != 4)
            {
                return 2;
            }

            var childStartInfo = new ProcessStartInfo(Environment.ProcessPath ??
                throw new InvalidOperationException("The fixture process path is unavailable."))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true
            };
            childStartInfo.ArgumentList.Add("sleep");
            childStartInfo.ArgumentList.Add(arguments[2]);

            using (var childProcess = Process.Start(childStartInfo) ??
                throw new InvalidOperationException("The fixture child process could not be started."))
            {
                await File.WriteAllTextAsync(
                    arguments[1],
                    childProcess.Id.ToString(CultureInfo.InvariantCulture));
            }

            return int.Parse(arguments[3], CultureInfo.InvariantCulture);

        case "sleep":
            if (arguments.Length != 2)
            {
                return 2;
            }

            await Task.Delay(int.Parse(arguments[1], CultureInfo.InvariantCulture));
            return 0;

        default:
            return 2;
    }
}

static async Task<int> RunHealthFixtureAsync(string pipeName, string nonce)
{
    if (nonce.All(static character => character == '0'))
    {
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    var acknowledgement = nonce.All(static character => character == 'f')
        ? new string('e', nonce.Length)
        : nonce;
    using var pipe = new NamedPipeClientStream(
        ".",
        pipeName,
        PipeDirection.Out,
        PipeOptions.Asynchronous);
    await pipe.ConnectAsync(5_000);
    await using var writer = new StreamWriter(
        pipe,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        bufferSize: 128,
        leaveOpen: true);
    writer.NewLine = "\n";
    await writer.WriteLineAsync(acknowledgement);
    await writer.FlushAsync();
    await Task.Delay(500);
    return 0;
}
