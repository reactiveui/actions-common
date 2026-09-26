#!/usr/bin/env dotnet
// Runs a .NET device test app on a headless Android emulator or iOS simulator, from boot to shutdown.
//
//   dotnet run --file device-tests.cs -- android --project <csproj> [options]
//   dotnet run --file device-tests.cs -- ios --project <csproj> [options]
//
// Shared options:
//   --project <path>          A device test project. Required. Repeat it, or separate paths with ';' or new lines, to
//                             run several; each then reports into its own subfolder of --results.
//   --workspace <dir>         The folder relative --project and --results paths start from. Default: the current folder.
//   --args <text>             Extra test platform arguments for the iOS app, such as a tree-node filter.
//
// An option given an empty value takes its default.
//   --configuration <name>    Build configuration. Default: Debug.
//   --framework <tfm>         Target framework. Default: the project's first -android or -ios target framework.
//   --results <dir>           Folder for the TRX report, logs and exit code. Default: ./device-test-results/<platform>.
//   --timeout <minutes>       Time allowed for the test run. Default: 30.
//   --keep                    Leave the emulator or simulator running afterwards.
//
// Android options:
//   --avd <name>              The AVD to boot. Default: rxui-device-tests, created when missing.
//   --system-image <package>  The system image for a created AVD. Default: system-images;android-36;google_apis;<host abi>.
//   --port <number>           The emulator console port, which fixes its adb serial. Default: 5580.
//   --boot-timeout <minutes>  Time allowed for the emulator to boot. Default: 15.
//
// iOS options:
//   --device-type <name>      The simulator device type. Default: the newest iPhone the runtime offers.
//   --runtime <identifier>    The simulator runtime. Default: the newest installed iOS runtime.
//
// Android runs through the .NET SDK's own device support: `dotnet test --device <serial>` installs the app, starts its
// instrumentation, streams each result and writes the TRX report on the host. iOS builds the app, installs it on a
// fresh simulator with simctl and launches it with its console attached; the app writes the TRX report and its exit
// code into the results folder, which the simulator shares with the Mac.
//
// Exit codes: 0 when every test passed, the test run's exit code when a test failed, 2 for bad arguments, 3 for a
// host that cannot run the platform, 4 when the device does not start, and 5 when the app crashes or times out.
#:property PublishAot=false

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using static System.Environment;

const int BadArguments = 2;

if (args is not [var platform, .. var rest] || platform is not ("android" or "ios"))
{
    Console.WriteLine("::error::Usage: device-tests.cs <android|ios> --project <csproj> [options]. See the header of device-tests.cs.");
    return BadArguments;
}

if (Options.Parse(rest) is not { } options)
{
    return BadArguments;
}

var workspace = Path.GetFullPath(options.Get("workspace") ?? Directory.GetCurrentDirectory());
string[] projects = [.. options.Projects.Select(p => Path.GetFullPath(p, workspace))];

if (projects is [])
{
    Console.WriteLine("::error::Pass at least one --project.");
    return BadArguments;
}

if (projects.FirstOrDefault(static p => !File.Exists(p)) is { } missing)
{
    Console.WriteLine($"::error::--project must name an existing project file, got '{missing}'.");
    return BadArguments;
}

var root = Path.GetFullPath(options.Get("results") ?? Path.Combine("device-test-results", platform), workspace);
Directory.CreateDirectory(root);

// One project reports straight into the results folder; several get a subfolder each.
Target[] targets =
[
    .. projects.Select(p => new Target(p, projects.Length == 1 ? root : Path.Combine(root, Path.GetFileNameWithoutExtension(p)))),
];
foreach (var target in targets)
{
    Directory.CreateDirectory(target.Results);
}

return platform is "android"
    ? Android.Run(targets, root, options)
    : Apple.Run(targets, options);

/// <summary>A device test project and the folder its results go to.</summary>
internal sealed record Target(string Project, string Results);

