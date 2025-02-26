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

using Task = System.Threading.Tasks.Task;
using Microsoft.VisualStudio.Shell.Interop;
using BranchTabManager;

namespace SwitchTabExtension
{
    // Register the package so that it autoloads when a solution exists.
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class SwitchTabPackage : AsyncPackage
    {
        private DTE2 _dte;
        private DocumentEvents _documentEvents;
        private DTEEvents _DTEEvents;
        private string _solutionDir;
        private string _repoPath;      // Repository root (where .git exists)
        private string _switchTabDir;  // Folder to store JSON files
        private string _currentBranch;  // Folder to store JSON files
        private FileSystemWatcher _gitHeadWatcher;
        private bool _isloaded = true;
        private bool _closeIDE = false;

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

            _documentEvents = _dte.Events.DocumentEvents;
            _DTEEvents = _dte.Events.DTEEvents;
            _documentEvents.DocumentClosing += OnDocumentClosing;
            _documentEvents.DocumentOpening += OnDocumentOpened;
            _DTEEvents.OnBeginShutdown += OnIDEShutdown;
            _dte.Events.SolutionEvents.Opened += FirstLoad;
            _dte.Events.SolutionEvents.BeforeClosing += SolutionUnloaded;
            // Setup a FileSystemWatcher to detect changes in the Git HEAD file
            // (which indicates the current branch has changed).
            string gitHeadFile = Path.Combine(_repoPath, ".git", "HEAD");

            if (File.Exists(gitHeadFile))
            {
                GitBranchWatcher watcher = new GitBranchWatcher(Path.Combine(_repoPath, ".git"));
                watcher.BranchChanged += OnGitHeadChanged;  // Abonnement à l'événement
                watcher.Start();
            }
            _currentBranch = "";

            // Clean up any .switchtab JSON files whose branch no longer exists.
            CleanupSwitchTabFiles();
        }

        /// <summary>
        /// Called whenever any document is saved.
        /// Saves the list of open document file paths into a JSON file for the current branch.
        /// </summary>
        private void OnDocumentClosing(Document document)
        {
            Task.Delay(2000).ContinueWith(_ =>
            {
                ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();

                    CheckAndRefreshCurrentBranch();

                    if (!_isloaded) return;
                    SaveDocument();
                });
            });
        }

        private void CheckAndRefreshCurrentBranch()
        {
            string branch = GetCurrentBranchName();
            _isloaded = branch == _currentBranch;
            if (!_isloaded)
            {
                if (_currentBranch == "") _currentBranch = branch;
            }
        }

        private void OnDocumentOpened(string path, bool readOnly)
        {
            CheckAndRefreshCurrentBranch();
            if (!_isloaded) return;
            Task.Delay(500).ContinueWith(_ =>
            {
                ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    SaveDocument();
                });
            });
        }

        private void SaveDocument()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SaveOpenDocumentsForCurrentBranch();
        }

        private void OnIDEShutdown()
        {
            _closeIDE = true;
        }

        /// <summary>
        /// Called when the Git HEAD file changes. This indicates that the current branch may have changed.
        /// The handler delays slightly (to let Git finish writing) then loads the stored open documents
        /// and cleans up JSON files for branches that have been deleted.
        /// </summary>
        private void OnGitHeadChanged(string newBranch, bool isRemote)
        {
            _isloaded = false;

            Task.Delay(1000).ContinueWith(_ =>
            {
                ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    LoadOpenDocumentsForCurrentBranch();
                    CleanupSwitchTabFiles();
                    Task.Delay(500).Wait();  // Laisser un délai pour éviter un conflit
                    _isloaded = true;
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
                if (string.IsNullOrEmpty(branch)) return;

                if (_currentBranch != branch)
                {
                    _currentBranch = branch;
                }

                List<string> openFiles = new List<string>();

                foreach (Window window in _dte.Windows)
                {
                    if (window.Kind == "Document" && window.Document != null)
                    {
                        Document doc = window.Document;
                        if (!string.IsNullOrEmpty(doc.FullName))
                        {
                            openFiles.Add(doc.FullName);
                        }
                    }
                }

                if (openFiles.Count == 0) return; // Évite d'écraser le JSON avec une liste vide

                string json = JsonConvert.SerializeObject(openFiles, Formatting.Indented);
                string filePath = Path.Combine(_switchTabDir, $"{branch}.json");
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error in SaveOpenDocumentsForCurrentBranch: " + ex.Message);
            }
        }

        private void FirstLoad()
        {
            _isloaded = true;
            LoadOpenDocumentsForCurrentBranch();
        }

        private void SolutionUnloaded()
        {
            _isloaded = false;
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
                if (string.IsNullOrEmpty(branch)) return;

                if (_currentBranch != branch)
                {
                    _currentBranch = branch;  // Mise à jour correcte de la branche
                    string filePath = Path.Combine(_switchTabDir, $"{branch}.json");

                    if (File.Exists(filePath))
                    {
                        string json = File.ReadAllText(filePath);
                        List<string> openFiles = JsonConvert.DeserializeObject<List<string>>(json);

                        // Fermer tous les fichiers actuels avant d'ouvrir les nouveaux
                        foreach (Document doc in _dte.Documents)
                        {
                            doc.Close(vsSaveChanges.vsSaveChangesYes);
                        }

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
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error in LoadOpenDocumentsForCurrentBranch: " + ex.Message);
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

                string gitBranchesDir = Path.Combine(_solutionDir, ".git", "refs", "heads");
                List<string> branchNames = new List<string>();
                if (Directory.Exists(gitBranchesDir))
                {
                    // Get all the branch files from the refs/heads folder
                    string[] branchFiles = Directory.GetFiles(gitBranchesDir, "*", SearchOption.AllDirectories);

                    if (branchFiles.Length > 0)
                    {
                        Console.WriteLine("Branches:");
                        foreach (string branchFile in branchFiles)
                        {
                            // Extract the branch name from the file path
                            string branchName = branchFile.Replace(gitBranchesDir + "\\", "").Replace('\\', '-');
                            Console.WriteLine(branchName);
                            branchNames.Add(branchName);
                        }
                    }
                    else
                    {
                        Console.WriteLine("No branches found.");
                    }
                }
                else
                {
                    Console.WriteLine("The .git/refs/heads directory was not found.");
                }

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
            // Détermine automatiquement le chemin du dépôt Git à partir du dossier de la solution.

            string gitDir = Path.Combine(_solutionDir, ".git", "HEAD");

            if (File.Exists(gitDir))
            {
                // Read the contents of the HEAD file
                string headContent = File.ReadAllText(gitDir).Trim();

                // Extract the branch name
                if (headContent.StartsWith("ref: refs/heads/"))
                {
                    string branchName = headContent.Substring("ref: refs/heads/".Length);
                    Console.WriteLine($"Current branch: {branchName}");
                    return branchName.Replace('/', '-');
                }
                else
                {
                    Console.WriteLine("The HEAD file doesn't indicate a branch (detached HEAD or other state).");
                    return null;
                }
            }
            else
            {
                Console.WriteLine("The .git/HEAD file was not found.");
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