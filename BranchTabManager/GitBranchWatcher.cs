using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BranchTabManager
{
    public class GitBranchWatcher : IDisposable
    {
        private readonly string _gitPath;
        private readonly string _headFile;
        private readonly string _remotePath;
        private string _lastHeadContent = "";
        private string _lastRemoteState = "";
        private CancellationTokenSource _cancellationTokenSource;
        private int _pollingInterval = 1000;  // Start with 1 second polling
        private const int MaxPollingInterval = 10000; // Maximum polling interval of 10 seconds

        // Event to notify branch or remote changes
        public event Action<string, bool> BranchChanged;

        public GitBranchWatcher(string gitPath)
        {
            _gitPath = gitPath;
            _headFile = Path.Combine(_gitPath, "HEAD");
            _remotePath = Path.Combine(_gitPath, "refs", "remotes", "origin");
        }

        public void Start()
        {
            // Initial state capture
            _lastHeadContent = GetCurrentBranch();
            _lastRemoteState = GetRemoteState();
            Console.WriteLine("🟢 Current branch: " + _lastHeadContent);

            // Start the polling loop asynchronously
            _cancellationTokenSource = new CancellationTokenSource();
            _ = Task.Run(() => WatchLoopAsync(_cancellationTokenSource.Token));
        }

        public void Stop()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
            }
        }

        private async Task WatchLoopAsync(CancellationToken cancellationToken)
        {
            int noChangeCycles = 0; // Count how many cycles with no change

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(_pollingInterval, cancellationToken);

                    bool changed = CheckForChanges();

                    if (changed)
                    {
                        noChangeCycles = 0;
                        _pollingInterval = 1000; // Reset polling interval to default
                    }
                    else
                    {
                        // Increase polling interval after several cycles without change (exponential backoff)
                        noChangeCycles++;
                        if (noChangeCycles > 3 && _pollingInterval < MaxPollingInterval)
                        {
                            _pollingInterval = Math.Min(_pollingInterval * 2, MaxPollingInterval);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when Stop() is called, so we can ignore it.
            }
            catch (Exception ex)
            {
                // Log any other unexpected error to prevent the watcher from crashing silently.
                Console.WriteLine($"🔴 Error in GitBranchWatcher loop: {ex.Message}");
            }
        }

        private string GetCurrentBranch()
        {
            string headContent = ReadFileContent(_headFile);
            const string refPrefix = "ref: refs/heads/";
            if (headContent?.StartsWith(refPrefix) == true)
            {
                // Retourne le chemin relatif de la branche, ex: "main" ou "feature/my-feature"
                return headContent.Substring(refPrefix.Length);
            }
            return "(Detached HEAD)";
        }

        private string GetRemoteState()
        {
            if (!Directory.Exists(_remotePath))
                return "";

            try
            {
                // Build a state string from remote branch names and their commit hashes.
                // This detects new/deleted branches and updates to existing branches.
                var remoteFiles = Directory.EnumerateFiles(_remotePath, "*", SearchOption.AllDirectories)
                                           .OrderBy(f => f); // Sort for consistent order

                var remoteStateParts = remoteFiles.Select(file => $"{file.Replace(_remotePath + Path.DirectorySeparatorChar, "")}:{ReadFileContent(file)}");
                return string.Join(";", remoteStateParts);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"🔴 Error getting remote state: {ex.Message}");
                return ""; // Return empty string on error to avoid crash loops
            }
        }

        private string ReadFileContent(string filePath)
        {
            if (File.Exists(filePath))
            {
                try
                {
                    return File.ReadAllText(filePath).Trim();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"🔴 Error reading file {filePath}: {ex.Message}");
                }
            }
            return null;
        }

        private bool CheckForChanges()
        {
            bool hasChanged = false;

            // Check for changes in the current branch (HEAD)
            string newHead = GetCurrentBranch();
            if (_lastHeadContent != newHead)
            {
                _lastHeadContent = newHead;
                BranchChanged?.Invoke(newHead, false);
                hasChanged = true;
            }

            // Check for changes in the remote state
            string newRemoteState = GetRemoteState();
            if (_lastRemoteState != newRemoteState)
            {
                _lastRemoteState = newRemoteState;
                BranchChanged?.Invoke(null, true); // The specific state isn't needed, just the notification.
                hasChanged = true;
            }

            return hasChanged;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
