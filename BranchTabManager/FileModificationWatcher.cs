using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BranchTabManager
{
    class FileModificationWatcher
    {
        private FileSystemWatcher fileWatcher;

        // Event to notify when the file is modified
        public event EventHandler<FileSystemEventArgs> FileModified;

        public FileModificationWatcher(string filePath)
        {
            fileWatcher = new FileSystemWatcher(Path.GetDirectoryName(filePath), Path.GetFileName(filePath));
            fileWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastAccess ;
            fileWatcher.Changed += OnFileChanged;
            fileWatcher.EnableRaisingEvents = true;
            fileWatcher.Changed += OnFileChanged;
            fileWatcher.Created += OnFileChanged;
            fileWatcher.Deleted += OnFileChanged;
            fileWatcher.Renamed += OnFileChanged;
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            // Raise the custom event when the file is modified
            FileModified?.Invoke(this, e);
        }
    }
}
