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
using LibGit2Sharp;
using Task = System.Threading.Tasks.Task;
using Microsoft.VisualStudio.Shell.Interop;

namespace SwitchTabExtension
{
    // Register the package so that it autoloads when a solution exists.
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class SwitchTabPackage : AsyncPackage
    {
        private DTE2 _dte;
        private DocumentEvents _documentEvents;
        private string _solutionDir;
        private string _repoPath;      // Repository root (where .git exists)
        private string _switchTabDir;  // Folder to store JSON files
        private FileSystemWatcher _gitHeadWatcher;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // Switch to the UI thread as we’ll be calling DTE APIs.
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _dte = await GetServiceAsync(typeof(DTE)) as DTE2;
            if (_dte == null)
                return;

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

            // Subscribe to document save events.
            _documentEvents = _dte.Events.DocumentEvents;
            _documentEvents.DocumentSaved += OnDocumentSaved;

            // Setup a FileSystemWatcher to detect changes in the Git HEAD file
            // (which indicates the current branch has changed).
            string gitDir = Path.Combine(_repoPath, ".git");
            if (Directory.Exists(gitDir))
            {
                _gitHeadWatcher = new FileSystemWatcher
                {
                    Path = gitDir,
                    Filter = "HEAD",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };
                _gitHeadWatcher.Changed += OnGitHeadChanged;
                _gitHeadWatcher.Created += OnGitHeadChanged;
                _gitHeadWatcher.EnableRaisingEvents = true;
            }

            // (Optional) When the solution opens, load the stored open files for the current branch.
            LoadOpenDocumentsForCurrentBranch();

            // Clean up any .switchtab JSON files whose branch no longer exists.
            CleanupSwitchTabFiles();
        }

        /// <summary>
        /// Called whenever any document is saved.
        /// Saves the list of open document file paths into a JSON file for the current branch.
        /// </summary>
        private void OnDocumentSaved(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SaveOpenDocumentsForCurrentBranch();
        }

        /// <summary>
        /// Called when the Git HEAD file changes. This indicates that the current branch may have changed.
        /// The handler delays slightly (to let Git finish writing) then loads the stored open documents
        /// and cleans up JSON files for branches that have been deleted.
        /// </summary>
        private void OnGitHeadChanged(object sender, FileSystemEventArgs e)
        {
            // Delay a short time to ensure the HEAD file is fully updated.
            Task.Delay(500).ContinueWith(_ =>
            {
                ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    LoadOpenDocumentsForCurrentBranch();
                    CleanupSwitchTabFiles();
                });
            });
        }

        /// <summary>
        /// Saves the full paths of all open documents into a JSON file whose name is the current branch.
        /// </summary>
        private void SaveOpenDocumentsForCurrentBranch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                string branch = GetCurrentBranchName();
                if (string.IsNullOrEmpty(branch))
                    return;

                List<string> openFiles = new List<string>();
                foreach (Document doc in _dte.Documents)
                {
                    if (!string.IsNullOrEmpty(doc.FullName))
                    {
                        openFiles.Add(doc.FullName);
                    }
                }

                string json = JsonConvert.SerializeObject(openFiles, Formatting.Indented);
                string filePath = Path.Combine(_switchTabDir, $"{branch}.json");
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                // Optionally log or handle errors.
            }
        }

        /// <summary>
        /// If a JSON file for the current branch exists in the .switchtab folder,
        /// loads the list of file paths and opens them.
        /// </summary>
        private void LoadOpenDocumentsForCurrentBranch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                string branch = GetCurrentBranchName();
                if (string.IsNullOrEmpty(branch))
                    return;

                string filePath = Path.Combine(_switchTabDir, $"{branch}.json");
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    List<string> openFiles = JsonConvert.DeserializeObject<List<string>>(json);
                    if (openFiles != null)
                    {
                        foreach (string file in openFiles)
                        {
                            if (File.Exists(file))
                            {
                                // This will open the file in the editor.
                                _dte.ItemOperations.OpenFile(file);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Optionally log or handle errors.
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

                using (var repo = new Repository(_repoPath))
                {
                    // Get all branch names from the repo.
                    var branchNames = repo.Branches.Select(b => b.FriendlyName)
                                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var jsonFiles = Directory.GetFiles(_switchTabDir, "*.json");
                    foreach (var file in jsonFiles)
                    {
                        // The file name (without extension) is used as the branch name.
                        string branchName = Path.GetFileNameWithoutExtension(file);
                        if (!branchNames.Contains(branchName))
                        {
                            // If the branch no longer exists, delete its JSON file.
                            File.Delete(file);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Optionally log or handle errors.
            }
        }

        /// <summary>
        /// Opens the Git repository (via LibGit2Sharp) and returns the current branch name.
        /// </summary>
        private string GetCurrentBranchName()
        {
            try
            {
                using (var repo = new Repository(_repoPath))
                {
                    return repo.Head.FriendlyName;
                }
            }
            catch (Exception ex)
            {
                // Optionally log or handle errors.
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_gitHeadWatcher != null)
                {
                    _gitHeadWatcher.Dispose();
                    _gitHeadWatcher = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
