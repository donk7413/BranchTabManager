# Branch Tab Manager for Visual Studio 2022

This VSIX extension is designed to enhance your Visual Studio development workflow by automatically managing open tabs based on your Git branches. It remembers the files you have open for each branch and restores them when you switch branches, helping you maintain a clean and focused working context.

## Overview

The extension performs the following tasks:
- **Branch-Specific Tab Management:**  
  Monitors the current Git branch by reading the `.git/HEAD` file and saves the list of open document file paths into a JSON file named after the branch.
  
- **Automatic Saving & Restoration:**  
  On each document save, it records the currently open documents. When you switch branches (detected via changes in the Git HEAD), it automatically closes the current tabs and opens the saved files for the new branch.
  
- **Cleanup of Obsolete Data:**  
  It checks the repository for existing branches and deletes any saved JSON files (stored in a hidden `.switchtab` folder) that correspond to branches which no longer exist.

## How It Works

1. **Detecting the Repository & Branch:**  
   The extension searches upward from the solution directory to locate the `.git` folder, then reads the `.git/HEAD` file to determine the current branch.

2. **Saving Open Tabs:**  
   Each time a document is saved, the extension gathers all currently open file paths and writes them to a JSON file in the `.switchtab` directory. This file is named after the current branch.

3. **Restoring Tabs on Branch Switch:**  
   When the Git branch changes, the extension:
   - Delays briefly to allow Git to finish its updates.
   - Closes the current open documents.
   - Loads and opens the files listed in the JSON file corresponding to the new branch.

4. **Cleanup:**  
   The extension scans the repository’s branch directory (`.git/refs/heads`) and removes any JSON files in `.switchtab` that no longer match an existing branch.

## Requirements

- **Visual Studio 2022** (version 17.13.0)
- A solution located inside a Git repository
- .NET Framework (as required by the project)

## Installation (Standard Mode)
1. **Download and install [BranchTabManager.vsix]([https://github.com/donk7413/BranchTabManager/raw/54e49d1c41e27cc70bdb3d1b959f9112877f2264/BranchTabManager.vsix]). **


## Installation (Debug Mode)

1. **Clone the Repository**
2. **Open the Solution:**
Open the solution in Visual Studio 2022.
3. **Build the Extension:**
Build the project to produce the VSIX package.
4. **Run or Debug:**
Press F5 to launch an experimental instance of Visual Studio with the extension enabled.
5. **Deploy:**
Alternatively, locate the generated .vsix file in the output directory and install it by double-clicking the file.

## Usage

### Saving Tabs:
Each time you save a document, the extension updates the list of open tabs for the current branch.

### Restoring Tabs:
When you switch Git branches, the extension automatically closes the open documents and restores the tabs saved for that branch.
