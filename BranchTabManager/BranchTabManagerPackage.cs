extern alias EnvDTE;
extern alias EnvDTE80;
extern alias Interop;

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;

using EnvDTE80 = EnvDTE80.DTE2;
using EnvDTE = EnvDTE.DTE;
using Document = EnvDTE.Document;
using DocumentEvents = EnvDTE.DocumentEvents;
using DTEEvents = EnvDTE.DTEEvents;
using SolutionEvents = EnvDTE.SolutionEvents;
using vsSaveChanges = EnvDTE.vsSaveChanges;

using IVsActivityLog = Interop.Microsoft.VisualStudio.Shell.Interop.IVsActivityLog;
using SVsActivityLog = Interop.Microsoft.VisualStudio.Shell.Interop.SVsActivityLog;
using UIContextGuids80 = Interop.Microsoft.VisualStudio.Shell.Interop.UIContextGuids80;
using __ACTIVITYLOG_ENTRYTYPE = Interop.Microsoft.VisualStudio.Shell.Interop.__ACTIVITYLOG_ENTRYTYPE;


namespace BranchTabManager
{
    // Register the package so that it autoloads when a solution exists.
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class BranchTabManagerPackage : AsyncPackage, IAsyncDisposable
    {
        private EnvDTE80 _dte;
        private DocumentEvents _documentEvents;
        private DTEEvents _DTEEvents;
        private SolutionEvents _solutionEvents;
        private string _solutionDir;
        private string _repoPath;      // Repository root (where .git exists)
        private string _switchTabDir;  // Folder to store JSON files
        private string _currentBranch;
        private GitBranchWatcher _branchWatcher;
        private IVsActivityLog _activityLog;
        private bool _isSwitchingBranch = false;
        private HashSet<string> _openDocuments = new HashSet<string>();
        private CancellationTokenSource _saveDebounceTokenSource;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // Switch to the UI thread as we’ll be calling DTE APIs.
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _dte = await GetServiceAsync(typeof(EnvDTE)) as EnvDTE80;
            if (_dte == null)
                return;

            _activityLog = await GetServiceAsync(typeof(SVsActivityLog)) as IVsActivityLog;

            // Ensure a solution is loaded.
            if (_dte.Solution == null || string.IsNullOrEmpty(_dte.Solution.FullName))
                return;

            // Get the solution folder.
            _solutionDir = Path.GetDirectoryName(_dte.Solution.FullName);
            if (string.IsNullOrEmpty(_solutionDir))
                return;

            // Find the repository root by searching upward for a .git folder.
            _repoPath = FindRepositoryPath(_solutionDir);
            if (string.IsNullOrEmpty(_repoPath))
            {
                // The solution is not in a Git repository; nothing to do.
                return;
            }

            // Create the .switchtab folder under the solution if it does not exist.
            _switchTabDir = Path.Combine(_solutionDir, ".switchtab");
            if (!Directory.Exists(_switchTabDir))
            {
                Directory.CreateDirectory(_switchTabDir);
            }

            _documentEvents = _dte.Events.DocumentEvents;
            _DTEEvents = _dte.Events.DTEEvents;
            _solutionEvents = _dte.Events.SolutionEvents;
            _documentEvents.DocumentClosing += OnDocumentClosing;
            _documentEvents.DocumentOpening += OnDocumentOpened;
            _DTEEvents.OnBeginShutdown += OnIDEShutdown;
            _solutionEvents.Opened += OnSolutionOpened;
            _solutionEvents.BeforeClosing += OnSolutionClosing;

            // Setup the GitBranchWatcher to detect branch changes.
            _branchWatcher = new GitBranchWatcher(Path.Combine(_repoPath, ".git"));
            _branchWatcher.BranchChanged += OnBranchChanged;
            _branchWatcher.Start();

            // Initial load
            await OnSolutionOpenedAsync();

            // Clean up any .switchtab JSON files whose branch no longer exists.
            CleanupSwitchTabFiles();
        }

        private void OnDocumentClosing(Document document)
        {
            if (document.FullName != null && _openDocuments.Contains(document.FullName))
            {
                _openDocuments.Remove(document.FullName);
                ScheduleSave();
            }
        }

