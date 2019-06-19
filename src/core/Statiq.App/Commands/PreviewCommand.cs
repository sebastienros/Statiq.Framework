﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Cli;
using Statiq.App.Tracing;
using Statiq.Common.Configuration;
using Statiq.Common.Execution;
using Statiq.Common.IO;
using Statiq.Common.Meta;
using Statiq.Common.Tracing;
using Statiq.Hosting;

namespace Statiq.App.Commands
{
    [Description("Builds the site and serves it, optionally watching for changes and rebuilding by default.")]
    public class PreviewCommand : BaseCommand<PreviewCommand.Settings>
    {
        public class Settings : BuildCommand.Settings
        {
            [CommandOption("-p|--port")]
            [Description("Start the preview web server on the specified port (default is 5080).")]
            public int Port { get; set; } = 5080;

            [CommandOption("--force-ext")]
            [Description("Force the use of extensions in the preview web server (by default, extensionless URLs may be used).")]
            public bool ForceExt { get; set; }

            [CommandOption("--virtual-dir")]
            [Description("Serve files in the preview web server under the specified virtual directory.")]
            public string VirtualDirectory { get; set; }

            [CommandOption("--content-type")]
            [Description("Specifies additional supported content types for the preview server as extension=contenttype.")]
            public string[] ContentTypes { get; set; }

            [CommandOption("--no-watch")]
            [Description("Turns off watching the input folder(s) for changes and rebuilding.")]
            public bool NoWatch { get; set; }

            [CommandOption("--no-reload")]
            [Description("urns off LiveReload support in the preview server.")]
            public bool NoReload { get; set; }
        }

        private readonly IConfigurableBootstrapper _bootstrapper;
        private readonly IServiceProvider _serviceProvider;

        private readonly ConcurrentQueue<string> _changedFiles = new ConcurrentQueue<string>();
        private readonly AutoResetEvent _messageEvent = new AutoResetEvent(false);
        private readonly InterlockedBool _exit = new InterlockedBool(false);

        public PreviewCommand(IConfigurableBootstrapper bootstrapper, IServiceProvider serviceProvider)
        {
            _bootstrapper = bootstrapper;
            _serviceProvider = serviceProvider;
        }

