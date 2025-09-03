using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using EnvDTE;
using EnvDTE80;
using Newtonsoft.Json;

using Microsoft.VisualStudio.Shell.Interop;

namespace BranchTabManager
{
    // Register the package so that it autoloads when a solution exists.
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class BranchTabManagerPackage : AsyncPackage
    {
        private DTE2 _dte;
        private DocumentEvents _documentEvents;
        private DTEEvents _DTEEvents;
        private string _solutionDir;
        private string _repoPath;      // Repository root (where .git exists)
        private string _switchTabDir;  // Folder to store JSON files
        private string _currentBranch;  // Folder to store JSON files
        private GitBranchWatcher _branchWatcher;
        private IVsActivityLog _activityLog;
        private bool _isSwitchingBranch = false; // Flag to prevent saving while loading tabs for a new branch

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // Switch to the UI thread as we’ll be calling DTE APIs.
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _dte = await GetServiceAsync(typeof(DTE)) as DTE2;
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
            _documentEvents.DocumentClosing += OnDocumentClosing;
            _documentEvents.DocumentOpening += OnDocumentOpened;
            _DTEEvents.OnBeginShutdown += OnIDEShutdown;
            _dte.Events.SolutionEvents.Opened += OnSolutionOpened;
            _dte.Events.SolutionEvents.BeforeClosing += OnSolutionClosing;

            // Setup the GitBranchWatcher to detect branch changes.
            _branchWatcher = new GitBranchWatcher(Path.Combine(_repoPath, ".git"));
            _branchWatcher.BranchChanged += OnBranchChanged;
            _branchWatcher.Start();

            // Initial load
            await OnSolutionOpenedAsync();

            // Clean up any .switchtab JSON files whose branch no longer exists.
            CleanupSwitchTabFiles();
        }