/// <summary>Drives the Android emulator and <c>dotnet test --device</c>.</summary>
internal static class Android
{
    public static int Run(IReadOnlyList<Target> targets, string results, Options options)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            Console.WriteLine("::error::The Android emulator runs on Linux, macOS and Windows only.");
            return 3;
        }

        if (OperatingSystem.IsLinux() && !File.Exists("/dev/kvm"))
        {
            Console.WriteLine("::error::/dev/kvm is missing. The Android emulator needs KVM on Linux; enable it or run on a host with hardware virtualization.");
            return 3;
        }

        if (OperatingSystem.IsLinux() && !CanOpenReadWrite("/dev/kvm"))
        {
            Console.WriteLine("::error::/dev/kvm exists but this user cannot open it. Add the user to the kvm group, or on a GitHub runner add a udev rule that opens it (MODE=\"0666\").");
            return 3;
        }

        if (FindSdk() is not { } sdk)
        {
            Console.WriteLine("::error::No Android SDK found. Set ANDROID_HOME or ANDROID_SDK_ROOT.");
            return 3;
        }

        var exe = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var bat = OperatingSystem.IsWindows() ? ".bat" : string.Empty;
        var adb = Path.Combine(sdk, "platform-tools", "adb" + exe);
        var emulator = Path.Combine(sdk, "emulator", "emulator" + exe);
        var cmdlineTools = Path.Combine(sdk, "cmdline-tools", "latest", "bin");
        foreach (var tool in (string[])[adb, emulator])
        {
            if (!File.Exists(tool))
            {
                Console.WriteLine($"::error::{tool} is missing. Install the platform-tools and emulator SDK packages.");
                return 3;
            }
        }

        var port = options.GetInt("port", 5580);
        var serial = $"emulator-{port}";
        var avd = options.Get("avd") ?? "rxui-device-tests";
        var abi = RuntimeInformation.OSArchitecture is Architecture.Arm64 ? "arm64-v8a" : "x86_64";
        var image = options.Get("system-image") ?? $"system-images;android-36;google_apis;{abi}";

        // avdmanager and the emulator can disagree on where AVDs live (hosted runners set ANDROID_USER_HOME and
        // ANDROID_SDK_HOME differently), so pin one folder for every child process.
        var avdHome = GetEnvironmentVariable("ANDROID_AVD_HOME") is { Length: > 0 } configured
            ? configured
            : Path.Combine(GetFolderPath(SpecialFolder.UserProfile), ".android", "avd");
        Directory.CreateDirectory(avdHome);
        SetEnvironmentVariable("ANDROID_AVD_HOME", avdHome);

        if (!AvdExists(emulator, avd))
        {
            Console.WriteLine($"Creating AVD '{avd}' from {image}");
            var imageFolder = Path.Combine([sdk, .. image.Split(';')]);
            if (!Directory.Exists(imageFolder)
                && Process.Run(Path.Combine(cmdlineTools, "sdkmanager" + bat), ["--install", image]) is { ExitCode: not 0 } install)
            {
                Console.WriteLine($"::error::sdkmanager could not install {image} (exit {install.ExitCode}).");
                return 4;
            }

            var create = new ProcessStartInfo(Path.Combine(cmdlineTools, "avdmanager" + bat), ["create", "avd", "--force", "--name", avd, "--package", image, "--device", "pixel_7"])
            {
                RedirectStandardInput = true,
            };
            using (var avdmanager = Process.Start(create)!)
            {
                // avdmanager asks whether to write a custom hardware profile.
                avdmanager.StandardInput.WriteLine("no");
                avdmanager.StandardInput.Close();
                avdmanager.WaitForExit();
                if (avdmanager.ExitCode is not 0)
                {
                    Console.WriteLine($"::error::avdmanager could not create '{avd}' (exit {avdmanager.ExitCode}).");
                    return 4;
                }
            }

            if (!AvdExists(emulator, avd))
            {
                Console.WriteLine($"::error::avdmanager reported success, but the emulator cannot find '{avd}' in {avdHome}.");
                return 4;
            }
        }

        if (Adb(adb, serial, "get-state") is { ExitStatus.ExitCode: 0 })
        {
            Console.WriteLine($"::error::{serial} is already running. Stop it or pass another --port.");
            return 4;
        }

        var emulatorLogPath = Path.Combine(results, "emulator.log");
        using var emulatorLog = File.OpenHandle(emulatorLogPath, FileMode.Create, FileAccess.Write);
        Console.WriteLine($"Booting {avd} as {serial}");

        // -read-only lets this run share an AVD with a copy the developer already has open. The memory and core counts
        // fit a hosted runner and cut a cold boot's time.
        var emulatorPid = Process.StartAndForget(new ProcessStartInfo(
            emulator,
            ["-avd", avd, "-port", port.ToString(CultureInfo.InvariantCulture), "-no-window", "-no-audio", "-no-boot-anim",
             "-gpu", "swiftshader_indirect", "-no-snapshot", "-read-only", "-no-metrics", "-memory", "4096", "-cores", "4",
             "-camera-back", "none", "-camera-front", "none"])
        {
            StartDetached = true,
            StandardInputHandle = File.OpenNullHandle(),
            StandardOutputHandle = emulatorLog,
            StandardErrorHandle = emulatorLog,
        });

        var cancelled = false;
        Console.CancelKeyPress += (_, e) =>
        {
            cancelled = true;
            e.Cancel = true;
            Shutdown(adb, serial, options);
        };

        try
        {
            if (!WaitForBoot(adb, serial, emulatorPid, TimeSpan.FromMinutes(options.GetInt("boot-timeout", 15))))
            {
                Console.WriteLine($"::error::{serial} did not finish booting. emulator.log follows.");
                Console.WriteLine($"::group::emulator.log");
                Console.WriteLine(ReadShared(emulatorLogPath));
                Console.WriteLine("::endgroup::");
                return 4;
            }

            // Animations slow every activity transition the tests drive.
            foreach (var setting in (string[])["window_animation_scale", "transition_animation_scale", "animator_duration_scale"])
            {
                _ = Adb(adb, serial, "shell", "settings", "put", "global", setting, "0");
            }

            var configuration = options.Get("configuration") ?? "Debug";
            var exitCode = 0;
            foreach (var (project, projectResults) in targets)
            {
                if ((options.Get("framework") ?? Frameworks.Find(project, "android")) is not { } framework)
                {
                    Console.WriteLine($"::error::{Path.GetFileName(project)} has no Android target framework. Pass --framework.");
                    exitCode = exitCode is 0 ? 2 : exitCode;
                    continue;
                }

                _ = Adb(adb, serial, "logcat", "-c");

                // dotnet test reads the test runner from the global.json above the project.
                var test = new ProcessStartInfo(
                    "dotnet",
                    ["test", "--project", project, "--framework", framework, "--configuration", configuration,
                     "--device", serial, "--results-directory", projectResults, "--report-trx", "--no-progress"])
                {
                    WorkingDirectory = Path.GetDirectoryName(project),
                };
                test.Environment["ANDROID_HOME"] = sdk;

                Console.WriteLine($"Running {Path.GetFileName(project)} ({framework}, {configuration}) on {serial}");
                var run = Process.Run(test, TimeSpan.FromMinutes(options.GetInt("timeout", 30)));

                CollectLogs(adb, serial, project, framework, projectResults);

                if (cancelled || run.Canceled)
                {
                    Console.WriteLine($"::error::The {Path.GetFileName(project)} run timed out or was cancelled.");
                    return 5;
                }

                if (run.ExitCode is not 0)
                {
                    Console.WriteLine($"::error::{Path.GetFileName(project)} failed (dotnet test exit {run.ExitCode}). The TRX report and logcat.txt are in {projectResults}.");
                    exitCode = exitCode is 0 ? run.ExitCode : exitCode;
                }
            }

            return exitCode;
        }
        finally
        {
            Shutdown(adb, serial, options);
        }
    }

    private static string? FindSdk()
    {
        string?[] candidates =
        [
            GetEnvironmentVariable("ANDROID_HOME"),
            GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Path.Combine(GetFolderPath(SpecialFolder.UserProfile), "Android", "Sdk"),
            Path.Combine(GetFolderPath(SpecialFolder.UserProfile), "Library", "Android", "sdk"),
            Path.Combine(GetFolderPath(SpecialFolder.LocalApplicationData), "Android", "Sdk"),
        ];

        return candidates.FirstOrDefault(static path => path is not (null or "") && Directory.Exists(Path.Combine(path, "platform-tools")));
    }

    private static bool AvdExists(string emulator, string avd) =>
        Process.RunAndCaptureText(emulator, ["-list-avds"]).StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(avd, StringComparer.Ordinal);

    private static ProcessTextOutput Adb(string adb, string serial, params IEnumerable<string> arguments) =>
        Process.RunAndCaptureText(adb, ["-s", serial, .. arguments], TimeSpan.FromMinutes(2));

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool CanOpenReadWrite(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Waits for the boot to complete, and gives up early when the emulator process has already exited.</summary>
    private static bool WaitForBoot(string adb, string serial, int emulatorPid, TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            if (!IsRunning(emulatorPid))
            {
                Console.WriteLine($"::error::The emulator exited before {serial} booted.");
                return false;
            }

            if (Adb(adb, serial, "shell", "getprop", "sys.boot_completed") is { ExitStatus.ExitCode: 0, StandardOutput: var booted } && booted.Trim() is "1")
            {
                Console.WriteLine($"{serial} booted in {Stopwatch.GetElapsedTime(started).TotalSeconds:0}s");
                return true;
            }

            Thread.Sleep(TimeSpan.FromSeconds(3));
        }

        return false;
    }

    private static void CollectLogs(string adb, string serial, string project, string framework, string results)
    {
        File.WriteAllText(Path.Combine(results, "logcat.txt"), Adb(adb, serial, "logcat", "-d", "-v", "threadtime").StandardOutput);

        // The app also writes a TRX on the device, with real test durations; the host report has the same results.
        if (MsBuildProperty(project, framework, "ApplicationId") is { Length: > 0 } applicationId)
        {
            var device = Path.Combine(results, "device");
            Directory.CreateDirectory(device);
            _ = Adb(adb, serial, "pull", $"/sdcard/Android/data/{applicationId}/files/TestResults/.", device);
        }
    }

    private static void Shutdown(string adb, string serial, Options options)
    {
        if (options.Has("keep"))
        {
            Console.WriteLine($"Leaving {serial} running (--keep).");
            return;
        }

        if (Adb(adb, serial, "emu", "kill") is { ExitStatus.ExitCode: 0 })
        {
            Console.WriteLine($"Stopped {serial}");
        }

        var started = Stopwatch.GetTimestamp();
        while (Adb(adb, serial, "get-state") is { ExitStatus.ExitCode: 0 } && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
        {
            Thread.Sleep(TimeSpan.FromSeconds(1));
        }
    }

    internal static string? MsBuildProperty(string project, string framework, string property) =>
        Process.RunAndCaptureText("dotnet", ["msbuild", project, $"-getProperty:{property}", $"-p:TargetFramework={framework}", "-nologo"])
            is { ExitStatus.ExitCode: 0, StandardOutput: var value } ? value.Trim() : null;
}