        private void OnDocumentOpened(string path, bool readOnly)
        {
            if (!string.IsNullOrEmpty(path) && Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                _openDocuments.Add(path);
                ScheduleSave();
            }
        }

        private void ScheduleSave()
        {
            _saveDebounceTokenSource?.Cancel();
            _saveDebounceTokenSource = new CancellationTokenSource();

            _ = Task.Delay(500, _saveDebounceTokenSource.Token).ContinueWith(async t =>
            {
                if (!t.IsFaulted && !t.IsCanceled)
                {
                    await SaveOpenDocumentsForCurrentBranchAsync();
                }
            }, TaskScheduler.Default);
        }

        private void OnIDEShutdown()
        {
            _branchWatcher?.Stop();
        }

        private void OnBranchChanged(string newBranchOrState, bool isRemote)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                if (isRemote)
                {
                    CleanupSwitchTabFiles();
                }
                else
                {
                    await HandleBranchChangeAsync();
                }
            });
        }

        private async Task SaveOpenDocumentsForCurrentBranchAsync()
        {
            if (_isSwitchingBranch) return;

            await JoinableTaskFactory.SwitchToMainThreadAsync();

            var currentBranch = await GetCurrentBranchNameAsync();
            if (string.IsNullOrEmpty(currentBranch)) return;

            try
            {
                var json = JsonConvert.SerializeObject(_openDocuments.ToList(), Formatting.Indented);
                var filePath = GetBranchFilePath(currentBranch);
                await WriteAllTextAsync(filePath, json);
            }
            catch (Exception ex)
            {
                LogToActivityLog($"Error saving document list for branch '{currentBranch}': {ex.Message}", __ACTIVITYLOG_ENTRYTYPE.ALE_ERROR);
            }
        }

        private void OnSolutionOpened()
        {
            _ = OnSolutionOpenedAsync();
        }

        private async Task OnSolutionOpenedAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            _currentBranch = await GetCurrentBranchNameAsync();
            await LoadOpenDocumentsForBranchAsync(_currentBranch);
        }

        private void OnSolutionClosing()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = SaveOpenDocumentsForCurrentBranchAsync();
            _branchWatcher?.Stop();
        }

        private async Task LoadOpenDocumentsForCurrentBranchAsync()
        {
            string branch = await GetCurrentBranchNameAsync();
            await LoadOpenDocumentsForBranchAsync(branch);
        }

        private async Task LoadOpenDocumentsForBranchAsync(string branch)
        {
            if (string.IsNullOrEmpty(branch)) return;

            await JoinableTaskFactory.SwitchToMainThreadAsync();

            _isSwitchingBranch = true;
            _currentBranch = branch;

            try
            {
                var filePath = GetBranchFilePath(branch);
                if (!File.Exists(filePath)) return;

                var json = await ReadAllTextAsync(filePath);
                var targetDocuments = new HashSet<string>(JsonConvert.DeserializeObject<List<string>>(json));

                var currentDocuments = new HashSet<string>(_dte.Documents.Cast<Document>().Select(d => d.FullName));

                var documentsToClose = currentDocuments.Except(targetDocuments).ToList();
                var documentsToOpen = targetDocuments.Except(currentDocuments).ToList();

                foreach (var docPath in documentsToClose)
                {
                    var doc = _dte.Documents.Item(docPath);
                    doc.Close(vsSaveChanges.vsSaveChangesPrompt);
                }

                foreach (var docPath in documentsToOpen)
                {
                    if (File.Exists(docPath))
                    {
                        _dte.ItemOperations.OpenFile(docPath);
                    }
                }

                _openDocuments = new HashSet<string>(targetDocuments);
            }
            catch (Exception ex)
            {
                LogToActivityLog($"Error loading documents for branch '{branch}': {ex.Message}", __ACTIVITYLOG_ENTRYTYPE.ALE_ERROR);
            }
            finally
            {
                _isSwitchingBranch = false;
            }
        }

        private void CleanupSwitchTabFiles()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!Directory.Exists(_switchTabDir))
                    return;

                string gitBranchesDir = Path.Combine(_repoPath, ".git", "refs", "heads");
                List<string> branchNames = new List<string>();
                if (Directory.Exists(gitBranchesDir))
                {
                    var branchFiles = Directory.EnumerateFiles(gitBranchesDir, "*", SearchOption.AllDirectories);
                    branchNames.AddRange(branchFiles.Select(file =>
                        file.Substring(gitBranchesDir.Length + 1).Replace('\\', '/')
                    ));
                }
                else
                {
                    Console.WriteLine("The .git/refs/heads directory was not found.");
                }

                var jsonFiles = Directory.GetFiles(_switchTabDir, "*.json");
                foreach (var file in jsonFiles)
                {
                    string branchNameFromFile = GetBranchNameFromFileName(file);
                    if (!branchNames.Contains(branchNameFromFile))
                    {
                        File.Delete(file);
                        LogToActivityLog($"Cleaned up tab data for deleted branch: {branchNameFromFile}", __ACTIVITYLOG_ENTRYTYPE.ALE_INFORMATION);
                    }
                }
            }
            catch (Exception ex)
            {
                LogToActivityLog($"Error during cleanup of tab files: {ex.Message}", __ACTIVITYLOG_ENTRYTYPE.ALE_WARNING);
            }
        }

        private async Task HandleBranchChangeAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            await SaveOpenDocumentsForCurrentBranchAsync();
            await LoadOpenDocumentsForCurrentBranchAsync();
            CleanupSwitchTabFiles();
        }

        private async Task<string> GetCurrentBranchNameAsync()
        {
            string headFile = Path.Combine(_repoPath, ".git", "HEAD");

            if (File.Exists(headFile))
            {
                string headContent = (await ReadAllTextAsync(headFile)).Trim();
                const string refPrefix = "ref: refs/heads/";
                if (headContent.StartsWith(refPrefix))
                {
                    return headContent.Substring(refPrefix.Length).Replace('\\', '/');
                }
                else
                {
                    return "DETACHED_HEAD";
                }
            }
            else
            {
                LogToActivityLog("The .git/HEAD file was not found.", __ACTIVITYLOG_ENTRYTYPE.ALE_WARNING);
                return null;
            }
        }

        private string FindRepositoryPath(string startPath)
        {
            string current = startPath;
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(Path.Combine(current, ".git")))
                    return current;
                DirectoryInfo parent = Directory.GetParent(current);
                current = parent?.FullName;
            }
            return null;
        }

        private string GetBranchFilePath(string branchName)
        {
            string safeFileName = $"{branchName.Replace('/', '-')}.json";
            return Path.Combine(_switchTabDir, safeFileName);
        }

        private string GetBranchNameFromFileName(string filePath)
        {
            return Path.GetFileNameWithoutExtension(filePath).Replace('-', '/');
        }

        private void LogToActivityLog(string message, __ACTIVITYLOG_ENTRYTYPE type)
        {
            _activityLog?.LogEntry((uint)type, "BranchTabManager", message);
        }

        private static async Task<string> ReadAllTextAsync(string filePath)
        {
            using (var reader = new StreamReader(filePath))
            {
                return await reader.ReadToEndAsync();
            }
        }

        private static async Task WriteAllTextAsync(string filePath, string content)
        {
            using (var writer = new StreamWriter(filePath, false)) // false to overwrite
            {
                await writer.WriteAsync(content);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_documentEvents != null)
            {
                _documentEvents.DocumentClosing -= OnDocumentClosing;
                _documentEvents.DocumentOpening -= OnDocumentOpened;
            }
            if (_DTEEvents != null)
            {
                _DTEEvents.OnBeginShutdown -= OnIDEShutdown;
            }
            if (_solutionEvents != null)
            {
                _solutionEvents.Opened -= OnSolutionOpened;
                _solutionEvents.BeforeClosing -= OnSolutionClosing;
            }

            if (_branchWatcher != null)
            {
                _branchWatcher.Dispose();
                _branchWatcher = null;
            }
        }
    }
}