        /// <summary>
        /// Called whenever any document is saved.
        /// Saves the list of open document file paths into a JSON file for the current branch.
        /// </summary>
        private void OnDocumentClosing(Document document)
        {
            // Schedule the save to allow multiple documents to close before saving.
            // Using a small delay to batch operations.
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(500); // Debounce
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                await SaveOpenDocumentsForCurrentBranchAsync();
            });
        }

        private void OnDocumentOpened(string path, bool readOnly)
        {
            // Schedule the save.
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await System.Threading.Tasks.Task.Delay(500); // Debounce
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                await SaveOpenDocumentsForCurrentBranchAsync();
            });
        }

        private void OnIDEShutdown()
        {
            _branchWatcher?.Stop();
        }

        /// <summary>
        /// Called when the Git HEAD file changes. This indicates that the current branch may have changed.
        /// The handler delays slightly (to let Git finish writing) then loads the stored open documents
        /// and cleans up JSON files for branches that have been deleted.
        /// </summary>
        private void OnBranchChanged(string newBranchOrState, bool isRemote)
        {
            // The event is fired from a background thread. Switch to the main thread.
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

        /// <summary>
        /// Saves the full paths of all open documents into a JSON file whose name is the current branch.
        /// </summary>
        private async Task SaveOpenDocumentsForCurrentBranchAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            // Do not save the tab list if we are in the middle of switching branches
            if (_isSwitchingBranch) return;

            try
            {
                string branch = await GetCurrentBranchNameAsync();
                if (string.IsNullOrEmpty(branch)) return;

                _currentBranch = branch;

                List<string> openFiles = new List<string>();

                foreach (Window window in _dte.Windows)
                {
                    if (window.Kind == "Document" && window.Document != null)
                    {
                        // Only save documents that are linked to a file on disk.
                        if (!string.IsNullOrEmpty(window.Document.FullName) &&
                            window.Document.ActiveWindow != null &&
                            Uri.TryCreate(window.Document.FullName, UriKind.Absolute, out var uri) &&
                            uri.IsFile)
                        {
                            openFiles.Add(window.Document.FullName);
                        }
                    }
                }

                string json = JsonConvert.SerializeObject(openFiles, Formatting.Indented);
                string filePath = GetBranchFilePath(branch);
                await WriteAllTextAsync(filePath, json);
            }
            catch (Exception ex)
            {
                LogToActivityLog($"Error saving document list for branch '{_currentBranch}': {ex.Message}", __ACTIVITYLOG_ENTRYTYPE.ALE_ERROR);
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

        /// <summary>
        /// If a JSON file for the current branch exists in the .switchtab folder,
        /// loads the list of file paths and opens them.
        /// </summary>
        private async Task LoadOpenDocumentsForCurrentBranchAsync()
        {
            string branch = await GetCurrentBranchNameAsync();
            await LoadOpenDocumentsForBranchAsync(branch);
        }

        private async Task LoadOpenDocumentsForBranchAsync(string branch)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            if (string.IsNullOrEmpty(branch)) return;

            _isSwitchingBranch = true;
            _currentBranch = branch;

            try
            {
                string filePath = GetBranchFilePath(branch);

                if (File.Exists(filePath))
                {
                    string json = await ReadAllTextAsync(filePath);
                    var openFiles = JsonConvert.DeserializeObject<List<string>>(json);

                    // Close all currently open documents.
                    _dte.Documents.CloseAll(vsSaveChanges.vsSaveChangesPrompt);

                    if (openFiles != null)
                    {
                        foreach (string file in openFiles)
                        {
                            if (File.Exists(file))
                            {
                                _dte.ItemOperations.OpenFile(file);
                            }
                        }
                    }
                }
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

        /// <summary>
        /// Deletes any .switchtab JSON file whose filename (i.e. branch name)
        /// does not match any branch in the current repository.
        /// </summary>
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
                        file.Substring(gitBranchesDir.Length + 1) // +1 for the path separator
                            .Replace('\\', '/') // Normalize to forward slashes
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
                        // If the branch no longer exists, delete its JSON file.
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

            // Save tabs for the old branch before switching
            await SaveOpenDocumentsForCurrentBranchAsync();

            // Load tabs for the new branch
            await LoadOpenDocumentsForCurrentBranchAsync();

            CleanupSwitchTabFiles();
        }

        /// <summary>
        /// Reads the .git/HEAD file to get the current branch name.
        /// </summary>
        private async Task<string> GetCurrentBranchNameAsync()
        {
            string headFile = Path.Combine(_repoPath, ".git", "HEAD");

            if (File.Exists(headFile))
            {
                // Read the contents of the HEAD file
                string headContent = (await ReadAllTextAsync(headFile)).Trim();
                const string refPrefix = "ref: refs/heads/";
                // Extract the branch name
                if (headContent.StartsWith(refPrefix))
                {
                    return headContent.Substring(refPrefix.Length).Replace('\\', '/');
                }
                else
                {
                    // Handle detached HEAD state, maybe return the commit hash or a special value
                    return "DETACHED_HEAD";
                }
            }
            else
            {
                LogToActivityLog("The .git/HEAD file was not found.", __ACTIVITYLOG_ENTRYTYPE.ALE_WARNING);
                return null;
            }
        }

        /// <summary>
        /// Walks upward from the given path until it finds a folder that contains a ".git" subfolder.
        /// </summary>
        /// <param name="startPath">The starting directory.</param>
        /// <returns>The path to the repository root, or null if not found.</returns>
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

        #region Helper Methods

        private string GetBranchFilePath(string branchName)
        {
            // Replace invalid file path characters from branch name.
            string safeFileName = $"{branchName.Replace('/', '-')}.json";
            return Path.Combine(_switchTabDir, safeFileName);
        }

        private string GetBranchNameFromFileName(string filePath)
        {
            // Convert the file name back to the original branch name.
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

        #endregion

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _branchWatcher?.Stop();
            }
            base.Dispose(disposing);
        }
    }
}