/// <summary>Drives an iOS simulator with simctl.</summary>
internal static class Apple
{
    public static int Run(IReadOnlyList<Target> targets, Options options)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.WriteLine("::error::iOS device tests need macOS with Xcode: the iOS simulator exists only there. Run the ios leg on a macOS host or runner.");
            return 3;
        }

        if (Process.RunAndCaptureText("xcrun", ["simctl", "list", "--json", "devicetypes", "runtimes"]) is not { ExitStatus.ExitCode: 0, StandardOutput: var listing })
        {
            Console.WriteLine("::error::xcrun simctl is unavailable. Install Xcode and select it with xcode-select.");
            return 3;
        }

        using var json = JsonDocument.Parse(listing);
        var runtime = options.Get("runtime") ?? json.RootElement.GetProperty("runtimes").EnumerateArray()
            .Where(static r => r.GetProperty("isAvailable").GetBoolean() && r.GetProperty("platform").GetString() is "iOS")
            .OrderByDescending(static r => System.Version.Parse(r.GetProperty("version").GetString()!))
            .Select(static r => r.GetProperty("identifier").GetString())
            .FirstOrDefault();
        if (runtime is null)
        {
            Console.WriteLine("::error::No iOS simulator runtime is installed. Install one with xcodebuild -downloadPlatform iOS.");
            return 3;
        }

        // Only the runtime's own supported device types boot on it; the newest iPhone is listed last.
        var deviceType = options.Get("device-type") ?? json.RootElement.GetProperty("runtimes").EnumerateArray()
            .Where(r => r.GetProperty("identifier").GetString() == runtime && r.TryGetProperty("supportedDeviceTypes", out _))
            .SelectMany(static r => r.GetProperty("supportedDeviceTypes").EnumerateArray())
            .Where(static d => d.GetProperty("productFamily").GetString() is "iPhone")
            .Select(static d => d.GetProperty("name").GetString()!)
            .LastOrDefault(static name => !name.Contains(" SE", StringComparison.Ordinal));
        if (deviceType is null)
        {
            Console.WriteLine("::error::No iPhone simulator device type is available.");
            return 3;
        }

        if (Process.RunAndCaptureText("xcrun", ["simctl", "create", "rxui-device-tests", deviceType, runtime]) is not { ExitStatus.ExitCode: 0, StandardOutput: var created })
        {
            Console.WriteLine($"::error::Could not create a '{deviceType}' simulator on {runtime}.");
            return 4;
        }

        var udid = created.Trim();
        Console.WriteLine($"Created simulator {udid} ({deviceType}, {runtime})");
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Shutdown(udid, options);
        };

        try
        {
            if (Process.Run("xcrun", ["simctl", "bootstatus", udid, "-b"], silent: false, timeout: TimeSpan.FromMinutes(options.GetInt("boot-timeout", 10))) is { ExitCode: not 0 })
            {
                Console.WriteLine($"::error::Simulator {udid} did not boot.");
                return 4;
            }

            var exitCode = 0;
            foreach (var target in targets)
            {
                var result = RunApp(udid, target, options);
                exitCode = exitCode is 0 ? result : exitCode;
            }

            return exitCode;
        }
        finally
        {
            Shutdown(udid, options);
        }
    }

    /// <summary>Builds one app, installs it on the booted simulator and runs it to completion.</summary>
    private static int RunApp(string udid, Target target, Options options)
    {
        var (project, results) = target;
        if ((options.Get("framework") ?? Frameworks.Find(project, "ios")) is not { } framework)
        {
            Console.WriteLine($"::error::{Path.GetFileName(project)} has no iOS target framework. Pass --framework.");
            return 2;
        }

        var configuration = options.Get("configuration") ?? "Debug";
        var rid = RuntimeInformation.OSArchitecture is Architecture.Arm64 ? "iossimulator-arm64" : "iossimulator-x64";
        string[] buildProperties = [$"-p:TargetFramework={framework}", $"-p:Configuration={configuration}", $"-p:RuntimeIdentifier={rid}"];

        Console.WriteLine($"Building {Path.GetFileName(project)} ({framework}, {configuration}, {rid})");
        if (Process.Run("dotnet", ["build", project, .. buildProperties]) is { ExitCode: not 0 } build)
        {
            Console.WriteLine($"::error::{Path.GetFileName(project)} did not build (exit {build.ExitCode}).");
            return build.ExitCode;
        }

        // AppBundleDir is set by a build target, so evaluation alone often returns nothing; fall back to the one
        // .app folder the build left in the output path, which evaluation does know.
        var appBundle = Property(project, buildProperties, "AppBundleDir") is { Length: > 0 } bundleDir
            ? Path.GetFullPath(bundleDir, Path.GetDirectoryName(project)!)
            : FindAppBundle(project, Property(project, buildProperties, "OutputPath"));
        var bundleId = Property(project, buildProperties, "ApplicationId");
        if (appBundle is null || bundleId is null || !Directory.Exists(appBundle))
        {
            Console.WriteLine($"::error::Could not find the built app bundle ('{appBundle}') or its bundle id ('{bundleId}').");
            return 5;
        }

        if (Process.Run("xcrun", ["simctl", "install", udid, appBundle]) is { ExitCode: not 0 } install)
        {
            Console.WriteLine($"::error::simctl could not install {appBundle} (exit {install.ExitCode}).");
            return 5;
        }

        var exitCodeFile = Path.Combine(results, "exit-code.txt");
        File.Delete(exitCodeFile);

        // --console-pty keeps simctl attached until the app exits. simctl hands SIMCTL_CHILD_* variables to the app
        // without the prefix.
        var launch = new ProcessStartInfo("xcrun", ["simctl", "launch", "--console-pty", "--terminate-running-process", udid, bundleId])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        launch.Environment["SIMCTL_CHILD_RXUI_DEVICE_TEST_RESULTS"] = results;
        if (options.Get("args") is { } extra)
        {
            launch.Environment["SIMCTL_CHILD_RXUI_DEVICE_TEST_ARGS"] = extra;
        }

        Console.WriteLine($"Running {bundleId} on {udid}");
        var run = Process.RunAndCaptureText(launch, TimeSpan.FromMinutes(options.GetInt("timeout", 30)));
        File.WriteAllText(Path.Combine(results, "console.log"), run.StandardOutput + run.StandardError);
        Console.Write(run.StandardOutput);

        var processName = Path.GetFileNameWithoutExtension(appBundle);
        var log = Process.RunAndCaptureText("xcrun", ["simctl", "spawn", udid, "log", "show", "--style", "compact", "--last", "30m", "--predicate", $"process == \"{processName}\""]);
        File.WriteAllText(Path.Combine(results, "simulator.log"), log.StandardOutput);

        if (run.ExitStatus.Canceled)
        {
            Console.WriteLine($"::error::{Path.GetFileName(project)} timed out.");
            return 5;
        }

        if (!File.Exists(exitCodeFile) || !int.TryParse(File.ReadAllText(exitCodeFile).Trim(), CultureInfo.InvariantCulture, out var exitCode))
        {
            Console.WriteLine($"::error::{Path.GetFileName(project)} ended without writing its exit code, so it crashed. See console.log and simulator.log in {results}.");
            ReportCrash(Path.GetFileName(project), run.StandardOutput + run.StandardError, log.StandardOutput);
            return 5;
        }

        if (exitCode is not 0)
        {
            Console.WriteLine($"::error::{Path.GetFileName(project)} failed (exit {exitCode}). The TRX report is in {results}.");
        }

        return exitCode;
    }

    // Lines that start a crash report: the .NET runtime's fatal error and unhandled exception blocks, and UIKit's
    // uncaught Objective-C exception.
    private static readonly string[] CrashMarkers =
        ["Fatal error.", "Unhandled exception", "Got a SIG", "Terminating app due to uncaught exception", "[ERROR]"];

    // Prints where a crashed app got to and the crash itself as one annotation, so the job summary shows the cause
    // without opening the artifact.
    private static void ReportCrash(string app, string console, string simulatorLog)
    {
        var lines = console.Split('\n', StringSplitOptions.TrimEntries);
        var passed = lines.Count(static line => line.Contains("[PASSED]", StringComparison.Ordinal));
        var failed = lines.Count(static line => line.Contains("[FAILED]", StringComparison.Ordinal));
        var lastResult = lines.LastOrDefault(static line => line.Contains("[PASSED]", StringComparison.Ordinal) || line.Contains("[FAILED]", StringComparison.Ordinal) || line.Contains("[SKIPPED]", StringComparison.Ordinal));
        Console.WriteLine($"{app} reported {passed} passed and {failed} failed tests before it ended.");
        if (lastResult is not null)
        {
            Console.WriteLine($"Last finished test: {lastResult}");
        }

        var crash = FindCrash(lines) ?? FindCrash(simulatorLog.Split('\n', StringSplitOptions.TrimEntries));
        if (crash is null)
        {
            Console.WriteLine($"::error::{app} left no crash report in its console or the simulator log.");
            return;
        }

        Console.WriteLine($"::error title={app} crashed::{Escape(string.Join('\n', crash))}");

        static string[]? FindCrash(string[] lines)
        {
            var start = Array.FindIndex(lines, static line => CrashMarkers.Any(marker => line.Contains(marker, StringComparison.Ordinal)));
            return start < 0 ? null : lines[start..Math.Min(lines.Length, start + 25)];
        }

        static string Escape(string text) =>
            text.Replace("%", "%25", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);
    }

    private static string? Property(string project, string[] buildProperties, string property) =>
        Process.RunAndCaptureText("dotnet", ["msbuild", project, $"-getProperty:{property}", .. buildProperties, "-nologo"])
            is { ExitStatus.ExitCode: 0, StandardOutput: var value } && value.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    // Looks for the .app folder the build wrote: first in the evaluated output path, then anywhere under the project's
    // bin folder (the newest one wins). Prints what it searched when it finds nothing, so a CI failure explains itself.
    private static string? FindAppBundle(string project, string? outputPath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(project))!;
        var outputDirectory = outputPath is null ? null : Path.GetFullPath(outputPath, projectDirectory);
        if (outputDirectory is not null && Directory.Exists(outputDirectory)
            && Directory.EnumerateDirectories(outputDirectory, "*.app", SearchOption.TopDirectoryOnly).FirstOrDefault() is { } direct)
        {
            return direct;
        }

        var binDirectory = Path.Combine(projectDirectory, "bin");
        var found = Directory.Exists(binDirectory)
            ? Directory.EnumerateDirectories(binDirectory, "*.app", SearchOption.AllDirectories)
                .Where(static path => !Path.GetDirectoryName(path)!.EndsWith(".app", StringComparison.Ordinal))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (found is null)
        {
            Console.WriteLine($"No .app folder found. OutputPath evaluated to '{outputPath}' ({outputDirectory}); searched {binDirectory}.");
            if (Directory.Exists(binDirectory))
            {
                foreach (var directory in Directory.EnumerateDirectories(binDirectory, "*", SearchOption.AllDirectories).Take(40))
                {
                    Console.WriteLine($"  {Path.GetRelativePath(projectDirectory, directory)}");
                }
            }
        }

        return found;
    }

    private static void Shutdown(string udid, Options options)
    {
        if (options.Has("keep"))
        {
            Console.WriteLine($"Leaving simulator {udid} running (--keep).");
            return;
        }

        _ = Process.Run("xcrun", ["simctl", "shutdown", udid], silent: true);
        _ = Process.Run("xcrun", ["simctl", "delete", udid], silent: true);
        Console.WriteLine($"Deleted simulator {udid}");
    }
}

