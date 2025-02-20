using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BranchTabManager
{
    public class GitBranchWatcher
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
            Task.Run(() => WatchLoop(_cancellationTokenSource.Token));
        }

        public void Stop()
        {
            _cancellationTokenSource?.Cancel();
        }

        private async Task WatchLoop(CancellationToken cancellationToken)
        {
            int noChangeCycles = 0; // Count how many cycles with no change

            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_pollingInterval, cancellationToken); // Non-blocking wait

                // Check for changes in the current branch
                string newHead = GetCurrentBranch();
                if (_lastHeadContent != newHead)
                {
                    _lastHeadContent = newHead;
                    BranchChanged?.Invoke(newHead, false); // Trigger branch change event
                    noChangeCycles = 0; // Reset no change cycles when a change happens
                    _pollingInterval = 1000; // Reset polling interval to default
                }
                else
                {
                    // Increase polling interval after several cycles without change
                    noChangeCycles++;
                    if (noChangeCycles > 3 && _pollingInterval < MaxPollingInterval)
                    {
                        _pollingInterval = Math.Min(_pollingInterval * 2, MaxPollingInterval); // Exponential backoff
                    }
                }

                // Check for changes in the remote state
                string newRemoteState = GetRemoteState();
                if (_lastRemoteState != newRemoteState)
                {
                    _lastRemoteState = newRemoteState;
                    BranchChanged?.Invoke(newRemoteState, true); // Trigger remote state change event
                    noChangeCycles = 0; // Reset no change cycles when a change happens
                    _pollingInterval = 1000; // Reset polling interval to default
                }
            }
        }

        private string GetCurrentBranch()
        {
            string headContent = ReadFileContent(_headFile);
            return headContent?.StartsWith("ref:") == true ? headContent.Substring(16) : "(Detached HEAD)";
        }

        private string GetRemoteState()
        {
            if (!Directory.Exists(_remotePath))
                return "";

            // Efficiently build remote state string by reading file names
            var remoteState = string.Join("|", Directory.GetFiles(_remotePath, "*", SearchOption.AllDirectories));
            return remoteState;
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
                    Console.WriteLine($"Error reading file {filePath}: {ex.Message}");
                }
            }
            return null;
        }
    }
}