        public override async Task<int> ExecuteCommandAsync(CommandContext context, Settings settings)
        {
            ExitCode exitCode = ExitCode.Normal;

            using (EngineManager engineManager = new EngineManager(_bootstrapper, settings))
            {
                // Execute the engine for the first time
                if (!await engineManager.ExecuteAsync(_serviceProvider))
                {
                    return (int)ExitCode.ExecutionError;
                }

                // Start the preview server
                Dictionary<string, string> contentTypes = settings.ContentTypes?.Length > 0
                    ? GetContentTypes(settings.ContentTypes)
                    : new Dictionary<string, string>();
                Server previewServer = await StartPreviewServerAsync(
                    (await engineManager.Engine.FileSystem.GetOutputDirectoryAsync()).Path,
                    settings.Port,
                    settings.ForceExt,
                    settings.VirtualDirectory,
                    !settings.NoReload,
                    contentTypes);

                // Start the watchers
                ActionFileSystemWatcher inputFolderWatcher = null;
                if (!settings.NoWatch)
                {
                    Trace.Information("Watching paths(s) {0}", string.Join(", ", engineManager.Engine.FileSystem.InputPaths));
                    inputFolderWatcher = new ActionFileSystemWatcher(
                        (await engineManager.Engine.FileSystem.GetOutputDirectoryAsync()).Path,
                        (await engineManager.Engine.FileSystem.GetInputDirectoriesAsync()).Select(x => x.Path),
                        true,
                        "*.*",
                        path =>
                        {
                            _changedFiles.Enqueue(path);
                            _messageEvent.Set();
                        });
                }

                // Start the message pump

                // Only wait for a key if console input has not been redirected, otherwise it's on the caller to exit
                if (!Console.IsInputRedirected)
                {
                    // Start the key listening thread
                    Thread thread = new Thread(() =>
                    {
                        Trace.Information("Hit Ctrl-C to exit");
                        Console.TreatControlCAsInput = true;
                        while (true)
                        {
                            // Would have prefered to use Console.CancelKeyPress, but that bubbles up to calling batch files
                            // The (ConsoleKey)3 check is to support a bug in VS Code: https://github.com/Microsoft/vscode/issues/9347
                            ConsoleKeyInfo consoleKey = Console.ReadKey(true);
                            if (consoleKey.Key == (ConsoleKey)3 || (consoleKey.Key == ConsoleKey.C && (consoleKey.Modifiers & ConsoleModifiers.Control) != 0))
                            {
                                _exit.Set();
                                _messageEvent.Set();
                                break;
                            }
                        }
                    })
                    {
                        IsBackground = true
                    };
                    thread.Start();
                }

                // Wait for activity
                while (true)
                {
                    _messageEvent.WaitOne(); // Blocks the current thread until a signal
                    if (_exit)
                    {
                        break;
                    }

                    // Execute if files have changed
                    HashSet<string> changedFiles = new HashSet<string>();
                    while (_changedFiles.TryDequeue(out string changedFile))
                    {
                        if (changedFiles.Add(changedFile))
                        {
                            Trace.Verbose("{0} has changed", changedFile);
                        }
                    }
                    if (changedFiles.Count > 0)
                    {
                        Trace.Information("{0} files have changed, re-executing", changedFiles.Count);

                        // Reset caches when an error occurs during the previous preview
                        object existingResetCacheSetting = null;
                        bool setResetCacheSetting = false;
                        if (exitCode == ExitCode.ExecutionError)
                        {
                            existingResetCacheSetting = engineManager.Engine.Settings.GetValueOrDefault(Keys.ResetCache);
                            setResetCacheSetting = true;
                            engineManager.Engine.Settings[Keys.ResetCache] = true;
                        }

                        // If there was an execution error due to reload, keep previewing but clear the cache
                        exitCode = await engineManager.ExecuteAsync(_serviceProvider)
                            ? ExitCode.Normal
                            : ExitCode.ExecutionError;

                        // Reset the reset cache setting after removing it
                        if (setResetCacheSetting)
                        {
                            if (existingResetCacheSetting == null)
                            {
                                engineManager.Engine.Settings.Remove(Keys.ResetCache);
                            }
                            {
                                engineManager.Engine.Settings[Keys.ResetCache] = existingResetCacheSetting;
                            }
                        }

                        await previewServer.TriggerReloadAsync();
                    }

                    // Check one more time for exit
                    if (_exit)
                    {
                        break;
                    }
                    Trace.Information("Hit Ctrl-C to exit");
                    _messageEvent.Reset();
                }

                // Shutdown
                Trace.Information("Shutting down");
                inputFolderWatcher?.Dispose();
                previewServer.Dispose();
            }

            return (int)exitCode;
        }

        private static Dictionary<string, string> GetContentTypes(string[] contentTypes)
        {
            Dictionary<string, string> contentTypeDictionary = new Dictionary<string, string>();
            foreach (string contentType in contentTypes)
            {
                string[] splitContentType = contentType.Split('=');
                if (splitContentType.Length != 2)
                {
                    throw new Exception($"Invalid content type {contentType} specified.");
                }
                contentTypeDictionary[splitContentType[0].Trim().Trim('\"')] = splitContentType[1].Trim().Trim('\"');
            }
            return contentTypeDictionary;
        }

        private static async Task<Server> StartPreviewServerAsync(DirectoryPath path, int port, bool forceExtension, DirectoryPath virtualDirectory, bool liveReload, IDictionary<string, string> contentTypes)
        {
            Server server;
            try
            {
                server = new Server(path.FullPath, port, !forceExtension, virtualDirectory?.FullPath, liveReload, contentTypes, new TraceLoggerProvider());
                await server.StartAsync();
            }
            catch (Exception ex)
            {
                Trace.Critical($"Error while running preview server: {ex}");
                return null;
            }

            string urlPath = server.VirtualDirectory ?? string.Empty;
            Trace.Information($"Preview server listening at http://localhost:{port}{urlPath} and serving from path {path}"
                + (liveReload ? " with LiveReload support" : string.Empty));
            return server;
        }
    }
}