/// <summary>Finds a project's target framework for a platform.</summary>
internal static class Frameworks
{
    /// <summary>Returns the project's first target framework for <paramref name="platform"/>, such as <c>net11.0-android37</c>.</summary>
    public static string? Find(string project, string platform) =>
        Process.RunAndCaptureText("dotnet", ["msbuild", project, "-getProperty:TargetFrameworks", "-getProperty:TargetFramework", "-nologo"]) is { ExitStatus.ExitCode: 0, StandardOutput: var output }
            ? JsonElement.Parse(output).GetProperty("Properties").EnumerateObject()
                .SelectMany(static p => (p.Value.GetString() ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .FirstOrDefault(tfm => tfm.Contains($"-{platform}", StringComparison.OrdinalIgnoreCase))
            : null;
}

/// <summary>The <c>--name value</c> and <c>--flag</c> options after the platform.</summary>
internal sealed class Options
{
    private static readonly HashSet<string> Flags = ["keep"];

    private static readonly HashSet<string> Valued =
        ["workspace", "configuration", "framework", "results", "timeout", "avd", "system-image", "port", "boot-timeout", "device-type", "runtime", "args"];

    private readonly Dictionary<string, string?> _values = [with(StringComparer.Ordinal)];

    private readonly List<string> _projects = [];

    /// <summary>Gets the projects, from every <c>--project</c>; a value may hold several, one per line or separated by <c>;</c>.</summary>
    public IReadOnlyList<string> Projects => _projects;

    public static Options? Parse(IReadOnlyList<string> arguments)
    {
        var options = new Options();
        for (var i = 0; i < arguments.Count; i++)
        {
            var name = arguments[i].StartsWith("--", StringComparison.Ordinal) ? arguments[i][2..] : null;
            if (name is not null && Flags.Contains(name))
            {
                options._values[name] = null;
            }
            else if (name is "project" && i + 1 < arguments.Count)
            {
                options._projects.AddRange(arguments[++i].Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else if (name is not null && Valued.Contains(name) && i + 1 < arguments.Count)
            {
                options._values[name] = arguments[++i];
            }
            else
            {
                Console.WriteLine($"::error::Unknown or incomplete option '{arguments[i]}'.");
                return null;
            }
        }

        return options;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    /// <summary>Gets an option's value; an empty value counts as not given, so a workflow can pass an unset input through.</summary>
    public string? Get(string name) => _values.GetValueOrDefault(name) is { Length: > 0 } value ? value : null;

    public int GetInt(string name, int fallback) =>
        Get(name) is { } value && int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}
