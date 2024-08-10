﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using sfw.net;
using Unity.Editor.Tasks.Logging;

namespace Unity.VersionControl.Git
{
    using IO;

    public interface IRepositoryWatcher : IDisposable
    {
        void Start();
        void Stop();
        event Action HeadChanged;
        event Action IndexChanged;
        event Action ConfigChanged;
        event Action RepositoryCommitted;
        event Action RepositoryChanged;
        event Action LocalBranchesChanged;
        event Action RemoteBranchesChanged;
        void Initialize();
        int CheckAndProcessEvents();
    }

    public class RepositoryWatcher : IRepositoryWatcher
    {
        private readonly RepositoryPathConfiguration paths;
        private readonly CancellationToken cancellationToken;
        private readonly SPath[] ignoredPaths;
        private readonly ManualResetEventSlim pauseEvent;
        private NativeInterface nativeInterface;
        private NativeInterface worktreeNativeInterface;
        private bool running;
        private int lastCountOfProcessedEvents = 0;
        private bool processingEvents;
        private readonly ManualResetEventSlim signalProcessingEventsDone = new ManualResetEventSlim(false);
        private readonly SPath projectPath;
        private readonly SPath assetsPath;

        public event Action HeadChanged;
        public event Action IndexChanged;
        public event Action ConfigChanged;
        public event Action RepositoryCommitted;
        public event Action RepositoryChanged;
        public event Action LocalBranchesChanged;
        public event Action RemoteBranchesChanged;

        public RepositoryWatcher(IPlatform platform, RepositoryPathConfiguration paths, CancellationToken cancellationToken)
        {
            this.paths = paths;
            this.cancellationToken = cancellationToken;

            projectPath = platform.Environment.UnityProjectPath.ToSPath();
            assetsPath = projectPath.Combine("Assets");
            ignoredPaths = new[] {
                projectPath.Combine("Library"),
                projectPath.Combine("Temp"),
                projectPath.Combine(".vs"),
                projectPath.Combine(".idea")
            };

            pauseEvent = new ManualResetEventSlim();
            //disableNative = !platform.Environment.IsWindows;
        }

