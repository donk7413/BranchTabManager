using System;
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
        private FileSystemWatcher _headWatcher;
        private Timer _remoteWatcherTimer;
        private string _lastHeadContent;
        private string _lastRemoteState;

        public event Action<string, bool> BranchChanged;

        public GitBranchWatcher(string gitPath)
        {
            _gitPath = gitPath;
            _headFile = Path.Combine(_gitPath, "HEAD");
            _remotePath = Path.Combine(_gitPath, "refs", "remotes");
        }

        public void Start()
        {
            _lastHeadContent = GetCurrentBranch();
            _lastRemoteState = GetRemoteState();

            _headWatcher = new FileSystemWatcher(Path.GetDirectoryName(_headFile), Path.GetFileName(_headFile));
            _headWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
            _headWatcher.Changed += OnHeadChanged;
            _headWatcher.Created += OnHeadChanged;
            _headWatcher.Renamed += OnHeadChanged;
            _headWatcher.EnableRaisingEvents = true;

            _remoteWatcherTimer = new Timer(CheckRemoteState, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public void Stop()
        {
            if (_headWatcher != null)
            {
                _headWatcher.EnableRaisingEvents = false;
                _headWatcher.Dispose();
                _headWatcher = null;
            }

            if (_remoteWatcherTimer != null)
            {
                _remoteWatcherTimer.Dispose();
                _remoteWatcherTimer = null;
            }
        }

        private void OnHeadChanged(object sender, FileSystemEventArgs e)
        {
            Task.Delay(100).ContinueWith(_ =>
            {
                var newHead = GetCurrentBranch();
                if (newHead != _lastHeadContent)
                {
                    _lastHeadContent = newHead;
                    BranchChanged?.Invoke(newHead, false);
                }
            });
        }

        private void CheckRemoteState(object state)
        {
            var newRemoteState = GetRemoteState();
            if (newRemoteState != _lastRemoteState)
            {
                _lastRemoteState = newRemoteState;
                BranchChanged?.Invoke(null, true);
            }
        }

        private string GetCurrentBranch()
        {
            try
            {
                if (File.Exists(_headFile))
                {
                    var headContent = File.ReadAllText(_headFile).Trim();
                    const string refPrefix = "ref: refs/heads/";
                    if (headContent.StartsWith(refPrefix))
                    {
                        return headContent.Substring(refPrefix.Length);
                    }
                    return "(Detached HEAD)";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"🔴 Error reading git HEAD file: {ex.Message}");
            }
            return null;
        }

        private string GetRemoteState()
        {
            if (!Directory.Exists(_remotePath))
                return "";

            try
            {
                var remoteFiles = Directory.EnumerateFiles(_remotePath, "*", SearchOption.AllDirectories)
                                           .OrderBy(f => f);

                var remoteStateParts = remoteFiles.Select(file => $"{file.Replace(_remotePath + Path.DirectorySeparatorChar, "")}:{File.ReadAllText(file).Trim()}");
                return string.Join(";", remoteStateParts);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"🔴 Error getting remote state: {ex.Message}");
                return "";
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
