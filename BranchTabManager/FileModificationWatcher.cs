using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        private bool _running = false;

        public event Action<string, bool> BranchChanged;

        public GitBranchWatcher(string gitPath)
        {
            _gitPath = gitPath;
            _headFile = Path.Combine(_gitPath, "HEAD");
            _remotePath = Path.Combine(_gitPath, "refs", "remotes", "origin");
        }

        public void Start()
        {
            _lastHeadContent = GetCurrentBranch();
            _lastRemoteState = GetRemoteState();
            Console.WriteLine("🟢 Branche actuelle : " + _lastHeadContent);

            _running = true;
            new Thread(WatchLoop).Start();
        }

        public void Stop()
        {
            _running = false;
        }

        private void WatchLoop()
        {
            while (_running)
            {
                Thread.Sleep(500); // Vérification toutes les 500ms

                string newHead = GetCurrentBranch();
                if (_lastHeadContent != newHead)
                {
                    _lastHeadContent = newHead;
                    if (BranchChanged != null)
                        BranchChanged(newHead, false);
                }

                string newRemoteState = GetRemoteState();
                if (_lastRemoteState != newRemoteState)
                {
                    _lastRemoteState = newRemoteState;
                    if (BranchChanged != null)
                        BranchChanged(newRemoteState, true);
                }
            }
        }

        private string GetCurrentBranch()
        {
            if (File.Exists(_headFile))
            {
                string headContent = File.ReadAllText(_headFile).Trim();
                if (headContent.StartsWith("ref:"))
                {
                    return headContent.Substring(16); // Enlève "ref: refs/heads/"
                }
                else
                {
                    return "(HEAD détaché)";
                }
            }
            return "";
        }

        private string GetRemoteState()
        {
            if (!Directory.Exists(_remotePath))
                return "";

            string[] files = Directory.GetFiles(_remotePath, "*", SearchOption.AllDirectories);
            string remoteState = "";
            foreach (string file in files)
            {
                remoteState += file + "|";
            }
            return remoteState;
        }
    }
}
