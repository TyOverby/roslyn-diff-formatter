# Roslyn Diff Formatter

Use git-diff and Roslyn to format just the lines that you've changed!

## Usage

For just your working changes

`git diff | RoslynDiffFormatter.exe mysolution.sln`

For all changes (against another branch)

`git diff master | RoslynDiffFormatter.exe mysolution.sln`