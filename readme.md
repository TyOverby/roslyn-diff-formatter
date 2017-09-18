# Roslyn Diff Formatter

This is a code formatting tool that uses the Roslyn API to format only the source code that you've modified in version control

## Usage

```
git diff master | diff-formatter.exe MySolution.sln
^                                    ^
|                                     \- Specify the solution that contains these files
 \- Specify the changes that you want to formater
```