        public void Initialize()
        {
            var pathsRepositoryPath = paths.RepositoryPath.ToString();

            try
            {
                nativeInterface = new NativeInterface(pathsRepositoryPath);

                if (paths.IsWorktree)
                {
                    worktreeNativeInterface = new NativeInterface(paths.WorktreeDotGitPath);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        public void Start()
        {
            if (nativeInterface == null)
            {
                Logger.Warning("NativeInterface is null");
                throw new InvalidOperationException("NativeInterface is null");
            }

            Logger.Trace("Watching Path: \"{0}\"", paths.RepositoryPath.ToString());

            if (paths.IsWorktree)
            {
                if (worktreeNativeInterface == null)
                {
                    Logger.Warning("Worktree NativeInterface is null");
                    throw new InvalidOperationException("Worktree NativeInterface is null");
                }

                Logger.Trace("Watching Additional Path for Worktree: \"{0}\"", paths.WorktreeDotGitPath);
            }

            running = true;
            pauseEvent.Reset();
            Task.Factory.StartNew(WatcherLoop, cancellationToken, TaskCreationOptions.None, TaskScheduler.Default);
        }

        public void Stop()
        {
            if (!running)
                return;

            running = false;
            pauseEvent.Set();
        }

        private void WatcherLoop()
        {
            while (running)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Stop();
                    break;
                }

                CheckAndProcessEvents();

                if (pauseEvent.Wait(1000))
                {
                    break;
                }
            }
        }

        public int CheckAndProcessEvents()
        {
            if (processingEvents)
            {
                signalProcessingEventsDone.Wait(cancellationToken);
                return lastCountOfProcessedEvents;
            }

            signalProcessingEventsDone.Reset();
            processingEvents = true;
            var processedEventCount = 0;

            var fileEvents = nativeInterface.GetEvents();
            if (fileEvents.Length > 0)
            {
                processedEventCount = ProcessEvents(fileEvents);
            }

            if (worktreeNativeInterface != null)
            {
                fileEvents = worktreeNativeInterface.GetEvents();
                if (fileEvents.Length > 0)
                {
                    processedEventCount = processedEventCount + ProcessEvents(fileEvents);
                }
            }

            lastCountOfProcessedEvents = processedEventCount;
            processingEvents = false;
            signalProcessingEventsDone.Set();

            return processedEventCount;
        }

        private int ProcessEvents(Event[] fileEvents)
        {
            var events = new HashSet<EventType>();
            foreach (var fileEvent in fileEvents)
            {
                if (!running)
                {
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    Stop();
                    break;
                }

                var eventDirectory = new SPath(fileEvent.Directory);
                var fileA = eventDirectory.Combine(fileEvent.FileA);

                // handling events in .git/*
                if (fileA.IsChildOf(paths.DotGitPath) || (paths.WorktreeDotGitPath.IsInitialized && fileA.IsChildOf(paths.WorktreeDotGitPath)))
                {
                    if (!events.Contains(EventType.ConfigChanged) && fileA.Equals(paths.DotGitConfig))
                    {
                        events.Add(EventType.ConfigChanged);
                    }
                    else if (!events.Contains(EventType.HeadChanged) && fileA.Equals(paths.DotGitHead))
                    {
                        events.Add(EventType.HeadChanged);
                    }
                    else if (!events.Contains(EventType.IndexChanged) && fileA.Equals(paths.DotGitIndex))
                    {
                        events.Add(EventType.IndexChanged);
                    }
                    else if (!events.Contains(EventType.RemoteBranchesChanged) && fileA.IsChildOf(paths.RemotesPath))
                    {
                        events.Add(EventType.RemoteBranchesChanged);
                    }
                    else if (!events.Contains(EventType.LocalBranchesChanged) && fileA.IsChildOf(paths.BranchesPath))
                    {
                        events.Add(EventType.LocalBranchesChanged);
                    }
                    else if (!events.Contains(EventType.RepositoryCommitted) && fileA.IsChildOf(paths.DotGitCommitEditMsg))
                    {
                        events.Add(EventType.RepositoryCommitted);
                    }
                }
                else
                {
                    if (events.Contains(EventType.RepositoryChanged) ||
                        fileA.FileName.StartsWith("~UnityDirMonSync") ||
                        fileA.FileName.StartsWith(".vs") ||
                        fileA.DirectoryExists() ||
                        ignoredPaths.Any(ignoredPath => fileA.IsChildOf(ignoredPath)))
                    {
                        continue;
                    }
                    events.Add(EventType.RepositoryChanged);
                }
            }

            return FireEvents(events);
        }

        private int FireEvents(HashSet<EventType> events)
        {
            int eventsProcessed = 0;
            if (events.Contains(EventType.ConfigChanged))
            {
                ConfigChanged?.Invoke();
                eventsProcessed++;
            }

            if (events.Contains(EventType.HeadChanged))
            {
                HeadChanged?.Invoke();
                eventsProcessed++;
            }

            if (events.Contains(EventType.LocalBranchesChanged))
            {
                LocalBranchesChanged?.Invoke();
                eventsProcessed++;
            }

            if (events.Contains(EventType.RemoteBranchesChanged))
            {
                RemoteBranchesChanged?.Invoke();
                eventsProcessed++;
            }

            if (events.Contains(EventType.IndexChanged))
            {
                if (!events.Contains(EventType.RepositoryChanged))
                {
                    IndexChanged?.Invoke();
                    eventsProcessed++;
                }
            }

            if (events.Contains(EventType.RepositoryChanged))
            {
                RepositoryChanged?.Invoke();
                eventsProcessed++;
            }

            if (events.Contains(EventType.RepositoryCommitted))
            {
                RepositoryCommitted?.Invoke();
                eventsProcessed++;
            }

            return eventsProcessed;
        }

        private bool disposed;
        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!disposed)
                {
                    disposed = true;
                    HeadChanged = null;
                    IndexChanged = null;
                    ConfigChanged = null;
                    RepositoryCommitted = null;
                    RepositoryChanged = null;
                    LocalBranchesChanged = null;
                    RemoteBranchesChanged = null;

                    Stop();
                    if (nativeInterface != null)
                    {
                        nativeInterface.Dispose();
                        nativeInterface = null;
                    }
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected static ILogging Logger { get; } = LogHelper.GetLogger<RepositoryWatcher>();

        private enum EventType
        {
            None,
            ConfigChanged,
            HeadChanged,
            IndexChanged,
            LocalBranchesChanged,
            RemoteBranchesChanged,
            RepositoryChanged,
            RepositoryCommitted
        }
    }
}
