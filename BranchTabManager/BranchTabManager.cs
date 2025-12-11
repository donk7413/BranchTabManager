using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;
// On retire les références EnvDTE explicites pour éviter la guerre des clones !
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio;

namespace BranchTabManager
{
    // C'est ici que la magie opère Johnny !
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)] // On charge dès qu'une solution est là
    public sealed class BranchTabManager : AsyncPackage, IVsRunningDocTableEvents
    {
        public const string PackageGuidString = "a1b2c3d4-e5f6-7890-1234-56789abcdef0"; // Change ça pour un vrai GUID unique !

        // On utilise 'dynamic' pour contourner les conflits de types (EnvDTE vs Microsoft.VisualStudio.Interop)
        private dynamic _dte;

        private string _solutionDir;
        private FileSystemWatcher _gitWatcher;
        private string _currentBranch;
        private readonly string _storeDirName = ".switchtab";

        // Le RDT (Running Document Table) : Le registre officiel de VS
        private IVsRunningDocumentTable _rdt;
        private uint _rdtCookie;

        // Output Window pour les logs
        private IVsOutputWindowPane _outputPane;
        // GUID personnalisé pour NOTRE panneau de log (plus facile à trouver)
        private Guid _outputPaneGuid = new Guid("E16E2D96-5C0B-4D43-88A2-3A68F4750123");

        // Timers
        private System.Threading.Timer _saveDebouncer;
        private System.Threading.Timer _gitDebouncer;

        // Constante locale pour éviter d'importer EnvDTE.vsSaveChanges
        private const int vsSaveChangesPrompt = 3;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // On switch sur le thread UI
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Init des logs AVEC un panneau personnalisé
            await SetupOutputWindowAsync();
            await LogAsync("InitializeAsync : Démarrage de l'extension SwitchTab...");

            _dte = await GetServiceAsync(typeof(SDTE)) as dynamic;

            // Récupération du service RDT pour espionner les fichiers
            _rdt = await GetServiceAsync(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            if (_rdt != null)
            {
                _rdt.AdviseRunningDocTableEvents(this, out _rdtCookie);
            }

            if (_dte == null)
            {
                await LogAsync("Erreur : DTE est null !");
                return;
            }

            // Si une solution est déjà ouverte
            try
            {
                if (_dte.Solution.IsOpen)
                {
                    // On lance le processus d'ouverture de solution (avec délai)
                    OnSolutionOpened();
                }
            }
            catch (Exception ex)
            {
                await LogAsync($"Erreur lors de la vérification de solution ouverte : {ex.Message}");
            }
        }

        private async Task SetupOutputWindowAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            IVsOutputWindow outputWindow = await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (outputWindow != null)
            {
                // On CRÉE notre propre panneau "SwitchTab Extension" pour qu'il apparaisse dans la liste !
                string customPaneName = "SwitchTab Extension";
                outputWindow.CreatePane(ref _outputPaneGuid, customPaneName, 1, 1);

                // On récupère le panneau qu'on vient de créer
                outputWindow.GetPane(ref _outputPaneGuid, out _outputPane);

                // On l'active pour que tu le voies direct (Optionnel, peut être gênant si trop intrusif)
                // _outputPane.Activate(); 
            }
        }

        private async Task LogAsync(string message)
        {
            if (_outputPane != null)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                _outputPane.OutputStringThreadSafe($"[SwitchTab] {DateTime.Now:HH:mm:ss} : {message}\n");
            }
        }

        // --- Implémentation de IVsRunningDocTableEvents ---

        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        {
            TriggerSave("Ouverture d'un document détectée");
            return VSConstants.S_OK;
        }

        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
        {
            TriggerSave("Fermeture d'un document détectée");
            return VSConstants.S_OK;
        }

        public int OnAfterSave(uint docCookie) { return VSConstants.S_OK; }
        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) { return VSConstants.S_OK; }

        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame)
        {
            if (fFirstShow != 0) TriggerSave("Affichage fenêtre document");
            return VSConstants.S_OK;
        }

        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) { return VSConstants.S_OK; }

        // --- Fin IVsRunningDocTableEvents ---

        private void TriggerSave(string reason)
        {
            // Délai de 5 secondes après l'action (Open/Close) pour être sûr que c'est stable
            if (_saveDebouncer == null)
            {
                _saveDebouncer = new System.Threading.Timer(SaveTabsCallback, reason, 5000, Timeout.Infinite);
            }
            else
            {
                _saveDebouncer.Change(5000, Timeout.Infinite);
            }
        }

        private void SaveTabsCallback(object state)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                string reason = state as string;
                // Vérification de sécurité
                if (string.IsNullOrEmpty(_currentBranch)) return;

                try
                {
                    if (_dte.Solution.IsOpen)
                    {
                        await LogAsync($"Sauvegarde automatique déclenchée ({reason}) sur la branche {_currentBranch}...");
                        SaveTabs(_currentBranch);
                    }
                }
                catch { }
            });
        }

        private void OnSolutionOpened()
        {
            // On lance ça en async pour ne pas bloquer l'UI
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await Task.Delay(5000); // On attend 5 secondes que VS charge tout son bazar
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                try
                {
                    await LogAsync("Solution ouverte. Initialisation...");
                    string fullName = _dte.Solution.FullName;
                    _solutionDir = Path.GetDirectoryName(fullName);
                }
                catch (Exception ex)
                {
                    await LogAsync($"Erreur récupération dossier solution : {ex.Message}");
                    return;
                }

                if (string.IsNullOrEmpty(_solutionDir)) return;

                // Setup du watcher sur le dossier .git
                SetupGitWatcher();

                // On charge la branche actuelle
                _currentBranch = GetCurrentGitBranch();
                await LogAsync($"Branche initiale détectée : {_currentBranch}");

                // Au chargement de la solution : Unload All -> Reload
                if (!string.IsNullOrEmpty(_currentBranch))
                {
                    await LogAsync("Fermeture préventive des onglets...");
                    CloseAllTabs();
                    await LogAsync("Restauration des onglets de la branche...");
                    await RestoreTabs(_currentBranch); // Ajout du await ici
                }
            });
        }

        private void OnSolutionClosing()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Pas de log ici, output window peut être déjà détruit
            if (_gitWatcher != null)
            {
                _gitWatcher.EnableRaisingEvents = false;
                _gitWatcher.Dispose();
                _gitWatcher = null;
            }
            if (_rdt != null && _rdtCookie != 0)
            {
                _rdt.UnadviseRunningDocTableEvents(_rdtCookie);
            }
            DisposeTimers();
        }

        private void DisposeTimers()
        {
            if (_saveDebouncer != null) { _saveDebouncer.Dispose(); _saveDebouncer = null; }
            if (_gitDebouncer != null) { _gitDebouncer.Dispose(); _gitDebouncer = null; }
        }

        private void SetupGitWatcher()
        {
            var gitHeadPath = Path.Combine(_solutionDir, ".git");
            if (!Directory.Exists(gitHeadPath))
            {
                _ = LogAsync("Pas de dossier .git trouvé.");
                return;
            }

            _gitWatcher = new FileSystemWatcher(gitHeadPath, "HEAD");
            _gitWatcher.NotifyFilter = NotifyFilters.LastWrite;
            _gitWatcher.Changed += OnGitHeadChanged;
            _gitWatcher.EnableRaisingEvents = true;
            _ = LogAsync("Git Watcher activé sur HEAD.");
        }

        private void OnGitHeadChanged(object sender, FileSystemEventArgs e)
        {
            if (_gitDebouncer == null)
            {
                _gitDebouncer = new System.Threading.Timer(GitChangeCallback, null, 1000, Timeout.Infinite);
            }
            else
            {
                _gitDebouncer.Change(1000, Timeout.Infinite);
            }
        }

        private void GitChangeCallback(object state)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();

                // On vérifie qu'on est toujours dans un état valide
                if (_dte == null || !_dte.Solution.IsOpen) return;

                string detectedBranch = GetCurrentGitBranch();

                // Si la branche n'a pas changé ou est invalide
                if (string.IsNullOrEmpty(detectedBranch) || detectedBranch == _currentBranch) return;

                await LogAsync($"Changement de branche détecté : {_currentBranch} -> {detectedBranch}");

                // 1. Sauvegarde l'état de l'ANCIENNE branche
                if (!string.IsNullOrEmpty(_currentBranch))
                {
                    await LogAsync($"Sauvegarde finale de {_currentBranch} avant switch...");
                    SaveTabs(_currentBranch);
                }

                // 2. Unload ALL (Fermer tous les onglets)
                await LogAsync("Fermeture de tous les onglets...");
                CloseAllTabs();

                // 3. Pause café (10 secondes pour laisser VS recharger les projets C#)
                await LogAsync("Pause de 10 secondes pour rechargement des projets...");
                await Task.Delay(10000);

                // 4. Mise à jour référence et restauration
                _currentBranch = detectedBranch;
                await JoinableTaskFactory.SwitchToMainThreadAsync(); // On s'assure d'être sur le thread UI

                await LogAsync($"Restauration des onglets pour {detectedBranch}...");
                await RestoreTabs(detectedBranch); // Ajout du await ici
            });
        }

        private void CloseAllTabs()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                // Liste temporaire pour ne pas modifier la collection en itérant
                var docsToClose = new List<dynamic>();
                foreach (dynamic doc in _dte.Documents)
                {
                    docsToClose.Add(doc);
                }

                foreach (dynamic doc in docsToClose)
                {
                    try { doc.Close(vsSaveChangesPrompt); } catch { }
                }
            }
            catch (Exception ex)
            {
                _ = LogAsync($"Erreur lors de la fermeture des onglets : {ex.Message}");
            }
        }

        private void SaveTabs(string branchName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var openDocuments = new List<string>();

                foreach (dynamic doc in _dte.Documents)
                {
                    // On filtre un peu les trucs bizarres
                    string path = doc.FullName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        openDocuments.Add(path);
                    }
                }

                var storePath = Path.Combine(_solutionDir, _storeDirName);
                if (!Directory.Exists(storePath))
                {
                    Directory.CreateDirectory(storePath);
                    File.SetAttributes(storePath, File.GetAttributes(storePath) | FileAttributes.Hidden);
                }

                var safeBranchName = SanitizeFileName(branchName);
                var filePath = Path.Combine(storePath, $"{safeBranchName}.json");

                var json = JsonConvert.SerializeObject(openDocuments, Formatting.Indented);
                File.WriteAllText(filePath, json);

                _ = LogAsync($"Sauvegardé {openDocuments.Count} onglets dans {filePath}");
            }
            catch (Exception ex)
            {
                _ = LogAsync($"Erreur critique sauvegarde : {ex.Message}");
            }
        }

        // Passage de void à async Task pour permettre les awaits internes
        private async Task RestoreTabs(string branchName)
        {
            // CORRECTION JOHNNY : On ne jette pas d'exception dans une méthode async, on switch proprement !
            await this.JoinableTaskFactory.SwitchToMainThreadAsync();

            var safeBranchName = SanitizeFileName(branchName);
            var filePath = Path.Combine(_solutionDir, _storeDirName, $"{safeBranchName}.json");

            if (!File.Exists(filePath))
            {
                await LogAsync($"Aucun fichier de sauvegarde trouvé pour {branchName} (c'est peut-être la première fois ?)");
                return;
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var savedDocs = JsonConvert.DeserializeObject<List<string>>(json);

                if (savedDocs == null) return;

                int restoredCount = 0;
                await LogAsync($"Tentative de restauration de {savedDocs.Count} onglets...");

                foreach (var docPath in savedDocs)
                {
                    if (File.Exists(docPath))
                    {
                        try
                        {
                            // Utilisation de Documents.Open (plus robuste que ItemOperations)
                            _dte.Documents.Open(docPath);
                            restoredCount++;

                            // Petit délai pour éviter de spammer VS et de tout bloquer
                            await Task.Delay(200);
                        }
                        catch (Exception innerEx)
                        {
                            await LogAsync($"Impossible d'ouvrir {docPath} : {innerEx.Message}");
                        }
                    }
                }
                await LogAsync($"Restauré {restoredCount} onglets.");
            }
            catch (Exception ex)
            {
                await LogAsync($"Erreur critique restauration : {ex.Message}");
            }
        }

        private string GetCurrentGitBranch()
        {
            try
            {
                var headPath = Path.Combine(_solutionDir, ".git", "HEAD");
                if (!File.Exists(headPath)) return null;

                var headContent = File.ReadAllText(headPath).Trim();

                if (headContent.StartsWith("ref:"))
                {
                    var branchPath = headContent.Substring(5).Trim();
                    return branchPath.Replace("refs/heads/", "");
                }
                else
                {
                    // Cas du detached head ou hash direct
                    return headContent;
                }
            }
            catch
            {
                return null;
            }
        }

        private string SanitizeFileName(string name)
        {
            // Correction potentielle pour "develop" ou noms simples
            if (string.IsNullOrWhiteSpace(name)) return "unknown_branch";

            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);

            string sanitized = Regex.Replace(name, invalidRegStr, "_");
            return sanitized.Replace("/", "_").Replace("\\", "_");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                OnSolutionClosing();
            }
            base.Dispose(disposing);
        }
    }
}